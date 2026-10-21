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
