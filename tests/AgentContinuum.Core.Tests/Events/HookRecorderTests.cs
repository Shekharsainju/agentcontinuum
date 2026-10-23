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

    private static string PayloadOfBytes(int totalBytes)
    {
        var head = $$"""{"hook_event_name":"StopFailure","session_id":"{{SessionId}}","pad":" """;
        const string tail = "\"}";
        var payload = head + new string('x', totalBytes - Encoding.UTF8.GetByteCount(head) - tail.Length) + tail;
        Assert.Equal(totalBytes, Encoding.UTF8.GetByteCount(payload));
        return payload;
    }

    private string[] EventKinds()
    {
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = "SELECT kind || ':' || fields_json FROM events ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }
        return rows.ToArray();
    }

    [Fact]
    public void HookRecorder_OversizedInput_RecordsHookErrorOnly()
    {
        HookRecorder.Record(_store, "op1", Input(PayloadOfBytes(64 * 1024)), Rules);
        HookRecorder.Record(_store, "op2", Input(PayloadOfBytes(64 * 1024 + 1)), Rules);

        Assert.Equal("StopFailure", Assert.Single(HookRecorder.ForOp(_store, "op1")).HookName);
        var error = Assert.Single(HookRecorder.ForOp(_store, "op2"));
        Assert.Equal((HookRecorder.HookErrorKind, (string?)null), (error.HookName, error.SessionId));
        Assert.Empty(error.Fields);
        Assert.Equal("hook_error:{}", EventKinds()[1]);
    }

    private sealed class FailingStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("pipe broken");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[\"StopFailure\"]")]
    [InlineData("{\"hook_event_name\":\"StopFailure\"} trailing")]
    public void Record_UnparseableInput_RecordsHookError(string input)
    {
        HookRecorder.Record(_store, "op1", Input(input), Rules);

        Assert.Equal(HookRecorder.HookErrorKind, Assert.Single(HookRecorder.ForOp(_store, "op1")).HookName);
    }

    [Fact]
    public void Record_StreamThrows_RecordsHookErrorAndDoesNotThrow()
    {
        HookRecorder.Record(_store, "op1", new FailingStream(), Rules);

        Assert.Equal(HookRecorder.HookErrorKind, Assert.Single(HookRecorder.ForOp(_store, "op1")).HookName);
    }

    [Fact]
    public void Record_StoreUnusable_DoesNotThrow()
    {
        _store.Dispose();

        var error = Record.Exception(() => HookRecorder.Record(_store, "op1", Input(Payload), Rules));

        Assert.Null(error);
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
