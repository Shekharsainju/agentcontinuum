using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentContinuum.Core.Workspace;

/// <summary><see cref="Error"/> never names a file or holds content.</summary>
public sealed record FingerprintResult(string? Hash, bool Incomplete, string? Error);

/// <summary>SHA-256 over every tracked or unignored file and HEAD. Only the hash leaves this class.</summary>
public static class WorkspaceFingerprint
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    // A repository's own config can name an fsmonitor or hook program; overriding both stops git running it.
    private static readonly string[] SafeConfig = ["-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL"];

    public const int MaxFiles = 50_000;
    public const long MaxBytes = 2L * 1024 * 1024 * 1024;

    public static FingerprintResult Compute(string worktree, string gitExe, int maxFiles = MaxFiles, long maxBytes = MaxBytes)
    {
        var (files, listError) = RunGit(gitExe, worktree, "ls-files", "-z", "--cached", "--others", "--exclude-standard");
        if (listError is not null)
        {
            return new FingerprintResult(null, false, listError);
        }
        var (head, headError) = RunGit(gitExe, worktree, "rev-parse", "HEAD");
        if (headError is not null)
        {
            return new FingerprintResult(null, false, headError);
        }

        // Unmerged files are listed once per stage.
        var paths = files.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (paths.Count > maxFiles)
        {
            return new FingerprintResult(null, true, null);
        }
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalBytes = 0;
        try
        {
            foreach (var path in paths)
            {
                var fullPath = Path.Combine(worktree, path);
                totalBytes += FileSize(fullPath);
                if (totalBytes > maxBytes)
                {
                    return new FingerprintResult(null, true, null);
                }
                sha.AppendData(Encoding.UTF8.GetBytes(path));
                sha.AppendData([0]);
                sha.AppendData(Encoding.ASCII.GetBytes(ContentHash(fullPath)));
                sha.AppendData([0]);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new FingerprintResult(null, false, "a file in the worktree could not be read");
        }
        sha.AppendData(Encoding.ASCII.GetBytes(head.Trim()));
        return new FingerprintResult(Hex(sha.GetHashAndReset()), false, null);
    }

    // A tracked file deleted from disk and a submodule directory get fixed tokens so they still change the hash.
    private static string ContentHash(string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (info.LinkTarget is { } target)
        {
            return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(target)));
        }
        if (!info.Exists)
        {
            return Directory.Exists(fullPath) ? "directory" : "missing";
        }
        using var stream = info.OpenRead();
        return Hex(SHA256.HashData(stream));
    }

    private static long FileSize(string fullPath)
    {
        var info = new FileInfo(fullPath);
        return info.LinkTarget is null && info.Exists ? info.Length : 0;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static (string Output, string? Error) RunGit(string gitExe, string worktree, params string[] args)
    {
        var info = new ProcessStartInfo(gitExe)
        {
            WorkingDirectory = worktree,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in SafeConfig.Concat(args))
        {
            info.ArgumentList.Add(arg);
        }
        // GIT_DIR, GIT_WORK_TREE and similar would point git at a different repository than the worktree.
        foreach (var name in info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(name);
        }

        Process process;
        try
        {
            process = Process.Start(info)!;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            return ("", $"git {args[0]} could not start");
        }
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitTimeout))
            {
                process.Kill(entireProcessTree: true);
                return ("", $"git {args[0]} timed out");
            }
            return process.ExitCode == 0 ? (stdout.Result, null) : ("", $"git {args[0]} exited {process.ExitCode}");
        }
    }
}
