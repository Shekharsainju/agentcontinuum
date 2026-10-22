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

    // Pre-flight and the launch that follows it must judge the run by the same rules.
    [Fact]
    public void Preflight_BillingUsesTheRulesItWasGiven()
    {
        _f.EnsureAdded();
        var withoutP5 = Rules.VersionRules.For("2.1.304")! with { P5 = null };
        var runner = new PreflightRunner(_f.Store, _f.Lister, _f.Runner, _f.Billing, _f.Clock, Host(), _ => withoutP5);

        var result = runner.Run(PreflightFixture.TaskId, false);

        Assert.Equal([$"{PreflightReasons.BillingUnverified}: P5_UNEVALUABLE"], result.Reasons);
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
        var reason = Assert.Single(result.Reasons, r => r.StartsWith("CLI_CHANGED", StringComparison.Ordinal));
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

    private const string BypassSettings = """{"permissions":{"defaultMode":"bypassPermissions"}}""";

    [Theory]
    [InlineData("project")]
    [InlineData("project-local")]
    [InlineData("user")]
    [InlineData("managed")]
    public void Preflight_BypassInProjectSettings_PermissionUnsafe(string scope)
    {
        var path = scope switch
        {
            "project" => _f.Repo.File(@".claude\settings.json"),
            "project-local" => _f.Repo.File(@".claude\settings.local.json"),
            "user" => Path.Combine(_f.UserProfile, ".claude", "settings.json"),
            _ => _f.ManagedSettingsPaths[1],
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, BypassSettings);

        Assert.Equal([$"PERMISSION_UNSAFE: {scope} sets bypassPermissions"], Run().Reasons);
    }

    [Fact]
    public void Preflight_BypassAnywhereInSettings_PermissionUnsafe()
    {
        _f.Repo.Write(@".claude\settings.json", """{"permissions":{"allow":[],"other":["BypassPermissions"]}}""");

        Assert.Equal(["PERMISSION_UNSAFE: project sets bypassPermissions"], Run().Reasons);
    }

    [Fact]
    public void Preflight_SettingsWithoutBypass_Passes()
    {
        _f.Repo.Write(@".claude\settings.json", """{"permissions":{"defaultMode":"acceptEdits","disableBypassPermissionsMode":"disable"}}""");

        Assert.True(Run().Pass);
    }

    [Fact]
    public void Preflight_UnreadableSettings_PermissionUnsafe()
    {
        _f.Repo.Write(@".claude\settings.json", "{");

        Assert.Contains("PERMISSION_UNSAFE: project unreadable", Run().Reasons);
    }

    [Theory]
    [InlineData("bypassPermissions")]
    [InlineData("dontAsk")]
    [InlineData("Default")]
    [InlineData("")]
    public void Preflight_PolicyModeNotAllowed_PermissionUnsafe(string mode)
    {
        _f.Task = _f.Task with { PermissionMode = mode };

        Assert.Equal(["PERMISSION_UNSAFE: policy mode not allowed"], Run().Reasons);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("acceptEdits")]
    [InlineData("plan")]
    public void Preflight_AllowedPolicyMode_Passes(string mode)
    {
        _f.Task = _f.Task with { PermissionMode = mode };

        Assert.True(Run().Pass);
    }

    [Theory]
    [InlineData(true, false, "BILLING_UNVERIFIED: P2")]
    [InlineData(false, true, "BILLING_UNVERIFIED: P4")]
    [InlineData(true, true, "BILLING_UNVERIFIED: P2, P4")]
    public void Preflight_BillingProofFails_BillingUnverifiedWithProofIds(bool settingsKey, bool authFails, string expected)
    {
        if (settingsKey)
        {
            _f.Repo.Write(@".claude\settings.json", """{"env":{"ANTHROPIC_API_KEY":"SENTINEL-p2"}}""");
        }
        if (authFails)
        {
            _f.Runner.AuthStatus = new CommandResult(1, "", false);
        }

        var result = Run();

        Assert.Equal([expected], result.Reasons);
        Assert.DoesNotContain(result.Reasons, r => r.Contains("SENTINEL", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("missing")]
    public void Preflight_CliNotPinned_AuthStatusNotRun(string fault)
    {
        _f.Task = fault == "hash"
            ? _f.Task with { CliSha256 = new string('b', 64) }
            : _f.Task with { CliPath = _f.Outside("claude.exe") };

        var result = Run();

        Assert.Contains("BILLING_UNVERIFIED: P4_UNEVALUABLE", result.Reasons);
        Assert.Empty(_f.Runner.Calls);
    }

    [Fact]
    public void Preflight_NoChildEnvironment_BillingUnverified_NothingRun()
    {
        _f.ParentEnv.Remove("SystemRoot");

        var result = Run();

        Assert.Contains("BILLING_UNVERIFIED: P1, P4_UNEVALUABLE", result.Reasons);
        Assert.Empty(_f.Runner.Calls);
    }

    private void Sql(string sql)
    {
        using var cmd = _f.Store.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Theory]
    [InlineData("UPDATE meta SET value = '2' WHERE key = 'schema_version'")]
    [InlineData("DELETE FROM meta WHERE key = 'schema_version'")]
    public void Preflight_SchemaVersionDiffers_ControllerState(string sql)
    {
        Sql(sql);

        Assert.Equal(["CONTROLLER_STATE"], Run().Reasons);
    }

    [Fact]
    public void Preflight_IntegrityCheckFails_ControllerState()
    {
        // integrity_check verifies CHECK constraints, so a row written past one fails it while every page still reads.
        Sql("""
            CREATE TABLE probe (x INTEGER CHECK (x > 0));
            PRAGMA ignore_check_constraints = ON;
            INSERT INTO probe VALUES (-1);
            PRAGMA ignore_check_constraints = OFF;
            """);
        Assert.False(_f.Store.IntegrityOk());

        Assert.Equal(["CONTROLLER_STATE"], Run().Reasons);
    }

    [Fact]
    public void Preflight_UnknownTask_AdmissionState()
    {
        var result = new PreflightRunner(_f.Store, _f.Lister, _f.Runner, _f.Billing, _f.Clock, Host()).Run("nope", false);

        Assert.Equal(["ADMISSION_STATE"], result.Reasons);
    }
}
