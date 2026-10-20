using System.Text.Json;
using AgentContinuum.Core.Model;
using Microsoft.Data.Sqlite;

namespace AgentContinuum.Core.State;

public sealed class TaskRepository
{
    private const string Columns = """
        task_id, label, worktree, prompt_file, permission_mode, allowed_tools_json, max_attempts, deadline_utc,
        session_id, cli_path, cli_sha256, cli_version, state, attempts_used, reset_expected_at,
        attempt_permitted_at, baseline_fingerprint, attention_reason
        """;

    private readonly StateStore _store;

    public TaskRepository(StateStore store)
    {
        _store = store;
    }

    public void Add(TaskRecord task)
    {
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO tasks ({Columns})
            VALUES ($task_id, $label, $worktree, $prompt_file, $permission_mode, $allowed_tools_json, $max_attempts,
                $deadline_utc, $session_id, $cli_path, $cli_sha256, $cli_version, $state, $attempts_used,
                $reset_expected_at, $attempt_permitted_at, $baseline_fingerprint, $attention_reason)
            """;
        cmd.Parameters.AddWithValue("$task_id", task.TaskId);
        cmd.Parameters.AddWithValue("$label", task.Label);
        cmd.Parameters.AddWithValue("$worktree", task.Worktree);
        cmd.Parameters.AddWithValue("$prompt_file", task.PromptFile);
        cmd.Parameters.AddWithValue("$permission_mode", task.PermissionMode);
        cmd.Parameters.AddWithValue("$allowed_tools_json", JsonSerializer.Serialize(task.AllowedTools));
        cmd.Parameters.AddWithValue("$max_attempts", task.MaxAttempts);
        cmd.Parameters.AddWithValue("$deadline_utc", task.DeadlineUtc.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$session_id", task.SessionId.ToString("D"));
        cmd.Parameters.AddWithValue("$cli_path", task.CliPath);
        cmd.Parameters.AddWithValue("$cli_sha256", task.CliSha256);
        cmd.Parameters.AddWithValue("$cli_version", task.CliVersion);
        cmd.Parameters.AddWithValue("$state", StateText.ToText(task.State));
        cmd.Parameters.AddWithValue("$attempts_used", task.AttemptsUsed);
        cmd.Parameters.AddWithValue("$reset_expected_at", (object?)task.ResetExpectedAtEpoch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$attempt_permitted_at", (object?)task.AttemptPermittedAtEpoch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$baseline_fingerprint", (object?)task.BaselineFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$attention_reason", (object?)task.AttentionReason ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public TaskRecord? Get(string taskId)
    {
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM tasks WHERE task_id = $task_id";
        cmd.Parameters.AddWithValue("$task_id", taskId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<TaskRecord> List()
    {
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM tasks ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var tasks = new List<TaskRecord>();
        while (reader.Read())
        {
            tasks.Add(Read(reader));
        }
        return tasks;
    }

    public void SetState(string taskId, TaskState state, string? reason) =>
        Update(taskId, "state = $state, attention_reason = $reason", ("$state", StateText.ToText(state)), ("$reason", reason));

    public bool TryMoveState(string taskId, TaskState from, TaskState to)
    {
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = "UPDATE tasks SET state = $to WHERE task_id = $task_id AND state = $from";
        cmd.Parameters.AddWithValue("$task_id", taskId);
        cmd.Parameters.AddWithValue("$from", StateText.ToText(from));
        cmd.Parameters.AddWithValue("$to", StateText.ToText(to));
        return cmd.ExecuteNonQuery() == 1;
    }

    public void SetSchedule(string taskId, long? resetEpoch, long? permittedEpoch) =>
        Update(taskId, "reset_expected_at = $reset, attempt_permitted_at = $permitted", ("$reset", resetEpoch), ("$permitted", permittedEpoch));

    public void SetBaseline(string taskId, string fingerprint) =>
        Update(taskId, "baseline_fingerprint = $fingerprint", ("$fingerprint", fingerprint));

    public void SetCliPin(string taskId, string path, string sha256, string version) =>
        Update(taskId, "cli_path = $path, cli_sha256 = $sha256, cli_version = $version", ("$path", path), ("$sha256", sha256), ("$version", version));

    private void Update(string taskId, string assignments, params (string Name, object? Value)[] values)
    {
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = $"UPDATE tasks SET {assignments} WHERE task_id = $task_id";
        cmd.Parameters.AddWithValue("$task_id", taskId);
        foreach (var (name, value) in values)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        if (cmd.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"No task {taskId}.");
        }
    }

    private static TaskRecord Read(SqliteDataReader r) => new(
        TaskId: r.GetString(0),
        Label: r.GetString(1),
        Worktree: r.GetString(2),
        PromptFile: r.GetString(3),
        PermissionMode: r.GetString(4),
        AllowedTools: JsonSerializer.Deserialize<string[]>(r.GetString(5)) ?? [],
        MaxAttempts: r.GetInt32(6),
        DeadlineUtc: DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(7)),
        SessionId: Guid.Parse(r.GetString(8)),
        CliPath: r.GetString(9),
        CliSha256: r.GetString(10),
        CliVersion: r.GetString(11),
        State: StateText.Parse<TaskState>(r.GetString(12)),
        AttemptsUsed: r.GetInt32(13),
        ResetExpectedAtEpoch: r.IsDBNull(14) ? null : r.GetInt64(14),
        AttemptPermittedAtEpoch: r.IsDBNull(15) ? null : r.GetInt64(15),
        BaselineFingerprint: r.IsDBNull(16) ? null : r.GetString(16),
        AttentionReason: r.IsDBNull(17) ? null : r.GetString(17));
}
