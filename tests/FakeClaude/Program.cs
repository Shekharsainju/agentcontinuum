using System.Text.Json.Nodes;

if (args is ["--version"])
{
    Console.WriteLine("2.1.304 (Claude Code)");
    return 0;
}

if (!Options.TryParse(args, out var options))
{
    Console.Error.WriteLine("fake-claude: unsupported arguments");
    return 64;
}

var scenarioPath = Path.Combine(Environment.CurrentDirectory, ".fake-claude.json");
if (!File.Exists(scenarioPath))
{
    Console.Error.WriteLine("fake-claude: no .fake-claude.json in the working directory");
    return 70;
}
var scenario = JsonNode.Parse(File.ReadAllText(scenarioPath))!.AsObject();
var sessionId = options.SessionId ?? options.Resume ?? "";

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

return scenario["exitCode"]?.GetValue<int>() ?? 0;

static void Emit(JsonObject e)
{
    Console.Out.Write(e.ToJsonString() + "\n");
    Console.Out.Flush();
}

internal sealed record Options(string? SessionId, string? Resume, string? SettingsPath)
{
    private static readonly string[] ValueFlags =
        ["--session-id", "--resume", "--output-format", "--permission-mode", "--allowedTools", "--settings"];

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
