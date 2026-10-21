using System.Security.Cryptography;
using AgentContinuum.Core.Billing;

namespace AgentContinuum.Cli.Commands;

public sealed record Pin(string Path, string Sha256, string Version);

public static class CliPin
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The pin for the CLI at <paramref name="path"/>, or an error that names no content.</summary>
    public static (Pin? Pin, string? Error) Read(AppServices services, string path)
    {
        // The process check matches running CLIs by image name, which a .cmd shim or a link would not give.
        if (!System.IO.Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            return (null, "cli not found");
        }
        if (!string.Equals(System.IO.Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "cli must be an .exe file");
        }
        if (new FileInfo(path).LinkTarget is not null)
        {
            return (null, "cli must not be a link");
        }
        string hash;
        using (var stream = File.OpenRead(path))
        {
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        var gitDir = System.IO.Path.GetDirectoryName(services.Preflight.GitExe) ?? "";
        var env = ChildEnvironment.Build(services.Preflight.ParentEnv, System.IO.Path.GetDirectoryName(path)!, gitDir);
        var result = services.Runner.Run(path, ["--version"], env, VersionTimeout);
        var version = result.StdOut.Trim().Split(' ', 2)[0];
        if (result.TimedOut || result.ExitCode != 0 || version.Length == 0)
        {
            return (null, "cli version unreadable");
        }
        return (new Pin(path, hash, version), null);
    }

    public static string DefaultPath(AppServices services) =>
        AppServices.FindOnPath("claude.exe", services.Preflight.ParentEnv)
        ?? System.IO.Path.Combine(services.Preflight.UserProfile, ".local", "bin", "claude.exe");
}
