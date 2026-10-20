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

    private const string Session = "6f9c1f3e-2b7a-4c55-9d0e-1a2b3c4d5e6f";

    private static string[] HeadlessArgs(string sessionFlag = "--session-id") =>
    [
        "-p", sessionFlag, Session, "--output-format", "stream-json", "--verbose",
        "--permission-mode", "acceptEdits", "--allowedTools", "Read,Edit",
    ];

    private void Scenario(string json) => File.WriteAllText(_dir.File(".fake-claude.json"), json);

    [Fact]
    public void EmitsScenarioEvents()
    {
        Scenario("""
            {
              "authSource": "none",
              "events": [
                { "type": "assistant", "message": { "content": [] } },
                { "type": "result", "subtype": "success", "is_error": false, "terminal_reason": "completed", "session_id": "other" }
              ],
              "exitCode": 3
            }
            """);

        var (exitCode, stdout) = FakeClaudeProcess.Run(_dir.Path, HeadlessArgs("--resume"), "prompt");

        Assert.Equal(3, exitCode);
        Assert.Equal(
            [
                $$"""{"type":"system","subtype":"init","session_id":"{{Session}}","apiKeySource":"none"}""",
                $$"""{"type":"assistant","message":{"content":[]},"session_id":"{{Session}}"}""",
                """{"type":"result","subtype":"success","is_error":false,"terminal_reason":"completed","session_id":"other"}""",
            ],
            stdout);
    }

    [Fact]
    public void NoAuthSource_NoInitEvent()
    {
        Scenario("""{ "events": [ { "type": "assistant" } ], "exitCode": 0 }""");

        var (_, stdout) = FakeClaudeProcess.Run(_dir.Path, HeadlessArgs());

        Assert.Equal([$$"""{"type":"assistant","session_id":"{{Session}}"}"""], stdout);
    }

    [Theory]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("--continue")]
    public void UnknownFlag_Rejected(string flag)
    {
        Scenario("""{ "events": [], "exitCode": 0 }""");

        var (exitCode, stdout) = FakeClaudeProcess.Run(_dir.Path, [.. HeadlessArgs(), flag]);

        Assert.Equal(64, exitCode);
        Assert.Empty(stdout);
    }
}
