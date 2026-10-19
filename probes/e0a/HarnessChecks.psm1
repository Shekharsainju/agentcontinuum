# HarnessChecks.psm1 - one Invoke-<Id> function per E0a probe check (Windows PowerShell 5.1).
# Each function: -Result <hashtable> -Ctx <hashtable> -> void. Each adds exactly one check via
# Add-ProbeCheck, except Invoke-C3FreezeThaw which adds two (C3, C3b). Every function is
# internally try/catch-guarded so an exception becomes an `error` outcome and never escapes.
Set-StrictMode -Version 2.0

# No -Force here: a -Force re-import from inside a module detaches any existing global-scope
# binding the caller (Harness.ps1 / a test) already made for these modules. Importing without
# -Force is a no-op if already loaded, and otherwise loads privately into this module's scope,
# which is all Invoke-C* need since they call these functions from within this module.
Import-Module (Join-Path $PSScriptRoot 'ProbeCommon.psm1')
Import-Module (Join-Path $PSScriptRoot 'ProbeNative.psm1')

function New-CheckExchange {
  param([Parameter(Mandatory)][string]$Exchange, [Parameter(Mandatory)][string]$Id)
  $p = Join-Path $Exchange $Id
  New-Item -ItemType Directory -Force -Path $p | Out-Null
  $p
}

function Get-C5ExchangeBase {
  param([Parameter(Mandatory)][hashtable]$Ctx)
  # C5 spawns its sub-harness and grandchild as the harness account (ctl under A/B), which has only
  # read access to the agent exchange; its scratch goes in the ctl-owned folder so those processes
  # can create and write it. Self mode passes no CtlDir and falls back to the writable exchange.
  if ($Ctx.ContainsKey('CtlDir') -and $Ctx.CtlDir) { return $Ctx.CtlDir }
  $Ctx.Exchange
}

function Wait-ForFile {
  # Polls for $Path to exist and parse as JSON; returns $null on timeout.
  param([Parameter(Mandatory)][string]$Path, [double]$TimeoutSeconds = 10)
  $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
  while ([datetime]::UtcNow -lt $deadline) {
    if (Test-Path -LiteralPath $Path) {
      try { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) } catch {}
    }
    Start-Sleep -Milliseconds 200
  }
  $null
}

