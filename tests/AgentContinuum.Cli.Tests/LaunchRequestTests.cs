using System.Text.Json.Nodes;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Preflight;
using AgentContinuum.Core.State;

namespace AgentContinuum.Cli.Tests;

public sealed class LaunchRequestTests : IDisposable
{
    private const string TaskId = CliFixture.TaskId;

    private readonly CliFixture _c = new();

    public void Dispose() => _c.Dispose();

    private async Task<JsonObject> Request(Daemon daemon, string kind, string args = "{}")
    {
        var queue = new RequestQueue(_c.F.Store, _c.F.Clock);
        var id = queue.Submit(kind, TaskId, args);
        await daemon.ProcessRequests().WaitAsync(TimeSpan.FromSeconds(10));
        return JsonNode.Parse(queue.WaitFor(id, TimeSpan.Zero)!)!.AsObject();
    }

    private Daemon Started()
    {
        var daemon = new Daemon(_c.Services());
        Assert.True(daemon.TryStart());
        return daemon;
    }

    [Fact]
    public async Task StaleRequest_ExpiredOnDaemonStart_NeverLaunches()
    {
        _c.Services();
        var queue = new RequestQueue(_c.F.Store, _c.F.Clock);
        var stale = queue.Submit(RequestKinds.Start, TaskId, "{}");
        _c.F.Clock.Advance(TimeSpan.FromSeconds(61));
        var fresh = queue.Submit(RequestKinds.Resume, "other", "{}");

        using var daemon = Started();
        await daemon.ProcessRequests();

        Assert.Empty(_c.Host.Started);
        Assert.Null(queue.WaitFor(stale, TimeSpan.Zero));
        Assert.NotNull(queue.WaitFor(fresh, TimeSpan.Zero));
        Assert.Equal(TaskState.Queued, _c.F.Tasks.Get(TaskId)!.State);
    }

    [Fact]
    public async Task StartRequest_RepliesWithOpThenRunsToTheEnd()
    {
        _c.Host.Run.Lines.AddRange([_c.InitLine(), _c.SuccessLine()]);
        using var daemon = Started();

        var reply = await Request(daemon, "start");
        await daemon.WhenIdle().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(reply["launched"]!.GetValue<bool>());
        var op = _c.F.Ledger.Get(reply["op"]!.GetValue<string>())!;
        Assert.Equal(OpState.Classified, op.State);
        Assert.Contains("--session-id", Assert.Single(_c.Host.Started).Args);
        Assert.Equal(TaskState.Completed, _c.F.Tasks.Get(TaskId)!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeRequest_NowWaivesEligibility(bool now)
    {
        _c.F.Task = _c.F.Task with
        {
            State = TaskState.ResumeOffered,
            AttemptPermittedAtEpoch = _c.F.Clock.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            BaselineFingerprint = Core.Workspace.WorkspaceFingerprint.Compute(_c.F.Repo.Path, Core.Tests.GitRepo.GitExe).Hash,
        };
        _c.Host.Run.Lines.AddRange([_c.InitLine(), _c.SuccessLine()]);
        using var daemon = Started();

        var reply = await Request(daemon, "resume", now ? """{"now":true}""" : "{}");
        await daemon.WhenIdle().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(now, reply["launched"]!.GetValue<bool>());
        if (now)
        {
            Assert.Contains("--resume", Assert.Single(_c.Host.Started).Args);
        }
        else
        {
            Assert.Equal([PreflightReasons.NotYetEligible], reply["reasons"]!.AsArray().Select(r => r!.GetValue<string>()));
            Assert.Empty(_c.Host.Started);
        }
    }
}
