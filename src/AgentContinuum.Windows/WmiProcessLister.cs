using System.Management;
using System.Security.Principal;
using AgentContinuum.Core.Platform;
using static AgentContinuum.Windows.NativeMethods;

namespace AgentContinuum.Windows;

/// <summary>Lists the current user's processes from <c>Win32_Process</c>.</summary>
public sealed class WmiProcessLister : IProcessLister
{
    private const string Query = "SELECT ProcessId, Name, CreationDate, ExecutablePath, CommandLine FROM Win32_Process";

    private readonly Func<IEnumerable<string>> _pinnedCliPaths;
    private readonly Func<int, SecurityIdentifier?> _ownerOf;

    /// <param name="pinnedCliPaths">Every task's pinned CLI path, read on each listing.</param>
    /// <param name="ownerOf">Owner of a process, or null when it cannot be read; for tests.</param>
    public WmiProcessLister(Func<IEnumerable<string>> pinnedCliPaths, Func<int, SecurityIdentifier?>? ownerOf = null)
    {
        _pinnedCliPaths = pinnedCliPaths;
        _ownerOf = ownerOf ?? OwnerSid;
    }

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
            var cliNames = _pinnedCliPaths().Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var searcher = new ManagementObjectSearcher(Query);
            using var results = searcher.Get();
            foreach (ManagementObject process in results)
            {
                using (process)
                {
                    var pid = unchecked((int)(uint)process["ProcessId"]);
                    var owner = _ownerOf(pid);
                    if (owner is null)
                    {
                        // Other users' processes are unreadable too, so only a CLI by name is kept, and kept as
                        // unknown: an elevated run of ours would otherwise vanish and look like no live session.
                        if (cliNames.Contains(process["Name"] as string ?? ""))
                        {
                            own.Add(new ProcessInfo(pid, CreatedAt(process), null, null));
                        }
                    }
                    else if (owner.Equals(user))
                    {
                        own.Add(new ProcessInfo(pid, CreatedAt(process), process["ExecutablePath"] as string, process["CommandLine"] as string));
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

    private static long CreatedAt(ManagementObject process) =>
        process["CreationDate"] is string date ? ManagementDateTimeConverter.ToDateTime(date).ToFileTimeUtc() : 0;
}
