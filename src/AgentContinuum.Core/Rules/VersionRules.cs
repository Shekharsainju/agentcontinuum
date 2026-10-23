using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentContinuum.Core.Rules;

public sealed partial class VersionRules
{
    private const string ResourceName = "AgentContinuum.Core.Rules.claude-code.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<VersionRules> Embedded = new(LoadEmbedded);

    private readonly (ClaudeRules Rules, Version Lower, Version Upper)[] _ranges;

    private VersionRules(IReadOnlyList<ClaudeRules> entries)
    {
        Entries = entries;
        _ranges = entries.Select(e =>
        {
            var (lower, upper) = ParseRange(e.Versions);
            return (e, lower, upper);
        }).ToArray();
    }

    public IReadOnlyList<ClaudeRules> Entries { get; }

    public static ClaudeRules? For(string cliVersion) => Embedded.Value.Match(cliVersion);

    public static VersionRules Load(string json) =>
        new(JsonSerializer.Deserialize<ClaudeRules[]>(json, Options) ?? throw new FormatException("Version rules are empty."));

    public static VersionRules LoadEmbedded()
    {
        using var stream = typeof(VersionRules).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}.");
        using var reader = new StreamReader(stream);
        return Load(reader.ReadToEnd());
    }

    public ClaudeRules? Match(string cliVersion)
    {
        if (!ExactVersion().IsMatch(cliVersion))
        {
            return null;
        }
        var version = Version.Parse(cliVersion);
        return _ranges.FirstOrDefault(r => version >= r.Lower && version < r.Upper).Rules;
    }

    private static (Version Lower, Version Upper) ParseRange(string? versions)
    {
        var match = Range().Match(versions ?? "");
        if (!match.Success)
        {
            throw new FormatException($"Unsupported version range '{versions}'. Only '>=a.b.c <x.y.z' is supported.");
        }
        var lower = Version.Parse(match.Groups[1].Value);
        var upper = Version.Parse(match.Groups[2].Value);
        if (lower >= upper)
        {
            throw new FormatException($"Empty version range '{versions}'.");
        }
        return (lower, upper);
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex ExactVersion();

    [GeneratedRegex(@"^>=(\d+\.\d+\.\d+) <(\d+\.\d+\.\d+)$")]
    private static partial Regex Range();
}
