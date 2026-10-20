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

    [Fact]
    public void RecordsArgsWithoutStdinContent()
    {
        Scenario("""{ "events": [], "exitCode": 0 }""");
        var env = FakeClaudeProcess.MinimalEnv();
        env["USERPROFILE"] = _dir.Path;
        const string prompt = "SENTINEL-stdin: fix the bug in ü.txt";

        FakeClaudeProcess.Run(_dir.Path, HeadlessArgs(), prompt, env);

        var recorded = File.ReadAllText(_dir.File("fake-claude-args.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(recorded);
        var root = doc.RootElement;
        Assert.Equal(HeadlessArgs(), root.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(
            env.Keys.Order(StringComparer.OrdinalIgnoreCase),
            root.GetProperty("envNames").EnumerateArray().Select(a => a.GetString()!).Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(prompt), root.GetProperty("stdinLength").GetInt32());
        Assert.DoesNotContain("SENTINEL-stdin", recorded);
    }

    private string HookSettings(string hookName, string sinkPath)
    {
        var command = $"\"{FakeClaudeProcess.ExePath}\" --fake-hook-sink \"{sinkPath}\"";
        var settings = new System.Text.Json.Nodes.JsonObject
        {
            ["hooks"] = new System.Text.Json.Nodes.JsonObject
            {
                [hookName] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["hooks"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = command,
                    }),
                }),
            },
        };
        var path = _dir.File("launch settings.json");
        File.WriteAllText(path, settings.ToJsonString());
        return path;
    }

    [Fact]
    public void RunsHookFromSettings()
    {
        Scenario("""
            {
              "events": [ { "type": "result", "subtype": "success", "is_error": true } ],
              "hook": { "name": "StopFailure", "payload": { "error": "rate_limit", "resets_at": 1800000000 } },
              "exitCode": 1
            }
            """);
        var sink = _dir.File("hook input.json");

        FakeClaudeProcess.Run(_dir.Path, [.. HeadlessArgs(), "--settings", HookSettings("StopFailure", sink)]);

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sink));
        var payload = doc.RootElement;
        Assert.Equal("StopFailure", payload.GetProperty("hook_event_name").GetString());
        Assert.Equal(Session, payload.GetProperty("session_id").GetString());
        Assert.Equal("rate_limit", payload.GetProperty("error").GetString());
        Assert.Equal(1800000000, payload.GetProperty("resets_at").GetInt64());
    }

    [Fact]
    public void HookNotInSettings_NotRun()
    {
        Scenario("""{ "events": [], "hook": { "name": "StopFailure", "payload": {} }, "exitCode": 1 }""");
        var sink = _dir.File("hook input.json");

        FakeClaudeProcess.Run(_dir.Path, [.. HeadlessArgs(), "--settings", HookSettings("Stop", sink)]);

        Assert.False(File.Exists(sink));
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
