using System.Text.Json;

namespace AgentContinuum.Core.Billing;

internal static class JsonPaths
{
    public static bool TryGet(JsonElement root, string dottedPath, out JsonElement value)
    {
        value = root;
        foreach (var part in dottedPath.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value))
            {
                return false;
            }
        }
        return true;
    }

    public static bool Has(JsonElement root, string dottedPath) => TryGet(root, dottedPath, out _);

    /// <summary>The parsed file when it holds one JSON object; null when it cannot be read or is anything else.</summary>
    public static JsonDocument? ReadObject(string path)
    {
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return doc;
            }
            doc.Dispose();
            return null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
