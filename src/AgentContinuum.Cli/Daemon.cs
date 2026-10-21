using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentContinuum.Core.Billing;
using AgentContinuum.Core.Classify;
using AgentContinuum.Core.Launch;
using AgentContinuum.Core.Preflight;
using AgentContinuum.Core.Recovery;
using AgentContinuum.Core.Schedule;
using AgentContinuum.Core.State;

namespace AgentContinuum.Cli;

/// <summary>The one process that launches tasks, polls requests and makes offers.</summary>
public sealed class Daemon : IDisposable
{
    public const int AlreadyRunning = 2;

    private readonly AppServices _services;
    private readonly ConcurrentDictionary<string, RunningLaunch> _launches = new();
    private Semaphore? _lock;
    private StateStore? _store;
    private TaskRepository _tasks = null!;
    private ResetScheduler _scheduler = null!;
    private RequestQueue _queue = null!;

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
        _tasks = new TaskRepository(_store);
        _scheduler = new ResetScheduler(_tasks, _services.Clock, _services.Log);
        _queue = new RequestQueue(_store, _services.Clock);
        var ledger = new LaunchLedger(_store);
        new RecoveryService(_store, ledger, _tasks, _services.Lister, new OutcomeApplier(_tasks, _scheduler), _services.RulesFor)
            .Reconcile();
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
                await ProcessRequests().ConfigureAwait(false);
                await Task.Delay(_services.RequestPoll, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        return 0;
    }

    /// <summary>Handles every waiting request in order.</summary>
    public async Task ProcessRequests()
    {
        while (_queue.Next() is { } request)
        {
            JsonObject reply;
            try
            {
                reply = await Handle(request).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _services.Log.Write($"request {request.Id} {request.Kind} failed: {e.GetType().Name}");
                reply = new JsonObject { ["error"] = e.GetType().Name };
            }
            _queue.Complete(request.Id, reply.ToJsonString());
        }
    }

    /// <summary>Completes when every launch the daemon started has ended.</summary>
    public Task WhenIdle() => Task.WhenAll(_launches.Values.Select(l => l.Ended.Task));

    private Task<JsonObject> Handle(Request request) => request.Kind switch
    {
        RequestKinds.Start => Launch(request.TaskId!, LaunchKind.First, now: false),
        RequestKinds.Resume => Launch(request.TaskId!, LaunchKind.Resume, ReadNow(request.ArgsJson)),
        _ => Task.FromResult(new JsonObject { ["error"] = "unknown request" }),
    };

    // The reply goes back once the process is running or the launch is refused; the run itself can last hours.
    private async Task<JsonObject> Launch(string taskId, LaunchKind kind, bool now)
    {
        // Registered before the launch starts, so a revoke always finds it. The stop source is never disposed,
        // because a revoke may still cancel it after the run has ended.
        var entry = new RunningLaunch(
            new CancellationTokenSource(), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_launches.TryAdd(taskId, entry))
        {
            return Refused([PreflightReasons.AdmissionState]);
        }
        var store = _services.OpenStore();
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = Coordinator(store);
        var launch = Task.Run(() => coordinator.Launch(taskId, kind, now, op => started.TrySetResult(op), entry.Stop.Token));
        _ = launch.ContinueWith(
            finished =>
            {
                if (finished.Exception is { } e)
                {
                    _services.Log.Write($"launch of task {taskId} failed: {e.InnerException?.GetType().Name}");
                }
                _launches.TryRemove(KeyValuePair.Create(taskId, entry));
                store.Dispose();
                entry.Ended.SetResult();
            },
            TaskScheduler.Default);

        if (await Task.WhenAny(launch, started.Task).ConfigureAwait(false) == started.Task)
        {
            return new JsonObject { ["launched"] = true, ["op"] = started.Task.Result };
        }
        var report = await launch.ConfigureAwait(false);
        return Refused(report.Preflight.Pass
            ? [_tasks.Get(taskId)?.AttentionReason ?? LaunchCoordinator.LaunchFailedReason]
            : report.Preflight.Reasons);
    }

    private static JsonObject Refused(IEnumerable<string> reasons) => new()
    {
        ["launched"] = false,
        ["reasons"] = new JsonArray(reasons.Select(r => (JsonNode?)r).ToArray()),
    };

    private LaunchCoordinator Coordinator(StateStore store)
    {
        var s = _services;
        var preflight = new PreflightRunner(store, s.Lister, s.Runner, new BillingProofs(s.Runner, s.Clock, store), s.Clock, s.Preflight);
        var scheduler = new ResetScheduler(new TaskRepository(store), s.Clock, s.Log);
        return new LaunchCoordinator(
            store, preflight, s.Host, scheduler, s.Notifier, s.Log, new LaunchPaths(s.AcExe, s.Paths.LaunchRoot), s.Preflight,
            s.RulesFor, s.LaunchDelay);
    }

    private static bool ReadNow(string argsJson) =>
        JsonNode.Parse(argsJson) is JsonObject args && args["now"] is JsonValue now && now.GetValueKind() == JsonValueKind.True;

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

    private sealed record RunningLaunch(CancellationTokenSource Stop, TaskCompletionSource Ended);
}

public static class RequestKinds
{
    public const string Start = "start";
    public const string Resume = "resume";
    public const string Revoke = "revoke";
}
