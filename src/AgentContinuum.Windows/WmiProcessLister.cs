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
    private readonly TimeSpan _timeout;
    private readonly Func<int, SecurityIdentifier?> _ownerOf;

    /// <param name="pinnedCliPaths">Every task's pinned CLI path, read on each listing.</param>
    /// <param name="timeout">How long a listing may take before it fails; 10 seconds when null.</param>
    /// <param name="ownerOf">Owner of a process, or null when it cannot be read; for tests.</param>
    public WmiProcessLister(
        Func<IEnumerable<string>> pinnedCliPaths, TimeSpan? timeout = null, Func<int, SecurityIdentifier?>? ownerOf = null)
    {
        _pinnedCliPaths = pinnedCliPaths;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        _ownerOf = ownerOf ?? OwnerSid;
    }

    public IReadOnlyList<ProcessInfo> ListOwnProcesses()
    {
        // WMI calls have no deadline of their own, and a stalled winmgmt must block a launch rather than hang it.
        try
        {
            return Task.Run(List).WaitAsync(_timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException e)
        {
            throw new ProcessListException("Listing processes through WMI timed out.", e);
        }
    }

    private List<ProcessInfo> List()
    {
        SecurityIdentifier user;
        using (var self = WindowsIdentity.GetCurrent())
        {
            user = self.User!;
        }
        var own = new List<ProcessInfo>();
        try
        {
            var cliNames = _pinnedCliPaths().Select(p => Path.GetFileName(p) ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var searcher = new ManagementObjectSearcher(Query);
            using var results = searcher.Get();
            foreach (ManagementObject process in results)
            {
                using (process)
                {
                    var row = new Row(
                        unchecked((int)(uint)process["ProcessId"]),
                        process["Name"] as string ?? "",
                        CreatedAt(process),
                        process["ExecutablePath"] as string,
                        process["CommandLine"] as string);
                    if (Own(row, _ownerOf(row.Pid), user, cliNames) is { } info)
                    {
                        own.Add(info);
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

    /// <summary>One <c>Win32_Process</c> row as WMI reported it.</summary>
    public sealed record Row(int Pid, string Name, long CreatedAtFileTime, string? ExecutablePath, string? CommandLine);

    /// <summary>The row as one of your processes, or null when it is not one or cannot matter.</summary>
    public static ProcessInfo? Own(Row row, SecurityIdentifier? owner, SecurityIdentifier user, IReadOnlySet<string> cliNames)
    {
        if (owner is not null && !owner.Equals(user))
        {
            return null;
        }
        if (owner is not null && (row.ExecutablePath is not null || !string.IsNullOrEmpty(row.CommandLine)))
        {
            return new ProcessInfo(row.Pid, row.CreatedAtFileTime, row.ExecutablePath, row.CommandLine);
        }
        // A process WMI cannot read, whether another user's or an elevated or protected one of ours, is kept only
        // when its name is a pinned CLI's, and kept as unknown: an elevated run of ours would otherwise vanish and
        // look like no live session, while every other such process, ctfmon among them, would refuse every launch.
        return cliNames.Contains(row.Name) ? new ProcessInfo(row.Pid, row.CreatedAtFileTime, null, null) : null;
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
