using AgentContinuum.Cli.Commands;
using AgentContinuum.Core.Tests;
using AgentContinuum.Core.Tests.Preflight;

namespace AgentContinuum.Cli.Tests;

public sealed class CliPinTests : IDisposable
{
    private readonly CliFixture _c = new();

    public void Dispose() => _c.Dispose();

    [Fact]
    public void Read_ExeGivesHashAndVersion()
    {
        var (pin, error) = CliPin.Read(_c.Services(), FakeClaudeProcess.ExePath);

        Assert.Null(error);
        Assert.Equal(new Pin(FakeClaudeProcess.ExePath, PreflightFixture.Sha256(FakeClaudeProcess.ExePath), "2.1.304"), pin);
    }

    [Theory]
    [InlineData("claude.cmd", "cli must be an .exe file")]
    [InlineData("missing.exe", "cli not found")]
    public void Read_RefusesAnythingButAnExistingExe(string name, string expected)
    {
        var path = _c.F.Outside(name);
        if (name.EndsWith(".cmd", StringComparison.Ordinal))
        {
            File.WriteAllText(path, "@echo off");
        }

        Assert.Equal((null, expected), CliPin.Read(_c.Services(), path));
    }

    [Fact]
    public void Read_VersionFails_Refused()
    {
        _c.F.Runner.Version = new Core.Platform.CommandResult(1, "", false);

        Assert.Equal((null, "cli version unreadable"), CliPin.Read(_c.Services(), FakeClaudeProcess.ExePath));
    }
}
