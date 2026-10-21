using AgentContinuum.Core.Platform;

namespace AgentContinuum.Windows.Tests;

public sealed class WmiProcessListerTests : IDisposable
{
    private readonly FakeClaudeLaunch _launch = new();

    public void Dispose() => _launch.Dispose();

    private IHostedRun StartLingering()
    {
        _launch.Scenario("""{ "lingerSeconds": 30 }""");
        return new JobProcessHost().Start(_launch.Spec());
    }

    [Fact]
    public void Lister_SeesFakeClaudeWithSessionId()
    {
        using var run = StartLingering();

        var processes = new WmiProcessLister(() => [FakeClaudeLaunch.ExePath]).ListOwnProcesses();

        var fake = Assert.Single(processes, p => p.Pid == run.Pid);
        Assert.Contains(_launch.SessionId.ToString("D"), fake.CommandLine);
        Assert.Equal(FakeClaudeLaunch.ExePath, fake.ExecutablePath, ignoreCase: true);
        // WMI reports creation time to the microsecond, which is ten FILETIME ticks.
        Assert.InRange(fake.CreatedAtFileTime, run.CreatedAtFileTime - 10, run.CreatedAtFileTime + 10);
        Assert.Contains(processes, p => p.Pid == Environment.ProcessId);
        Assert.DoesNotContain(processes, p => p.Pid == 4);
    }

    [Fact]
    public void Lister_PinnedCliWithUnreadableOwner_KeptAsUnknown()
    {
        using var run = StartLingering();

        var processes = new WmiProcessLister(() => [FakeClaudeLaunch.ExePath], ownerOf: _ => null).ListOwnProcesses();

        var fake = Assert.Single(processes, p => p.Pid == run.Pid);
        Assert.Null(fake.ExecutablePath);
        Assert.Null(fake.CommandLine);
        Assert.DoesNotContain(processes, p => p.Pid == Environment.ProcessId);
    }
}
