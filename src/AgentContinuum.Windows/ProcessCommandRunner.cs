using System.Diagnostics;
using AgentContinuum.Core.Platform;

namespace AgentContinuum.Windows;

public sealed class ProcessCommandRunner : ICommandRunner
{
    public CommandResult Run(string exe, string[] args, IReadOnlyDictionary<string, string> env, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        info.Environment.Clear();
        foreach (var (name, value) in env)
        {
            info.Environment[name] = value;
        }

        var deadline = Stopwatch.StartNew();
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The process did not start.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();

        // A child that inherited stdout can keep it open after the process exits, so the read shares the deadline.
        if (!process.WaitForExit(timeout) || !stdout.Wait(Remaining(timeout, deadline)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            return new CommandResult(-1, "", true);
        }
        return new CommandResult(process.ExitCode, stdout.Result, false);
    }

    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch elapsed) =>
        timeout > elapsed.Elapsed ? timeout - elapsed.Elapsed : TimeSpan.Zero;
}
