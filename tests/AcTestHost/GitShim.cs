using System.Collections;
using System.Diagnostics;
using System.Globalization;
using AgentContinuum.Cli;

namespace AgentContinuum.AcTestHost;

/// <summary>Stands in for git when the daemon is to crash after a run exits; otherwise forwards to the real git.</summary>
public static class GitShim
{
    // The fingerprint always passes its safe -c settings first, and no ac command starts with -c.
    public static bool Matches(string[] args) => args is ["-c", ..];

    public static int Run(string[] args)
    {
        if (Environment.GetEnvironmentVariable(Crash.KillOnGitVariable) is { } daemonPid)
        {
            using var daemon = Process.GetProcessById(int.Parse(daemonPid, CultureInfo.InvariantCulture));
            daemon.Kill();
            daemon.WaitForExit();
            return 1;
        }

        var env = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
        var git = AppServices.FindOnPath("git.exe", env) ?? throw new InvalidOperationException("git.exe is not on PATH.");
        var info = new ProcessStartInfo(git) { UseShellExecute = false };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info)!;
        process.WaitForExit();
        return process.ExitCode;
    }
}
