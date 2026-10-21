using System.Management;
using System.Security.Principal;
using AgentContinuum.Core.Platform;
using static AgentContinuum.Windows.NativeMethods;

namespace AgentContinuum.Windows;

/// <summary>Lists the current user's processes from <c>Win32_Process</c>.</summary>
public sealed class WmiProcessLister : IProcessLister
{
    private const string Query = "SELECT ProcessId, CreationDate, ExecutablePath, CommandLine FROM Win32_Process";

    public IReadOnlyList<ProcessInfo> ListOwnProcesses()
    {
        SecurityIdentifier user;
        using (var self = WindowsIdentity.GetCurrent())
        {
            user = self.User!;
        }
        var own = new List<ProcessInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(Query);
            using var results = searcher.Get();
            foreach (ManagementObject process in results)
            {
                using (process)
                {
                    var pid = unchecked((int)(uint)process["ProcessId"]);
                    if (OwnerSid(pid) is { } owner && owner.Equals(user))
                    {
                        own.Add(ToInfo(pid, process));
                    }
                }
            }
        }
        // A row that cannot be read must fail the listing; skipping it could hide a live session.
        catch (Exception e)
        {
            throw new ProcessListException("Could not list processes through WMI.", e);
        }
        return own;
    }

    // The owner is read from the token because WMI's GetOwnerSid takes seconds across a few hundred processes.
    // A process whose owner cannot be read is left out: other users' processes look exactly like that,
    // and counting them would make every unreadable command line look like one of ours.
    private static SecurityIdentifier? OwnerSid(int pid)
    {
        using var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, TOKEN_QUERY, out var token))
        {
            return null;
        }
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            return identity.User;
        }
    }

    private static ProcessInfo ToInfo(int pid, ManagementObject process)
    {
        var created = process["CreationDate"] is string date
            ? ManagementDateTimeConverter.ToDateTime(date).ToFileTimeUtc()
            : 0;
        return new ProcessInfo(
            pid,
            created,
            process["ExecutablePath"] as string,
            process["CommandLine"] as string);
    }
}
