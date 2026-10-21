using System.Collections;
using AgentContinuum.Core.Billing;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.Preflight;
using AgentContinuum.Core.Rules;
using AgentContinuum.Core.State;
using AgentContinuum.Windows;

namespace AgentContinuum.Cli;

/// <summary>Everything a command touches outside its own logic, so tests can replace each part.</summary>
public sealed record AppServices(
    AppPaths Paths,
    IClock Clock,
    ILog Log,
    INotifier Notifier,
    IProcessHost Host,
    IProcessLister Lister,
    ICommandRunner Runner,
    PreflightHost Preflight,
    string AcExe,
    TextWriter Out,
    Stream In,
    string DaemonLockName,
    TimeSpan ClientWait,
    TimeSpan ReplyWait,
    TimeSpan RequestPoll,
    TimeSpan TickInterval,
    Func<string, ClaudeRules?> RulesFor,
    Func<TimeSpan, Task>? LaunchDelay = null)
{
    public const string UserDaemonLock = @"Local\AgentContinuum.Daemon";

    public StateStore OpenStore()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.Database)!);
        return StateStore.Open(Paths.Database);
    }

    public static AppServices ForCurrentUser()
    {
        var paths = AppPaths.ForCurrentUser();
        var clock = new SystemClock();
        var parentEnv = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
        return new AppServices(
            paths,
            clock,
            new FileLog(paths.LogFile, clock),
            new ToastNotifier(),
            new JobProcessHost(),
            new WmiProcessLister(() => PinnedCliPaths(paths)),
            new ProcessCommandRunner(),
            new PreflightHost(
                FindOnPath("git.exe", parentEnv) ?? "",
                parentEnv,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                SettingsScanner.DefaultManagedPaths()),
            Environment.ProcessPath!,
            Console.Out,
            Console.OpenStandardInput(),
            UserDaemonLock,
            // Start-up, which takes about a second on a busy machine, comes out of the same 5 s the user is promised.
            ClientWait: TimeSpan.FromSeconds(3),
            ReplyWait: TimeSpan.FromMinutes(2),
            RequestPoll: TimeSpan.FromSeconds(1),
            TickInterval: TimeSpan.FromSeconds(30),
            VersionRules.For);
    }

    // Read on every listing, so a CLI pinned after the daemon started is still recognised.
    private static IEnumerable<string> PinnedCliPaths(AppPaths paths)
    {
        using var store = StateStore.Open(paths.Database);
        return new TaskRepository(store).List().Select(t => t.CliPath).ToList();
    }

    public static string? FindOnPath(string exe, IReadOnlyDictionary<string, string> env) =>
        (env.TryGetValue("PATH", out var path) ? path : "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathFullyQualified)
            .Select(dir => Path.Combine(dir, exe))
            .FirstOrDefault(File.Exists);
}
