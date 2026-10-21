using AgentContinuum.Cli.Commands;
using AgentContinuum.Core.Tests;
using AgentContinuum.Core.Tests.Preflight;

namespace AgentContinuum.Cli.Tests;

public sealed class LocalCommandTests : IDisposable
{
    private const string TaskId = CliFixture.TaskId;

    private readonly CliFixture _c = new();

    public void Dispose() => _c.Dispose();

    [Fact]
    public async Task PinCli_RepinsToTheCurrentBinary()
    {
        _c.F.Task = _c.F.Task with { CliSha256 = new string('0', 64), CliVersion = "2.1.300" };
        var services = _c.Services();

        var exit = await App.Run(["pin-cli", TaskId], services);

        Assert.Equal(0, exit);
        Assert.Equal("pinned 2.1.304", _c.Output.Trim());
        var task = _c.F.Tasks.Get(TaskId)!;
        Assert.Equal((PreflightFixture.Sha256(FakeClaudeProcess.ExePath), "2.1.304"), (task.CliSha256, task.CliVersion));
    }

    [Fact]
    public async Task AcceptWorkspace_TakesANewBaseline()
    {
        _c.F.Task = _c.F.Task with { BaselineFingerprint = new string('0', 64) };
        var services = _c.Services();
        _c.F.Repo.Write("edited-by-you.txt", "a change made by hand");

        var exit = await App.Run(["accept-workspace", TaskId], services);

        Assert.Equal(0, exit);
        Assert.Equal("baseline taken", _c.Output.Trim());
        Assert.Equal(
            Core.Workspace.WorkspaceFingerprint.Compute(_c.F.Repo.Path, GitRepo.GitExe).Hash,
            _c.F.Tasks.Get(TaskId)!.BaselineFingerprint);
    }

    [Fact]
    public async Task AcceptWorkspace_RefusedWhileALaunchIsLive()
    {
        var services = _c.Services();
        _c.F.Ledger.BeginIntent(TaskId);

        var exit = await App.Run(["accept-workspace", TaskId], services);

        Assert.Equal(1, exit);
        Assert.Equal("error: task has a live launch", _c.Output.Trim());
        Assert.Null(_c.F.Tasks.Get(TaskId)!.BaselineFingerprint);
    }

    [Fact]
    public async Task AttestOverageOff_RecordsNowValidFor30Days()
    {
        var services = _c.Services();
        _c.F.Clock.Advance(TimeSpan.FromDays(3));

        var exit = await App.Run(["attest-overage-off"], services);

        Assert.Equal(0, exit);
        Assert.Equal("overage off attested, valid until 2026-11-22 12:00 UTC", _c.Output.Trim());
        Assert.Equal(_c.F.Clock.UtcNow, new Core.State.Attestations(_c.F.Store).Latest(Core.State.Attestations.OverageOff));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_ReportsPreflightAndChangesNothing(bool fails)
    {
        if (fails)
        {
            _c.F.Lister.Throw = new Core.Platform.ProcessListException("enumeration failed");
        }
        var services = _c.Services();

        var exit = await App.Run(["preview", TaskId], services);

        Assert.Equal(fails ? 1 : 0, exit);
        Assert.Equal(fails ? "pre-flight failed: PROCESS_UNKNOWN" : "pre-flight passed", _c.Output.Trim());
        Assert.Empty(_c.Host.Started);
        Assert.Equal(_c.F.Task, _c.F.Tasks.Get(TaskId)! with { AllowedTools = _c.F.Task.AllowedTools });
        Assert.Empty(_c.Notifier.Attentions);
    }

    private static MemoryStream HookPayload(string session) => new(System.Text.Encoding.UTF8.GetBytes(
        $$"""{"hook_event_name":"StopFailure","session_id":"{{session}}","error_type":"rate_limit","message":"SENTINEL-hook"}"""));

    [Fact]
    public async Task HookStopFailure_RecordsAllowlistedFieldsSilently()
    {
        var services = _c.Services();
        var opId = _c.F.Ledger.BeginIntent(TaskId);

        var exit = await App.Run(["hook", "stop-failure", "--op", opId], services with { In = HookPayload(_c.Session) });

        Assert.Equal(0, exit);
        Assert.Equal("", _c.Output);
        var hook = Assert.Single(Core.Events.HookRecorder.ForOp(_c.F.Store, opId));
        Assert.Equal(("StopFailure", _c.Session), (hook.HookName, hook.SessionId));
        using var cmd = _c.F.Store.Connection.CreateCommand();
        cmd.CommandText = "SELECT fields_json FROM events";
        Assert.DoesNotContain("SENTINEL", (string)cmd.ExecuteScalar()!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HookStopFailure_NoLiveOp_RecordsNothing(bool finished)
    {
        var services = _c.Services();
        var opId = "0123456789abcdef0123456789abcdef";
        if (finished)
        {
            opId = _c.F.Ledger.BeginIntent(TaskId);
            _c.F.Ledger.Abandon(opId);
        }

        var exit = await App.Run(["hook", "stop-failure", "--op", opId], services with { In = HookPayload(_c.Session) });

        Assert.Equal(0, exit);
        Assert.Equal("", _c.Output);
        Assert.Empty(Core.Events.HookRecorder.ForOp(_c.F.Store, opId));
    }

    [Fact]
    public async Task PinCli_RefusesANonExe()
    {
        var services = _c.Services();
        var shim = _c.F.Outside("claude.cmd");
        File.WriteAllText(shim, "@echo off");

        var exit = await App.Run(["pin-cli", TaskId, "--cli", shim], services);

        Assert.Equal(1, exit);
        Assert.Equal("error: cli must be an .exe file", _c.Output.Trim());
        Assert.Equal(_c.F.Task.CliSha256, _c.F.Tasks.Get(TaskId)!.CliSha256);
    }
}
