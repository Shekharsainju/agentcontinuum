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
                "--permission-mode", "acceptEdits", "--allowedTools", "Read,Edit"],
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

    private sealed class RecordingNotifier : INotifier
    {
        public List<Offer> Offers { get; } = [];

        public List<(string TaskId, string Label, string ReasonCode)> Attentions { get; } = [];

        public void OfferResume(Offer offer) => Offers.Add(offer);

        public void Attention(string taskId, string label, string reasonCode) => Attentions.Add((taskId, label, reasonCode));
    }
}
