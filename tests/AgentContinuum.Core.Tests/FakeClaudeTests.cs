namespace AgentContinuum.Core.Tests;

public sealed class FakeClaudeTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Version_Prints2_1_304()
    {
        var (exitCode, stdout) = FakeClaudeProcess.Run(_dir.Path, ["--version"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["2.1.304 (Claude Code)"], stdout);
    }
}
