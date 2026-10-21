using System.Globalization;
using AgentContinuum.Core.Model;
using AgentContinuum.Core.State;

namespace AgentContinuum.Cli.Commands;

public static class TaskCommands
{
    private const int MaxAttemptsLimit = 100;

    private static readonly string[] AddOptions =
    [
        "--label", "--worktree", "--prompt-file", "--permission-mode", "--allowed-tools", "--max-attempts", "--deadline", "--cli",
    ];

    private static readonly string[] Required =
        ["--label", "--worktree", "--prompt-file", "--permission-mode", "--max-attempts", "--deadline"];

    public static int Add(AppServices services, IReadOnlyList<string> args)
    {
        if (Options.Parse(args, AddOptions, ["--allowed-tools"]) is not { } options || Required.Any(r => options.One(r) is null))
        {
            var required = string.Join(' ', Required.Select(r => $"{r} <value>"));
            services.Out.WriteLine($"usage: ac task add {required} [--allowed-tools <rule>]... [--cli <claude.exe>]");
            return App.UsageError;
        }
        if (Validate(services, options) is { } error)
        {
            services.Out.WriteLine($"error: {error.Message}");
            return 1;
        }
        var (pin, pinError) = CliPin.Read(services, options.One("--cli") ?? CliPin.DefaultPath(services));
        if (pin is null)
        {
            services.Out.WriteLine($"error: {pinError}");
            return 1;
        }

        var worktree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.One("--worktree")!));
        var task = new TaskRecord(
            TaskId: Guid.NewGuid().ToString("N")[..8],
            Label: options.One("--label")!,
            Worktree: worktree,
            PromptFile: Path.GetRelativePath(worktree, Path.GetFullPath(Path.Combine(worktree, options.One("--prompt-file")!))),
            PermissionMode: options.One("--permission-mode")!,
            AllowedTools: [.. options.All("--allowed-tools")],
            MaxAttempts: int.Parse(options.One("--max-attempts")!, CultureInfo.InvariantCulture),
            DeadlineUtc: ParseDeadline(options.One("--deadline")!)!.Value,
            SessionId: Guid.NewGuid(),
            CliPath: pin.Path,
            CliSha256: pin.Sha256,
            CliVersion: pin.Version,
            State: TaskState.Queued,
            AttemptsUsed: 0,
            ResetExpectedAtEpoch: null,
            AttemptPermittedAtEpoch: null,
            BaselineFingerprint: null,
            AttentionReason: null);
        using var store = services.OpenStore();
        new TaskRepository(store).Add(task);
        services.Log.Write($"added task {task.TaskId}");
        services.Out.WriteLine(task.TaskId);
        return 0;
    }

    private static InvalidOperationException? Validate(AppServices services, Options options)
    {
        var worktree = options.One("--worktree")!;
        if (!Path.IsPathFullyQualified(worktree) || !Directory.Exists(worktree))
        {
            return new("worktree must be an existing absolute folder");
        }
        if (!PromptInsideWorktree(worktree, options.One("--prompt-file")!))
        {
            return new("prompt file must be a non-empty file inside the worktree");
        }
        if (!LaunchPolicy.IsAllowedMode(options.One("--permission-mode")!))
        {
            var modes = LaunchPolicy.PermissionModes;
            return new($"permission mode must be {string.Join(", ", modes[..^1])} or {modes[^1]}");
        }
        if (!options.All("--allowed-tools").All(LaunchPolicy.IsAllowedToolRule))
        {
            return new("a tool rule must not be empty, start with - or hold control characters");
        }
        if (!int.TryParse(options.One("--max-attempts"), NumberStyles.None, CultureInfo.InvariantCulture, out var attempts)
            || attempts < 1 || attempts > MaxAttemptsLimit)
        {
            return new($"max attempts must be a whole number from 1 to {MaxAttemptsLimit}");
        }
        if (ParseDeadline(options.One("--deadline")!) is not { } deadline || deadline <= services.Clock.UtcNow)
        {
            return new("deadline must be a future UTC time");
        }
        return null;
    }

    // Pre-flight checks this again on every launch; checking here only saves adding a task that can never run.
    private static bool PromptInsideWorktree(string worktree, string promptFile)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
        var prompt = Path.GetFullPath(Path.Combine(root, promptFile));
        if (!prompt.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var info = new FileInfo(prompt);
        return info.LinkTarget is null && info.Exists && info.Length > 0;
    }

    private static DateTimeOffset? ParseDeadline(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var deadline)
            ? deadline.ToUniversalTime()
            : null;
}
