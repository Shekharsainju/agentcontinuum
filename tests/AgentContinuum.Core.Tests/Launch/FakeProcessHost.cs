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

    public IHostedRun Start(LaunchSpec spec)
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

    public List<string> Lines { get; } = [];

    public int? ExitCode { get; set; } = 0;

    /// <summary>Overrides the default of one process while running and none after exit.</summary>
    public Func<int>? Active { get; set; }

    public KillResult KillResult { get; set; } = new(true, 0);

    public int Kills { get; private set; }

    public int ActiveQueries { get; private set; }

    public bool Disposed { get; private set; }

    public int Pid { get; set; } = 4321;

    public long CreatedAtFileTime { get; set; } = 133_000_000_000_000_000;

    public IAsyncEnumerable<string> StdoutLines => ReadLines();

    public Task<int> WaitForExit() => _exit.Task;

    public int ActiveProcesses()
    {
        ActiveQueries++;
        return Active?.Invoke() ?? (_exit.Task.IsCompleted ? 0 : 1);
    }

    public KillResult Terminate()
    {
        Kills++;
        _exit.TrySetResult(1);
        return KillResult;
    }

    public void Dispose() => Disposed = true;

    private async IAsyncEnumerable<string> ReadLines()
    {
        foreach (var line in Lines)
        {
            await Task.Yield();
            yield return line;
        }
        if (ExitCode is { } code)
        {
            _exit.TrySetResult(code);
        }
        await _exit.Task;
    }
}