function Invoke-C2LaunchAssign {
  [CmdletBinding()]
  param([Parameter(Mandatory)][hashtable]$Result, [Parameter(Mandatory)][hashtable]$Ctx)
  $outcome = 'error'; $details = @{}
  $job = [IntPtr]::Zero; $proc = $null
  try {
    $job = [AcProbe.Native]::CreateKillOnCloseJob()
    if ($job -eq [IntPtr]::Zero) { throw "job creation failed, Win32Error=$([AcProbe.Native]::LastError)" }
    $exch = New-CheckExchange -Exchange $Ctx.Exchange -Id 'C2'
    $proc = & $Ctx.Launch 'Heartbeat' "-Exchange `"$exch`"" $true
    if ($proc.Win32Error -ne 0) { throw "launch failed, Win32Error=$($proc.Win32Error)" }
    $details['child_pid'] = $proc.Pid
    $details['child_in_job_before'] = [AcProbe.Native]::IsInJob($proc.Process, [IntPtr]::Zero)
    $assigned = [AcProbe.Native]::Assign($job, $proc.Process)
    $details['assign_win32_error'] = [AcProbe.Native]::LastError
    [void][AcProbe.Native]::Resume($proc.Thread)
    $inJob = [AcProbe.Native]::IsInJob($proc.Process, $job)
    $pids = [AcProbe.Native]::JobPids($job)
    $details['job_has_pid'] = [bool]($pids -contains $proc.Pid)
    $outcome = if ($assigned -and $inJob) { 'pass' } else { 'fail' }
  }
  catch {
    $outcome = 'error'
    $details['error'] = [string]$_.Exception.Message
    if ($proc) { $details['child_pid'] = $proc.Pid }
  }
  finally {
    if ($proc -and $proc.Pid) { try { [void][AcProbe.Native]::KillPid($proc.Pid) } catch {} }
    if ($job -ne [IntPtr]::Zero) { try { [void][AcProbe.Native]::TerminateJob($job) } catch {} }
  }
  Add-ProbeCheck -Result $Result -Id 'C2' -Name 'launch_and_assign' -Outcome $outcome -Details $details
}

function Invoke-C3FreezeThaw {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][hashtable]$Result,
    [Parameter(Mandatory)][hashtable]$Ctx,
    [switch]$FailAfterFreeze   # hidden, test-only: throws after freezing but before thawing.
  )
  $job = [IntPtr]::Zero
  $proc = $null
  $addedC3 = $false
  $addedC3b = $false
  $c3Outcome = 'error'; $c3Details = @{}
  $c3bOutcome = 'error'; $c3bDetails = @{}

  try {
    $job = [AcProbe.Native]::CreateKillOnCloseJob()
    if ($job -eq [IntPtr]::Zero) { throw "job creation failed, Win32Error=$([AcProbe.Native]::LastError)" }
    $exch = New-CheckExchange -Exchange $Ctx.Exchange -Id 'C3'
    $beatPath = Join-Path $exch 'heartbeat.txt'
    $proc = & $Ctx.Launch 'Heartbeat' "-Exchange `"$exch`"" $true
    if ($proc.Win32Error -ne 0) { throw "launch failed, Win32Error=$($proc.Win32Error)" }
    $c3Details['child_pid'] = $proc.Pid
    $c3bDetails['child_pid'] = $proc.Pid
    if (-not [AcProbe.Native]::Assign($job, $proc.Process)) { throw "assign failed, Win32Error=$([AcProbe.Native]::LastError)" }
    [void][AcProbe.Native]::Resume($proc.Thread)

    # Wait for the first heartbeat before freezing, because child process startup time (new
    # powershell.exe + module load) is variable, so this must not race ahead regardless.
    $deadline0 = [datetime]::UtcNow.AddSeconds(15)
    while ((@(Read-Heartbeat $beatPath)).Count -eq 0 -and [datetime]::UtcNow -lt $deadline0) { Start-Sleep -Milliseconds 100 }
    if ((@(Read-Heartbeat $beatPath)).Count -eq 0) { throw 'child did not heartbeat within 15 s of launch' }

    $frozen = $false
    try {
      $freezeAt = [datetime]::UtcNow
      $freezeStatus = [AcProbe.Native]::SetJobFreeze($job, $true)
      $c3Details['freeze_ntstatus'] = $freezeStatus
      if ($freezeStatus -ge 0) { $frozen = $true }
      if ($FailAfterFreeze) { throw 'injected failure after freeze (test only)' }
      Start-Sleep -Seconds 6
      $thawAt = [datetime]::UtcNow
      $thawStatus = [AcProbe.Native]::SetJobFreeze($job, $false)
      $frozen = $false
      $c3Details['thaw_ntstatus'] = $thawStatus
      Start-Sleep -Seconds 3
      $beats = Read-Heartbeat $beatPath
      $verdict = Get-FreezeVerdict -Beats $beats -FreezeAt $freezeAt -ThawAt $thawAt
      $c3Details['gap_seconds'] = $verdict.GapSeconds
      $c3Details['resume_seconds'] = $verdict.ResumeSeconds
      $c3Details['beats_during_freeze'] = $verdict.BeatsDuringFreeze
      $c3Outcome = if ($freezeStatus -lt 0 -or $thawStatus -lt 0) { 'error' } else { $verdict.Result }
    }
    finally {
      if ($frozen) {
        $thawStatus = [AcProbe.Native]::SetJobFreeze($job, $false)
        $frozen = $false
        $c3Details['thaw_ntstatus'] = $thawStatus
        if ($thawStatus -lt 0) { $c3Details['thaw_error_win32'] = [AcProbe.Native]::LastError }
      }
    }
    Add-ProbeCheck -Result $Result -Id 'C3' -Name 'freeze_and_thaw' -Outcome $c3Outcome -Details $c3Details
    $addedC3 = $true

    # C3b: secondary suspend/resume test on the held child handle (covers only the child).
    Start-Sleep -Milliseconds 500
    try {
      $suspendAt = [datetime]::UtcNow
      $suspendStatus = [AcProbe.Native]::SuspendProcess($proc.Process)
      Start-Sleep -Seconds 6
      $resumeAt = [datetime]::UtcNow
      $resumeStatus = [AcProbe.Native]::ResumeProcess($proc.Process)
      Start-Sleep -Seconds 3
      $beats2 = Read-Heartbeat $beatPath
      $verdict2 = Get-FreezeVerdict -Beats $beats2 -FreezeAt $suspendAt -ThawAt $resumeAt
      $c3bDetails['suspend_ntstatus'] = $suspendStatus
      $c3bDetails['resume_ntstatus'] = $resumeStatus
      $c3bDetails['gap_seconds'] = $verdict2.GapSeconds
      $c3bDetails['resume_seconds'] = $verdict2.ResumeSeconds
      $c3bDetails['beats_during_freeze'] = $verdict2.BeatsDuringFreeze
      $c3bOutcome = if ($suspendStatus -lt 0 -or $resumeStatus -lt 0) { 'error' } else { $verdict2.Result }
    }
    finally {
      [void][AcProbe.Native]::ResumeProcess($proc.Process)
    }
    Add-ProbeCheck -Result $Result -Id 'C3b' -Name 'suspend_resume' -Outcome $c3bOutcome -Details $c3bDetails
    $addedC3b = $true
  }
  catch {
    $msg = [string]$_.Exception.Message
    if (-not $addedC3) {
      $c3Details['error'] = $msg
      Add-ProbeCheck -Result $Result -Id 'C3' -Name 'freeze_and_thaw' -Outcome 'error' -Details $c3Details
      $addedC3 = $true
    }
    if (-not $addedC3b) {
      $c3bDetails['error'] = $msg
      Add-ProbeCheck -Result $Result -Id 'C3b' -Name 'suspend_resume' -Outcome 'error' -Details $c3bDetails
      $addedC3b = $true
    }
  }
  finally {
    if ($proc -and $proc.Process) { try { [void][AcProbe.Native]::ResumeProcess($proc.Process) } catch {} }
    if (-not $FailAfterFreeze) {
      # FailAfterFreeze is test-only and the test owns cleanup so it can observe the resumed
      # heartbeat; the normal path cleans up its own child and job.
      if ($proc -and $proc.Pid) { try { [void][AcProbe.Native]::KillPid($proc.Pid) } catch {} }
      if ($job -ne [IntPtr]::Zero) { try { [void][AcProbe.Native]::TerminateJob($job) } catch {} }
    }
  }
}

