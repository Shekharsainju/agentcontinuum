using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentContinuum.AcTestHost;
using AgentContinuum.Cli;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.State;
using AgentContinuum.Core.Tests;

namespace AgentContinuum.Acceptance.Tests;

/// <summary>
/// One isolated install for ac-test.exe: state, profile, FakeClaude copy, rules with a test-only limit signal,
/// clock offset and captured notifications, all under one temp folder.
/// </summary>
public sealed class AcceptanceRoot : IDisposable
{
    public const string LimitHook = "StopFailure";
    public const string LimitReasonField = "error";
    public const string LimitReason = "rate_limit";
    public const string LimitResetField = "resets_at";

    private readonly TempDirectory _dir = new();
    private readonly List<Process> _daemons = [];
    private TestHostConfig _config;

    public AcceptanceRoot(bool attest = true)
    {
        Directory.CreateDirectory(LocalAppData);
        Directory.CreateDirectory(Path.Combine(LocalAppData, "AgentContinuum"));
        WriteProfile();
        CopyFakeClaude();
        WriteRules();
        _config = new TestHostConfig(
            LocalAppData,
            Profile,
            RulesFile: RulesFile,
            ClockOffsetFile: ClockOffsetFile,
            NotificationsFile: NotificationsFile,
            TickIntervalSeconds: 0.5);
        WriteConfig();
        Clock = new OffsetClock(ClockOffsetFile);
        if (attest)
        {
            Ok(Ac("attest-overage-off"));
        }
    }

    public string Root => _dir.Path;

    public string LocalAppData => _dir.File("lad");

    public string Profile => _dir.File("profile");

    public string CliPath => Path.Combine(_dir.File("cli"), $"claude-{Path.GetFileName(Root)[..8]}.exe");

    public string RulesFile => _dir.File("rules.json");

    public string ClockOffsetFile => _dir.File("clock-offset.txt");

    public string NotificationsFile => _dir.File("notifications.txt");

    public string StateFolder => Path.Combine(LocalAppData, "AgentContinuum");

    public string Database => AppPaths.ForRoot(StateFolder).Database;

    public string LogFile => AppPaths.ForRoot(StateFolder).LogFile;

    public OffsetClock Clock { get; }

    public TestHostConfig Config
    {
        get => _config;
        set
        {
            _config = value;
            WriteConfig();
        }
    }

    public void SetClockOffset(TimeSpan offset) =>
        File.WriteAllText(ClockOffsetFile, offset.TotalSeconds.ToString(CultureInfo.InvariantCulture));

    public void EditRules(Action<JsonObject> edit)
    {
        var rules = JsonNode.Parse(File.ReadAllText(RulesFile))!.AsArray();
        edit(rules[0]!.AsObject());
        File.WriteAllText(RulesFile, rules.ToJsonString());
    }

    /// <summary>A git repo whose FakeClaude scenario is <paramref name="scenario"/>; FakeClaude's own files are ignored.</summary>
    public GitRepo Worktree(JsonObject scenario, string prompt = "Fix the failing test.")
    {
        var repo = GitRepo.Init(_dir.File($"wt-{Guid.NewGuid():N}"[..11]));
        repo.Write(".gitignore", "fake-claude-*\n.fake-claude.json\n");
        repo.Write("prompt.md", prompt);
        repo.Write("README.md", "test repo\n");
        repo.CommitAll();
        SetScenario(repo, scenario);
        return repo;
    }

    public static void SetScenario(GitRepo repo, JsonObject scenario) => File.WriteAllText(repo.File(".fake-claude.json"), scenario.ToJsonString());

    public string AddTask(GitRepo worktree, int maxAttempts = 5, string label = "acceptance task")
    {
        var deadline = Clock.UtcNow.AddDays(7).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var (exit, stdout) = Ac(
            "task", "add", "--label", label, "--worktree", worktree.Path, "--prompt-file", "prompt.md",
            "--permission-mode", "acceptEdits", "--max-attempts", maxAttempts.ToString(CultureInfo.InvariantCulture),
            "--deadline", deadline, "--cli", CliPath);
        Assert.True(exit == 0, $"task add failed: {stdout}");
        return stdout;
    }

    public (int ExitCode, string StdOut) Ac(params string[] args) => AcTestProcess.Run(LocalAppData, args);

    public static void Ok((int ExitCode, string StdOut) result) => Assert.True(result.ExitCode == 0, result.StdOut);

