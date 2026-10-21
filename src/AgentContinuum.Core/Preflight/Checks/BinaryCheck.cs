using System.Security.Cryptography;
using AgentContinuum.Core.Platform;

namespace AgentContinuum.Core.Preflight.Checks;

internal sealed class BinaryCheck : IPreflightCheck
{
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);

    private readonly ICommandRunner _runner;

    public BinaryCheck(ICommandRunner runner)
    {
        _runner = runner;
    }

    public IEnumerable<string> Reasons(PreflightContext ctx)
    {
        var problem = Problem(ctx);
        return problem is null ? [] : [$"{PreflightReasons.CliChanged}: {problem}, re-pin with ac pin-cli"];
    }

    private string? Problem(PreflightContext ctx)
    {
        var task = ctx.Task;
        if (!Path.IsPathFullyQualified(task.CliPath) || !File.Exists(task.CliPath))
        {
            return "binary missing";
        }
        if (Sha256(task.CliPath) is not { } hash || !hash.Equals(task.CliSha256, StringComparison.OrdinalIgnoreCase))
        {
            return "binary hash differs";
        }
        // Only a binary whose hash matched is run, so pre-flight never executes an unpinned CLI.
        return ReportsPinnedVersion(ctx) ? null : "version differs";
    }

    private bool ReportsPinnedVersion(PreflightContext ctx)
    {
        if (ctx.ChildEnv is not { } env)
        {
            return false;
        }
        CommandResult result;
        try
        {
            result = _runner.Run(ctx.Task.CliPath, ["--version"], env, VersionTimeout);
        }
        catch (Exception)
        {
            return false;
        }
        if (result.TimedOut || result.ExitCode != 0)
        {
            return false;
        }
        var reported = result.StdOut.Trim().Split(' ', 2)[0];
        return reported == ctx.Task.CliVersion;
    }

    private static string? Sha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
