using System.Text;
using System.Text.Json;

namespace AgentContinuum.Core.Events;

internal sealed class JsonAllowlist
{
    private readonly HashSet<string> _paths;
    private readonly int _maxValueChars;

    // Child names per object path, so keys are compared as UTF-8 and never decoded unless kept.
    private readonly Dictionary<string, (string Name, byte[] Utf8)[]> _children;

    public JsonAllowlist(IEnumerable<string> paths, int maxValueChars)
    {
        _paths = new HashSet<string>(paths, StringComparer.Ordinal);
        _maxValueChars = maxValueChars;
        _children = _paths
            .SelectMany(ParentChildPairs)
            .Distinct()
            .GroupBy(p => p.Parent, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => (p.Child, Encoding.UTF8.GetBytes(p.Child))).ToArray(),
                StringComparer.Ordinal);
    }

    /// <summary>Scalar values at the allowlisted dotted paths, or null unless the input is exactly one JSON object.</summary>
    public Dictionary<string, JsonScalar>? Read(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            var kept = new Dictionary<string, JsonScalar>(StringComparer.Ordinal);
            ReadObject(ref reader, "", kept);
            return reader.Read() ? null : kept;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void ReadObject(ref Utf8JsonReader reader, string path, Dictionary<string, JsonScalar> kept)
    {
        _children.TryGetValue(path, out var children);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string? name = null;
            foreach (var child in children ?? [])
            {
                if (reader.ValueTextEquals(child.Utf8))
                {
                    name = child.Name;
                    break;
                }
            }

            reader.Read();
            if (name is null)
            {
                reader.Skip();
                continue;
            }

            var childPath = path.Length == 0 ? name : $"{path}.{name}";
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject when _children.ContainsKey(childPath):
                    ReadObject(ref reader, childPath, kept);
                    break;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    reader.Skip();
                    break;
                default:
                    if (_paths.Contains(childPath))
                    {
                        kept[childPath] = JsonScalar.Read(ref reader, _maxValueChars);
                    }
                    break;
            }
        }
    }

    private static IEnumerable<(string Parent, string Child)> ParentChildPairs(string path)
    {
        var parts = path.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            yield return (string.Join('.', parts[..i]), parts[i]);
        }
    }
}

internal readonly record struct JsonScalar(JsonTokenType Kind, string Text)
{
    public string Literal => Kind == JsonTokenType.String ? JsonSerializer.Serialize(Text) : Text;

    public static JsonScalar Read(ref Utf8JsonReader reader, int maxChars) => reader.TokenType switch
    {
        JsonTokenType.String => new(JsonTokenType.String, Cap(reader.GetString()!, maxChars)),
        JsonTokenType.True => new(JsonTokenType.True, "true"),
        JsonTokenType.False => new(JsonTokenType.False, "false"),
        JsonTokenType.Null => new(JsonTokenType.Null, "null"),
        _ => new(reader.TokenType, Cap(Encoding.UTF8.GetString(reader.ValueSpan), maxChars)),
    };

    private static string Cap(string value, int maxChars) => value.Length > maxChars ? value[..maxChars] : value;
}
