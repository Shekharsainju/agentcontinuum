using System.Text;
using System.Text.Json.Nodes;
using AgentContinuum.Core.Billing;
using AgentContinuum.Core.Classify;
using AgentContinuum.Core.Events;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Preflight;
using AgentContinuum.Core.Rules;
using AgentContinuum.Core.Schedule;
using AgentContinuum.Core.State;
using AgentContinuum.Core.Workspace;

namespace AgentContinuum.Core.Launch;

public enum LaunchKind
{
    First,
    Resume,
}

public sealed record LaunchReport(bool Launched, PreflightResult Preflight, string? OpId, Outcome? Outcome);

/// <summary><see cref="LaunchRoot"/> holds one folder per launch op for its settings file.</summary>
public sealed record LaunchPaths(string AcExe, string LaunchRoot);

/// <summary>Runs the spec's launch steps for a first launch or a resume, each behind the same pre-flight.</summary>
public sealed class LaunchCoordinator
{
    public const string ResumePrompt =
        "Continue the task from where you stopped. Your usage limit has reset. Check the repository state before you act.";

    public static readonly TimeSpan LimitKillAfter = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan HookPollInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan EmptyJobPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly StateStore _store;
    private readonly PreflightRunner _preflight;
    private readonly IProcessHost _host;
    private readonly INotifier _notifier;
    private readonly ILog _log;
    private readonly LaunchPaths _paths;
    private readonly PreflightHost _preflightHost;
    private readonly Func<string, ClaudeRules?> _rulesFor;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly TaskRepository _tasks;
    private readonly LaunchLedger _ledger;
    private readonly OutcomeApplier _outcomes;

    // The stream reader and the limit watcher run at once, and a SqliteConnection is not safe across threads.
    private readonly object _db = new();

    /// <param name="delay">Waits between checks of a running launch; for tests.</param>
    public LaunchCoordinator(
        StateStore store,
        PreflightRunner preflight,
        IProcessHost host,
        IUsageLimitHandler usageLimits,
        INotifier notifier,
        ILog log,
        LaunchPaths paths,
        PreflightHost preflightHost,
        Func<string, ClaudeRules?>? rulesFor = null,
        Func<TimeSpan, Task>? delay = null)
    {
        _store = store;
        _preflight = preflight;
        _host = host;
        _notifier = notifier;
        _log = log;
        _paths = paths;
        _preflightHost = preflightHost;
        _rulesFor = rulesFor ?? VersionRules.For;
        _delay = delay ?? (d => Task.Delay(d));
        _tasks = new TaskRepository(store);
        _ledger = new LaunchLedger(store);
        _outcomes = new OutcomeApplier(_tasks, usageLimits);
    }

    public async Task<LaunchReport> Launch(string taskId, LaunchKind kind, bool now)
    {
        var preflight = _preflight.Run(taskId, now);
        if (!preflight.Pass)
        {
            FlagAttention(taskId, string.Join(", ", preflight.Reasons));
            return new LaunchReport(false, preflight, null, null);
        }

        var task = _tasks.Get(taskId)!;
        var rules = _rulesFor(task.CliVersion);
        var opId = _ledger.BeginIntent(taskId);
        // Without a verified limit signal the hook could not be classified, so it is not installed.
        var settingsPath = rules?.Limit is { } limit ? WriteSettings(opId, limit) : null;
        // The host feeds stdin in the background and leaves the source open, so it is closed here after the run.
        using Stream stdin = kind == LaunchKind.First
            ? File.OpenRead(Path.GetFullPath(Path.Combine(task.Worktree, task.PromptFile)))
            : new MemoryStream(Encoding.UTF8.GetBytes(ResumePrompt));
        using var run = _host.Start(
            new LaunchSpec(task.CliPath, Args(task, kind, settingsPath), task.Worktree, ChildEnv(task), stdin));
        _ledger.MarkSpawned(opId, run.Pid, run.CreatedAtFileTime, task.SessionId);

        var exited = run.WaitForExit();
        var watch = rules?.Limit is { } watched ? WatchForLimit(run, opId, task.SessionId, watched, exited) : Task.CompletedTask;
        await ReadStream(run, opId, rules);
        await watch;
        var exitCode = await run.WaitForExit();
        _ledger.MarkExited(opId, exitCode);
        await TakeBaseline(run, task);

        var classification = RunClassifier.Classify(
            rules, task.SessionId, StreamEventLog.ForOp(_store, opId), HookRecorder.ForOp(_store, opId), exitCode);
        _outcomes.Apply(taskId, classification);
        _ledger.Classify(opId, classification.Outcome, classification.Reason);
        return new LaunchReport(true, preflight, opId, classification.Outcome);
    }

