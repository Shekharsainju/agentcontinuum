using System.Security.Cryptography;
using System.Text;
using AgentContinuum.Cli;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Rules;

namespace AgentContinuum.AcTestHost;

public static class TestServices
{
    public static AppServices Build(TestHostConfig config, string[] args)
    {
        // Set before the real services are built, so the state paths and the environment every child inherits
        // already name the test folders.
        Environment.SetEnvironmentVariable("LOCALAPPDATA", config.LocalAppData);
        Environment.SetEnvironmentVariable("USERPROFILE", config.UserProfile);
        var real = AppServices.ForCurrentUser();

        var clock = new OffsetClock(config.ClockOffsetFile);
        var rules = config.RulesFile is { } rulesFile ? VersionRules.Load(File.ReadAllText(rulesFile)) : VersionRules.LoadEmbedded();
        // Only the daemon launches, so commands that set a task up run whole whatever the crash point.
        var crash = args is ["daemon"] ? config.CrashPoint : CrashPoint.None;
        var preflight = real.Preflight with
        {
            UserProfile = config.UserProfile,
            ManagedSettingsPaths = config.ManagedSettingsPaths ?? [Path.Combine(config.LocalAppData, "managed", "managed-settings.json")],
            GitExe = crash == CrashPoint.AfterExit ? Environment.ProcessPath! : real.Preflight.GitExe,
        };
        return real with
        {
            Clock = clock,
            Log = new FileLog(real.Paths.LogFile, clock),
            Notifier = new CaptureNotifier(config.NotificationsFile, config.RealToasts ? real.Notifier : null),
            Host = new CrashingHost(real.Host, crash),
            Runner = new CrashingRunner(real.Runner, crash),
            Preflight = preflight,
            RulesFor = rules.Match,
            DaemonLockName = config.LockName ?? DefaultLockName(config.LocalAppData),
            TickInterval = TimeSpan.FromSeconds(config.TickIntervalSeconds),
        };
    }

    // One lock per state folder, so test daemons run side by side and never collide with your own daemon.
    private static string DefaultLockName(string localAppData)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localAppData)).ToUpperInvariant();
        return $@"Local\AgentContinuum.Test.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]}";
    }
}
