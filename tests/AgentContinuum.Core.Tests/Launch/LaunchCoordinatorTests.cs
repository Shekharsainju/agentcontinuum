using System.Text;
using System.Text.Json;
using AgentContinuum.Core.Events;
using AgentContinuum.Core.Launch;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Preflight;
using AgentContinuum.Core.Rules;
using AgentContinuum.Core.Schedule;
using AgentContinuum.Core.State;
using AgentContinuum.Core.Tests.Preflight;
using AgentContinuum.Core.Workspace;

namespace AgentContinuum.Core.Tests.Launch;

public sealed class LaunchCoordinatorTests : IDisposable
{
    private const string TaskId = PreflightFixture.TaskId;

    private readonly PreflightFixture _f = new();
    private readonly FakeProcessHost _host = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly RecordingLog _log = new();
    private readonly List<TimeSpan> _delays = [];
    private ClaudeRules? _rules = VersionRules.For("2.1.304");

    public void Dispose() => _f.Dispose();

    private LaunchCoordinator Coordinator()
    {
        _f.EnsureAdded();
        var preflightHost = new PreflightHost(GitRepo.GitExe, _f.ParentEnv, _f.UserProfile, _f.ManagedSettingsPaths);
        return new LaunchCoordinator(
            _f.Store,
            new PreflightRunner(_f.Store, _f.Lister, _f.Runner, _f.Billing, _f.Clock, preflightHost),
            _host,
            new ResetScheduler(_f.Tasks, _f.Clock, _log),
            _notifier,
            _log,
            new LaunchPaths(AcExe, _f.Outside("launch")),
            preflightHost,
            rulesFor: _ => _rules,
            delay: async d =>
            {
                lock (_delays)
                {
                    _delays.Add(d);
                }
                await Task.Yield();
            });
    }

    private long OpCount()
    {
        using var cmd = _f.Store.Connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM launch_ops";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task PreflightFails_NoIntentNoAttempt()
    {
        _f.Lister.Throw = new ProcessListException("enumeration failed");

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false);

        Assert.False(report.Launched);
        Assert.Equal([PreflightReasons.ProcessUnknown], report.Preflight.Reasons);
        Assert.Null(report.OpId);
        Assert.Empty(_host.Started);
        Assert.Equal(0, OpCount());
        var task = _f.Tasks.Get(TaskId)!;
        Assert.Equal(0, task.AttemptsUsed);
        Assert.Equal(TaskState.NeedsAttention, task.State);
        Assert.Equal(PreflightReasons.ProcessUnknown, task.AttentionReason);
        Assert.Equal([(TaskId, "sample", PreflightReasons.ProcessUnknown)], _notifier.Attentions);
    }

    [Theory]
    [InlineData(TaskState.Revoked)]
    [InlineData(TaskState.Completed)]
    [InlineData(TaskState.Running)]
    public async Task PreflightFails_NotLaunchable_StateUnchanged(TaskState state)
    {
        _f.Task = _f.Task with { State = state };

        var report = await Coordinator().Launch(TaskId, LaunchKind.Resume, now: true);

        Assert.False(report.Launched);
        Assert.Contains(PreflightReasons.AdmissionState, report.Preflight.Reasons);
        Assert.Equal(state, _f.Tasks.Get(TaskId)!.State);
        Assert.Empty(_notifier.Attentions);
        Assert.Empty(_host.Started);
    }

    [Fact]
    public async Task HostStartFails_AbandonedNeedsAttention()
    {
        _host.StartThrows = new IOException("could not start");

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false);

