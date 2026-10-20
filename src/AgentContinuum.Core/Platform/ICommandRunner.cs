namespace AgentContinuum.Core.Platform;

public interface ICommandRunner
{
    /// <summary>Runs <paramref name="exe"/> with exactly <paramref name="env"/> and no stdin; stderr is discarded.</summary>
    CommandResult Run(string exe, string[] args, IReadOnlyDictionary<string, string> env, TimeSpan timeout);
}

public sealed record CommandResult(int ExitCode, string StdOut, bool TimedOut);
