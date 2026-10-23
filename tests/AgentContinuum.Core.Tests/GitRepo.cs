using System.Diagnostics;

namespace AgentContinuum.Core.Tests;

public sealed class GitRepo
{
    private GitRepo(string path)
    {
        Path = path;
    }

    public static string GitExe { get; } = FindGit();

    public string Path { get; }

    public static GitRepo Init(string path)
    {
        Directory.CreateDirectory(path);
        var repo = new GitRepo(path);
        repo.Git("init", "-q");
        repo.Git("config", "user.name", "test");
        repo.Git("config", "user.email", "test@example.com");
        repo.Git("config", "core.autocrlf", "false");
        return repo;
    }

    public string File(string relative) => System.IO.Path.Combine(Path, relative);

    public void Write(string relative, string content)
    {
        var path = File(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }

    public void CommitAll(string message = "commit")
    {
        Git("add", "-A");
        Git("commit", "-q", "--allow-empty", "-m", message);
    }

    public string Git(params string[] args)
    {
        var info = new ProcessStartInfo(GitExe)
        {
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Result}");
        }
        return stdout.Result;
    }

    private static string FindGit() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => System.IO.Path.Combine(dir, "git.exe"))
            .FirstOrDefault(System.IO.File.Exists)
        ?? throw new InvalidOperationException("git.exe is not on PATH.");
}
