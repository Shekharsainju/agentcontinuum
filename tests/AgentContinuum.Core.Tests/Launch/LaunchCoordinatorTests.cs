using AgentContinuum.Core.Launch;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Preflight;
using AgentContinuum.Core.Rules;
using AgentContinuum.Core.Schedule;
using AgentContinuum.Core.State;
using AgentContinuum.Core.Tests.Preflight;

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
            new LaunchPaths(@"C:\Program Files\AgentContinuum\ac.exe", _f.Outside("launch")),
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

    private sealed class RecordingNotifier : INotifier
    {
        public List<Offer> Offers { get; } = [];

        public List<(string TaskId, string Label, string ReasonCode)> Attentions { get; } = [];

        public void OfferResume(Offer offer) => Offers.Add(offer);

        public void Attention(string taskId, string label, string reasonCode) => Attentions.Add((taskId, label, reasonCode));
    }
}