function Invoke-C4Terminate {
  [CmdletBinding()]
  param([Parameter(Mandatory)][hashtable]$Result, [Parameter(Mandatory)][hashtable]$Ctx)
  $outcome = 'error'; $details = @{}
  $job = [IntPtr]::Zero; $proc = $null
  try {
    $job = [AcProbe.Native]::CreateKillOnCloseJob()
    if ($job -eq [IntPtr]::Zero) { throw "job creation failed, Win32Error=$([AcProbe.Native]::LastError)" }
    $exch = New-CheckExchange -Exchange $Ctx.Exchange -Id 'C4'
    $beatPath = Join-Path $exch 'heartbeat.txt'
    $proc = & $Ctx.Launch 'Grandchildren' "-Exchange `"$exch`"" $true
    if ($proc.Win32Error -ne 0) { throw "launch failed, Win32Error=$($proc.Win32Error)" }
    $details['child_pid'] = $proc.Pid
    if (-not [AcProbe.Native]::Assign($job, $proc.Process)) { throw "assign failed, Win32Error=$([AcProbe.Native]::LastError)" }
    [void][AcProbe.Native]::Resume($proc.Thread)

    # Give the child and its two grandchildren time to start heartbeating. Child-process
    # startup time is variable, so wait generously rather than racing ahead regardless.
    $deadline0 = [datetime]::UtcNow.AddSeconds(15)
    while ((@(Read-Heartbeat $beatPath)).Count -eq 0 -and [datetime]::UtcNow -lt $deadline0) { Start-Sleep -Milliseconds 100 }
    if ((@(Read-Heartbeat $beatPath)).Count -eq 0) { throw 'child did not heartbeat within 15 s of launch' }
    Start-Sleep -Milliseconds 500

    $termOk = [AcProbe.Native]::TerminateJob($job)
    $details['terminate_ok'] = $termOk
    $details['terminate_win32_error'] = [AcProbe.Native]::LastError

    $deadline1 = [datetime]::UtcNow.AddSeconds(5)
    $stopped = $false; $active = -1
    while ([datetime]::UtcNow -lt $deadline1) {
      $active = [AcProbe.Native]::JobActive($job)
      $before = (@(Read-Heartbeat $beatPath)).Count
      Start-Sleep -Milliseconds 300
      $after = (@(Read-Heartbeat $beatPath)).Count
      if ($active -eq 0 -and $after -eq $before) { $stopped = $true; break }
    }
    $details['active_processes'] = $active
    $details['heartbeat_stopped'] = $stopped
    $outcome = if ($termOk -and $stopped) { 'pass' } else { 'fail' }
  }
  catch {
    $outcome = 'error'
    $details['error'] = [string]$_.Exception.Message
    if ($proc) { $details['child_pid'] = $proc.Pid }
  }
  finally {
    if ($job -ne [IntPtr]::Zero) { try { [void][AcProbe.Native]::TerminateJob($job) } catch {} }
    if ($proc -and $proc.Pid) { try { [void][AcProbe.Native]::KillPid($proc.Pid) } catch {} }
  }
  Add-ProbeCheck -Result $Result -Id 'C4' -Name 'terminate' -Outcome $outcome -Details $details
}

function Invoke-C5KillOnClose {
  [CmdletBinding()]
  param([Parameter(Mandatory)][hashtable]$Result, [Parameter(Mandatory)][hashtable]$Ctx)
  $outcome = 'error'; $details = @{}
  $sub = $null
  try {
    $exch = New-CheckExchange -Exchange (Get-C5ExchangeBase -Ctx $Ctx) -Id 'C5'
    $subHarness = Join-Path $Ctx.BinDir 'SubHarness.ps1'
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'powershell.exe'
    $psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$subHarness`" -Exchange `"$exch`" -BinDir `"$($Ctx.BinDir)`" -Run self"
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardInput = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $sub = [System.Diagnostics.Process]::Start($psi)
    $details['sub_pid'] = $sub.Id
    $sub.StandardInput.Close()   # self mode: no secret to hand down.

    $readTask = $sub.StandardOutput.ReadLineAsync()
    if (-not $readTask.Wait(10000)) { throw 'timeout waiting for CHILD line from sub-harness' }
    $line = $readTask.Result
    if ($null -eq $line -or $line -notmatch '^CHILD (\d+) (\d+)$') { throw "unexpected sub-harness output: '$line'" }
    $gPid = [int]$Matches[1]
    $gCreation = [long]$Matches[2]
    $details['grandchild_pid'] = $gPid
    $details['grandchild_creation'] = $gCreation

    [void][AcProbe.Native]::KillPid($sub.Id)

    $beatPath = Join-Path (Join-Path $exch 'grandchild') 'heartbeat.txt'
    $deadline = [datetime]::UtcNow.AddSeconds(5)
    $gone = $false
    while ([datetime]::UtcNow -lt $deadline) {
      $cur = [AcProbe.Native]::CreationTime($gPid)
      $pidGone = ($cur -eq -1) -or ($cur -ne $gCreation)
      if ($pidGone) {
        $before = (@(Read-Heartbeat $beatPath)).Count
        Start-Sleep -Seconds 1
        $after = (@(Read-Heartbeat $beatPath)).Count
        if ($after -eq $before) { $gone = $true; break }
      }
      Start-Sleep -Milliseconds 200
    }
    $details['grandchild_gone'] = $gone
    $outcome = if ($gone) { 'pass' } else { 'fail' }
  }
  catch {
    $outcome = 'error'
    $details['error'] = [string]$_.Exception.Message
  }
  finally {
    if ($sub -and -not $sub.HasExited) { try { [void][AcProbe.Native]::KillPid($sub.Id) } catch {} }
    if ($details.ContainsKey('grandchild_pid') -and $details.ContainsKey('grandchild_creation')) {
      try { [void][AcProbe.Native]::KillPidIfCreation([int]$details['grandchild_pid'], [long]$details['grandchild_creation']) } catch {}
    }
  }
  Add-ProbeCheck -Result $Result -Id 'C5' -Name 'kill_on_job_close' -Outcome $outcome -Details $details
}

