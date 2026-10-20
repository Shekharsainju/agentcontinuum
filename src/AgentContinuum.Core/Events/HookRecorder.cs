using System.Text;
using System.Text.Json;
using AgentContinuum.Core.Rules;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Events;

public static class HookRecorder
{
    public const string HookKind = "hook";

    private const string HookNameField = "hook_event_name";
    private const string SessionIdField = "session_id";

    public static void Record(StateStore store, string opId, Stream stdin, ClaudeRules? rules)
    {
        using var buffer = new MemoryStream();
        stdin.CopyTo(buffer);

        var kept = new JsonAllowlist(AllowedPaths(rules), EventFilter.MaxValueChars).Read(buffer.ToArray());
        if (kept is null)
        {
            return;
        }

        using var json = new MemoryStream();
        using (var writer = new Utf8JsonWriter(json))
        {
            writer.WriteStartObject();
            foreach (var (path, value) in kept)
            {
                writer.WritePropertyName(path);
                writer.WriteRawValue(value.Literal);
            }
            writer.WriteEndObject();
        }
        Insert(store, opId, HookKind, Encoding.UTF8.GetString(json.ToArray()));
    }

    public static IReadOnlyList<HookEvent> ForOp(StateStore store, string opId)
    {
        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = "SELECT kind, fields_json FROM events WHERE op_id = $op_id AND kind = $hook ORDER BY id";
        cmd.Parameters.AddWithValue("$op_id", opId);
        cmd.Parameters.AddWithValue("$hook", HookKind);
        using var reader = cmd.ExecuteReader();
        var hooks = new List<HookEvent>();
        while (reader.Read())
        {
            using var doc = JsonDocument.Parse(reader.GetString(1));
            var root = doc.RootElement;
            hooks.Add(new HookEvent(
                HookName: StringField(root, HookNameField) ?? "",
                SessionId: StringField(root, SessionIdField),
                Fields: root.EnumerateObject()
                    .Where(p => p.Name is not (HookNameField or SessionIdField))
                    .ToDictionary(p => p.Name, p => p.Value.GetRawText(), StringComparer.Ordinal)));
        }
        return hooks;
    }

    private static IEnumerable<string> AllowedPaths(ClaudeRules? rules)
    {
        yield return HookNameField;
        yield return SessionIdField;
        if (rules?.Limit is { } limit)
        {
            yield return limit.ReasonField;
            if (limit.ResetField is { } resetField)
            {
                yield return resetField;
            }
        }
    }

    private static string? StringField(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Insert(StateStore store, string opId, string kind, string fieldsJson)
    {
        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO events (op_id, kind, fields_json, at_utc) VALUES ($op_id, $kind, $fields, $at)";
        cmd.Parameters.AddWithValue("$op_id", opId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$fields", fieldsJson);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }
}
