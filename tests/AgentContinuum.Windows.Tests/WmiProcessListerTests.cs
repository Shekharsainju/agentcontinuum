namespace AgentContinuum.Windows.Tests;

public sealed class WmiProcessListerTests : IDisposable
{
    private readonly FakeClaudeLaunch _launch = new();

    public void Dispose() => _launch.Dispose();

    [Fact]
    public void Lister_SeesFakeClaudeWithSessionId()
    {
        _launch.Scenario("""{ "lingerSeconds": 30 }""");
        using var run = new JobProcessHost().Start(_launch.Spec());

        var processes = new WmiProcessLister().ListOwnProcesses();

        var fake = Assert.Single(processes, p => p.Pid == run.Pid);
        Assert.Contains(_launch.SessionId.ToString("D"), fake.CommandLine);
        Assert.Equal(FakeClaudeLaunch.ExePath, fake.ExecutablePath, ignoreCase: true);
        // WMI reports creation time to the microsecond, which is ten FILETIME ticks.
        Assert.InRange(fake.CreatedAtFileTime, run.CreatedAtFileTime - 10, run.CreatedAtFileTime + 10);
        Assert.Contains(processes, p => p.Pid == Environment.ProcessId);
        Assert.DoesNotContain(processes, p => p.Pid == 4);
    }
}
