using AgentContinuum.Core.Model;
using AgentContinuum.Core.Preflight;

namespace AgentContinuum.Core.Tests.Preflight;

public sealed class PreflightRunnerTests : IDisposable
{
    private readonly PreflightFixture _f = new();

    public void Dispose() => _f.Dispose();

    private PreflightResult Run(bool now = false)
    {
        _f.EnsureAdded();
        var host = new PreflightHost(GitRepo.GitExe, _f.ParentEnv, _f.UserProfile, _f.ManagedSettingsPaths);
        return new PreflightRunner(_f.Store, _f.Lister, _f.Runner, _f.Billing, _f.Clock, host).Run(PreflightFixture.TaskId, now);
    }

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

    [Fact]
    public void Preflight_UnknownTask_AdmissionState()
    {
        var host = new PreflightHost(GitRepo.GitExe, _f.ParentEnv, _f.UserProfile, _f.ManagedSettingsPaths);

        var result = new PreflightRunner(_f.Store, _f.Lister, _f.Runner, _f.Billing, _f.Clock, host).Run("nope", false);

        Assert.Equal(["ADMISSION_STATE"], result.Reasons);
    }
}
