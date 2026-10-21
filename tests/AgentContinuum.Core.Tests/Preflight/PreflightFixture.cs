using System.Security.Cryptography;
using AgentContinuum.Core.Billing;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.Platform;
using AgentContinuum.Core.State;

namespace AgentContinuum.Core.Tests.Preflight;

/// <summary>A task that passes every pre-flight check until a test breaks one thing.</summary>
public sealed class PreflightFixture : IDisposable
{
    public const string TaskId = "t1";

    public const string SubscriptionStatus =
        """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"SENTINEL-p4@example.com","subscriptionType":"pro"}""";

    private readonly TempDirectory _dir = new();

    public PreflightFixture()
    {
        Store = StateStore.Open(_dir.File("ac.db"));
        Tasks = new TaskRepository(Store);
        Ledger = new LaunchLedger(Store);
        Repo = GitRepo.Init(_dir.File("work tree"));
        Repo.Write("prompt.md", "SENTINEL-prompt: fix the bug");
        Repo.CommitAll();
        UserProfile = Directory.CreateDirectory(_dir.File("profile")).FullName;
        WriteProfileFile(Path.Combine(".claude", ".credentials.json"),
            """{"claudeAiOauth":{"accessToken":"SENTINEL-p3","refreshToken":"SENTINEL-p3","subscriptionType":"SENTINEL-p3"}}""");
        WriteProfileFile(".claude.json", """{"oauthAccount":{"emailAddress":"SENTINEL-p3@example.com"}}""");
        new Attestations(Store).Record(Attestations.OverageOff, Clock.UtcNow.AddDays(-1));
        ParentEnv = new Dictionary<string, string>
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!,
            ["USERPROFILE"] = UserProfile,
            ["ANTHROPIC_API_KEY"] = "SENTINEL-env",
        };
        ManagedSettingsPaths = [_dir.File("ProgramData managed-settings.json"), _dir.File("Program Files managed-settings.json")];
        Lister.Processes.Add(new ProcessInfo(4242, 1, @"C:\Windows\explorer.exe", "explorer.exe"));

        Task = TestTasks.Sample(TaskId) with
        {
            Worktree = Repo.Path,
            PromptFile = Repo.File("prompt.md"),
            DeadlineUtc = Clock.UtcNow.AddDays(7),
            CliPath = FakeClaudeProcess.ExePath,
            CliSha256 = Sha256(FakeClaudeProcess.ExePath),
        };
        Billing = new BillingProofs(Runner, Clock, Store);
    }

    public StateStore Store { get; }

    public TaskRepository Tasks { get; }

    public LaunchLedger Ledger { get; }

    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));

    public GitRepo Repo { get; }

    public string UserProfile { get; }

    public Dictionary<string, string> ParentEnv { get; }

    public string[] ManagedSettingsPaths { get; }

    public ScriptedRunner Runner { get; } = new();

    public StubLister Lister { get; } = new();

    public BillingProofs Billing { get; }

    /// <summary>Stored on the first <see cref="EnsureAdded"/>, so a test can change it before then.</summary>
    public TaskRecord Task { get; set; }

    public void EnsureAdded()
    {
        if (Tasks.Get(TaskId) is null)
        {
            Tasks.Add(Task);
        }
    }

    public string Outside(string name) => _dir.File(name);

    public void WriteProfileFile(string relative, string content)
    {
        var path = Path.Combine(UserProfile, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        Store.Dispose();
        _dir.Dispose();
    }

    public sealed class ScriptedRunner : ICommandRunner
    {
        public CommandResult Version { get; set; } = new(0, "2.1.304 (Claude Code)\n", false);

        public CommandResult AuthStatus { get; set; } = new(0, SubscriptionStatus, false);

        public List<(string Exe, string[] Args)> Calls { get; } = [];

        /// <summary>Called on each run, so a test can change state while pre-flight is in progress.</summary>
        public Action<string[]>? OnRun { get; set; }

        public CommandResult Run(string exe, string[] args, IReadOnlyDictionary<string, string> env, TimeSpan timeout)
        {
            Calls.Add((exe, args));
            OnRun?.Invoke(args);
            return args is ["--version"] ? Version : AuthStatus;
        }
    }

    public sealed class StubLister : IProcessLister
    {
        public List<ProcessInfo> Processes { get; } = [];

        public Exception? Throw { get; set; }

        public IReadOnlyList<ProcessInfo> ListOwnProcesses() => Throw is { } e ? throw e : Processes;
    }
}
