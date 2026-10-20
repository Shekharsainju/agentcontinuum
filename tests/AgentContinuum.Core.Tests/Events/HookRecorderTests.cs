using System.Text;
using AgentContinuum.Core.Events;
using AgentContinuum.Core.Rules;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Tests.Events;

public sealed class HookRecorderTests : IDisposable
{
    private const string Sentinel = "SENTINEL-h00k";
    private const string SessionId = "9704e6d4-d947-4c1a-8224-b751c4def13f";

    private static readonly ClaudeRules Rules = VersionRules.LoadEmbedded().Entries[0] with
    {
        Limit = new LimitSignalRule("StopFailure", "error", ["rate_limit"], "resets_at", JsonLiteral.UnixSeconds),
    };

    private readonly TempDirectory _dir = new();
    private readonly StateStore _store;

    public HookRecorderTests()
    {
        _store = StateStore.Open(_dir.File("ac.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private static MemoryStream Input(string text) => new(Encoding.UTF8.GetBytes(text));

    private const string Payload = $$$"""
        {"hook_event_name":"StopFailure","session_id":"{{{SessionId}}}","transcript_path":"C:\\Users\\me\\.claude\\{{{Sentinel}}}.jsonl","cwd":"C:\\work","error":"rate_limit","resets_at":1800000000,"error_details":"{{{Sentinel}}} in a sentence","nested":{"error":"{{{Sentinel}}}"},"messages":["{{{Sentinel}}}"]}
        """;

    [Fact]
    public void Record_KeepsAllowlistedFields()
    {
        HookRecorder.Record(_store, "op1", Input(Payload), Rules);

        var hook = Assert.Single(HookRecorder.ForOp(_store, "op1"));
        Assert.Equal("StopFailure", hook.HookName);
        Assert.Equal(SessionId, hook.SessionId);
        Assert.Equal(
            new Dictionary<string, string> { ["error"] = "\"rate_limit\"", ["resets_at"] = "1800000000" },
            hook.Fields);
        Assert.Empty(HookRecorder.ForOp(_store, "op2"));
    }

    [Fact]
    public void HookRecorder_DropsSentinelPayload()
    {
        HookRecorder.Record(_store, "op1", Input(Payload), Rules);
        HookRecorder.Record(_store, "op1", Input(Payload), null);
        Assert.Equal(2, HookRecorder.ForOp(_store, "op1").Count);
        _store.Dispose();

        foreach (var file in Directory.GetFiles(_dir.Path, "ac.db*"))
        {
            Assert.DoesNotContain(Sentinel, Encoding.UTF8.GetString(File.ReadAllBytes(file)));
        }
    }
}
