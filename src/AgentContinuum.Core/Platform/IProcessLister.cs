namespace AgentContinuum.Core.Platform;

public interface IProcessLister
{
    /// <summary>Processes owned by the current user. Throws <see cref="ProcessListException"/> when enumeration fails.</summary>
    IReadOnlyList<ProcessInfo> ListOwnProcesses();
}

public sealed record ProcessInfo(int Pid, long CreatedAtFileTime, string CommandLine);

public sealed class ProcessListException : Exception
{
    public ProcessListException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