    private string WriteSettings(string opId, LimitSignalRule limit)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_paths.LaunchRoot, opId)).FullName;
        var path = Path.Combine(dir, "settings.json");
        var command = new JsonObject { ["type"] = "command", ["command"] = $"\"{_paths.AcExe}\" hook stop-failure --op {opId}" };
        var settings = new JsonObject
        {
            ["hooks"] = new JsonObject { [limit.Hook] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(command) }) },
        };
        File.WriteAllText(path, settings.ToJsonString());
        return path;
    }

    private static string[] Args(TaskRecord task, LaunchKind kind, string? settingsPath)
    {
        List<string> args =
        [
            "-p", kind == LaunchKind.First ? "--session-id" : "--resume", task.SessionId.ToString("D"),
            "--output-format", "stream-json", "--verbose",
            "--permission-mode", task.PermissionMode,
        ];
        if (task.AllowedTools.Length > 0)
        {
            args.AddRange(["--allowedTools", string.Join(',', task.AllowedTools)]);
        }
        if (settingsPath is not null)
        {
            args.AddRange(["--settings", settingsPath]);
        }
        return [.. args];
    }

    private IReadOnlyDictionary<string, string> ChildEnv(TaskRecord task) => ChildEnvironment.Build(
        _preflightHost.ParentEnv, Path.GetDirectoryName(task.CliPath) ?? "", Path.GetDirectoryName(_preflightHost.GitExe) ?? "");

    // Only the filtered view of each line is kept; the raw line never leaves this method.
    private async Task ReadStream(IHostedRun run, string opId, ClaudeRules? rules)
    {
        var filter = rules is null ? null : new EventFilter(rules);
        var billing = FirstEventStatus.Pending;
        await foreach (var line in run.StdoutLines.ConfigureAwait(false))
        {
            if (filter?.Accept(line) is not { } filtered)
            {
                continue;
            }
            lock (_db)
            {
                StreamEventLog.Append(_store, opId, filtered);
            }
            var observed = FirstEventCheck.Observe(billing, filtered, rules);
            // Killed on the first sign, before more work can run on a route that may not be the subscription.
            if (observed == FirstEventStatus.Mismatch && billing != FirstEventStatus.Mismatch)
            {
                Kill(run, opId, "billing mismatch");
            }
            billing = observed;
        }
    }

    // The hook is seen up to one poll after it fired, so the wait is shortened by that much to keep the
    // whole job ended within LimitKillAfter of the signal.
    private async Task WatchForLimit(IHostedRun run, string opId, Guid sessionId, LimitSignalRule limit, Task exited)
    {
        while (!exited.IsCompleted)
        {
            IReadOnlyList<HookEvent> hooks;
            lock (_db)
            {
                hooks = HookRecorder.ForOp(_store, opId);
            }
            if (hooks.Any(h => RunClassifier.IsVerifiedLimit(limit, sessionId, h)))
            {
                await _delay(LimitKillAfter - HookPollInterval).ConfigureAwait(false);
                if (run.ActiveProcesses() > 0)
                {
                    Kill(run, opId, "limit signal");
                }
                return;
            }
            await _delay(HookPollInterval).ConfigureAwait(false);
        }
    }

    // A child can outlive the main process and keep writing, so the snapshot waits for the job to empty.
    private async Task TakeBaseline(IHostedRun run, TaskRecord task)
    {
        while (run.ActiveProcesses() > 0)
        {
            await _delay(EmptyJobPollInterval).ConfigureAwait(false);
        }
        var result = WorkspaceFingerprint.Compute(task.Worktree, _preflightHost.GitExe, _preflightHost.FingerprintMaxFiles);
        if (result is { Hash: { } hash, Incomplete: false })
        {
            _tasks.SetBaseline(task.TaskId, hash);
        }
    }

    private void Kill(IHostedRun run, string opId, string reason)
    {
        var result = run.Terminate();
        _log.Write($"kill op {opId} reason {reason} ok {result.Ok} error {result.Win32Error}");
    }

    private void FlagAttention(string taskId, string reason)
    {
        if (_tasks.Get(taskId) is not { } task)
        {
            return;
        }
        _tasks.SetState(taskId, TaskState.NeedsAttention, reason);
        _notifier.Attention(taskId, task.Label, reason);
    }
}
