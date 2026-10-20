using AgentContinuum.Core.Model;
using Microsoft.Data.Sqlite;

namespace AgentContinuum.Core.State;

public sealed class LaunchLedger
{
    private const string Columns = """
        op_id, task_id, window_id, attempt_no, state, pid, proc_created_at, session_id, exit_code, outcome, reason, created_utc
        """;

    private readonly StateStore _store;
    private readonly TimeProvider _time;

    public LaunchLedger(StateStore store, TimeProvider? time = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    public string BeginIntent(string taskId)
    {
        var opId = Guid.NewGuid().ToString("N");

        using var read = _store.Connection.CreateCommand();
        read.CommandText = "SELECT attempts_used FROM tasks WHERE task_id = $task_id";
        read.Parameters.AddWithValue("$task_id", taskId);
        var attemptsUsed = read.ExecuteScalar() is long used
            ? used
            : throw new InvalidOperationException($"No task {taskId}.");

        using var insert = _store.Connection.CreateCommand();
        insert.CommandText = $"""
            INSERT INTO launch_ops (op_id, task_id, window_id, attempt_no, state, created_utc)
            VALUES ($op_id, $task_id, 0, $attempt_no, $state, $created_utc)
            """;
        insert.Parameters.AddWithValue("$op_id", opId);
        insert.Parameters.AddWithValue("$task_id", taskId);
        insert.Parameters.AddWithValue("$attempt_no", attemptsUsed + 1);
        insert.Parameters.AddWithValue("$state", StateText.ToText(OpState.Intent));
        insert.Parameters.AddWithValue("$created_utc", _time.GetUtcNow().ToUnixTimeSeconds());
        insert.ExecuteNonQuery();

        using var count = _store.Connection.CreateCommand();
        count.CommandText = "UPDATE tasks SET attempts_used = attempts_used + 1, state = $state WHERE task_id = $task_id";
        count.Parameters.AddWithValue("$task_id", taskId);
        count.Parameters.AddWithValue("$state", StateText.ToText(TaskState.Running));
        count.ExecuteNonQuery();

        return opId;
    }

    public LaunchOp? Get(string opId)
    {
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM launch_ops WHERE op_id = $op_id";
        cmd.Parameters.AddWithValue("$op_id", opId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static LaunchOp Read(SqliteDataReader r) => new(
        OpId: r.GetString(0),
        TaskId: r.GetString(1),
        WindowId: r.GetInt32(2),
        AttemptNo: r.GetInt32(3),
        State: StateText.Parse<OpState>(r.GetString(4)),
        Pid: r.IsDBNull(5) ? null : r.GetInt32(5),
        ProcCreatedAtFileTime: r.IsDBNull(6) ? null : r.GetInt64(6),
        SessionId: r.IsDBNull(7) ? null : Guid.Parse(r.GetString(7)),
        ExitCode: r.IsDBNull(8) ? null : r.GetInt32(8),
        Outcome: r.IsDBNull(9) ? null : StateText.Parse<Outcome>(r.GetString(9)),
        Reason: r.IsDBNull(10) ? null : r.GetString(10),
        CreatedAtEpoch: r.GetInt64(11));
}
