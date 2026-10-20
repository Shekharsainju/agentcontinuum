using System.Diagnostics;

namespace AgentContinuum.Core.Tests;

public static class FakeClaudeProcess
{
    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "claude.exe");

    public static Dictionary<string, string> MinimalEnv() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")!,
        ["PATH"] = Environment.GetEnvironmentVariable("SystemRoot") + @"\System32",
    };

    public static (int ExitCode, string[] StdOut) Run(
        string workingDir,
        string[] args,
        string stdin = "",
        IReadOnlyDictionary<string, string>? env = null)
    {
        var info = new ProcessStartInfo(ExePath)
        {
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        info.Environment.Clear();
        foreach (var (name, value) in env ?? MinimalEnv())
        {
            info.Environment[name] = value;
        }

        using var process = Process.Start(info)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("FakeClaude did not exit within 30 s.");
        }
        return (process.ExitCode, stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
