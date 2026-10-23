using AgentContinuum.Core.Platform;

namespace AgentContinuum.Core.Preflight.Checks;

/// <summary>Matches recovery: the session UUID on any command line is live; a CLI process we cannot read is unknown.</summary>
internal sealed class ProcessCheck : IPreflightCheck
{
    private readonly IProcessLister _lister;

    public ProcessCheck(IProcessLister lister)
    {
        _lister = lister;
    }

    public IEnumerable<string> Reasons(PreflightContext ctx)
    {
        IReadOnlyList<ProcessInfo> processes;
        try
        {
            processes = _lister.ListOwnProcesses();
        }
        catch (Exception)
        {
            return [PreflightReasons.ProcessUnknown];
        }

        // An empty list cannot be true while this process runs, so it means enumeration failed quietly.
        if (processes.Count == 0)
        {
            return [PreflightReasons.ProcessUnknown];
        }
        var session = ctx.Task.SessionId.ToString("D");
        if (processes.Any(p => p.CommandLine?.Contains(session, StringComparison.OrdinalIgnoreCase) == true))
        {
            return [PreflightReasons.ProcessLive];
        }
        if (processes.Any(p => string.IsNullOrEmpty(p.CommandLine)
            && (p.ExecutablePath is null || string.Equals(p.ExecutablePath, ctx.Task.CliPath, StringComparison.OrdinalIgnoreCase))))
        {
            return [PreflightReasons.ProcessUnknown];
        }
        return [];
    }
}
