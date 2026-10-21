using AgentContinuum.Cli.Commands;

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
}
