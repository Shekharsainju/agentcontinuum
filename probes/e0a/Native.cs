// Native.cs - P/Invoke layer for the E0a probe. Compiled by Add-Type (C# 5, .NET Framework).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AcProbe
{
    public class ProcInfo { public IntPtr Process; public IntPtr Thread; public int Pid; public int Win32Error; }
    public class OpenResult { public bool Ok; public int Win32Error; public IntPtr Handle; }
    public class PipeOpen { public SafePipeHandle Handle; public int Win32Error; }

    public static class Native
    {
        public static int LastError;

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }
        // winsiderss/phnt ntpsapi.h: Flags(4) Freeze(1) Swap(1) Reserved0[2] WakeFilter{High,Low}(8) = 16 bytes.
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_FREEZE_INFORMATION
        {
            public uint Flags;
            public byte Freeze;
            public byte Swap;
            public ushort Reserved;
            public uint HighEdgeFilter;
            public uint LowEdgeFilter;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }
        [StructLayout(LayoutKind.Sequential)]
        struct LSA_UNICODE_STRING
        {
            public ushort Length, MaximumLength;
            public IntPtr Buffer;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct LSA_OBJECT_ATTRIBUTES
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public int Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        const int JobObjectExtendedLimitInformation = 9;
        const int JobObjectBasicProcessIdList = 3;
        const int JobObjectBasicAccountingInformation = 1;
        const int JobObjectFreezeInformation = 18;
        const uint LOGON_WITH_PROFILE = 0x00000001;
        const uint CREATE_SUSPENDED = 0x00000004;
        const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation, int cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool QueryInformationJobObject(IntPtr hJob, int JobObjectInformationClass, IntPtr lpJobObjectInformation, int cbJobObjectInformationLength, out int lpReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool IsProcessInJob(IntPtr ProcessHandle, IntPtr JobHandle, out bool Result);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        [DllImport("ntdll.dll", SetLastError = true)]
        static extern int NtSetInformationJobObject(IntPtr hJob, int JobObjectInformationClass, ref JOBOBJECT_FREEZE_INFORMATION lpJobObjectInformation, int cbJobObjectInformationLength);

        [DllImport("ntdll.dll", SetLastError = true)]
        static extern int NtSuspendProcess(IntPtr ProcessHandle);

        [DllImport("ntdll.dll", SetLastError = true)]
        static extern int NtResumeProcess(IntPtr ProcessHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessWithLogonW(
            string lpUsername, string lpDomain, string lpPassword, uint dwLogonFlags,
            string lpApplicationName, string lpCommandLine, uint dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcessW(
            string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafePipeHandle CreateNamedPipeW(
            string lpName, uint dwOpenMode, uint dwPipeMode, uint nMaxInstances,
            uint nOutBufferSize, uint nInBufferSize, uint nDefaultTimeOut, ref SECURITY_ATTRIBUTES lpSecurityAttributes);

        const uint PROCESS_ALL_ACCESS = 0x001F0FFF;

        public static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            LastError = Marshal.GetLastWin32Error();
            if (job == IntPtr.Zero) return job;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            bool ok = SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
            LastError = Marshal.GetLastWin32Error();
            if (!ok)
            {
                // Never hand back a job lacking KILL_ON_JOB_CLOSE.
                int err = LastError;
                CloseHandle(job);
                LastError = err;
                return IntPtr.Zero;
            }
            return job;
        }

        public static bool IsInJob(IntPtr proc, IntPtr job)
        {
            bool result;
            bool ok = IsProcessInJob(proc, job, out result);
            LastError = Marshal.GetLastWin32Error();
            return ok && result;
        }

        public static int[] JobPids(IntPtr job)
        {
            // JOBOBJECT_BASIC_PROCESS_ID_LIST: NumberOfAssignedProcesses, NumberOfProcessIdsInList, then ProcessIdList[].
            int capacity = 64;
            IntPtr buf = IntPtr.Zero;
            try
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    int size = 8 + capacity * IntPtr.Size;
                    buf = Marshal.AllocHGlobal(size);
                    int returned;
                    bool ok = QueryInformationJobObject(job, JobObjectBasicProcessIdList, buf, size, out returned);
                    LastError = Marshal.GetLastWin32Error();
                    if (ok)
                    {
                        int numInList = Marshal.ReadInt32(buf, 4);
                        var pids = new List<int>();
                        for (int i = 0; i < numInList; i++)
                        {
                            IntPtr pidPtr = Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size);
                            pids.Add((int)(long)pidPtr);
                        }
                        return pids.ToArray();
                    }
                    Marshal.FreeHGlobal(buf);
                    buf = IntPtr.Zero;
                    capacity *= 4;
                }
                return new int[0];
            }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        public static int JobActive(IntPtr job)
        {
            int size = Marshal.SizeOf(typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION));
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int returned;
                bool ok = QueryInformationJobObject(job, JobObjectBasicAccountingInformation, buf, size, out returned);
                LastError = Marshal.GetLastWin32Error();
                if (!ok) return -1;
                var info = (JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)Marshal.PtrToStructure(buf, typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION));
                return (int)info.ActiveProcesses;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static int FreezeInfoSize()
        {
            return Marshal.SizeOf(typeof(JOBOBJECT_FREEZE_INFORMATION));
        }

        public static int SetJobFreeze(IntPtr job, bool freeze)
        {
            var info = new JOBOBJECT_FREEZE_INFORMATION();
            info.Flags = 1; // FreezeOperation
            info.Freeze = freeze ? (byte)1 : (byte)0;
            info.Swap = 0;
            int status = NtSetInformationJobObject(job, JobObjectFreezeInformation, ref info, FreezeInfoSize());
            LastError = NtToWin32(status);
            return status;
        }

        public static int SuspendProcess(IntPtr proc)
        {
            int status = NtSuspendProcess(proc);
            LastError = NtToWin32(status);
            return status;
        }

        public static int ResumeProcess(IntPtr proc)
        {
            int status = NtResumeProcess(proc);
            LastError = NtToWin32(status);
            return status;
        }

        [DllImport("ntdll.dll")]
        static extern int RtlNtStatusToDosError(int Status);

        // NTSTATUS APIs do not SetLastError; derive the Win32 error from the status (0 on success).
        static int NtToWin32(int status)
        {
            return status >= 0 ? 0 : RtlNtStatusToDosError(status);
        }

        public static ProcInfo StartAsUser(string user, string domain, string password, string cmdLine, bool suspended)
        {
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            PROCESS_INFORMATION pi;
            uint flags = CREATE_UNICODE_ENVIRONMENT;
            if (suspended) flags |= CREATE_SUSPENDED;
            bool ok = CreateProcessWithLogonW(user, domain, password, LOGON_WITH_PROFILE, null, cmdLine, flags, IntPtr.Zero, null, ref si, out pi);
            int err = Marshal.GetLastWin32Error();
            LastError = err;
            return new ProcInfo { Process = pi.hProcess, Thread = pi.hThread, Pid = pi.dwProcessId, Win32Error = ok ? 0 : err };
        }

        public static ProcInfo StartSelf(string cmdLine, bool suspended, bool breakaway)
        {
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            PROCESS_INFORMATION pi;
            uint flags = 0;
            if (suspended) flags |= CREATE_SUSPENDED;
            if (breakaway) flags |= CREATE_BREAKAWAY_FROM_JOB;
            var sb = new StringBuilder(cmdLine);
            bool ok = CreateProcessW(null, sb.ToString(), IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, null, ref si, out pi);
            int err = Marshal.GetLastWin32Error();
            LastError = err;
            return new ProcInfo { Process = pi.hProcess, Thread = pi.hThread, Pid = pi.dwProcessId, Win32Error = ok ? 0 : err };
        }

        public static bool Assign(IntPtr job, IntPtr proc)
        {
            bool ok = AssignProcessToJobObject(job, proc);
            LastError = Marshal.GetLastWin32Error();
            return ok;
        }

        public static int Resume(IntPtr thread)
        {
            int prev = ResumeThread(thread);
            LastError = Marshal.GetLastWin32Error();
            return prev;
        }

        public static bool TerminateJob(IntPtr job)
        {
            bool ok = TerminateJobObject(job, 1);
            LastError = Marshal.GetLastWin32Error();
            return ok;
        }

        public static OpenResult TryOpen(int pid, uint access)
        {
            IntPtr h = OpenProcess(access, false, pid);
            int err = Marshal.GetLastWin32Error();
            return new OpenResult { Ok = h != IntPtr.Zero, Win32Error = h == IntPtr.Zero ? err : 0, Handle = h };
        }

        public static bool KillPid(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
            LastError = Marshal.GetLastWin32Error();
            if (h == IntPtr.Zero) return false;
            try
            {
                bool ok = TerminateProcess(h, 1);
                LastError = Marshal.GetLastWin32Error();
                return ok;
            }
            finally { CloseHandle(h); }
        }

        public static long CreationTime(int pid)
        {
            const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            LastError = Marshal.GetLastWin32Error();
            if (h == IntPtr.Zero) return -1;
            try
            {
                long creation, exit, kernel, user;
                bool ok = GetProcessTimes(h, out creation, out exit, out kernel, out user);
                LastError = Marshal.GetLastWin32Error();
                return ok ? creation : -1;
            }
            finally { CloseHandle(h); }
        }

        // --- LSA account rights (admin only; compiled here, not exercised by self-user tests) ---

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int LsaOpenPolicy(ref LSA_UNICODE_STRING SystemName, ref LSA_OBJECT_ATTRIBUTES ObjectAttributes, int DesiredAccess, out IntPtr PolicyHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int LsaClose(IntPtr ObjectHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int LsaNtStatusToWinError(int Status);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int LsaEnumerateAccountRights(IntPtr PolicyHandle, IntPtr AccountSid, out IntPtr UserRights, out int CountOfRights);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int LsaAddAccountRights(IntPtr PolicyHandle, IntPtr AccountSid, LSA_UNICODE_STRING[] UserRights, int CountOfRights);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int LsaRemoveAccountRights(IntPtr PolicyHandle, IntPtr AccountSid, bool AllRights, LSA_UNICODE_STRING[] UserRights, int CountOfRights);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int LsaFreeMemory(IntPtr Buffer);

        const int POLICY_LOOKUP_NAMES = 0x00000800;
        const int POLICY_CREATE_ACCOUNT = 0x00000010;

        // Returns the NTSTATUS from LsaOpenPolicy; policy is IntPtr.Zero on failure.
        static int OpenPolicy(int access, out IntPtr policy)
        {
            var sysName = new LSA_UNICODE_STRING();
            var oa = new LSA_OBJECT_ATTRIBUTES();
            return LsaOpenPolicy(ref sysName, ref oa, access, out policy);
        }

        static LSA_UNICODE_STRING MakeLsaString(IntPtr buf, string s)
        {
            return new LSA_UNICODE_STRING { Buffer = buf, Length = (ushort)(s.Length * 2), MaximumLength = (ushort)(s.Length * 2) };
        }

        public static string[] AccountRights(string sid)
        {
            IntPtr sidPtr;
            if (!ConvertStringSidToSid(sid, out sidPtr)) { LastError = Marshal.GetLastWin32Error(); return new string[0]; }
            IntPtr policy;
            IntPtr rightsPtr = IntPtr.Zero;
            try
            {
                int openStatus = OpenPolicy(POLICY_LOOKUP_NAMES, out policy);
                if (openStatus != 0) { LastError = LsaNtStatusToWinError(openStatus); return new string[0]; }
                try
                {
                    int count;
                    int status = LsaEnumerateAccountRights(policy, sidPtr, out rightsPtr, out count);
                    LastError = status == 0 ? 0 : LsaNtStatusToWinError(status);
                    if (status != 0) return new string[0];
                    var rights = new string[count];
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr item = new IntPtr(rightsPtr.ToInt64() + i * Marshal.SizeOf(typeof(LSA_UNICODE_STRING)));
                        var lsaStr = (LSA_UNICODE_STRING)Marshal.PtrToStructure(item, typeof(LSA_UNICODE_STRING));
                        rights[i] = Marshal.PtrToStringUni(lsaStr.Buffer, lsaStr.Length / 2);
                    }
                    return rights;
                }
                finally
                {
                    if (rightsPtr != IntPtr.Zero) LsaFreeMemory(rightsPtr);
                    LsaClose(policy);
                }
            }
            finally { Marshal.FreeHGlobal(sidPtr); }
        }

        // Returns 0 on success, otherwise a Win32 error code (also stored in LastError).
        public static int AddRight(string sid, string right)
        {
            return ChangeRight(sid, right, true);
        }

        public static int RemoveRight(string sid, string right)
        {
            return ChangeRight(sid, right, false);
        }

        static int ChangeRight(string sid, string right, bool add)
        {
            IntPtr sidPtr;
            if (!ConvertStringSidToSid(sid, out sidPtr)) { LastError = Marshal.GetLastWin32Error(); return LastError; }
            IntPtr rightBuf = IntPtr.Zero;
            try
            {
                IntPtr policy;
                int openStatus = OpenPolicy(POLICY_CREATE_ACCOUNT, out policy);
                if (openStatus != 0) { LastError = LsaNtStatusToWinError(openStatus); return LastError; }
                try
                {
                    rightBuf = Marshal.StringToHGlobalUni(right);
                    var arr = new[] { MakeLsaString(rightBuf, right) };
                    int status = add ? LsaAddAccountRights(policy, sidPtr, arr, 1)
                                     : LsaRemoveAccountRights(policy, sidPtr, false, arr, 1);
                    LastError = status == 0 ? 0 : LsaNtStatusToWinError(status);
                    return LastError;
                }
                finally { LsaClose(policy); }
            }
            finally
            {
                Marshal.FreeHGlobal(sidPtr);
                if (rightBuf != IntPtr.Zero) Marshal.FreeHGlobal(rightBuf);
            }
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ConvertStringSidToSid(string StringSid, out IntPtr Sid);


        public static PipeOpen CreateFirstInstancePipe(string name, byte[] securityDescriptor)
        {
            const uint PIPE_ACCESS_OUTBOUND = 0x00000002;
            const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
            const uint PIPE_TYPE_BYTE = 0x00000000;
            const uint PIPE_REJECT_REMOTE_CLIENTS = 0x00000008;

            IntPtr sdPtr = IntPtr.Zero;
            if (securityDescriptor != null && securityDescriptor.Length > 0)
            {
                sdPtr = Marshal.AllocHGlobal(securityDescriptor.Length);
                Marshal.Copy(securityDescriptor, 0, sdPtr, securityDescriptor.Length);
            }
            try
            {
                var sa = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)),
                    lpSecurityDescriptor = sdPtr,
                    bInheritHandle = 0
                };
                SafePipeHandle handle = CreateNamedPipeW(
                    name,
                    PIPE_ACCESS_OUTBOUND | FILE_FLAG_FIRST_PIPE_INSTANCE,
                    PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS,
                    1, 0, 0, 0, ref sa);
                int err = Marshal.GetLastWin32Error();
                LastError = err;
                return new PipeOpen { Handle = handle, Win32Error = (handle == null || handle.IsInvalid) ? err : 0 };
            }
            finally
            {
                if (sdPtr != IntPtr.Zero) Marshal.FreeHGlobal(sdPtr);
            }
        }
    }
}
