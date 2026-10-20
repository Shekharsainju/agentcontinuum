using AgentContinuum.Core.Billing;

namespace AgentContinuum.Core.Tests.Billing;

public sealed class ChildEnvironmentTests
{
    private const string CliDir = @"C:\Users\me\.local\bin";
    private const string GitDir = @"C:\Program Files\Git\cmd";

    private static readonly string[] Allowlist =
    [
        "SystemRoot", "windir", "ComSpec", "PATHEXT", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "HOMEDRIVE", "HOMEPATH", "USERNAME", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS",
    ];

    private static Dictionary<string, string> Parent()
    {
        var parent = Allowlist.ToDictionary(name => name, name => $"value-of-{name}", StringComparer.OrdinalIgnoreCase);
        parent["SystemRoot"] = @"C:\Windows";
        parent["Path"] = @"C:\evil;C:\Windows\System32";
        parent["ANTHROPIC_API_KEY"] = "sk-ant-secret";
        parent["ANTHROPIC_BASE_URL"] = "https://proxy.example";
        parent["CLAUDE_CODE_USE_BEDROCK"] = "1";
        parent["NODE_OPTIONS"] = "--require evil.js";
        return parent;
    }

    [Fact]
    public void Env_DropsAnthropicVariables()
    {
        var env = ChildEnvironment.Build(Parent(), CliDir, GitDir);

        Assert.DoesNotContain(env.Keys, k => k.Equals("ANTHROPIC_API_KEY", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(env.Keys, k => k.Equals("ANTHROPIC_BASE_URL", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(env.Keys, k => k.Equals("CLAUDE_CODE_USE_BEDROCK", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("sk-ant-secret", env.Values);
    }

    [Fact]
    public void Env_ContainsOnlyAllowlist()
    {
        var env = ChildEnvironment.Build(Parent(), CliDir, GitDir);

        Assert.Equal(Allowlist.Append("PATH").Order(StringComparer.Ordinal), env.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("value-of-TEMP", env["TEMP"]);
        Assert.Equal($@"C:\Windows\System32;C:\Windows;{CliDir};{GitDir}", env["PATH"]);
    }

    [Fact]
    public void Env_MissingAllowlistedVariable_Omitted()
    {
        var parent = Parent();
        parent.Remove("TMP");

        var env = ChildEnvironment.Build(parent, CliDir, GitDir);

        Assert.False(env.ContainsKey("TMP"));
    }

    [Fact]
    public void Env_NoSystemRoot_Throws()
    {
        var parent = Parent();
        parent.Remove("SystemRoot");

        Assert.Throws<InvalidOperationException>(() => ChildEnvironment.Build(parent, CliDir, GitDir));
    }

    [Fact]
    public void IsAllowlisted_RejectsForeignNames()
    {
        var env = new Dictionary<string, string>(ChildEnvironment.Build(Parent(), CliDir, GitDir)) { ["ANTHROPIC_API_KEY"] = "x" };

        Assert.True(ChildEnvironment.IsAllowlisted(ChildEnvironment.Build(Parent(), CliDir, GitDir)));
        Assert.False(ChildEnvironment.IsAllowlisted(env));
    }
}
