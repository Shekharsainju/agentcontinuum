using System.Text.Json;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.Core.Tests.Rules;

public sealed class VersionRulesTests
{
    [Fact]
    public void LoadEmbedded_Parses2_1_304Entry()
    {
        var entry = Assert.Single(VersionRules.LoadEmbedded().Entries);

        Assert.Equal(">=2.1.304 <2.1.305", entry.Versions);
        Assert.Empty(entry.P2ExtraKeys);
        Assert.Equal(".claude/.credentials.json", entry.P3!.File);
        Assert.Equal(["primaryApiKey"], entry.P3.ApiKeyKeys);
        Assert.Equal(["auth", "status"], entry.P4!.Args);
        Assert.Equal("authMethod", entry.P4.Require[1].Path);
        Assert.Equal("claude.ai", entry.P4.Require[1].Values[0].GetString());
        Assert.Equal(JsonValueKind.True, entry.P4.Require[0].Values[0].ValueKind);
        Assert.Equal(["subscriptionType"], entry.P4.RequirePresent);
        Assert.Equal(("system", "init", "apiKeySource"), (entry.P5!.Type, entry.P5.Subtype, entry.P5.Field));
        Assert.Equal(["none"], entry.P5.SubscriptionValues);
        Assert.Equal("result", entry.Result!.Type);
        Assert.Equal(["is_error", "terminal_reason"], entry.Result.SuccessWhen.Select(r => r.Path));
        Assert.Equal("rate_limit_info.resetsAt", entry.Usage!.ResetField);
        Assert.Equal("unix_seconds", entry.Usage.ResetFormat);
        Assert.Equal(2, entry.Usage.UtilizationFields.Length);
        Assert.Equal("rate_limit_info.isUsingOverage", entry.Usage.Overage.InUseField);
        Assert.Equal(JsonValueKind.False, entry.Usage.Overage.SafeInUse[0].ValueKind);
        Assert.Null(entry.Limit);
    }
}
