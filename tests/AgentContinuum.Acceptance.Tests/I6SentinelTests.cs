using System.Text;
using System.Text.Json.Nodes;
using AgentContinuum.Cli;

namespace AgentContinuum.Acceptance.Tests;

/// <summary>I6: sentinels in the prompt and the stream appear nowhere in the database, logs or notifications.</summary>
public sealed class I6SentinelTests : IDisposable
{
    private const string PromptSentinel = "SENTINEL-PROMPT-7f3a";
    private const string StreamSentinel = "SENTINEL-STREAM-7f3a";

    private readonly AcceptanceRoot _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void I6_SentinelsNowhere()
    {
        // A reset already past, so the run also produces an offer to capture.
        var scenario = AcceptanceRoot.LimitScenario(_root.Clock.UtcNow.AddHours(-1).ToUnixTimeSeconds());
        var events = scenario["events"]!.AsArray();
        events.Insert(0, new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = StreamSentinel }) },
        });
        events[^1]!["result"] = StreamSentinel;
        events[^1]!["sentinel_extra"] = StreamSentinel;
        events[^2]!["rate_limit_info"]!["sentinel_extra"] = StreamSentinel;
        var worktree = _root.Worktree(scenario, $"Fix the bug. {PromptSentinel}");
        var taskId = _root.AddTask(worktree);
        _root.StartDaemon();

        var reply = _root.Reply(_root.Submit(RequestKinds.Start, taskId), TimeSpan.FromSeconds(60))!;
        Assert.True(reply["launched"]!.GetValue<bool>(), reply.ToJsonString());
        AcceptanceRoot.Eventually(
            () => _root.Notifications().Any(line => line.StartsWith($"offer\t{taskId}\t", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(60),
            "the offer");

        // Read while the daemon still holds the database, so recent writes are still in the WAL.
        var files = Directory.EnumerateFiles(_root.StateFolder, "*", SearchOption.AllDirectories).Append(_root.NotificationsFile).ToList();
        Assert.Contains(files, f => f.EndsWith("ac.db-wal", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.EndsWith("ac.log", StringComparison.OrdinalIgnoreCase));
        foreach (var file in files)
        {
            var bytes = ReadShared(file);
            foreach (var sentinel in new[] { PromptSentinel, StreamSentinel })
            {
                Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(sentinel)), $"{sentinel} in {file}");
                Assert.False(Contains(bytes, Encoding.Unicode.GetBytes(sentinel)), $"{sentinel} as UTF-16 in {file}");
            }
        }
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
