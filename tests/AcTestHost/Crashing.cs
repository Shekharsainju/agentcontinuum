using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using AgentContinuum.Core.Platform;

namespace AgentContinuum.AcTestHost;

/// <summary>Ends the daemon the way a crash would: no finally blocks, no disposal, the job closed by Windows.</summary>
public static class Crash
{
    public const string KillOnGitVariable = "AC_TEST_KILL_ON_GIT";

    public static void Now() => Process.GetCurrentProcess().Kill();
}

/// <summary>The first command pre-flight runs comes before INTENT is written.</summary>
public sealed class CrashingRunner : ICommandRunner
{
    private readonly ICommandRunner _inner;
    private readonly CrashPoint _point;

    public CrashingRunner(ICommandRunner inner, CrashPoint point)
    {
        _inner = inner;
        _point = point;
    }

    public CommandResult Run(string exe, string[] args, IReadOnlyDictionary<string, string> env, TimeSpan timeout)
    {
        if (_point == CrashPoint.BeforeIntent)
        {
            Crash.Now();
        }
        return _inner.Run(exe, args, env, timeout);
    }
}

public sealed class CrashingHost : IProcessHost
{
    private readonly IProcessHost _inner;
    private readonly CrashPoint _point;

    public CrashingHost(IProcessHost inner, CrashPoint point)
    {
        _inner = inner;
        _point = point;
    }

    public IHostedRun Start(LaunchSpec spec)
    {
        if (_point == CrashPoint.BeforeSpawn)
        {
            Crash.Now();
        }
        var run = _inner.Start(spec);
        if (_point == CrashPoint.AfterSpawn)
        {
            Crash.Now();
        }
        return _point is CrashPoint.WhileRunning or CrashPoint.AfterExit ? new CrashingRun(run, _point) : run;
    }
}

public sealed class CrashingRun : IHostedRun
{
    private readonly IHostedRun _inner;
    private readonly CrashPoint _point;

    public CrashingRun(IHostedRun inner, CrashPoint point)
    {
        _inner = inner;
        _point = point;
    }

    public int Pid => _inner.Pid;

    public long CreatedAtFileTime => _inner.CreatedAtFileTime;

    public IAsyncEnumerable<string> StdoutLines => _point == CrashPoint.WhileRunning ? CrashOnFirstLine() : _inner.StdoutLines;

    // The daemon's next git call after the exit is the baseline taken after EXITED is written; the git shim
    // ends the daemon there, before the run is classified.
    public async Task<int> WaitForExit()
    {
        var exitCode = await _inner.WaitForExit().ConfigureAwait(false);
        if (_point == CrashPoint.AfterExit)
        {
            Environment.SetEnvironmentVariable(Crash.KillOnGitVariable, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        return exitCode;
    }

    public int ActiveProcesses() => _inner.ActiveProcesses();

    public KillResult Terminate() => _inner.Terminate();

    public void Dispose() => _inner.Dispose();

    private async IAsyncEnumerable<string> CrashOnFirstLine([EnumeratorCancellation] CancellationToken cancel = default)
    {
        await foreach (var _ in _inner.StdoutLines.WithCancellation(cancel).ConfigureAwait(false))
        {
            Crash.Now();
        }
        yield break;
    }
}
