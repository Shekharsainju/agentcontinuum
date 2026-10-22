using System.Text.Json.Nodes;

if (args is ["--version"])
{
    Console.WriteLine("2.1.304 (Claude Code)");
    return 0;
}

// The override lives in the profile the controller scans, so a test can fail P4 alone.
if (args is ["auth", "status"])
{
    var overridePath = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", ".fake-claude-auth.json");
    Console.WriteLine(File.Exists(overridePath)
        ? File.ReadAllText(overridePath).Trim()
        : """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","subscriptionType":"max"}""");
    return 0;
}

if (args is ["--fake-child", var childSeconds])
{
    Thread.Sleep(TimeSpan.FromSeconds(int.Parse(childSeconds)));
    return 0;
}

if (args is ["--fake-hook-sink", var sinkPath])
{
    using var sink = File.Create(sinkPath);
    Console.OpenStandardInput().CopyTo(sink);
    return 0;
}

if (!Options.TryParse(args, out var options))
{
    Console.Error.WriteLine("fake-claude: unsupported arguments");
    return 64;
}

var sessionId = options.SessionId ?? options.Resume ?? "";
// Written first, so a run killed at any later point is still counted as a start.
File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "fake-claude-starts.log"), sessionId + "\n");

var scenarioPath = Path.Combine(Environment.CurrentDirectory, ".fake-claude.json");
if (!File.Exists(scenarioPath))
{
    Console.Error.WriteLine("fake-claude: no .fake-claude.json in the working directory");
    return 70;
}
var scenario = JsonNode.Parse(File.ReadAllText(scenarioPath))!.AsObject();

using (var stdin = new MemoryStream())
{
    Console.OpenStandardInput().CopyTo(stdin);
    var record = new JsonObject
    {
        ["args"] = new JsonArray(args.Select(a => (JsonNode?)a).ToArray()),
        ["envNames"] = new JsonArray(Environment.GetEnvironmentVariables().Keys.Cast<string>().Order().Select(n => (JsonNode?)n).ToArray()),
        ["stdinLength"] = stdin.Length,
    };
    File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "fake-claude-args.json"), record.ToJsonString());
}

var lingerSeconds = scenario["lingerSeconds"]?.GetValue<int>() ?? 0;
SpawnChildren(scenario["spawnChildren"]?.GetValue<int>() ?? 0, lingerSeconds);

if (scenario["authSource"]?.GetValue<string>() is { } authSource)
{
    Emit(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = sessionId, ["apiKeySource"] = authSource });
}
foreach (var node in scenario["events"]?.AsArray() ?? [])
{
    var e = node!.DeepClone().AsObject();
    if (!e.ContainsKey("session_id"))
    {
        e["session_id"] = sessionId;
    }
    Emit(e);
}

if (scenario["hook"] is JsonObject hook && options.SettingsPath is { } settingsPath)
{
    RunHook(hook, settingsPath, sessionId);
}

Thread.Sleep(TimeSpan.FromSeconds(lingerSeconds));

return scenario["exitCode"]?.GetValue<int>() ?? 0;

// Children live as long as the parent lingers so tests of job teardown have something to kill.
static void SpawnChildren(int count, int seconds)
{
    if (count <= 0)
    {
        return;
    }
    var pids = new JsonArray();
    for (var i = 0; i < count; i++)
    {
        var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("--fake-child");
        info.ArgumentList.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var child = System.Diagnostics.Process.Start(info)!;
        pids.Add(child.Id);
    }
    File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "fake-claude-children.json"), pids.ToJsonString());
}

static void RunHook(JsonObject hook, string settingsPath, string sessionId)
{
    var name = hook["name"]!.GetValue<string>();
    var payload = hook["payload"]?.DeepClone().AsObject() ?? [];
    payload["hook_event_name"] = name;
    if (!payload.ContainsKey("session_id"))
    {
        payload["session_id"] = sessionId;
    }

    var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!;
    var commands = settings["hooks"]?[name]?.AsArray()
        .SelectMany(matcher => matcher!["hooks"]!.AsArray())
        .Select(h => h!["command"]!.GetValue<string>()) ?? [];
    foreach (var command in commands)
    {
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/d /s /c \"{command}\"",
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        process.StandardInput.Write(payload.ToJsonString());
        process.StandardInput.Close();
        process.WaitForExit();
    }
}

static void Emit(JsonObject e)
{
    Console.Out.Write(e.ToJsonString() + "\n");
    Console.Out.Flush();
}

internal sealed record Options(string? SessionId, string? Resume, string? SettingsPath)
{
    private static readonly string[] ValueFlags =
        ["--session-id", "--resume", "--output-format", "--permission-mode", "--settings"];

    private const string ListFlag = "--allowedTools";

    private static readonly string[] SwitchFlags = ["-p", "--verbose"];

    // Only the flags the controller is allowed to pass are accepted, so a test catches any other flag.
    public static bool TryParse(string[] args, out Options options)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (SwitchFlags.Contains(args[i]))
            {
                continue;
            }
            // The rules follow as separate arguments, up to the next flag.
            if (args[i] == ListFlag)
            {
                var first = i + 1;
                while (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    i++;
                }
                if (i < first)
                {
                    options = new Options(null, null, null);
                    return false;
                }
                continue;
            }
            if (!ValueFlags.Contains(args[i]) || i + 1 >= args.Length)
            {
                options = new Options(null, null, null);
                return false;
            }
            values[args[i]] = args[++i];
        }
        options = new Options(values.GetValueOrDefault("--session-id"), values.GetValueOrDefault("--resume"), values.GetValueOrDefault("--settings"));
        return true;
    }
}
