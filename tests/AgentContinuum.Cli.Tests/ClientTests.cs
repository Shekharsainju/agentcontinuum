using AgentContinuum.Cli.Commands;
using AgentContinuum.Core.Model;

namespace AgentContinuum.Cli.Tests;

public sealed class ClientTests : IDisposable
{
    private const string TaskId = CliFixture.TaskId;

    private readonly CliFixture _c = new();

    public void Dispose() => _c.Dispose();

    private async Task<T> WithDaemon<T>(Func<AppServices, Task<T>> client)
    {
        var services = _c.Services();
        using var daemon = new Daemon(services);
        using var stop = new CancellationTokenSource();
        var loop = Task.Run(() => daemon.Run(stop.Token));
        try
        {
            return await client(services).WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            stop.Cancel();
            await loop;
            await daemon.WhenIdle().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Submit_NoDaemon_TimesOutAndDeletesRequest()
    {
        var services = _c.Services();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var exit = await App.Run(["resume", TaskId], services);

        Assert.Equal(DaemonClient.NotRunning, exit);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal("daemon not running", _c.Output.Trim());
        using var cmd = _c.F.Store.Connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM requests";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [Fact]
    public async Task TaskStart_PrintsLaunchedOp()
    {
        _c.Host.Run.Lines.AddRange([_c.InitLine(), _c.SuccessLine()]);

        var exit = await WithDaemon(services => App.Run(["task", "start", TaskId], services));

        Assert.Equal(0, exit);
        Assert.Contains(_c.Output.Split(Environment.NewLine), line => System.Text.RegularExpressions.Regex.IsMatch(line, "^launched op [0-9a-f]{32}$"));
        Assert.Single(_c.Host.Started);
    }

    [Fact]
    public async Task Resume_Refused_PrintsReasonsExits1()
    {
        _c.F.Task = _c.F.Task with { State = TaskState.ResumeOffered };
        _c.F.Lister.Throw = new Core.Platform.ProcessListException("enumeration failed");

        var exit = await WithDaemon(services => App.Run(["resume", TaskId, "--now"], services));

        Assert.Equal(1, exit);
        Assert.Contains("refused: PROCESS_UNKNOWN", _c.Output.Split(Environment.NewLine));
        Assert.Empty(_c.Host.Started);
    }
}
