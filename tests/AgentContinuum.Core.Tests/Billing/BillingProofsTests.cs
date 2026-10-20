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
        }
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

        public List<(string Exe, string[] Args, IReadOnlyDictionary<string, string> Env, TimeSpan Timeout)> Calls { get; } = [];

        public CommandResult Run(string exe, string[] args, IReadOnlyDictionary<string, string> env, TimeSpan timeout)
        {
            Calls.Add((exe, args, env, timeout));
            return Result;
        }
    }
}