function Invoke-C6Breakaway {
  [CmdletBinding()]
  param([Parameter(Mandatory)][hashtable]$Result, [Parameter(Mandatory)][hashtable]$Ctx)
  $outcome = 'error'; $details = @{}
  $job = [IntPtr]::Zero; $proc = $null; $b = $null
  try {
    $job = [AcProbe.Native]::CreateKillOnCloseJob()
    if ($job -eq [IntPtr]::Zero) { throw "job creation failed, Win32Error=$([AcProbe.Native]::LastError)" }
    $exch = New-CheckExchange -Exchange $Ctx.Exchange -Id 'C6'
    $proc = & $Ctx.Launch 'Breakaway' "-Exchange `"$exch`"" $true
    if ($proc.Win32Error -ne 0) { throw "launch failed, Win32Error=$($proc.Win32Error)" }
    $details['child_pid'] = $proc.Pid
    if (-not [AcProbe.Native]::Assign($job, $proc.Process)) { throw "assign failed, Win32Error=$([AcProbe.Native]::LastError)" }
    [void][AcProbe.Native]::Resume($proc.Thread)

    $b = Wait-ForFile -Path (Join-Path $exch 'breakaway.json') -TimeoutSeconds 15
    if (-not $b) { throw 'breakaway.json did not appear within 15 s' }
    $details['child_reported_ok'] = $b.ok
    $details['child_win32_error'] = $b.win32_error
    $details['grandchild_pid'] = $b.pid
    if ($b.PSObject.Properties['creation']) { $details['grandchild_creation'] = $b.creation }

    if (-not $b.ok) {
      $outcome = 'pass'   # breakaway was rejected, so it cannot escape the job.
    }
    else {
      $pids = [AcProbe.Native]::JobPids($job)
      $details['job_pids'] = ($pids -join ',')
      $contained = [bool]($pids -contains [int]$b.pid)
      $outcome = if ($contained) { 'pass' } else { 'fail' }
    }
  }
  catch {
    $outcome = 'error'
    $details['error'] = [string]$_.Exception.Message
    if ($proc) { $details['child_pid'] = $proc.Pid }
  }
  finally {
    if ($b -and $b.ok -and $b.pid -and $b.PSObject.Properties['creation']) {
      try { [void][AcProbe.Native]::KillPidIfCreation([int]$b.pid, [long]$b.creation) } catch {}
    }
    if ($proc -and $proc.Pid) { try { [void][AcProbe.Native]::KillPid($proc.Pid) } catch {} }
    if ($job -ne [IntPtr]::Zero) { try { [void][AcProbe.Native]::TerminateJob($job) } catch {} }
  }
  Add-ProbeCheck -Result $Result -Id 'C6' -Name 'breakaway' -Outcome $outcome -Details $details
}

function Invoke-C7Escape {
  [CmdletBinding()]
  param([Parameter(Mandatory)][hashtable]$Result, [Parameter(Mandatory)][hashtable]$Ctx)
  $outcome = 'error'; $details = @{}
  $proc = $null
  try {
    $exch = New-CheckExchange -Exchange $Ctx.Exchange -Id 'C7'
    $proc = & $Ctx.Launch 'Escape' "-Exchange `"$exch`"" $false
    if ($proc.Win32Error -ne 0) { throw "launch failed, Win32Error=$($proc.Win32Error)" }
    $details['child_pid'] = $proc.Pid

    $e = Wait-ForFile -Path (Join-Path $exch 'escape.json') -TimeoutSeconds 15
    if (-not $e) { throw 'escape.json did not appear within 15 s' }
    $details['registered'] = $e.registered
    $details['register_error'] = $e.register_error
    $details['started'] = $e.started
    $details['start_error'] = $e.start_error
    $outcome = 'finding'   # C7 always records a finding, per spec, never pass/fail.
  }
  catch {
    $outcome = 'error'
    $details['error'] = [string]$_.Exception.Message
  }
  finally {
    if ($proc -and $proc.Pid) { try { [void][AcProbe.Native]::KillPid($proc.Pid) } catch {} }
    try { Unregister-ScheduledTask -TaskName 'ac-probe-agent-escape' -Confirm:$false -ErrorAction SilentlyContinue } catch {}
  }
  Add-ProbeCheck -Result $Result -Id 'C7' -Name 'escape_finding' -Outcome $outcome -Details $details
}

