using AgentContinuum.Core.Billing;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Rules;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Tests.Billing;

public sealed class BillingProofsTests : IDisposable
{
    private const string SubscriptionStatus =
        """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"SENTINEL-p4@example.com","subscriptionType":"pro"}""";

    private readonly TempDirectory _dir = new();
    private readonly StateStore _store;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeRunner _runner = new();
    private readonly string _userProfile;
    private readonly string _worktree;
    private readonly TaskRecord _task;
    private ClaudeRules? _rules = VersionRules.LoadEmbedded().Entries[0];
    private Dictionary<string, string> _childEnv;

    public BillingProofsTests()
    {
        _store = StateStore.Open(_dir.File("ac.db"));
        _userProfile = Directory.CreateDirectory(_dir.File("profile")).FullName;
        _worktree = Directory.CreateDirectory(_dir.File("work tree")).FullName;
        _task = TestTasks.Sample("t1") with { Worktree = _worktree };
        _childEnv = new Dictionary<string, string>(ChildEnvironment.Build(
            new Dictionary<string, string> { ["SystemRoot"] = @"C:\Windows", ["USERPROFILE"] = _userProfile },
            @"C:\cli",
            @"C:\git"));
        _runner.Result = new CommandResult(0, SubscriptionStatus, false);
        WriteProfileFile(CredentialsFile, Credentials);
        WriteProfileFile(".claude.json", """{"oauthAccount":{"emailAddress":"SENTINEL-p3@example.com"},"theme":"dark"}""");
        new Attestations(_store).Record(Attestations.OverageOff, _clock.UtcNow.AddDays(-1));
    }

    private const string Credentials =
        """{"claudeAiOauth":{"accessToken":"SENTINEL-p3","refreshToken":"SENTINEL-p3","subscriptionType":"SENTINEL-p3"}}""";

    private static readonly string CredentialsFile = Path.Combine(".claude", ".credentials.json");

    private ProofResult CheckP3() => CredentialCheck.Check(_userProfile, _rules!.P3!);

    public void Dispose()
    {
        _store.Dispose();
        _dir.Dispose();
    }

    private BillingVerdict Evaluate() =>
        new BillingProofs(_runner, _clock, _store).Evaluate(new BillingContext(_task, _rules, _childEnv, _userProfile, null));

