using System.Text;
using System.Text.Json;
using AgentContinuum.Core.Rules;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Events;

public static class HookRecorder
{
    public const string HookKind = "hook";
    public const string HookErrorKind = "hook_error";
    public const int MaxInputBytes = 64 * 1024;

    private const string HookNameField = "hook_event_name";
    private const string SessionIdField = "session_id";

    // Runs inside Claude Code's hook call, where an exception would surface as hook output. A failure is
    // recorded as a payload-free hook_error so the classifier still sees that a stop happened.
    public static void Record(StateStore store, string opId, Stream stdin, ClaudeRules? rules)
    {
        try
        {
            RecordOrThrow(store, opId, stdin);
        }
        catch (Exception)
        {
            try
            {
                Insert(store, opId, HookErrorKind, "{}");
            }
            catch (Exception)
            {
            }
        }
    }

    private static void RecordOrThrow(StateStore store, string opId, Stream stdin)
    {
        var input = ReadBounded(stdin);
        var kept = input is null ? null : new JsonAllowlist(AllowedPaths, EventFilter.MaxValueChars).Read(input);
        if (kept is null)
        {
            Insert(store, opId, HookErrorKind, "{}");
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
        cmd.CommandText = "SELECT kind, fields_json FROM events WHERE op_id = $op_id AND kind IN ($hook, $hook_error) ORDER BY id";
        cmd.Parameters.AddWithValue("$op_id", opId);
        cmd.Parameters.AddWithValue("$hook", HookKind);
        cmd.Parameters.AddWithValue("$hook_error", HookErrorKind);
        using var reader = cmd.ExecuteReader();
        var hooks = new List<HookEvent>();
        while (reader.Read())
        {
            if (reader.GetString(0) == HookErrorKind)
            {
                hooks.Add(new HookEvent(HookErrorKind, null, new Dictionary<string, string>()));
                continue;
            }
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

    private static byte[]? ReadBounded(Stream stdin)
    {
        var buffer = new byte[MaxInputBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stdin.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }
        return total > MaxInputBytes ? null : buffer[..total];
    }

    // The limit signal is read from the stream, so a hook payload keeps nothing beyond who sent it.
    private static readonly string[] AllowedPaths = [HookNameField, SessionIdField];

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