        Assert.False(report.Launched);
        Assert.Equal(OpState.Abandoned, _f.Ledger.Get(report.OpId!)!.State);
        var task = _f.Tasks.Get(TaskId)!;
        Assert.Equal(1, task.AttemptsUsed);
        Assert.Equal(TaskState.NeedsAttention, task.State);
        Assert.Equal("Launch failed", task.AttentionReason);
        Assert.Equal([(TaskId, "sample", "Launch failed")], _notifier.Attentions);
        Assert.Contains(_log.Messages, m => m == $"launch op {report.OpId} did not start: IOException");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TaskChangedDuringPreflight_NotLaunchedNoAttempt(bool revoke)
    {
        var before = _f.Task.State;
        _f.Runner.OnRun = _ =>
        {
            if (revoke)
            {
                _f.Tasks.SetState(TaskId, TaskState.Revoked, null);
                return;
            }
            using var cmd = _f.Store.Connection.CreateCommand();
            cmd.CommandText = "UPDATE tasks SET permission_mode = 'bypassPermissions'";
            cmd.ExecuteNonQuery();
        };

        var report = await Coordinator().Launch(TaskId, LaunchKind.Resume, now: true);

        Assert.False(report.Launched);
        Assert.Equal([PreflightReasons.AdmissionState], report.Preflight.Reasons);
        Assert.Empty(_host.Started);
        Assert.Equal(0, OpCount());
        var task = _f.Tasks.Get(TaskId)!;
        Assert.Equal(0, task.AttemptsUsed);
        Assert.Equal(revoke ? TaskState.Revoked : before, task.State);
        Assert.Empty(_notifier.Attentions);
    }

    [Theory]
    [InlineData("bypassPermissions")]
    [InlineData("dontAsk")]
    [InlineData("auto")]
    [InlineData("Default")]
    public void BuildArgs_ModeOutsidePolicy_Throws(string mode)
    {
        var task = _f.Task with { PermissionMode = mode };

        Assert.Throws<InvalidOperationException>(() => LaunchCoordinator.BuildArgs(task, LaunchKind.First, null));
    }

