using AgentContinuum.Core.Platform;
using Microsoft.Data.Sqlite;

namespace AgentContinuum.Core.State;

public sealed record Request(long Id, string Kind, string? TaskId, string ArgsJson, DateTimeOffset CreatedUtc);

/// <summary>Commands reach the daemon as rows in the requests table, which the daemon polls.</summary>
public sealed class RequestQueue
{
    private const string Pending = "PENDING";
    private const string Taken = "TAKEN";
    private const string Done = "DONE";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly StateStore _store;
    private readonly IClock _clock;

    public RequestQueue(StateStore store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public long Submit(string kind, string? taskId, string argsJson)
    {
        using var cmd = Command(
            "INSERT INTO requests (kind, task_id, args_json, created_utc, status) VALUES ($kind, $task_id, $args, $created, $pending) RETURNING id",
            ("$kind", kind), ("$task_id", taskId), ("$args", argsJson), ("$created", _clock.UtcNow.ToUnixTimeSeconds()), ("$pending", Pending));
        return (long)cmd.ExecuteScalar()!;
    }

    // Marking the row taken in the same statement means a request is acted on at most once, even across a
    // daemon crash: a taken request is never handed out again.
    public Request? Next()
    {
        using var cmd = Command(
            """
            UPDATE requests SET status = $taken
            WHERE id = (SELECT id FROM requests WHERE status = $pending ORDER BY id LIMIT 1)
            RETURNING id, kind, task_id, args_json, created_utc
            """,
            ("$taken", Taken), ("$pending", Pending));
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new Request(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)))
            : null;
    }

    /// <summary>Records the result; a request its client already gave up on is gone and is left so.</summary>
    public void Complete(long id, string resultJson)
    {
        using var cmd = Command(
            "UPDATE requests SET status = $done, result_json = $result WHERE id = $id",
            ("$done", Done), ("$result", resultJson), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes every unfinished request older than <paramref name="age"/> and returns how many.</summary>
    public int ExpireOlderThan(TimeSpan age)
    {
        using var cmd = Command(
            "DELETE FROM requests WHERE status IN ($pending, $taken) AND created_utc < $cutoff",
            ("$pending", Pending), ("$taken", Taken), ("$cutoff", (_clock.UtcNow - age).ToUnixTimeSeconds()));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes the request if no daemon has taken it yet.</summary>
    public bool Withdraw(long id)
    {
        using var cmd = Command("DELETE FROM requests WHERE id = $id AND status = $pending", ("$id", id), ("$pending", Pending));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>The result JSON, removing the request, or null when none is recorded within <paramref name="timeout"/>.</summary>
    public string? WaitFor(long id, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            using (var cmd = Command(
                "DELETE FROM requests WHERE id = $id AND status = $done RETURNING result_json", ("$id", id), ("$done", Done)))
            {
                if (cmd.ExecuteScalar() is string result)
                {
                    return result;
                }
            }
            if (DateTime.UtcNow >= until)
            {
                return null;
            }
            Thread.Sleep(PollInterval);
        }
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] values)
    {
        var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in values)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return cmd;
    }
}
