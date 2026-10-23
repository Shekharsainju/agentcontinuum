namespace AgentContinuum.Core.Platform;

public interface IProcessHost
{
    /// <summary>Starts the process inside a kill-on-close job before any of its code runs.</summary>
    /// <param name="created">
    /// Called with the PID and creation time once the process is in its job and before it runs. If it throws, the
    /// process is ended before it runs and Start throws.
    /// </param>
    IHostedRun Start(LaunchSpec spec, Action<int, long>? created = null);
}

/// <summary><see cref="Env"/> is the child's whole environment; nothing is inherited.</summary>
public sealed record LaunchSpec(string Exe, string[] Args, string WorkingDir, IReadOnlyDictionary<string, string> Env, Stream Stdin);

/// <summary>Disposing closes the job, which kills every process still in it.</summary>
public interface IHostedRun : IDisposable
{
    int Pid { get; }

    long CreatedAtFileTime { get; }

    IAsyncEnumerable<string> StdoutLines { get; }

    Task<int> WaitForExit();

    int ActiveProcesses();

    KillResult Terminate();
}

public sealed record KillResult(bool Ok, int Win32Error);
