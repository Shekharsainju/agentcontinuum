using AgentContinuum.Core.Model;
using Microsoft.Data.Sqlite;

namespace AgentContinuum.Core.State;

public sealed class LaunchLedger
{
    public const string IntentInserted = "intent-inserted";

    private const string Columns = """
        op_id, task_id, window_id, attempt_no, state, pid, proc_created_at, session_id, exit_code, outcome, reason, created_utc
        """;

    private readonly StateStore _store;
    private readonly TimeProvider _time;
    private readonly Action<string> _checkpoint;

    // checkpoint lets fault injection stop a launch at an R13 crash point.
    public LaunchLedger(StateStore store, TimeProvider? time = null, Action<string>? checkpoint = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
        _checkpoint = checkpoint ?? (_ => { });
    }

    public string BeginIntent(string taskId) => _store.InTransaction(tx =>
    {
        var opId = Guid.NewGuid().ToString("N");

        var (attemptsUsed, maxAttempts, state, mode) = Gate(tx, taskId);

        // Pre-flight checks these too. Repeating them here keeps I3 inside the transaction that counts, and stops
        // a revoke or a permission change that lands after pre-flight from launching.
        if (!LaunchPolicy.IsLaunchable(state) || !LaunchPolicy.IsAllowedMode(mode))
        {
            throw new InvalidOperationException($"Task {taskId} cannot launch in {StateText.ToText(state)} with its permission mode.");
        }

        if (attemptsUsed >= maxAttempts)
        {
            throw new InvalidOperationException($"Task {taskId} has used {attemptsUsed} of {maxAttempts} attempts.");
        }

        if (LiveOp(tx, taskId) is { } live)
        {
            throw new InvalidOperationException($"Task {taskId} already has live launch {live.OpId} in {StateText.ToText(live.State)}.");
        }

        Execute(
            tx,
            """
            INSERT INTO launch_ops (op_id, task_id, window_id, attempt_no, state, created_utc)
            VALUES (
                $op_id,
                $task_id,
                (SELECT count(*) FROM launch_ops WHERE task_id = $task_id AND outcome = $usage_limit),
                $attempt_no,
                $state,
                $created_utc)
            """,
            ("$op_id", opId),
            ("$usage_limit", StateText.ToText(Outcome.UsageLimit)),
            ("$task_id", taskId),
            ("$attempt_no", attemptsUsed + 1),
            ("$state", StateText.ToText(OpState.Intent)),
            ("$created_utc", _time.GetUtcNow().ToUnixTimeSeconds()));
        _checkpoint(IntentInserted);

        Execute(
            tx,
            "UPDATE tasks SET attempts_used = attempts_used + 1, state = $state WHERE task_id = $task_id",
            ("$task_id", taskId),
            ("$state", StateText.ToText(TaskState.Running)));

        return opId;
    });

    public void MarkSpawned(string opId, int pid, long createdAtFileTime, Guid sessionId) =>
        Transition(
            opId,
            [OpState.Intent],
            OpState.Spawned,
            "pid = $pid, proc_created_at = $created, session_id = $session",
            ("$pid", pid),
            ("$created", createdAtFileTime),
            ("$session", sessionId.ToString("D")));

    public void MarkExited(string opId, int exitCode) =>
        Transition(opId, [OpState.Spawned], OpState.Exited, "exit_code = $exit_code", ("$exit_code", exitCode));

    public void Classify(string opId, Outcome outcome, string reason) =>
        Transition(
            opId,
            [OpState.Exited],
            OpState.Classified,
            "outcome = $outcome, reason = $reason",
            ("$outcome", StateText.ToText(outcome)),
            ("$reason", reason));

    public void Abandon(string opId) => Transition(opId, [OpState.Intent], OpState.Abandoned, null);

    public void Interrupt(string opId) => Transition(opId, [OpState.Intent, OpState.Spawned], OpState.Interrupted, null);

    public LaunchOp? Get(string opId)
    {
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM launch_ops WHERE op_id = $op_id";
        cmd.Parameters.AddWithValue("$op_id", opId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public LaunchOp? Live(string taskId) => LiveOp(null, taskId);

    public IReadOnlyList<LaunchOp> AllLive()
    {
        using var cmd = Command(null, $"SELECT {Columns} FROM launch_ops WHERE state IN ('INTENT','SPAWNED','EXITED') ORDER BY rowid", []);
        using var reader = cmd.ExecuteReader();
        var ops = new List<LaunchOp>();
        while (reader.Read())
        {
            ops.Add(Read(reader));
        }
        return ops;
    }

    // The guarded UPDATE makes check and write one statement, so a concurrent writer cannot slip between them.
    private void Transition(string opId, OpState[] from, OpState to, string? assignments, params (string Name, object? Value)[] values)
    {
        var allowed = string.Join(",", from.Select(s => $"'{StateText.ToText(s)}'"));
        var set = assignments is null ? "state = $to" : $"state = $to, {assignments}";
        var changed = Execute(
            null,
            $"UPDATE launch_ops SET {set} WHERE op_id = $op_id AND state IN ({allowed})",
            [("$op_id", opId), ("$to", StateText.ToText(to)), .. values]);
        if (changed != 1)
        {
            var current = Get(opId)?.State;
            throw new InvalidOperationException(current is null
                ? $"No launch op {opId}."
                : $"Launch op {opId} cannot move from {StateText.ToText(current.Value)} to {StateText.ToText(to)}.");
        }
    }

    private (long Used, long Max, TaskState State, string Mode) Gate(SqliteTransaction tx, string taskId)
    {
        using var cmd = Command(tx, "SELECT attempts_used, max_attempts, state, permission_mode FROM tasks WHERE task_id = $task_id", [("$task_id", taskId)]);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? (reader.GetInt64(0), reader.GetInt64(1), StateText.Parse<TaskState>(reader.GetString(2)), reader.GetString(3))
            : throw new InvalidOperationException($"No task {taskId}.");
    }

    private LaunchOp? LiveOp(SqliteTransaction? tx, string taskId)
    {
        using var cmd = Command(
            tx,
            $"SELECT {Columns} FROM launch_ops WHERE task_id = $task_id AND state IN ('INTENT','SPAWNED','EXITED')",
            [("$task_id", taskId)]);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private SqliteCommand Command(SqliteTransaction? tx, string sql, (string Name, object? Value)[] values)
    {
        var cmd = _store.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in values)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return cmd;
    }

    private int Execute(SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    {
        using var cmd = Command(tx, sql, values);
        return cmd.ExecuteNonQuery();
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