    private void WriteProfileFile(string relative, string json)
    {
        var path = Path.Combine(_userProfile, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private void Break(string proof)
    {
        switch (proof)
        {
            case "P1":
                _childEnv["ANTHROPIC_API_KEY"] = "sk-ant-x";
                break;
            case "P2":
                WriteProfileFile(Path.Combine(".claude", "settings.json"), """{"apiKeyHelper":"x"}""");
                break;
            case "P3":
                File.Delete(Path.Combine(_userProfile, CredentialsFile));
                break;
            case "P3_RULE":
                _rules = _rules! with { P3 = null };
                break;
            case "P4":
                _runner.Result = new CommandResult(0, ApiKeyStatus, false);
                break;
            case "P4_RULE":
                _rules = _rules! with { P4 = null };
                break;
            case "P5_RULE":
                _rules = _rules! with { P5 = null };
                break;
            case "P6":
                _clock.UtcNow = _clock.UtcNow.AddDays(31);
                break;
        }
    }

    private const string ApiKeyStatus =
        """{"loggedIn":true,"authMethod":"api_key","apiProvider":"firstParty","apiKeySource":"ANTHROPIC_API_KEY"}""";

    [Fact]
    public void P4_SubscriptionValue_Passes()
    {
        var verdict = Evaluate();

        Assert.True(verdict.Verified);
        var call = Assert.Single(_runner.Calls);
        Assert.Equal(_task.CliPath, call.Exe);
        Assert.Equal(["auth", "status"], call.Args);
        Assert.Same(_childEnv, call.Env);
        Assert.Equal(TimeSpan.FromSeconds(15), call.Timeout);
    }

    [Fact]
    public void P4_Timeout_Fails()
    {
        _runner.Result = new CommandResult(-1, SubscriptionStatus, true);

        Assert.Equal(["P4"], Evaluate().FailedProofs);
    }

    [Theory]
    [InlineData(1, SubscriptionStatus)]
    [InlineData(0, "not json")]
    [InlineData(0, "[]")]
    [InlineData(0, """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty"}""")]
    [InlineData(0, """{"loggedIn":"true","authMethod":"claude.ai","apiProvider":"firstParty","subscriptionType":"pro"}""")]
    [InlineData(0, """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"bedrock","subscriptionType":"pro"}""")]
    public void P4_NotASubscription_Fails(int exitCode, string stdout)
    {
        _runner.Result = new CommandResult(exitCode, stdout, false);

        Assert.Equal(["P4"], Evaluate().FailedProofs);
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    [InlineData(31, false)]
    [InlineData(-1, false)]
    public void P6_AttestationAge(int daysOld, bool passes)
    {
        _clock.UtcNow = _clock.UtcNow.AddDays(daysOld - 1);

        Assert.Equal(passes, !Evaluate().FailedProofs.Contains("P6"));
    }

    [Fact]
    public void P6_31DaysOld_Fails()
    {
        _clock.UtcNow = _clock.UtcNow.AddDays(30);

        Assert.Equal(["P6"], Evaluate().FailedProofs);
    }

    [Fact]
    public void P6_NoAttestation_Fails()
    {
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM attestations";
        cmd.ExecuteNonQuery();

        Assert.Equal(["P6"], Evaluate().FailedProofs);
    }

    [Fact]
    public void NullRules_P3P4P5Unevaluable()
    {
        _rules = null;

        var verdict = Evaluate();

        Assert.False(verdict.Verified);
        Assert.Equal(["P3_UNEVALUABLE", "P4_UNEVALUABLE", "P5_UNEVALUABLE"], verdict.FailedProofs);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public void P4_RunnerThrows_Fails()
    {
        _runner.Throw = true;

        Assert.Equal(["P4"], Evaluate().FailedProofs);
    }

    [Fact]
    public void P3_KeyNamesOnly()
    {
        var pass = CheckP3();
        WriteProfileFile(CredentialsFile, """{"claudeAiOauth":{"accessToken":"SENTINEL-p3"}}""");
        var fail = CheckP3();

        Assert.True(pass.Pass);
        Assert.False(fail.Pass);
        Assert.Equal(["credentials: no claudeAiOauth.subscriptionType"], fail.Problems);
        Assert.DoesNotContain("SENTINEL-p3", System.Text.Json.JsonSerializer.Serialize(new[] { pass, fail }));
    }

    [Fact]
    public void P3_ApiKeyStored_Fails()
    {
        WriteProfileFile(".claude.json", """{"oauthAccount":{},"primaryApiKey":"SENTINEL-p3"}""");

        Assert.Equal(["api key file: primaryApiKey"], CheckP3().Problems);
    }

    [Theory]
    [InlineData(null, "credentials: missing")]
    [InlineData("not json", "credentials: unreadable")]
    [InlineData("[]", "credentials: unreadable")]
    public void P3_CredentialsMissingOrUnreadable_Fails(string? content, string problem)
    {
        File.Delete(Path.Combine(_userProfile, CredentialsFile));
        if (content is not null)
        {
            WriteProfileFile(CredentialsFile, content);
        }

        Assert.Equal([problem], CheckP3().Problems);
    }

    [Fact]
    public void P3_ApiKeyFileUnreadable_Fails()
    {
        WriteProfileFile(".claude.json", "{");

        Assert.Equal(["api key file: unreadable"], CheckP3().Problems);
    }

    [Fact]
    public void AllProofsPass_Verified()
    {
        var verdict = Evaluate();

        Assert.True(verdict.Verified, string.Join(",", verdict.FailedProofs));
        Assert.Empty(verdict.FailedProofs);
    }

    [Theory]
    [InlineData("P1", "P1")]
    [InlineData("P2", "P2")]
    [InlineData("P3", "P3")]
    [InlineData("P3_RULE", "P3_UNEVALUABLE")]
    [InlineData("P4", "P4")]
    [InlineData("P4_RULE", "P4_UNEVALUABLE")]
    [InlineData("P5_RULE", "P5_UNEVALUABLE")]
    [InlineData("P6", "P6")]
    public void EachProofAloneBlocks(string proof, string failed)
    {
        Break(proof);

        var verdict = Evaluate();

        Assert.False(verdict.Verified);
        Assert.Equal([failed], verdict.FailedProofs);
    }

    private sealed class FakeRunner : ICommandRunner
    {
        public CommandResult Result { get; set; } = new(1, "", false);

        public bool Throw { get; set; }

        public List<(string Exe, string[] Args, IReadOnlyDictionary<string, string> Env, TimeSpan Timeout)> Calls { get; } = [];

        public CommandResult Run(string exe, string[] args, IReadOnlyDictionary<string, string> env, TimeSpan timeout)
        {
            Calls.Add((exe, args, env, timeout));
            return Throw ? throw new System.ComponentModel.Win32Exception(2) : Result;
        }
    }
}
