using System.Text.Json;
using AgentContinuum.Core.Billing;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Tests.Billing;

public sealed class SettingsScannerTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly string _userProfile;
    private readonly string _worktree;
    private readonly string _managed;
    private readonly ClaudeRules _rules = VersionRules.LoadEmbedded().Entries[0];

    public SettingsScannerTests()
    {
        _userProfile = Directory.CreateDirectory(_dir.File("profile")).FullName;
        _worktree = Directory.CreateDirectory(_dir.File("work tree")).FullName;
        _managed = _dir.File("managed-settings.json");
    }

    public void Dispose() => _dir.Dispose();

    private void Write(string scope, string json)
    {
        var path = scope switch
        {
            "managed" => _managed,
            "user" => Path.Combine(_userProfile, ".claude", "settings.json"),
            "user-local" => Path.Combine(_userProfile, ".claude", "settings.local.json"),
            "project" => Path.Combine(_worktree, ".claude", "settings.json"),
            "project-local" => Path.Combine(_worktree, ".claude", "settings.local.json"),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private ProofResult Scan(ClaudeRules? rules = null) => SettingsScanner.Scan(_userProfile, _worktree, _managed, rules ?? _rules);

    [Fact]
    public void Scan_MissingFiles_Pass()
    {
        var result = Scan();

        Assert.True(result.Pass);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Scan_CleanFiles_Pass()
    {
        foreach (var scope in new[] { "managed", "user", "user-local", "project", "project-local" })
        {
            Write(scope, """{"theme":"dark","permissions":{"defaultMode":"acceptEdits"},"env":{"ANTHROPIC_MODEL":"x"}}""");
        }

        Assert.True(Scan().Pass);
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("user")]
    [InlineData("user-local")]
    [InlineData("project")]
    [InlineData("project-local")]
    public void Scan_ApiKeyHelperInUserSettings_Fails(string scope)
    {
        Write(scope, """{"apiKeyHelper":"C:/tools/SENTINEL-p2.cmd"}""");

        var result = Scan();

        Assert.False(result.Pass);
        Assert.Equal([$"{scope}: apiKeyHelper"], result.Problems);
        Assert.DoesNotContain("SENTINEL-p2", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("{\"apiKeyHelper\": ")]
    [InlineData("[\"apiKeyHelper\"]")]
    [InlineData("")]
    [InlineData("{\"env\":{}} trailing")]
    public void Scan_UnparseableFile_Fails(string content)
    {
        Write("user", content);

        var result = Scan();

        Assert.False(result.Pass);
        Assert.Equal(["user: unreadable"], result.Problems);
    }

    [Fact]
    public void Scan_RuleExtraKeys_Fail()
    {
        var rules = _rules with { P2ExtraKeys = ["awsAuthRefresh", "provider.baseUrl", "env.CLAUDE_CODE_USE_FOUNDRY"] };
        Write("user", """{"awsAuthRefresh":"x","provider":{"baseUrl":"y","name":"z"},"env":{"CLAUDE_CODE_USE_FOUNDRY":"1"}}""");
        Write("project", """{"provider":{"name":"z"},"awsAuthRefreshExtra":"x"}""");

        var result = Scan(rules);

        Assert.Equal(["user: awsAuthRefresh", "user: provider.baseUrl", "user: env.CLAUDE_CODE_USE_FOUNDRY"], result.Problems);
    }

    [Fact]
    public void Scan_RuleExtraEnvKey_MatchesAnyCase()
    {
        var rules = _rules with { P2ExtraKeys = ["env.AWS_BEARER_TOKEN_BEDROCK", "awsAuthRefresh"] };
        Write("project-local", """{"env":{"aws_bearer_token_bedrock":"SENTINEL-p2"},"AWSAUTHREFRESH":"x"}""");

        var result = Scan(rules);

        Assert.Equal(["project-local: env.AWS_BEARER_TOKEN_BEDROCK"], result.Problems);
        Assert.DoesNotContain("SENTINEL-p2", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Scan_LockedFile_Fails()
    {
        Write("project-local", "{}");
        var path = Path.Combine(_worktree, ".claude", "settings.local.json");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(["project-local: unreadable"], Scan().Problems);
    }

    [Fact]
    public void Scan_DirectoryInPlaceOfFile_Fails()
    {
        Directory.CreateDirectory(Path.Combine(_userProfile, ".claude", "settings.json"));

        Assert.Equal(["user: unreadable"], Scan().Problems);
    }

    [Theory]
    [InlineData("ANTHROPIC_BASE_URL")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("ANTHROPIC_AUTH_TOKEN")]
    [InlineData("CLAUDE_CODE_USE_BEDROCK")]
    [InlineData("CLAUDE_CODE_USE_VERTEX")]
    [InlineData("anthropic_base_url")]
    public void Scan_EnvBaseUrlInProjectSettings_Fails(string name)
    {
        Write("project", $$$"""{"env":{"{{{name}}}":"SENTINEL-p2"}}""");

        var result = Scan();

        Assert.False(result.Pass);
        Assert.Equal([$"project: env.{name.ToUpperInvariant()}"], result.Problems);
        Assert.DoesNotContain("SENTINEL-p2", JsonSerializer.Serialize(result));
    }
}
