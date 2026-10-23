using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Tests;

namespace AgentContinuum.Windows.Tests;

/// <summary>A working directory holding a FakeClaude scenario, and launch specs that run FakeClaude in it.</summary>
public sealed class FakeClaudeLaunch : IDisposable
{
    private readonly TempDirectory _dir = new();

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "claude.exe");

    public string WorkingDir => _dir.Path;

    public Guid SessionId { get; } = Guid.NewGuid();

    public void Scenario(string json) => File.WriteAllText(_dir.File(".fake-claude.json"), json);

    public static Dictionary<string, string> MinimalEnv() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!,
        ["PATH"] = Environment.GetEnvironmentVariable("SystemRoot") + @"\System32",
    };

    public LaunchSpec Spec(string[]? args = null, IReadOnlyDictionary<string, string>? env = null, string stdin = "") => new(
        ExePath,
        args ?? ["-p", "--session-id", SessionId.ToString("D")],
        WorkingDir,
        env ?? MinimalEnv(),
        new MemoryStream(new UTF8Encoding(false).GetBytes(stdin)));

    /// <summary>What FakeClaude recorded about its own start: args, environment names and stdin length.</summary>
    public JsonObject Recorded() => JsonNode.Parse(File.ReadAllText(_dir.File("fake-claude-args.json")))!.AsObject();

    public int[] ChildPids() =>
        JsonNode.Parse(File.ReadAllText(_dir.File("fake-claude-children.json")))!.AsArray().Select(n => n!.GetValue<int>()).ToArray();

    public static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(50);
        }
        return condition();
    }

    public void Dispose() => _dir.Dispose();
}
