using System.Text;
using System.Text.Json;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Events;

public sealed class EventFilter
{
    private const string TypeField = "type";
    private const string SubtypeField = "subtype";
    private const string SessionIdField = "session_id";
    private const string IsErrorField = "is_error";

    private readonly HashSet<string> _rulePaths;
    private readonly HashSet<string> _keptPaths;
    private readonly string? _authSourcePath;

    // Child names per object path, so keys are compared as UTF-8 and never decoded unless kept.
    private readonly Dictionary<string, (string Name, byte[] Utf8)[]> _children;

    public EventFilter(ClaudeRules rules)
    {
        _authSourcePath = rules.P5?.Field;
        _rulePaths = new HashSet<string>(RulePaths(rules), StringComparer.Ordinal);
        _keptPaths = new HashSet<string>(_rulePaths, StringComparer.Ordinal) { TypeField, SubtypeField, SessionIdField, IsErrorField };
        if (_authSourcePath is not null)
        {
            _keptPaths.Add(_authSourcePath);
        }

        _children = _keptPaths
            .SelectMany(ParentChildPairs)
            .Distinct()
            .GroupBy(p => p.Parent, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => (p.Child, Encoding.UTF8.GetBytes(p.Child))).ToArray(),
                StringComparer.Ordinal);
    }

    public FilteredEvent? Accept(string line)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            var kept = new Dictionary<string, JsonScalar>(StringComparer.Ordinal);
            ReadObject(ref reader, "", kept);

            if (reader.Read() || !kept.TryGetValue(TypeField, out var type) || type.Kind != JsonTokenType.String)
            {
                return null;
            }

            return new FilteredEvent(
                Type: type.Text,
                Subtype: StringOrNull(kept, SubtypeField),
                SessionId: StringOrNull(kept, SessionIdField),
                IsError: kept.TryGetValue(IsErrorField, out var isError) && isError.Kind is JsonTokenType.True or JsonTokenType.False
                    ? isError.Kind == JsonTokenType.True
                    : null,
                AuthSource: _authSourcePath is null ? null : StringOrNull(kept, _authSourcePath),
                LimitFields: kept
                    .Where(k => _rulePaths.Contains(k.Key))
                    .ToDictionary(k => k.Key, k => k.Value.Literal, StringComparer.Ordinal));
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
                    if (_keptPaths.Contains(childPath))
                    {
                        kept[childPath] = JsonScalar.Read(ref reader);
                    }
                    break;
            }
        }
    }

    private static string? StringOrNull(Dictionary<string, JsonScalar> kept, string path) =>
        kept.TryGetValue(path, out var value) && value.Kind == JsonTokenType.String ? value.Text : null;

    private static IEnumerable<(string Parent, string Child)> ParentChildPairs(string path)
    {
        var parts = path.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            yield return (string.Join('.', parts[..i]), parts[i]);
        }
    }

    private static IEnumerable<string> RulePaths(ClaudeRules rules)
    {
        foreach (var requirement in rules.Result?.SuccessWhen ?? [])
        {
            yield return requirement.Path;
        }
        if (rules.Usage is { } usage)
        {
            yield return usage.ResetField;
            foreach (var field in usage.UtilizationFields)
            {
                yield return field;
            }
            yield return usage.Overage.StatusField;
            yield return usage.Overage.InUseField;
        }
    }

    private readonly record struct JsonScalar(JsonTokenType Kind, string Text)
    {
        public string Literal => Kind == JsonTokenType.String ? JsonSerializer.Serialize(Text) : Text;

        public static JsonScalar Read(ref Utf8JsonReader reader) => reader.TokenType switch
        {
            JsonTokenType.String => new(JsonTokenType.String, reader.GetString()!),
            JsonTokenType.True => new(JsonTokenType.True, "true"),
            JsonTokenType.False => new(JsonTokenType.False, "false"),
            JsonTokenType.Null => new(JsonTokenType.Null, "null"),
            _ => new(reader.TokenType, Encoding.UTF8.GetString(reader.ValueSpan)),
        };
    }
}
