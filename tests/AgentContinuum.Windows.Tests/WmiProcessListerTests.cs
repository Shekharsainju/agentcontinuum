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

    private static readonly System.Security.Principal.SecurityIdentifier User = new("S-1-5-21-1-2-3-1001");

    private static readonly HashSet<string> CliNames = new(["claude.exe"], StringComparer.OrdinalIgnoreCase);

    // Elevated and protected processes of your own, such as ctfmon or an elevated Task Manager, show WMI no path
    // or command line, and treating each as a possible session would refuse every launch.
    [Fact]
    public void Row_OwnUnreadableNonCli_Dropped()
    {
        var row = new WmiProcessLister.Row(42, "ctfmon.exe", 7, null, null);

        Assert.Null(WmiProcessLister.Own(row, User, User, CliNames));
    }

    [Fact]
    public void Row_OwnUnreadableCli_KeptAsUnknown()
    {
        var row = new WmiProcessLister.Row(42, "Claude.exe", 7, null, null);

        Assert.Equal(new ProcessInfo(42, 7, null, null), WmiProcessLister.Own(row, User, User, CliNames));
    }

    [Fact]
    public void Row_OwnReadable_KeptWhateverItsName()
    {
        var row = new WmiProcessLister.Row(42, "node.exe", 7, @"C:\node.exe", "node --session 1");

        Assert.Equal(new ProcessInfo(42, 7, @"C:\node.exe", "node --session 1"), WmiProcessLister.Own(row, User, User, CliNames));
    }

    [Fact]
    public void Row_OtherUser_Dropped()
    {
        var row = new WmiProcessLister.Row(42, "claude.exe", 7, @"C:\claude.exe", "claude");

        Assert.Null(WmiProcessLister.Own(row, new("S-1-5-21-1-2-3-1002"), User, CliNames));
    }

    [Fact]
    public void Lister_StalledListing_FailsInsteadOfHanging()
    {
        using var stall = new ManualResetEventSlim();
        var lister = new WmiProcessLister(() => [], timeout: TimeSpan.FromMilliseconds(200), ownerOf: _ =>
        {
            stall.Wait();
            return null;
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            Assert.Throws<ProcessListException>(lister.ListOwnProcesses);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        }
        finally
        {
            stall.Set();
        }
    }
}
