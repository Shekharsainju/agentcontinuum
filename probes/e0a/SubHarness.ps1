# SubHarness.ps1 - started by Harness.ps1 (C5) to prove kill-on-job-close across a process
# boundary. Creates its own kill-on-close job, starts a grandchild child under it, prints
# "CHILD <pid> <creationFileTime>" on stdout, then sleeps -- holding the job handle open for
# the lifetime of this process. When the harness kills this process (KillPid), Windows closes
# all of this process's handles, including the job's last handle, which triggers
# JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE and kills the grandchild.
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Exchange,
  [Parameter(Mandatory)][string]$BinDir,
  [Parameter(Mandatory)][string]$Run,
  [switch]$SecretFromStdin   # not used in self mode; reserved for A/B secret handoff.
)

Set-StrictMode -Version 2.0

Import-Module (Join-Path $BinDir 'ProbeNative.psm1') -Force

$native = Import-ProbeNative
if (-not $native.Loaded) {
  Write-Output "ERROR native_load_blocked $($native.Error)"
  exit 3
}

if ($SecretFromStdin) {
  # Reserved for A/B: reads a one-line secret from stdin before doing anything else.
  $null = [Console]::In.ReadLine()
}

New-Item -ItemType Directory -Force -Path $Exchange | Out-Null
$childExchange = Join-Path $Exchange 'grandchild'
$childPath = Join-Path $BinDir 'Child.ps1'

$job = [AcProbe.Native]::CreateKillOnCloseJob()
if ($job -eq [IntPtr]::Zero) {
  Write-Output "ERROR job_create_failed $([AcProbe.Native]::LastError)"
  exit 4
}

$cmd = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$childPath`" -Exchange `"$childExchange`" -Mode Heartbeat"
$proc = [AcProbe.Native]::StartSelf($cmd, $true, $false)
if ($proc.Win32Error -ne 0) {
  Write-Output "ERROR start_failed $($proc.Win32Error)"
  exit 5
}

if (-not [AcProbe.Native]::Assign($job, $proc.Process)) {
  Write-Output "ERROR assign_failed $([AcProbe.Native]::LastError)"
  exit 6
}
[void][AcProbe.Native]::Resume($proc.Thread)

$creation = [AcProbe.Native]::CreationTime($proc.Pid)
Write-Output "CHILD $($proc.Pid) $creation"
[Console]::Out.Flush()

# Hold the job handle open (it lives in $job for the rest of this process's life) and just
# sleep. The harness is expected to kill this process well before the timeout elapses.
Start-Sleep -Seconds 120
