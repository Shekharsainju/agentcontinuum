using System.Text;
using AgentContinuum.Core.Platform;

namespace AgentContinuum.Core.Tests.Launch;

/// <summary>Records each launch spec and its stdin, and hands back a scripted run.</summary>
public sealed class FakeProcessHost : IProcessHost
{
    public List<LaunchSpec> Started { get; } = [];

    public List<string> StdinTexts { get; } = [];

    public FakeRun Run { get; set; } = new();

    /// <summary>Called after the spec is recorded and before the run is returned.</summary>
    public Action<LaunchSpec>? OnStart { get; set; }

    public Exception? StartThrows { get; set; }

    public IHostedRun Start(LaunchSpec spec, Action<int, long>? created = null)
    {
        if (StartThrows is { } e)
        {
            throw e;
        }
        Started.Add(spec);
        using (var reader = new StreamReader(spec.Stdin, new UTF8Encoding(false), leaveOpen: false))
        {
            StdinTexts.Add(reader.ReadToEnd());
        }
        created?.Invoke(Run.Pid, Run.CreatedAtFileTime);
        OnStart?.Invoke(spec);
        return Run;
    }
}

/// <summary>
/// Emits <see cref="Lines"/>, then exits with <see cref="ExitCode"/>, or stays alive until terminated when it is null.
/// Like a real pipe, stdout ends only once the process has exited.
/// </summary>
public sealed class FakeRun : IHostedRun
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _jobKilled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<string> Lines { get; } = [];

    public int? ExitCode { get; set; } = 0;

    /// <summary>Overrides the default of one process while running and none after exit.</summary>
    public Func<int>? Active { get; set; }

    /// <summary>Thrown by the stdout reader after <see cref="Lines"/>, while the process is still running.</summary>
    public Exception? StdoutThrows { get; set; }

    /// <summary>A grandchild inherits stdout and keeps it open after the main process exits, until the job is killed.</summary>
    public bool GrandchildHoldsStdout { get; set; }

    public KillResult KillResult { get; set; } = new(true, 0);

    /// <summary>Handed out by successive kills before <see cref="KillResult"/> applies. A failed kill leaves the run going.</summary>
    public Queue<KillResult> KillResults { get; } = new();

    public int Kills { get; private set; }

    public int ActiveQueries { get; private set; }

    public bool Disposed { get; private set; }

    public bool UsedAfterDispose { get; private set; }

    public int Pid { get; set; } = 4321;

    public long CreatedAtFileTime { get; set; } = 133_000_000_000_000_000;

    public IAsyncEnumerable<string> StdoutLines => ReadLines();

    public Task<int> WaitForExit() => _exit.Task;

    public int ActiveProcesses()
    {
        UsedAfterDispose |= Disposed;
        ActiveQueries++;
        return Active?.Invoke() ?? (!_exit.Task.IsCompleted || GrandchildAlive ? 1 : 0);
    }

    public KillResult Terminate()
    {
        UsedAfterDispose |= Disposed;
        Kills++;
        var result = KillResults.Count > 0 ? KillResults.Dequeue() : KillResult;
        if (result.Ok)
        {
            _exit.TrySetResult(1);
            _jobKilled.TrySetResult();
        }
        return result;
    }

    private bool GrandchildAlive => GrandchildHoldsStdout && !_jobKilled.Task.IsCompleted;

    // Closing the job kills the process, as the real host does.
    public void Dispose()
    {
        Disposed = true;
        _exit.TrySetResult(1);
        _jobKilled.TrySetResult();
    }

    private async IAsyncEnumerable<string> ReadLines()
    {
        foreach (var line in Lines)
        {
            await Task.Yield();
            yield return line;
        }
        if (StdoutThrows is { } e)
        {
            throw e;
        }
        if (ExitCode is { } code)
        {
            _exit.TrySetResult(code);
        }
        await _exit.Task;
        if (GrandchildHoldsStdout)
        {
            await _jobKilled.Task;
        }
    }
}
