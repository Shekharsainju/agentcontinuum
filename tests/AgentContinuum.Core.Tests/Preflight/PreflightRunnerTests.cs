using AgentContinuum.Core.Model;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Preflight;
using AgentContinuum.Core.Workspace;

namespace AgentContinuum.Core.Tests.Preflight;

public sealed class PreflightRunnerTests : IDisposable
{
    private readonly PreflightFixture _f = new();

    public void Dispose() => _f.Dispose();

    private PreflightHost Host() => new(GitRepo.GitExe, _f.ParentEnv, _f.UserProfile, _f.ManagedSettingsPaths);

    private PreflightResult Run(bool now = false, PreflightHost? host = null)
    {
        _f.EnsureAdded();
        return new PreflightRunner(_f.Store, _f.Lister, _f.Runner, _f.Billing, _f.Clock, host ?? Host()).Run(PreflightFixture.TaskId, now);
    }

    private string Baseline() => WorkspaceFingerprint.Compute(_f.Repo.Path, GitRepo.GitExe).Hash!;

    private void AssertNothingCounted()
    {
        var task = _f.Tasks.Get(PreflightFixture.TaskId)!;
        Assert.Equal(_f.Task.AttemptsUsed, task.AttemptsUsed);
        Assert.Equal(_f.Task.State, task.State);
        Assert.Null(_f.Ledger.Live(PreflightFixture.TaskId));
    }

    [Fact]
    public void Preflight_ReadyTask_Passes()
    {
        var result = Run();

        Assert.Empty(result.Reasons);
        Assert.True(result.Pass);
        AssertNothingCounted();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    public void Preflight_PromptMissing_PromptInvalid_NoAttempt(string fault)
    {
        if (fault == "missing")
        {
            File.Delete(_f.Repo.File("prompt.md"));
        }
        else
        {
            File.WriteAllText(_f.Repo.File("prompt.md"), "");
        }

        var result = Run();

        Assert.False(result.Pass);
        Assert.Equal(["PROMPT_INVALID"], result.Reasons);
        AssertNothingCounted();
    }

    [Theory]
    [InlineData("absolute")]
    [InlineData("traversal")]
    [InlineData("sibling")]
    public void Preflight_PromptOutsideWorktree_PromptInvalid(string shape)
    {
        var outside = _f.Outside(shape == "sibling" ? "work tree2\\prompt.md" : "prompt.md");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "outside");
        _f.Task = _f.Task with
        {
            PromptFile = shape == "traversal" ? Path.Combine(_f.Repo.Path, "..", "prompt.md") : outside,
        };

        Assert.Equal(["PROMPT_INVALID"], Run().Reasons);
    }

    [Fact]
    public void Preflight_PromptRelativeToWorktree_Passes()
    {
        _f.Task = _f.Task with { PromptFile = "prompt.md" };

        Assert.True(Run().Pass);
    }

    [Fact]
    public void Preflight_AttemptsUsedUp_AttemptsExhausted()
    {
        _f.Task = _f.Task with { AttemptsUsed = _f.Task.MaxAttempts };

        Assert.Equal(["ATTEMPTS_EXHAUSTED"], Run().Reasons);
    }

    [Fact]
    public void Preflight_AtDeadline_DeadlinePassed()
    {
        _f.Task = _f.Task with { DeadlineUtc = _f.Clock.UtcNow };

        Assert.Equal(["DEADLINE_PASSED"], Run().Reasons);
    }

    [Theory]
    [InlineData(TaskState.Running)]
    [InlineData(TaskState.Completed)]
    [InlineData(TaskState.Revoked)]
    public void Preflight_StateNotLaunchable_AdmissionState(TaskState state)
    {
        _f.Task = _f.Task with { State = state };

        Assert.Equal(["ADMISSION_STATE"], Run().Reasons);
    }

    [Theory]
    [InlineData(TaskState.Queued)]
    [InlineData(TaskState.WaitingForReset)]
    [InlineData(TaskState.ResumeOffered)]
    [InlineData(TaskState.NeedsAttention)]
    public void Preflight_LaunchableState_Passes(TaskState state)
    {
        _f.Task = _f.Task with { State = state };

        Assert.True(Run().Pass);
    }

    [Fact]
    public void Preflight_LiveOp_AdmissionStateInsteadOfThrow()
    {
        _f.EnsureAdded();
        _f.Ledger.BeginIntent(PreflightFixture.TaskId);

        Assert.Equal(["ADMISSION_STATE"], Run().Reasons);
    }

    [Fact]
    public void Preflight_NowFlag_WaivesEligibilityOnly()
    {
        _f.Task = _f.Task with
        {
            State = TaskState.WaitingForReset,
            AttemptPermittedAtEpoch = _f.Clock.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
        };

        Assert.Equal(["NOT_YET_ELIGIBLE"], Run().Reasons);
        Assert.True(Run(now: true).Pass);

        _f.Clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(_f.Task.AttemptPermittedAtEpoch!.Value);
        Assert.True(Run().Pass);
    }

    [Fact]
    public void Preflight_NowFlag_DoesNotWaiveAdmission()
    {
        _f.Task = _f.Task with
        {
            AttemptsUsed = _f.Task.MaxAttempts,
            AttemptPermittedAtEpoch = _f.Clock.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
        };

        Assert.Equal(["ATTEMPTS_EXHAUSTED"], Run(now: true).Reasons);
    }

