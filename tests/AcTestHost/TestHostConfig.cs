using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentContinuum.AcTestHost;

public enum CrashPoint
{
    None,
    BeforeIntent,
    BeforeSpawn,
    AfterSpawn,
    WhileRunning,
    AfterExit,
}

/// <summary>Read from ac-test.json; every path must be absolute.</summary>
public sealed record TestHostConfig(
    string LocalAppData,
    string UserProfile,
    string[]? ManagedSettingsPaths = null,
    string? RulesFile = null,
    string? ClockOffsetFile = null,
    CrashPoint CrashPoint = CrashPoint.None,
    string? NotificationsFile = null,
    bool RealToasts = false,
    string? LockName = null,
    double TickIntervalSeconds = 30)
{
    public const string FileName = "ac-test.json";

    public const int NoConfigExitCode = 78;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    /// <summary>The config under %LOCALAPPDATA%\AgentContinuum, else beside the exe; null with a reason otherwise.</summary>
    public static TestHostConfig? Find(out string problem)
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(localAppData) && Path.IsPathFullyQualified(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "AgentContinuum", FileName));
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, FileName));
        if (candidates.FirstOrDefault(File.Exists) is not { } path)
        {
            problem = $"no {FileName}";
            return null;
        }

        TestHostConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<TestHostConfig>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            config = null;
        }
        problem = config is null ? $"{path} is not a valid config" : Problem(config) ?? "";
        return problem.Length == 0 ? config : null;
    }

    private static string? Problem(TestHostConfig c)
    {
        string?[] paths = [c.LocalAppData, c.UserProfile, c.RulesFile, c.ClockOffsetFile, c.NotificationsFile, .. c.ManagedSettingsPaths ?? []];
        if (c.LocalAppData is null || c.UserProfile is null || paths.Any(p => p is not null && !Path.IsPathFullyQualified(p)))
        {
            return "localAppData and userProfile are required and every path must be absolute";
        }
        if (SameFolder(c.LocalAppData, Environment.SpecialFolder.LocalApplicationData)
            || SameFolder(c.UserProfile, Environment.SpecialFolder.UserProfile))
        {
            return "the config names your own state or profile folder";
        }
        return null;
    }

    // The shell folder, not the variable: the variable is what a test changes.
    private static bool SameFolder(string path, Environment.SpecialFolder folder) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
        Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(folder)),
        StringComparison.OrdinalIgnoreCase);
}