function Invoke-C8Isolation {
  [CmdletBinding()]
  param([Parameter(Mandatory)][hashtable]$Result, [Parameter(Mandatory)][hashtable]$Ctx)
  $outcome = 'error'; $details = @{}
  $proc = $null
  try {
    $exch = New-CheckExchange -Exchange $Ctx.Exchange -Id 'C8'
    $extra = "-Exchange `"$exch`" -HarnessPid $PID -CtlDir `"$($Ctx.CtlDir)`" -ResultsDir `"$($Ctx.ResultsDir)`""
    $proc = & $Ctx.Launch 'Isolation' $extra $false
    if ($proc.Win32Error -ne 0) { throw "launch failed, Win32Error=$($proc.Win32Error)" }
    $details['child_pid'] = $proc.Pid

    $arr = Wait-ForFile -Path (Join-Path $exch 'isolation.json') -TimeoutSeconds 15
    if (-not $arr) { throw 'isolation.json did not appear within 15 s' }
    $failedAll = $true
    foreach ($entry in @($arr)) { if ($entry.ok) { $failedAll = $false } }
    $details['entries'] = @($arr)
    $outcome = if ($failedAll) { 'pass' } else { 'fail' }
  }
  catch {
    $outcome = 'error'
    $details['error'] = [string]$_.Exception.Message
  }
  finally {
    if ($proc -and $proc.Pid) { try { [void][AcProbe.Native]::KillPid($proc.Pid) } catch {} }
  }
  Add-ProbeCheck -Result $Result -Id 'C8' -Name 'isolation' -Outcome $outcome -Details $details
}

Export-ModuleMember -Function Invoke-C2LaunchAssign, Invoke-C3FreezeThaw, Invoke-C4Terminate,
  Invoke-C5KillOnClose, Invoke-C6Breakaway, Invoke-C7Escape, Invoke-C8Isolation, Get-C5ExchangeBase