    [Theory]
    [InlineData("--resume")]
    [InlineData("--session-id")]
    public void Preflight_ProcessWithSessionId_ProcessLive(string flag)
    {
        _f.Lister.Processes.Add(new ProcessInfo(7, 2, @"C:\other\node.exe",
            $"node.exe cli.js -p {flag} {_f.Task.SessionId.ToString("D").ToUpperInvariant()}"));

        Assert.Equal(["PROCESS_LIVE"], Run().Reasons);
    }

    [Theory]
    [InlineData("expected")]
    [InlineData("unexpected")]
    public void Preflight_ListerThrows_ProcessUnknown(string kind)
    {
        _f.Lister.Throw = kind == "expected"
            ? new ProcessListException("denied")
            : new InvalidOperationException("boom");

        Assert.Equal(["PROCESS_UNKNOWN"], Run().Reasons);
    }

    [Fact]
    public void Preflight_EmptyProcessList_ProcessUnknown()
    {
        _f.Lister.Processes.Clear();

        Assert.Equal(["PROCESS_UNKNOWN"], Run().Reasons);
    }

    [Theory]
    [InlineData("cli", "PROCESS_UNKNOWN")]
    [InlineData("unreadable", "PROCESS_UNKNOWN")]
    [InlineData("other", null)]
    public void Preflight_ProcessWithUnreadableCommandLine(string exe, string? reason)
    {
        var path = exe switch
        {
            "cli" => _f.Task.CliPath.ToUpperInvariant(),
            "other" => @"C:\Windows\System32\svchost.exe",
            _ => null,
        };
        _f.Lister.Processes.Add(new ProcessInfo(8, 3, path, null));

        Assert.Equal(reason is null ? [] : [reason], Run().Reasons);
    }

    private static void AssertCliChanged(PreflightResult result)
    {
        var reason = Assert.Single(result.Reasons);
        Assert.StartsWith("CLI_CHANGED: ", reason);
        Assert.Contains("ac pin-cli", reason);
    }

    [Fact]
    public void Preflight_CliHashChanged_CliChanged()
    {
        _f.Task = _f.Task with { CliSha256 = new string('b', 64) };

        AssertCliChanged(Run());
    }

    [Theory]
    [InlineData(0, "2.1.305 (Claude Code)", false)]
    [InlineData(0, "", false)]
    [InlineData(1, "2.1.304 (Claude Code)", false)]
    [InlineData(-1, "2.1.304 (Claude Code)", true)]
    public void Preflight_CliVersionNotPinned_CliChanged(int exitCode, string stdout, bool timedOut)
    {
        _f.Runner.Version = new CommandResult(exitCode, stdout, timedOut);

        AssertCliChanged(Run());
    }

    [Fact]
    public void Preflight_CliMissing_CliChanged()
    {
        _f.Task = _f.Task with { CliPath = _f.Outside("claude.exe") };

        AssertCliChanged(Run());
    }

    [Fact]
    public void Preflight_BaselineMatches_Passes()
    {
        _f.Task = _f.Task with { BaselineFingerprint = Baseline() };

        Assert.True(Run().Pass);
    }

    [Theory]
    [InlineData("prompt.md")]
    [InlineData("untracked.txt")]
    public void Preflight_WorkspaceDiffersFromBaseline_WorkspaceChanged(string file)
    {
        _f.Task = _f.Task with { BaselineFingerprint = Baseline() };
        _f.Repo.Write(file, "changed");

        Assert.Equal(["WORKSPACE_CHANGED"], Run().Reasons);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preflight_FingerprintFails_WorkspaceChanged(bool hasBaseline)
    {
        if (hasBaseline)
        {
            _f.Task = _f.Task with { BaselineFingerprint = Baseline() };
        }
        _f.Repo.Git("checkout", "-q", "--orphan", "unborn");

        var reason = Assert.Single(Run().Reasons);
        Assert.StartsWith("WORKSPACE_CHANGED: ", reason);
    }

    [Fact]
    public void Preflight_WorkspaceOverLimit_WorkspaceTooLarge()
    {
        _f.Repo.Write("second.txt", "two");

        Assert.Equal(["WORKSPACE_TOO_LARGE"], Run(host: Host() with { FingerprintMaxFiles = 1 }).Reasons);
    }

    [Fact]
    public void Preflight_CollectsAllReasons()
    {
        _f.Task = _f.Task with { AttemptsUsed = _f.Task.MaxAttempts, BaselineFingerprint = Baseline() };
        _f.Repo.Write("prompt.md", "changed");

        var result = Run();

        Assert.False(result.Pass);
        Assert.Equal(["ATTEMPTS_EXHAUSTED", "WORKSPACE_CHANGED"], result.Reasons);
    }

    [Fact]
    public void Preflight_UnknownTask_AdmissionState()
    {
        var result = new PreflightRunner(_f.Store, _f.Lister, _f.Runner, _f.Billing, _f.Clock, Host()).Run("nope", false);

        Assert.Equal(["ADMISSION_STATE"], result.Reasons);
    }
}
