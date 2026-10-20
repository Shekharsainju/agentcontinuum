using System.Text.Json;
using AgentContinuum.Core.Events;

namespace AgentContinuum.Core.Tests.Events;

public sealed class JsonLiteralTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Theory]
    [InlineData("false", "false", true)]
    [InlineData("\"false\"", "false", false)]
    [InlineData("false", "\"false\"", false)]
    [InlineData("\"completed\"", "\"completed\"", true)]
    [InlineData("\"Completed\"", "\"completed\"", false)]
    [InlineData("1.0", "1", true)]
    [InlineData("2", "1", false)]
    [InlineData("null", "null", true)]
    [InlineData("not json", "\"not json\"", false)]
    [InlineData(null, "null", false)]
    public void Matches_ComparesKindAndValue(string? literal, string expected, bool matches)
    {
        Assert.Equal(matches, JsonLiteral.Matches(literal, Json(expected)));
    }

    [Theory]
    [InlineData("\"none\"", "none")]
    [InlineData("true", null)]
    [InlineData("broken\"", null)]
    [InlineData(null, null)]
    public void AsString_ReadsOnlyStrings(string? literal, string? expected)
    {
        Assert.Equal(expected, JsonLiteral.AsString(literal));
    }

    [Theory]
    [InlineData("1800000000", "unix_seconds", 1_800_000_000L)]
    [InlineData("1800000000.5", "unix_seconds", null)]
    [InlineData("\"1800000000\"", "unix_seconds", null)]
    [InlineData("\"2027-01-15T08:00:00Z\"", "iso8601", 1_800_000_000L)]
    [InlineData("\"2027-01-15T09:00:00+01:00\"", "iso8601", 1_800_000_000L)]
    [InlineData("\"next tuesday\"", "iso8601", null)]
    [InlineData("1800000000", "unix_ms", null)]
    [InlineData("1800000000", null, null)]
    public void AsEpoch_ReadsKnownFormats(string literal, string? format, long? expected)
    {
        Assert.Equal(expected, JsonLiteral.AsEpoch(literal, format));
    }
}
