using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using AgentContinuum.Core.Platform;
using Microsoft.Win32.SafeHandles;
using static AgentContinuum.Windows.NativeMethods;

namespace AgentContinuum.Windows;

/// <summary>
/// Starts each launch suspended, puts it in its own kill-on-close job with breakaway disallowed, then resumes it.
/// </summary>
public sealed class JobProcessHost : IProcessHost
{
    private readonly Action<IntPtr>? _beforeResume;

    /// <param name="beforeResume">Called with the process handle after it joins the job and before it runs; for tests.</param>
    public JobProcessHost(Action<IntPtr>? beforeResume = null)
    {
        _beforeResume = beforeResume;
    }

    public IHostedRun Start(LaunchSpec spec, Action<int, long>? created = null)
    {
        if (!Path.IsPathFullyQualified(spec.Exe))
        {
            throw new ArgumentException("The executable path must be fully qualified.", nameof(spec));
        }
        var commandLine = (CommandLine.Build(spec.Exe, spec.Args) + '\0').ToCharArray();
        var environment = EnvironmentBlock(spec.Env);

        var inheritable = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        SafeFileHandle? stdinRead = null, stdinWrite = null, stdoutRead = null, stdoutWrite = null, nul = null;
        var started = false;
        try
        {
            CreatePipeWithParentEnd(ref inheritable, out stdinRead, out stdinWrite, parentEndIsRead: false);
            CreatePipeWithParentEnd(ref inheritable, out stdoutRead, out stdoutWrite, parentEndIsRead: true);
            nul = CreateFileW("NUL", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, ref inheritable, OPEN_EXISTING, 0, IntPtr.Zero);
            if (nul.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open NUL for stderr.");
            }

            var info = CreateSuspended(spec, commandLine, environment, stdinRead, stdoutWrite, nul);
            var process = new SafeProcessHandle(info.hProcess, ownsHandle: true);
            using var thread = new SafeThreadHandle(info.hThread);
            var job = JoinNewJob(process, thread, () => created?.Invoke(info.dwProcessId, CreationTime(process)));
            started = true;
            return new JobHostedRun(process, job, info.dwProcessId, stdoutRead, stdinWrite, spec.Stdin);
        }
        finally
        {
            // The child holds its own copies of these ends; ours must close or the stdout pipe never reports EOF.
            stdinRead?.Dispose();
            stdoutWrite?.Dispose();
            nul?.Dispose();
            if (!started)
            {
                stdinWrite?.Dispose();
                stdoutRead?.Dispose();
            }
        }
    }

    private SafeJobHandle JoinNewJob(SafeProcessHandle process, SafeThreadHandle thread, Action created)
    {
        SafeJobHandle? job = null;
        try
        {
            job = CreateKillOnCloseJob();
            if (!AssignProcessToJobObject(job, process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the process to its job.");
            }
            _beforeResume?.Invoke(process.DangerousGetHandle());
            created();
            if (ResumeThread(thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume the process.");
            }
            return job;
        }
        catch
        {
            // Outside the job nothing else would ever kill it, so it must not be left suspended or running.
            TerminateProcess(process, 1);
            process.Dispose();
            job?.Dispose();
            throw;
        }
    }

    internal static long CreationTime(SafeProcessHandle process) =>
        GetProcessTimes(process, out var created, out _, out _, out _) ? created : 0;

    private static SafeJobHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a job.");
        }
        // No breakaway flag is set, so no process in the job can leave it.
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "Could not set the job limits.");
        }
        return job;
    }

    private static void CreatePipeWithParentEnd(
        ref SECURITY_ATTRIBUTES inheritable, out SafeFileHandle read, out SafeFileHandle write, bool parentEndIsRead)
    {
        if (!CreatePipe(out read, out write, ref inheritable, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a pipe.");
        }
        if (!SetHandleInformation(parentEndIsRead ? read : write, HANDLE_FLAG_INHERIT, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not make the pipe end private.");
        }
    }

    // The handle list limits inheritance to the three stdio handles, so no other inheritable handle of ours leaks in.
    private static PROCESS_INFORMATION CreateSuspended(
        LaunchSpec spec, char[] commandLine, char[] environment, SafeFileHandle stdin, SafeFileHandle stdout, SafeFileHandle stderr)
    {
        var handles = new[] { stdin.DangerousGetHandle(), stdout.DangerousGetHandle(), stderr.DangerousGetHandle() };
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        var handleBuffer = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
        try
        {
            Marshal.Copy(handles, 0, handleBuffer, handles.Length);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the attribute list.");
            }
            try
            {
                if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleBuffer,
                        IntPtr.Size * handles.Length, IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not set the inherited handle list.");
                }
                var startup = new STARTUPINFOEX { lpAttributeList = attributes };
                startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                startup.StartupInfo.hStdInput = handles[0];
                startup.StartupInfo.hStdOutput = handles[1];
                startup.StartupInfo.hStdError = handles[2];
                const uint flags = CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT;
                if (!CreateProcessW(spec.Exe, commandLine, IntPtr.Zero, IntPtr.Zero, true, flags, environment, spec.WorkingDir,
                        ref startup, out var info))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the process.");
                }
                return info;
            }
            finally
            {
                DeleteProcThreadAttributeList(attributes);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(handleBuffer);
            Marshal.FreeHGlobal(attributes);
            GC.KeepAlive(stdin);
            GC.KeepAlive(stdout);
            GC.KeepAlive(stderr);
        }
    }

    private static char[] EnvironmentBlock(IReadOnlyDictionary<string, string> env)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Length == 0 || name.Contains('=') || name.Contains('\0') || value.Contains('\0'))
            {
                throw new ArgumentException("An environment entry is malformed.", nameof(env));
            }
            block.Append(name).Append('=').Append(value).Append('\0');
        }
        // An empty block still needs two terminators.
        if (block.Length == 0)
        {
            block.Append('\0');
        }
        return block.Append('\0').ToString().ToCharArray();
    }
}

