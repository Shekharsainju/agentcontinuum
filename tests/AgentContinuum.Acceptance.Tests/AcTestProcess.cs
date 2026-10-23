using System.Diagnostics;

namespace AgentContinuum.Acceptance.Tests;

/// <summary>Runs the built ac-test.exe as a real process.</summary>
public static class AcTestProcess
{
    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "ac-test.exe");

    public static ProcessStartInfo Info(string localAppData, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        info.Environment["LOCALAPPDATA"] = localAppData;
        return info;
    }

    public static (int ExitCode, string StdOut) Run(string localAppData, params string[] args)
    {
        using var process = Process.Start(Info(localAppData, args))!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"ac-test {string.Join(' ', args)} did not exit within 60 s.");
        }
        return (process.ExitCode, stdout.Result.Trim());
    }
}
