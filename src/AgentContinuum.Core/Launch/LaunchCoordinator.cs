using AgentContinuum.Core.Model;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Preflight;
using AgentContinuum.Core.Rules;
using AgentContinuum.Core.Schedule;
using AgentContinuum.Core.State;

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
    private readonly StateStore _store;
    private readonly PreflightRunner _preflight;
    private readonly IProcessHost _host;
    private readonly IUsageLimitHandler _usageLimits;
    private readonly INotifier _notifier;
    private readonly ILog _log;
    private readonly LaunchPaths _paths;
    private readonly PreflightHost _preflightHost;
    private readonly Func<string, ClaudeRules?> _rulesFor;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly TaskRepository _tasks;

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
        _usageLimits = usageLimits;
        _notifier = notifier;
        _log = log;
        _paths = paths;
        _preflightHost = preflightHost;
        _rulesFor = rulesFor ?? VersionRules.For;
        _delay = delay ?? (d => Task.Delay(d));
        _tasks = new TaskRepository(store);
    }

    public Task<LaunchReport> Launch(string taskId, LaunchKind kind, bool now)
    {
        var preflight = _preflight.Run(taskId, now);
        if (!preflight.Pass)
        {
            FlagAttention(taskId, string.Join(", ", preflight.Reasons));
            return Task.FromResult(new LaunchReport(false, preflight, null, null));
        }
        throw new NotImplementedException();
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
