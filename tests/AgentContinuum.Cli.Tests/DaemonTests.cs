using AgentContinuum.Cli.Commands;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Recovery;

namespace AgentContinuum.Cli.Tests;

public sealed class DaemonTests : IDisposable
{
    private readonly CliFixture _c = new();

    public void Dispose() => _c.Dispose();

    [Fact]
    public async Task SecondDaemon_Exits2_FirstKeepsLock()
    {
        var services = _c.Services();
        using var first = new Daemon(services);
        Assert.True(first.TryStart());

        var exit = await App.Run(["daemon"], services).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Daemon.AlreadyRunning, exit);
        Assert.Equal("daemon already running", _c.Output.Trim());
        using var third = new Daemon(services);
        Assert.False(third.TryStart());
        first.Dispose();
        using var afterFirst = new Daemon(services);
        Assert.True(afterFirst.TryStart());
    }

    [Fact]
    public async Task Run_OffersADueTaskOnceThroughTheNotifier()
    {
        _c.F.Task = _c.F.Task with
        {
            State = TaskState.WaitingForReset,
            ResetExpectedAtEpoch = _c.F.Clock.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
            AttemptPermittedAtEpoch = _c.F.Clock.UtcNow.AddMinutes(-3).ToUnixTimeSeconds(),
        };
        var services = _c.Services();
        using var daemon = new Daemon(services);
        using var stop = new CancellationTokenSource();

        var loop = Task.Run(() => daemon.Run(stop.Token));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        stop.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        var offer = Assert.Single(_c.Notifier.Offers);
        Assert.Equal((CliFixture.TaskId, "sample", false), (offer.TaskId, offer.Label, offer.ResetTimeUnknown));
        Assert.Equal(TaskState.ResumeOffered, _c.F.Tasks.Get(CliFixture.TaskId)!.State);
    }

    [Fact]
    public void Start_RecoversALaunchLeftLive()
    {
        var services = _c.Services();
        var opId = _c.F.Ledger.BeginIntent(CliFixture.TaskId);
        _c.F.Ledger.MarkSpawned(opId, 4321, 1, _c.F.Task.SessionId);

        using var daemon = new Daemon(services);
        Assert.True(daemon.TryStart());

        Assert.Equal(OpState.Interrupted, _c.F.Ledger.Get(opId)!.State);
        var task = _c.F.Tasks.Get(CliFixture.TaskId)!;
        Assert.Equal(TaskState.NeedsAttention, task.State);
        Assert.Equal(RecoveryService.InterruptedRunReason, task.AttentionReason);
        Assert.Empty(_c.Host.Started);
    }
}
