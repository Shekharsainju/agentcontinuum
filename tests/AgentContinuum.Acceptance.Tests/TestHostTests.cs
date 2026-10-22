using System.Text.Json.Nodes;
using AgentContinuum.Core.Tests;

namespace AgentContinuum.Acceptance.Tests;

/// <summary>The test host must never run against your own state.</summary>
public sealed class TestHostTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void NoConfig_ExitsWithoutTouchingState()
    {
        var (exit, _) = AcTestProcess.Run(_dir.Path, "status");

        Assert.NotEqual(0, exit);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir.Path));
    }

    [Fact]
    public void NoConfig_HookStillExits0Silently()
    {
        var (exit, stdout) = AcTestProcess.Run(_dir.Path, "hook", "stop-failure", "--op", "0123456789abcdef0123456789abcdef");

        Assert.Equal((0, ""), (exit, stdout));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir.Path));
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.LocalApplicationData, "localAppData")]
    [InlineData(Environment.SpecialFolder.UserProfile, "userProfile")]
    public void ConfigNamingYourOwnFolder_Refused(Environment.SpecialFolder folder, string field)
    {
        var real = Environment.GetFolderPath(folder);
        var config = new JsonObject
        {
            ["localAppData"] = _dir.Path,
            ["userProfile"] = _dir.File("profile"),
        };
        config[field] = real;
        var configDir = Directory.CreateDirectory(_dir.File("AgentContinuum")).FullName;
        File.WriteAllText(Path.Combine(configDir, "ac-test.json"), config.ToJsonString());
        var stateExisted = Directory.Exists(Path.Combine(real, "AgentContinuum", "state"));

        var (exit, _) = AcTestProcess.Run(_dir.Path, "status");

        Assert.NotEqual(0, exit);
        Assert.Equal(["ac-test.json"], Directory.EnumerateFileSystemEntries(configDir).Select(Path.GetFileName));
        Assert.Equal(stateExisted, Directory.Exists(Path.Combine(real, "AgentContinuum", "state")));
    }
}
