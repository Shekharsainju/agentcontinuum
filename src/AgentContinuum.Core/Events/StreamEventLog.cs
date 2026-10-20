using System.Text.Json;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Events;

/// <summary>Filtered stream events per launch, kept so an EXITED op can be classified after a crash.</summary>
public static class StreamEventLog
{
    public const string StreamKind = "stream";

    public static void Append(StateStore store, string opId, FilteredEvent filtered)
    {
        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO events (op_id, kind, fields_json, at_utc) VALUES ($op_id, $kind, $fields, $at)";
        cmd.Parameters.AddWithValue("$op_id", opId);
        cmd.Parameters.AddWithValue("$kind", StreamKind);
        cmd.Parameters.AddWithValue("$fields", JsonSerializer.Serialize(filtered));
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public static IReadOnlyList<FilteredEvent> ForOp(StateStore store, string opId)
    {
        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = "SELECT fields_json FROM events WHERE op_id = $op_id AND kind = $kind ORDER BY id";
        cmd.Parameters.AddWithValue("$op_id", opId);
        cmd.Parameters.AddWithValue("$kind", StreamKind);
        using var reader = cmd.ExecuteReader();
        var events = new List<FilteredEvent>();
        while (reader.Read())
        {
            events.Add(JsonSerializer.Deserialize<FilteredEvent>(reader.GetString(0)) ?? throw new JsonException("Empty stream event row."));
        }
        return events;
    }
}
