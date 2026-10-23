using System.Globalization;
using System.Text.Json;

namespace AgentContinuum.Core.Events;

/// <summary>Reads the JSON literals kept by the event filter and the hook recorder.</summary>
public static class JsonLiteral
{
    public const string UnixSeconds = "unix_seconds";
    public const string Iso8601 = "iso8601";

    public static bool Matches(string? literal, JsonElement expected)
    {
        if (Parse(literal) is not { } actual || actual.ValueKind != expected.ValueKind)
        {
            return false;
        }
        return actual.ValueKind switch
        {
            JsonValueKind.String => actual.GetString() == expected.GetString(),
            JsonValueKind.Number => actual.TryGetDecimal(out var a) && expected.TryGetDecimal(out var e) && a == e,
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false,
        };
    }

    public static bool MatchesAny(string? literal, IEnumerable<JsonElement> expected) => expected.Any(e => Matches(literal, e));

    public static string? AsString(string? literal) =>
        Parse(literal) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static long? AsEpoch(string? literal, string? format)
    {
        var value = Parse(literal);
        return format switch
        {
            UnixSeconds when value is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var seconds) => seconds,
            Iso8601 when value is { ValueKind: JsonValueKind.String } s
                && DateTimeOffset.TryParse(s.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                => at.ToUnixTimeSeconds(),
            _ => null,
        };
    }

    private static JsonElement? Parse(string? literal)
    {
        if (literal is null)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(literal);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