internal sealed class JobHostedRun : IHostedRun
{
    private readonly SafeProcessHandle _process;
    private readonly SafeJobHandle _job;
    private readonly FileStream _stdout;
    private readonly Lazy<Task<int>> _exit;

    public JobHostedRun(SafeProcessHandle process, SafeJobHandle job, int pid, SafeFileHandle stdoutRead, SafeFileHandle stdinWrite, Stream stdin)
    {
        _process = process;
        _job = job;
        Pid = pid;
        CreatedAtFileTime = JobProcessHost.CreationTime(process);
        _stdout = new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false);
        _exit = new Lazy<Task<int>>(WaitAsync);
        _ = FeedStdin(stdin, new FileStream(stdinWrite, FileAccess.Write, 4096, isAsync: false));
    }

    public int Pid { get; }

    public long CreatedAtFileTime { get; }

    public IAsyncEnumerable<string> StdoutLines => ReadLines();

    public Task<int> WaitForExit() => _exit.Value;

    public int ActiveProcesses()
    {
        if (!QueryInformationJobObject(_job, JobObjectBasicAccountingInformation, out var accounting,
                Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query the job.");
        }
        return (int)accounting.ActiveProcesses;
    }

    // Every kill result is recorded, so a kill on a closed job reports a result instead of throwing.
    public KillResult Terminate()
    {
        if (_job.IsClosed)
        {
            return new KillResult(false, ERROR_INVALID_HANDLE);
        }
        return TerminateJobObject(_job, 1) ? new KillResult(true, 0) : new KillResult(false, Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        _job.Dispose();
        _stdout.Dispose();
        _process.Dispose();
    }

    // Written in the background so a child that prints before reading stdin cannot deadlock against us.
    private static async Task FeedStdin(Stream source, FileStream pipe)
    {
        try
        {
            await source.CopyToAsync(pipe).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<string> ReadLines()
    {
        using var reader = new StreamReader(_stdout, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            yield return line;
        }
    }

    private Task<int> WaitAsync()
    {
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The run can be disposed while the process is still alive. The reference keeps the handle open for the
        // wait and the callback, which would otherwise throw on a thread-pool thread and end the daemon.
        var added = false;
        _process.DangerousAddRef(ref added);
        var waitHandle = new ProcessWaitHandle(_process);
        RegisteredWaitHandle? registration = null;
        registration = ThreadPool.RegisterWaitForSingleObject(waitHandle, (_, _) =>
        {
            registration?.Unregister(null);
            waitHandle.Dispose();
            try
            {
                if (GetExitCodeProcess(_process, out var code))
                {
                    exited.SetResult(unchecked((int)code));
                }
                else
                {
                    exited.SetException(new Win32Exception(Marshal.GetLastWin32Error()));
                }
            }
            finally
            {
                _process.DangerousRelease();
            }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        return exited.Task;
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process)
        {
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }
    }
}
