using AgentContinuum.Core.State;

namespace AgentContinuum.Cli;

/// <summary>The one process that launches tasks, polls requests and makes offers.</summary>
public sealed class Daemon : IDisposable
{
    public const int AlreadyRunning = 2;

    private readonly AppServices _services;
    private Semaphore? _lock;
    private StateStore? _store;

    public Daemon(AppServices services)
    {
        _services = services;
    }

    // A semaphore rather than a mutex: a mutex belongs to the thread that took it, and the loop's await
    // continuations run on other threads. Windows destroys it with its last handle, so a crashed daemon frees it.
    public bool TryStart()
    {
        var gate = new Semaphore(1, 1, _services.DaemonLockName);
        if (!gate.WaitOne(0))
        {
            gate.Dispose();
            return false;
        }
        _lock = gate;
        _store = _services.OpenStore();
        return true;
    }

    public async Task<int> Run(CancellationToken stop)
    {
        if (!TryStart())
        {
            _services.Out.WriteLine("daemon already running");
            return AlreadyRunning;
        }
        _services.Out.WriteLine("daemon running");
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(_services.RequestPoll, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        return 0;
    }

    public void Dispose()
    {
        _store?.Dispose();
        _store = null;
        if (_lock is { } gate)
        {
            gate.Release();
            gate.Dispose();
            _lock = null;
        }
    }
}