    [Fact]
    public async Task ClassifyFailsAfterOutcomeApplied_OutcomeKept()
    {
        _host.Run.Lines.AddRange([InitLine(), SuccessLine()]);
        _f.EnsureAdded();
        using (var cmd = _f.Store.Connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TRIGGER fail_completed BEFORE UPDATE ON launch_ops
                WHEN NEW.outcome = 'COMPLETED' BEGIN SELECT RAISE(ABORT, 'classify failed'); END
                """;
            cmd.ExecuteNonQuery();
        }

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false).WaitAsync(TimeSpan.FromSeconds(10));

        var op = _f.Ledger.Get(report.OpId!)!;
        Assert.Equal(OpState.Classified, op.State);
        Assert.Equal(Outcome.Ambiguous, op.Outcome);
        Assert.Equal("Run error", op.Reason);
        Assert.Equal(TaskState.Completed, _f.Tasks.Get(TaskId)!.State);
    }

    [Fact]
    public async Task ErrorWhileRunning_KilledAndSettledBeforeDispose()
    {
        _rules = _rules! with { Limit = Limit };
        _host.Run.ExitCode = null;
        _host.Run.Lines.Add(InitLine());
        _host.Run.StdoutThrows = new IOException("pipe broken");

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(_log.Messages, m => m == $"kill op {report.OpId} reason run error ok True error 0");
        Assert.True(_host.Run.Disposed);
        Assert.False(_host.Run.UsedAfterDispose);
    }

    [Fact]
    public void BuildArgs_OneArgumentPerAllowedToolRule()
    {
        var task = _f.Task with { AllowedTools = ["Read", "Bash(git diff:*,stat)"] };

        var args = LaunchCoordinator.BuildArgs(task, LaunchKind.First, null);

        Assert.Equal(["--allowedTools", "Read", "Bash(git diff:*,stat)"], args[^3..]);
    }

    [Theory]
    [InlineData(false, OpState.Interrupted)]
    [InlineData(true, OpState.Classified)]
    public async Task ErrorAfterStart_NeedsAttentionNotifiesAndFreesTask(bool afterExit, OpState opState)
    {
        _host.Run.Lines.AddRange([InitLine(), SuccessLine()]);
        if (afterExit)
        {
            _host.Run.Active = () => throw new IOException("job query failed");
        }
        else
        {
            _host.Run.ExitCode = null;
            _host.Run.StdoutThrows = new IOException("pipe broken");
        }

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(report.Launched);
        Assert.Null(report.Outcome);
        Assert.True(_host.Run.Disposed);
        Assert.Equal(opState, _f.Ledger.Get(report.OpId!)!.State);
        Assert.Null(_f.Ledger.Live(TaskId));
        var task = _f.Tasks.Get(TaskId)!;
        Assert.Equal(TaskState.NeedsAttention, task.State);
        Assert.Equal("Run error", task.AttentionReason);
        Assert.Equal([(TaskId, "sample", "Run error")], _notifier.Attentions);
        Assert.Contains(_log.Messages, m => m == $"launch op {report.OpId} failed: IOException");
    }

    private string Session => _f.Task.SessionId.ToString("D");

    private string InitLine(string authSource = "none") =>
        $$"""{"type":"system","subtype":"init","session_id":"{{Session}}","apiKeySource":"{{authSource}}"}""";

    private string SuccessLine() =>
        $$"""{"type":"result","subtype":"success","session_id":"{{Session}}","is_error":false,"terminal_reason":"completed"}""";

    [Fact]
    public async Task First_ArgsUseSessionId()
    {
        _host.Run.Lines.AddRange([InitLine(), SuccessLine()]);

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false);

        var spec = Assert.Single(_host.Started);
        Assert.Equal(_f.Task.CliPath, spec.Exe);
        Assert.Equal(
            ["-p", "--session-id", Session, "--output-format", "stream-json", "--verbose",
                "--permission-mode", "acceptEdits", "--allowedTools", "Read", "Edit"],
            spec.Args);
        Assert.Equal(_f.Repo.Path, spec.WorkingDir);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", spec.Env.Keys);
        Assert.Equal(_f.UserProfile, spec.Env["USERPROFILE"]);
        Assert.Equal(["SENTINEL-prompt: fix the bug"], _host.StdinTexts);

        Assert.True(report.Launched);
        Assert.Equal(Outcome.Completed, report.Outcome);
        var op = _f.Ledger.Get(report.OpId!)!;
        Assert.Equal(OpState.Classified, op.State);
        Assert.Equal(_host.Run.Pid, op.Pid);
        Assert.Equal(_host.Run.CreatedAtFileTime, op.ProcCreatedAtFileTime);
        Assert.Equal(0, op.ExitCode);
        var task = _f.Tasks.Get(TaskId)!;
        Assert.Equal(TaskState.Completed, task.State);
        Assert.Equal(1, task.AttemptsUsed);
        Assert.True(_host.Run.Disposed);
    }

    [Fact]
    public async Task Started_CalledOnceAfterSpawnWithOpId()
    {
        _host.Run.Lines.AddRange([InitLine(), SuccessLine()]);
        var started = new List<(string OpId, OpState State)>();

        var report = await Coordinator().Launch(
            TaskId, LaunchKind.First, now: false, started: op => started.Add((op, _f.Ledger.Get(op)!.State)));

        Assert.Equal([(report.OpId!, OpState.Spawned)], started);
    }

    [Fact]
    public async Task RevokedAndStopped_KilledAndStaysRevoked()
    {
        _host.Run.ExitCode = null;
        _host.Run.Lines.Add(InitLine());
        using var stop = new CancellationTokenSource();

        var report = await Coordinator().Launch(
            TaskId,
            LaunchKind.First,
            now: false,
            started: _ =>
            {
                _f.Tasks.SetState(TaskId, TaskState.Revoked, null);
                stop.Cancel();
            },
            stop: stop.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, _host.Run.Kills);
        Assert.Contains(_log.Messages, m => m == $"kill op {report.OpId} reason stopped ok True error 0");
        Assert.Null(_f.Ledger.Live(TaskId));
        Assert.Equal(TaskState.Revoked, _f.Tasks.Get(TaskId)!.State);
        Assert.Empty(_notifier.Attentions);
    }

    [Fact]
    public async Task Started_NotCalledWhenRefused()
    {
        _f.Lister.Throw = new ProcessListException("enumeration failed");
        var started = 0;

        await Coordinator().Launch(TaskId, LaunchKind.First, now: false, started: _ => started++);

        Assert.Equal(0, started);
    }

    [Fact]
    public async Task Resume_ArgsUseResumeAndFixedPrompt()
    {
        _host.Run.Lines.AddRange([InitLine(), SuccessLine()]);

        await Coordinator().Launch(TaskId, LaunchKind.Resume, now: false);

        var spec = Assert.Single(_host.Started);
        Assert.Equal(["-p", "--resume", Session], spec.Args[..3]);
        Assert.DoesNotContain("--session-id", spec.Args);
        Assert.Equal(
            ["Continue the task from where you stopped. Your usage limit has reset. Check the repository state before you act."],
            _host.StdinTexts);
    }

    [Theory]
    [InlineData("default", true)]
    [InlineData("acceptEdits", true)]
    [InlineData("plan", true)]
    [InlineData("bypassPermissions", false)]
    [InlineData("dontAsk", false)]
    [InlineData("auto", false)]
    public async Task Args_NeverContainBypass(string mode, bool launches)
    {
        _f.Task = _f.Task with { PermissionMode = mode };
        _host.Run.Lines.AddRange([InitLine(), SuccessLine()]);

        await Coordinator().Launch(TaskId, LaunchKind.First, now: false);

        Assert.Equal(launches, _host.Started.Count > 0);
        Assert.All(_host.Started, spec =>
        {
            Assert.DoesNotContain("--dangerously-skip-permissions", spec.Args);
            Assert.DoesNotContain(spec.Args, arg => arg.Contains("bypassPermissions", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task FirstEventMismatch_TerminatesBillingMismatch()
    {
        _host.Run.ExitCode = null;
        _host.Run.Lines.Add(InitLine(authSource: "ANTHROPIC_API_KEY"));

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, _host.Run.Kills);
        Assert.Equal(Outcome.BillingMismatch, report.Outcome);
        var task = _f.Tasks.Get(TaskId)!;
        Assert.Equal(TaskState.NeedsAttention, task.State);
        Assert.Equal("Billing unverified", task.AttentionReason);
        Assert.Contains(_log.Messages, m => m == $"kill op {report.OpId} reason billing mismatch ok True error 0");
    }

    [Fact]
    public async Task FirstEventMismatch_KillFailsOnce_Retried()
    {
        _host.Run.ExitCode = null;
        _host.Run.Lines.Add(InitLine(authSource: "ANTHROPIC_API_KEY"));
        _host.Run.KillResults.Enqueue(new KillResult(false, 5));

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, _host.Run.Kills);
        Assert.Equal(Outcome.BillingMismatch, report.Outcome);
        Assert.Equal(
            [$"kill op {report.OpId} reason billing mismatch ok False error 5", $"kill op {report.OpId} reason billing mismatch ok True error 0"],
            _log.Messages.Where(m => m.StartsWith("kill ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FirstEventMismatch_KillFailsTwice_JobClosed()
    {
        _host.Run.ExitCode = null;
        _host.Run.Lines.Add(InitLine(authSource: "ANTHROPIC_API_KEY"));
        _host.Run.KillResults.Enqueue(new KillResult(false, 5));
        _host.Run.KillResults.Enqueue(new KillResult(false, 5));

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, _host.Run.Kills);
        Assert.True(_host.Run.Disposed);
        Assert.Contains(_log.Messages, m => m == $"close op {report.OpId} reason billing mismatch");
        Assert.Equal(OpState.Interrupted, _f.Ledger.Get(report.OpId!)!.State);
        var task = _f.Tasks.Get(TaskId)!;
        Assert.Equal(TaskState.NeedsAttention, task.State);
        Assert.Equal("Billing unverified", task.AttentionReason);
        Assert.Equal([(TaskId, "sample", "Billing unverified")], _notifier.Attentions);
    }

    private const long ResetEpoch = 1_792_500_000;

    private const string AcExe = @"C:\Program Files\AgentContinuum\ac.exe";

    private static readonly LimitSignalRule Limit = new("StopFailure", "error_type", ["rate_limit"], "resets_at", "unix_seconds");

    // Records the hook the way `ac hook stop-failure` would, while the launch's op is live.
    private void FireLimitHookOnStart() => _host.OnStart = _ =>
    {
        var opId = _f.Ledger.Live(TaskId)!.OpId;
        var payload = $$"""
            {"hook_event_name":"StopFailure","session_id":"{{Session}}","error_type":"rate_limit","resets_at":{{ResetEpoch}},"message":"SENTINEL-hook"}
            """;
        HookRecorder.Record(_f.Store, opId, new MemoryStream(Encoding.UTF8.GetBytes(payload)), _rules);
    };

    [Fact]
    public async Task UsageLimit_SchedulesReset()
    {
        _rules = _rules! with { Limit = Limit };
        _host.Run.ExitCode = 1;
        _host.Run.Lines.Add(InitLine());
        FireLimitHookOnStart();

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false);

        Assert.Equal(Outcome.UsageLimit, report.Outcome);
        var task = _f.Tasks.Get(TaskId)!;
        Assert.Equal(TaskState.WaitingForReset, task.State);
        Assert.Equal(ResetEpoch, task.ResetExpectedAtEpoch);
        Assert.Equal(ResetEpoch + ResetScheduler.ResetMarginSeconds, task.AttemptPermittedAtEpoch);

        var settingsPath = Path.Combine(_f.Outside("launch"), report.OpId!, "settings.json");
        var args = _host.Started.Single().Args;
        Assert.Equal(["--settings", settingsPath], args[^2..]);
        using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var command = settings.RootElement.GetProperty("hooks").GetProperty("StopFailure")[0].GetProperty("hooks")[0];
        Assert.Equal("command", command.GetProperty("type").GetString());
        Assert.Equal($"\"{AcExe}\" hook stop-failure --op {report.OpId}", command.GetProperty("command").GetString());
    }

    [Fact]
    public async Task VerifiedLimit_StillAlive10s_Terminated()
    {
        _rules = _rules! with { Limit = Limit };
        _host.Run.ExitCode = null;
        _host.Run.Lines.Add(InitLine());
        FireLimitHookOnStart();

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, _host.Run.Kills);
        Assert.True(_delays.Aggregate(TimeSpan.Zero, (sum, d) => sum + d) <= TimeSpan.FromSeconds(10));
        Assert.Equal(Outcome.UsageLimit, report.Outcome);
        Assert.Contains(_log.Messages, m => m == $"kill op {report.OpId} reason limit signal ok True error 0");
    }

    [Fact]
    public async Task Baseline_NotTakenWhileProcessesActive()
    {
        _host.Run.Lines.AddRange([InitLine(), SuccessLine()]);
        var active = new Queue<int>([2, 1]);
        _host.Run.Active = () =>
        {
            var count = active.Count > 0 ? active.Dequeue() : 0;
            if (count > 0)
            {
                _f.Repo.Write($"late-{count}.txt", "written by a child after the main process exited");
            }
            return count;
        };

        await Coordinator().Launch(TaskId, LaunchKind.First, now: false);

        Assert.Equal(3, _host.Run.ActiveQueries);
        Assert.Equal(WorkspaceFingerprint.Compute(_f.Repo.Path, GitRepo.GitExe).Hash, _f.Tasks.Get(TaskId)!.BaselineFingerprint);
    }

    [Fact]
    public async Task Baseline_JobNeverEmpties_TerminatedAndNeedsAttention()
    {
        _rules = _rules! with { Limit = Limit };
        _host.Run.ExitCode = 1;
        _host.Run.Lines.Add(InitLine());
        _host.Run.Active = () => 1;
        FireLimitHookOnStart();

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Outcome.UsageLimit, report.Outcome);
        Assert.Contains(_log.Messages, m => m == $"kill op {report.OpId} reason processes after exit ok True error 0");
        var task = _f.Tasks.Get(TaskId)!;
        Assert.Null(task.BaselineFingerprint);
        Assert.Equal(TaskState.NeedsAttention, task.State);
        Assert.Equal("Workspace baseline not taken", task.AttentionReason);
    }

    [Fact]
    public async Task GrandchildHoldsStdout_JobKilledAfterExit()
    {
        _host.Run.Lines.AddRange([InitLine(), SuccessLine()]);
        _host.Run.GrandchildHoldsStdout = true;

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, _host.Run.Kills);
        Assert.Contains(_log.Messages, m => m == $"kill op {report.OpId} reason processes after exit ok True error 0");
        Assert.Equal(Outcome.Completed, report.Outcome);
        Assert.Equal(0, _f.Ledger.Get(report.OpId!)!.ExitCode);
        Assert.NotNull(_f.Tasks.Get(TaskId)!.BaselineFingerprint);
    }

    [Fact]
    public async Task GrandchildHoldsStdout_KillFails_JobClosedNeedsAttention()
    {
        _host.Run.Lines.AddRange([InitLine(), SuccessLine()]);
        _host.Run.GrandchildHoldsStdout = true;
        _host.Run.KillResult = new KillResult(false, 5);

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(_host.Run.Disposed);
        Assert.Contains(_log.Messages, m => m == $"close op {report.OpId} reason processes after exit");
        Assert.Null(_f.Ledger.Live(TaskId));
        var task = _f.Tasks.Get(TaskId)!;
        Assert.Equal(TaskState.NeedsAttention, task.State);
        Assert.Equal("Processes outlived the run", task.AttentionReason);
        Assert.Equal([(TaskId, "sample", "Processes outlived the run")], _notifier.Attentions);
    }

    [Fact]
    public async Task AttemptsExhausted_NeedsAttention()
    {
        _f.Task = _f.Task with { MaxAttempts = 1 };
        _rules = _rules! with { Limit = Limit };
        _host.Run.ExitCode = 1;
        _host.Run.Lines.Add(InitLine());
        FireLimitHookOnStart();

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false);

        Assert.Equal(Outcome.UsageLimit, report.Outcome);
        var task = _f.Tasks.Get(TaskId)!;
        Assert.Equal(TaskState.NeedsAttention, task.State);
        Assert.Equal("Attempts exhausted", task.AttentionReason);
        Assert.Null(task.AttemptPermittedAtEpoch);
        Assert.Equal([(TaskId, "sample", "Attempts exhausted")], _notifier.Attentions);
        Assert.Empty(_notifier.Offers);
    }

    [Fact]
    public async Task NoContentInDbOrLog()
    {
        _f.Task = _f.Task with { MaxAttempts = 1 };
        _rules = _rules! with { Limit = Limit };
        _host.Run.ExitCode = 1;
        _host.Run.Lines.AddRange(
        [
            InitLine(),
            $$$"""{"type":"assistant","session_id":"{{{Session}}}","message":{"content":[{"type":"text","text":"SENTINEL-stream"}]}}""",
            $$"""{"type":"result","session_id":"{{Session}}","is_error":true,"result":"SENTINEL-stream"}""",
            "SENTINEL-stream not json",
        ]);
        FireLimitHookOnStart();

        var report = await Coordinator().Launch(TaskId, LaunchKind.First, now: false);
        Assert.True(report.Launched);
        Assert.Contains("SENTINEL-prompt", _host.StdinTexts.Single());
        _f.Store.Connection.Close();

        var dbDir = Path.GetDirectoryName(_f.Store.Connection.DataSource)!;
        var written = Directory.GetFiles(dbDir, "ac.db*").Select(ReadShared)
            .Concat(Directory.GetFiles(_f.Outside("launch"), "*", SearchOption.AllDirectories).Select(ReadShared))
            .Concat(_log.Messages)
            .Concat(_notifier.Attentions.Select(a => $"{a.TaskId} {a.Label} {a.ReasonCode}"));
        Assert.All(written, text => Assert.DoesNotContain("SENTINEL", text));
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.Latin1);
        return reader.ReadToEnd();
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<Offer> Offers { get; } = [];

        public List<(string TaskId, string Label, string ReasonCode)> Attentions { get; } = [];

        public void OfferResume(Offer offer) => Offers.Add(offer);

        public void Attention(string taskId, string label, string reasonCode) => Attentions.Add((taskId, label, reasonCode));
    }
}