    /// <summary>Starts the daemon and returns once it holds its lock and has recovered.</summary>
    public Process StartDaemon()
    {
        var process = Process.Start(AcTestProcess.Info(LocalAppData, ["daemon"]))!;
        lock (_daemons)
        {
            _daemons.Add(process);
        }
        process.StandardInput.Close();
        _ = process.StandardError.ReadToEndAsync();
        var first = process.StandardOutput.ReadLineAsync();
        if (!first.Wait(TimeSpan.FromSeconds(60)) || first.Result != "daemon running")
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"daemon did not start: {(first.IsCompleted ? first.Result : "no output")}");
        }
        _ = process.StandardOutput.ReadToEndAsync();
        return process;
    }

    public static void StopDaemon(Process daemon)
    {
        if (!daemon.HasExited)
        {
            daemon.Kill(entireProcessTree: true);
        }
        daemon.WaitForExit();
    }

    public long Submit(string kind, string taskId, bool now = false)
    {
        using var store = StateStore.Open(Database);
        return new RequestQueue(store, Clock).Submit(kind, taskId, new JsonObject { ["now"] = now }.ToJsonString());
    }

    /// <summary>The daemon's reply, or null if none came within <paramref name="timeout"/>.</summary>
    public JsonObject? Reply(long requestId, TimeSpan timeout)
    {
        using var store = StateStore.Open(Database);
        return new RequestQueue(store, Clock).WaitFor(requestId, timeout) is { } reply ? JsonNode.Parse(reply)!.AsObject() : null;
    }

    public TaskRecord Task(string taskId)
    {
        using var store = StateStore.Open(Database);
        return new TaskRepository(store).Get(taskId)!;
    }

    public IReadOnlyList<OpRow> Ops(string taskId)
    {
        using var store = StateStore.Open(Database);
        using var cmd = store.Connection.CreateCommand();
        cmd.CommandText = "SELECT op_id, state, attempt_no, outcome, reason, pid FROM launch_ops WHERE task_id = $task ORDER BY rowid";
        cmd.Parameters.AddWithValue("$task", taskId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<OpRow>();
        while (reader.Read())
        {
            rows.Add(new OpRow(
                reader.GetString(0),
                StateText.Parse<OpState>(reader.GetString(1)),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : StateText.Parse<Outcome>(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5)));
        }
        return rows;
    }

    public string[] Notifications() => File.Exists(NotificationsFile) ? File.ReadAllLines(NotificationsFile) : [];

    public string[] LogLines() => File.Exists(LogFile) ? File.ReadAllLines(LogFile) : [];

    public static int StartsLogged(GitRepo worktree)
    {
        var log = worktree.File("fake-claude-starts.log");
        return File.Exists(log) ? File.ReadAllLines(log).Length : 0;
    }

    public static void Eventually(Func<bool> condition, TimeSpan timeout, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout)
            {
                throw new TimeoutException($"{what} did not happen within {timeout.TotalSeconds:0} s.");
            }
            Thread.Sleep(100);
        }
    }

    public static JsonObject SuccessScenario() => new()
    {
        ["authSource"] = "none",
        ["events"] = new JsonArray(new JsonObject
        {
            ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["terminal_reason"] = "completed",
        }),
        ["exitCode"] = 0,
    };

    public static JsonObject LimitScenario(long resetsAtEpoch, int lingerSeconds = 0, int spawnChildren = 0) => new()
    {
        ["authSource"] = "none",
        ["events"] = new JsonArray(new JsonObject
        {
            ["type"] = "result", ["subtype"] = "success", ["is_error"] = true, ["terminal_reason"] = "prompt_too_long",
        }),
        ["hook"] = new JsonObject
        {
            ["name"] = LimitHook,
            ["payload"] = new JsonObject { [LimitReasonField] = LimitReason, [LimitResetField] = resetsAtEpoch },
        },
        ["lingerSeconds"] = lingerSeconds,
        ["spawnChildren"] = spawnChildren,
        ["exitCode"] = 1,
    };

    public void Dispose()
    {
        lock (_daemons)
        {
            foreach (var daemon in _daemons)
            {
                try
                {
                    StopDaemon(daemon);
                }
                catch (InvalidOperationException)
                {
                }
                daemon.Dispose();
            }
        }
        _dir.Dispose();
    }

    private void WriteConfig() =>
        File.WriteAllText(Path.Combine(StateFolder, TestHostConfig.FileName), JsonSerializer.Serialize(_config, TestHostConfig.Json));

    // A subscription login as P3 reads it, by key names only.
    private void WriteProfile()
    {
        var claude = Directory.CreateDirectory(Path.Combine(Profile, ".claude")).FullName;
        File.WriteAllText(
            Path.Combine(claude, ".credentials.json"),
            """{"claudeAiOauth":{"accessToken":"test-token","subscriptionType":"max"}}""");
    }

    // The process check treats any CLI-named process it cannot read as a possible session, and other tests'
    // FakeClaude runs and your own Claude Code are all claude.exe. A name of its own keeps each root to its own
    // runs. The renamed apphost still loads claude.dll beside it.
    private void CopyFakeClaude()
    {
        var target = Directory.CreateDirectory(_dir.File("cli")).FullName;
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "claude.*"))
        {
            var name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(target, name == "claude.exe" ? Path.GetFileName(CliPath) : name));
        }
    }

    // The shipped rules have no limit signal until E1's capture runs, so the tests add one of their own.
    private void WriteRules()
    {
        using var stream = typeof(Core.Rules.VersionRules).Assembly.GetManifestResourceStream("AgentContinuum.Core.Rules.claude-code.json")!;
        var rules = JsonNode.Parse(stream)!.AsArray();
        rules[0]!["limit"] = new JsonObject
        {
            ["hook"] = LimitHook,
            ["reasonField"] = LimitReasonField,
            ["reasonValues"] = new JsonArray(LimitReason),
            ["resetField"] = LimitResetField,
            ["resetFormat"] = "unix_seconds",
        };
        File.WriteAllText(RulesFile, rules.ToJsonString());
    }
}

public sealed record OpRow(string OpId, OpState State, int AttemptNo, Outcome? Outcome, string? Reason, int? Pid);
