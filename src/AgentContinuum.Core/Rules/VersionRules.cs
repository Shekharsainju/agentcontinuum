using System.Text.Json;

namespace AgentContinuum.Core.Rules;

public sealed class VersionRules
{
    private const string ResourceName = "AgentContinuum.Core.Rules.claude-code.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private VersionRules(IReadOnlyList<ClaudeRules> entries)
    {
        Entries = entries;
    }

    public IReadOnlyList<ClaudeRules> Entries { get; }

    public static VersionRules Load(string json) =>
        new(JsonSerializer.Deserialize<ClaudeRules[]>(json, Options) ?? throw new FormatException("Version rules are empty."));

    public static VersionRules LoadEmbedded()
    {
        using var stream = typeof(VersionRules).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}.");
        using var reader = new StreamReader(stream);
        return Load(reader.ReadToEnd());
    }
}
