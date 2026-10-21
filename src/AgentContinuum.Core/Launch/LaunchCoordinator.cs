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

    public const string BaselineNotTakenReason = "Workspace baseline not taken";

    public const string LaunchFailedReason = "Launch failed";

    public const string RunErrorReason = "Run error";

    public const string JobNotEmptiedReason = "Processes outlived the run";

    public static readonly TimeSpan LimitKillAfter = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan KillRetryDelay = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan HookPollInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan EmptyJobPollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan EmptyJobWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan SettleWait = TimeSpan.FromSeconds(30);

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

    /// <param name="started">Called with the op ID once the process is running, long before the run ends.</param>
    /// <param name="stop">Kills the run when cancelled. A task moved out of RUNNING meanwhile keeps its new state.</param>
    public async Task<LaunchReport> Launch(
        string taskId, LaunchKind kind, bool now, Action<string>? started = null, CancellationToken stop = default)
    {
        var preflight = _preflight.Run(taskId, now);
        if (!preflight.Pass)
        {
            // A task that cannot launch from its state is revoked, finished or mid-launch. Moving it to attention
            // would make it resumable again, so it is left as it is.
            if (!preflight.Reasons.Contains(PreflightReasons.AdmissionState))
            {
                FlagAttention(taskId, string.Join(", ", preflight.Reasons));
            }
            return new LaunchReport(false, preflight, null, null);
        }

        var task = _tasks.Get(taskId)!;
        var rules = _rulesFor(task.CliVersion);
        string opId;
        try
        {
            opId = _ledger.BeginIntent(taskId);
        }
        catch (InvalidOperationException)
        {
            // The task changed after pre-flight passed. As with a refused admission it is left as it is.
            _log.Write($"launch of task {taskId} refused at intent");
            return new LaunchReport(false, new PreflightResult(false, [PreflightReasons.AdmissionState]), null, null);
        }
        if (TryStart(task, kind, opId, rules) is not { } launched)
        {
            // As in recovery of an INTENT op with no process: the attempt stays counted and the task is updated
            // before the op leaves its live state.
            FlagAttention(taskId, LaunchFailedReason);
            _ledger.Abandon(opId);
            return new LaunchReport(false, preflight, opId, null);
        }
        using var run = launched.Run;
        // The host feeds stdin in the background and leaves the source open, so it is closed here after the run.
        using var stdin = launched.Stdin;
        try
        {
            var outcome = await Supervise(task, opId, run, rules, started, stop);
            return new LaunchReport(true, preflight, opId, outcome);
        }
        catch (Exception e)
        {
            // Left alone, the task would stay RUNNING with a live op and every resume would be refused until
            // the daemon restarts, with nothing telling the user.
            run.Dispose();
            _log.Write($"launch op {opId} failed: {e.GetType().Name}");
            EndFailedRun(task, opId, e is RunStoppedException stopped ? stopped.Reason : RunErrorReason);
            return new LaunchReport(true, preflight, opId, null);
        }
    }

    private async Task<Outcome> Supervise(
        TaskRecord task, string opId, IHostedRun run, ClaudeRules? rules, Action<string>? started, CancellationToken stop)
    {
        _ledger.MarkSpawned(opId, run.Pid, run.CreatedAtFileTime, task.SessionId);
        using var stopping = stop.Register(() => Kill(run, opId, "stopped"));
        started?.Invoke(opId);

        var exited = run.WaitForExit();
        // Started with the stream rather than after it: a grandchild that inherited stdout keeps it open after the
        // main process exits, and only killing the job then ends the stream.
        var emptied = EmptyJobAfterExit(run, opId, exited);
        var watch = rules?.Limit is { } watched ? WatchForLimit(run, opId, task.SessionId, watched, exited) : Task.CompletedTask;
        try
        {
            return await Follow(task, opId, run, rules, exited, emptied, watch);
        }
        catch (Exception e)
        {
            await StopFailedRun(run, opId, e is RunStoppedException, exited, emptied, watch);
            throw;
        }
    }

    private async Task<Outcome> Follow(
        TaskRecord task, string opId, IHostedRun run, ClaudeRules? rules, Task<int> exited, Task<bool> emptied, Task watch)
    {
        var taskId = task.TaskId;
        var reading = ReadStream(run, opId, rules);
        // A job that will not empty after exit can hold the stream open for ever, and closing it is the last resort.
        if (await Task.WhenAny(reading, emptied) == emptied && emptied is { IsCompletedSuccessfully: true, Result: false })
        {
            _log.Write($"close op {opId} reason processes after exit");
            throw new RunStoppedException(JobNotEmptiedReason);
        }
        await reading;
        await watch;
        var exitCode = await exited;
        _ledger.MarkExited(opId, exitCode);
        var baselineTaken = await TakeBaseline(emptied, task);

        var classification = RunClassifier.Classify(
            rules, task.SessionId, StreamEventLog.ForOp(_store, opId), HookRecorder.ForOp(_store, opId), exitCode);
        // A task revoked during the run keeps that state, since any outcome would make it launchable again.
        var revoked = _tasks.Get(taskId) is not { State: TaskState.Running };
        // Without a fresh baseline the next pre-flight would compare against a stale one or none, so a task that
        // could launch again goes to attention instead, in the same single write the outcome would have made.
        if (revoked)
        {
            _log.Write($"launch op {opId} ended after its task left RUNNING; outcome not applied");
        }
        else if (baselineTaken || classification.Outcome == Outcome.Completed)
        {
            _outcomes.Apply(taskId, classification);
        }
        else
        {
            _tasks.SetState(taskId, TaskState.NeedsAttention, BaselineNotTakenReason);
        }
        _ledger.Classify(opId, classification.Outcome, classification.Reason);
        if (_tasks.Get(taskId) is { State: TaskState.NeedsAttention } flagged)
        {
            _notifier.Attention(taskId, flagged.Label, flagged.AttentionReason ?? "");
        }
        return classification.Outcome;
    }

    // The watcher and the empty-job wait still use the run and the store, so they finish before the run is
    // disposed. A run whose job is already being closed has nothing left to kill.
    private async Task StopFailedRun(IHostedRun run, string opId, bool jobClosing, params Task[] background)
    {
        if (jobClosing || !Kill(run, opId, "run error"))
        {
            run.Dispose();
        }
        try
        {
            await Task.WhenAll(background.Select(Settled)).WaitAsync(SettleWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.Write($"launch op {opId} background work still running");
        }
    }

    private static async Task Settled(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    // The task is updated before the op leaves its live state, as in recovery. The background limit watcher may
    // still be reading, so the writes take the same lock. The user is told even when the writes fail, since the
    // task may then stay RUNNING until the daemon recovers it.
    private void EndFailedRun(TaskRecord task, string opId, string reason)
    {
        var flagged = true;
        try
        {
            lock (_db)
            {
                var state = _ledger.Get(opId)?.State;
                if (state is not (OpState.Intent or OpState.Spawned or OpState.Exited))
                {
                    return;
                }
                flagged = FlagIfStillRunning(task.TaskId, reason);
                if (state == OpState.Exited)
                {
                    _ledger.Classify(opId, Outcome.Ambiguous, reason);
                }
                else
                {
                    _ledger.Interrupt(opId);
                }
            }
        }
        catch (Exception e)
        {
            _log.Write($"launch op {opId} not closed: {e.GetType().Name}");
        }
        if (flagged)
        {
            _notifier.Attention(task.TaskId, task.Label, reason);
        }
    }

    // A task moved during the run, by a revoke or by its outcome already being applied, keeps that state: flagging
    // it would make a revoked or completed task launchable again.
    private bool FlagIfStillRunning(string taskId, string reason)
    {
        if (_tasks.Get(taskId) is not { State: TaskState.Running })
        {
            return false;
        }
        _tasks.SetState(taskId, TaskState.NeedsAttention, reason);
        return true;
    }

    // The host ends any process it created before it throws, so a failure here means nothing ran.
    private (IHostedRun Run, Stream Stdin)? TryStart(TaskRecord task, LaunchKind kind, string opId, ClaudeRules? rules)
    {
        Stream? stdin = null;
        try
        {
            // Without a verified limit signal the hook could not be classified, so it is not installed.
            var settingsPath = rules?.Limit is { } limit ? WriteSettings(opId, limit) : null;
            stdin = kind == LaunchKind.First
                ? File.OpenRead(Path.GetFullPath(Path.Combine(task.Worktree, task.PromptFile)))
                : new MemoryStream(Encoding.UTF8.GetBytes(ResumePrompt));
            var run = _host.Start(
                new LaunchSpec(task.CliPath, BuildArgs(task, kind, settingsPath), task.Worktree, ChildEnv(task), stdin));
            return (run, stdin);
        }
        catch (Exception e)
        {
            stdin?.Dispose();
            _log.Write($"launch op {opId} did not start: {e.GetType().Name}");
            return null;
        }
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

    /// <summary>The CLI arguments for a launch; the prompt goes on stdin.</summary>
    public static string[] BuildArgs(TaskRecord task, LaunchKind kind, string? settingsPath)
    {
        // Pre-flight and the ledger refuse these modes already; the args are the last place one could slip through.
        if (!LaunchPolicy.IsAllowedMode(task.PermissionMode))
        {
            throw new InvalidOperationException("The permission mode is outside the launch policy.");
        }
        List<string> args =
        [
            "-p", kind == LaunchKind.First ? "--session-id" : "--resume", task.SessionId.ToString("D"),
            "--output-format", "stream-json", "--verbose",
            "--permission-mode", task.PermissionMode,
        ];
        if (task.AllowedTools.Length > 0)
        {
            // One argument per rule: a joined value would split any rule that itself contains a comma.
            args.AddRange(["--allowedTools", .. task.AllowedTools]);
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
                await StopForBilling(run, opId).ConfigureAwait(false);
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
    private async Task<bool> EmptyJobAfterExit(IHostedRun run, string opId, Task exited)
    {
        await exited.ConfigureAwait(false);
        if (await WaitForEmptyJob(run).ConfigureAwait(false))
        {
            return true;
        }
        Kill(run, opId, "processes after exit");
        return await WaitForEmptyJob(run).ConfigureAwait(false);
    }

    private async Task<bool> TakeBaseline(Task<bool> emptied, TaskRecord task)
    {
        if (!await emptied.ConfigureAwait(false))
        {
            return false;
        }
        var result = WorkspaceFingerprint.Compute(task.Worktree, _preflightHost.GitExe, _preflightHost.FingerprintMaxFiles);
        if (result is not { Hash: { } hash, Incomplete: false })
        {
            return false;
        }
        _tasks.SetBaseline(task.TaskId, hash);
        return true;
    }

    private async Task<bool> WaitForEmptyJob(IHostedRun run)
    {
        for (var waited = TimeSpan.Zero; run.ActiveProcesses() > 0; waited += EmptyJobPollInterval)
        {
            if (waited >= EmptyJobWait)
            {
                return false;
            }
            await _delay(EmptyJobPollInterval).ConfigureAwait(false);
        }
        return true;
    }

    // A run on a route that may not be the subscription must not go on. A failed kill is retried once, then the
    // job is closed, which kills everything in it but leaves nothing more to supervise.
    private async Task StopForBilling(IHostedRun run, string opId)
    {
        const string reason = "billing mismatch";
        if (Kill(run, opId, reason))
        {
            return;
        }
        await _delay(KillRetryDelay).ConfigureAwait(false);
        if (Kill(run, opId, reason))
        {
            return;
        }
        _log.Write($"close op {opId} reason {reason}");
        throw new RunStoppedException(RunClassifier.BillingMismatchReason);
    }

    private bool Kill(IHostedRun run, string opId, string reason)
    {
        var result = run.Terminate();
        _log.Write($"kill op {opId} reason {reason} ok {result.Ok} error {result.Win32Error}");
        return result.Ok;
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

internal sealed class RunStoppedException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
