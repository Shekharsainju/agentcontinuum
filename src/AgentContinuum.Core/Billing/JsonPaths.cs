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
}
