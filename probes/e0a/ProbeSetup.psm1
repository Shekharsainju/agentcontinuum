Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'ProbeCommon.psm1')
Import-Module (Join-Path $PSScriptRoot 'ProbeNative.psm1')
Import-Module (Join-Path $PSScriptRoot 'ProbeSecret.psm1')
Import-Module (Join-Path $PSScriptRoot 'ProbeTeardown.psm1')

function Test-ProbeElevated {
  $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
  $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-ProbePrecheck {
  <# Reports elevation and seclogon state. Never starts the service. #>
  $elevated = [bool](Test-ProbeElevated)
  $svc = Get-Service -Name seclogon -ErrorAction SilentlyContinue
  $status = if ($svc) { [string]$svc.Status } else { 'Missing' }
  [pscustomobject]@{
    Ok             = ($elevated -and $status -eq 'Running')
    Elevated       = $elevated
    SeclogonStatus = $status
  }
}

function Get-ProbeAclSpec {
  <# Maps each probe folder to its allowed SIDs and rights. #>
  param(
    [Parameter(Mandatory)][string]$ReaderSid,
    [Parameter(Mandatory)][string]$CtlSid,
    [Parameter(Mandatory)][string]$AgentSid
  )
  $admins = @{ Sid = 'S-1-5-32-544'; Rights = 'FullControl' }
  @{
    'C:\ProgramData\ac-probe'          = @($admins, @{ Sid = $CtlSid; Rights = 'ReadAndExecute' }, @{ Sid = $AgentSid; Rights = 'ReadAndExecute' })
    'C:\ProgramData\ac-probe\bin'      = @($admins, @{ Sid = $CtlSid; Rights = 'ReadAndExecute' }, @{ Sid = $AgentSid; Rights = 'ReadAndExecute' })
    'C:\ProgramData\ac-probe\ctl'      = @($admins, @{ Sid = $CtlSid; Rights = 'FullControl' })
    'C:\ProgramData\ac-probe\exchange' = @($admins, @{ Sid = $AgentSid; Rights = 'Modify' }, @{ Sid = $CtlSid; Rights = 'Read' })
    'C:\ProgramData\ac-probe-results'  = @($admins, @{ Sid = $CtlSid; Rights = 'Modify' }, @{ Sid = $ReaderSid; Rights = 'ReadAndExecute' })
  }
}

function Set-ProbeFolders {
  <# Creates each folder and applies its spec with inheritance disabled and no inherited rules kept. #>
  param([Parameter(Mandatory)][hashtable]$Spec)
  $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
  $none = [Security.AccessControl.PropagationFlags]::None
  $allow = [Security.AccessControl.AccessControlType]::Allow
  foreach ($path in ($Spec.Keys | Sort-Object { $_.Length })) {
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($entry in $Spec[$path]) {
      $sid = New-Object Security.Principal.SecurityIdentifier($entry.Sid)
      $rights = [Security.AccessControl.FileSystemRights]$entry.Rights
      $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, $rights, $inherit, $none, $allow)))
    }
    Set-Acl -LiteralPath $path -AclObject $acl
  }
}

function New-ProbeAccount {
  <# Creates a standard local user and returns its SID string. #>
  param(
    [Parameter(Mandatory, Position = 0)][string]$Name,
    [Parameter(Mandatory, Position = 1)][securestring]$Password
  )
  $user = New-LocalUser -Name $Name -Password $Password -Description 'AgentContinuum E0a probe - safe to delete' `
    -PasswordNeverExpires -UserMayNotChangePassword -AccountNeverExpires
  Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $Name
  [string]$user.SID.Value
}

$script:RunTimeoutMs = 300000
$script:ResultsRoot = 'C:\ProgramData\ac-probe-results'
$script:ProbeRoot = 'C:\ProgramData\ac-probe'
$script:ProbeSourceDir = $PSScriptRoot

function Get-ProbeHarnessArguments {
  param([Parameter(Mandatory)]$Ctx, [Parameter(Mandatory)][string]$Run, [Parameter(Mandatory)][string]$PipeName)
  $harness = Join-Path $Ctx.BinDir 'Harness.ps1'
  '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Run {1} -OutDir "{2}" -Exchange "{3}" -BinDir "{4}" -CtlDir "{5}" -ResultsDir "{6}" -PipeName {7}' -f
    $harness, $Run, $Ctx.ResultsDir, $Ctx.ExchangeDir, $Ctx.BinDir, $Ctx.CtlDir, $Ctx.ResultsRoot, $PipeName
}

function Get-ProbeStrayProcess {
  param([Parameter(Mandatory)][string[]]$Sids)
  @(Get-CimInstance -ClassName Win32_Process -ErrorAction SilentlyContinue | Where-Object {
      $owner = $null
      try { $owner = (Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid -ErrorAction SilentlyContinue).Sid } catch {}
      $owner -and ($Sids -contains $owner)
    } | ForEach-Object { [int]$_.ProcessId })
}

function Start-ProbeProcess {
  param([Parameter(Mandatory)][Diagnostics.ProcessStartInfo]$StartInfo)
  [Diagnostics.Process]::Start($StartInfo)
}

function Invoke-ProbeRunB {
  <# Runs the harness as ctl through Secondary Logon and waits up to five minutes. #>
  param([Parameter(Mandatory)]$Ctx)
  $pipe = New-ProbeSecretPipe -AllowedSid $Ctx.CtlSid -Secret $Ctx.AgentPassword
  $outcome = 'done'
  try {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = 'powershell.exe'
    $psi.Arguments = Get-ProbeHarnessArguments -Ctx $Ctx -Run 'B' -PipeName $pipe.Name
    $psi.UserName = $Ctx.CtlName
    $psi.Password = $Ctx.CtlPassword
    $psi.Domain = '.'
    $psi.LoadUserProfile = $true
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $Ctx.BinDir
    try {
      $proc = Start-ProbeProcess -StartInfo $psi
      if (-not $proc.WaitForExit($script:RunTimeoutMs)) {
        $outcome = 'timeout'
        try { $proc.Kill() } catch {}
      }
    }
    catch { $outcome = 'start_failed' }
  }
  finally {
    if ($pipe.Job) { Remove-Job -Job $pipe.Job -Force -ErrorAction SilentlyContinue }
  }
  [pscustomobject]@{
    Outcome            = $outcome
    BatchBefore        = $false
    BatchAfterRegister = $false
    NeededGrant        = $false
    StrayAfterRun      = @(Get-ProbeStrayProcess -Sids @($Ctx.CtlSid, $Ctx.AgentSid))
  }
}

$script:LogonTypeNotGranted = [uint32]2147943785
$script:TaskNeverRunOrRunning = @([uint32]267009, [uint32]267011)

function Get-ProbeBatchRight {
  param([Parameter(Mandatory)][string]$Sid)
  @([AcProbe.Native]::AccountRights($Sid)) -contains 'SeBatchLogonRight'
}

function Add-ProbeBatchRight {
  param([Parameter(Mandatory)][string]$Sid)
  $code = [AcProbe.Native]::AddRight($Sid, 'SeBatchLogonRight')
  if ($code -ne 0) { throw "AddRight failed with Win32 error $code" }
}

function Add-ProbeManifestGrant {
  param([Parameter(Mandatory)]$Ctx, [Parameter(Mandatory)][string]$Sid, [Parameter(Mandatory)][string]$Right)
  $manifest = Get-Content -LiteralPath $Ctx.ManifestPath -Raw | ConvertFrom-Json
  $grants = @()
  if ($manifest.PSObject.Properties['granted_rights']) { $grants = @($manifest.granted_rights) }
  $grants += [pscustomobject]@{ sid = $Sid; right = $Right }
  $manifest | Add-Member -NotePropertyName granted_rights -NotePropertyValue $grants -Force
  $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Ctx.ManifestPath -Encoding UTF8
}

function Wait-ProbeRunA {
  param([Parameter(Mandatory)]$Ctx, [Parameter(Mandatory)][string]$TaskName)
  $resultsFile = Join-Path $Ctx.ResultsDir 'results-A.json'
  $deadline = (Get-Date).AddMilliseconds($script:RunTimeoutMs)
  while ($true) {
    if (Test-Path -LiteralPath $resultsFile) { return 'done' }
    $task = Get-ScheduledTask -TaskName $TaskName
    $last = [uint32](Get-ScheduledTaskInfo -TaskName $TaskName).LastTaskResult
    if ($task.State -ne 'Running' -and $script:TaskNeverRunOrRunning -notcontains $last) {
      if ($last -eq 0) { return 'done' }
      if ($last -eq $script:LogonTypeNotGranted) { return 'logon_failed' }
      return 'failed'
    }
    if ((Get-Date) -gt $deadline) {
      Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
      return 'timeout'
    }
    Start-Sleep -Seconds 2
  }
}

function Invoke-ProbeRunAAttempt {
  param([Parameter(Mandatory)]$Ctx)
  $taskName = 'ac-probe-ctl-runA'
  $pipe = New-ProbeSecretPipe -AllowedSid $Ctx.CtlSid -Secret $Ctx.AgentPassword
  try {
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument (Get-ProbeHarnessArguments -Ctx $Ctx -Run 'A' -PipeName $pipe.Name)
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
    Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -User $Ctx.CtlName `
      -Password (ConvertFrom-ProbeSecureString $Ctx.CtlPassword) -RunLevel Limited | Out-Null
    $afterRegister = Get-ProbeBatchRight -Sid $Ctx.CtlSid
    Start-ScheduledTask -TaskName $taskName
    [pscustomobject]@{ State = (Wait-ProbeRunA -Ctx $Ctx -TaskName $taskName); BatchAfterRegister = $afterRegister }
  }
  finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    if ($pipe.Job) { Remove-Job -Job $pipe.Job -Force -ErrorAction SilentlyContinue }
  }
}

function Invoke-ProbeRunA {
  <# Runs the harness as ctl from a scheduled task, granting the batch right once if logon was refused. #>
  param([Parameter(Mandatory)]$Ctx)
  $batchBefore = [bool](Get-ProbeBatchRight -Sid $Ctx.CtlSid)
  $neededGrant = $false
  $attempt = Invoke-ProbeRunAAttempt -Ctx $Ctx
  $afterRegister = [bool]$attempt.BatchAfterRegister
  if ($attempt.State -eq 'logon_failed' -and -not $batchBefore) {
    Add-ProbeBatchRight -Sid $Ctx.CtlSid
    Add-ProbeManifestGrant -Ctx $Ctx -Sid $Ctx.CtlSid -Right 'SeBatchLogonRight'
    $neededGrant = $true
    $attempt = Invoke-ProbeRunAAttempt -Ctx $Ctx
  }
  $outcome = switch ($attempt.State) { 'done' { 'done' } 'timeout' { 'timeout' } default { 'start_failed' } }
  [pscustomobject]@{
    Outcome            = $outcome
    BatchBefore        = $batchBefore
    BatchAfterRegister = $afterRegister
    NeededGrant        = $neededGrant
    StrayAfterRun      = @(Get-ProbeStrayProcess -Sids @($Ctx.CtlSid, $Ctx.AgentSid))
  }
}

function Resolve-ProbeAccountSid {
  param([string]$Name)
  if (-not $Name) { return [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
  (New-Object Security.Principal.NTAccount($Name)).Translate([Security.Principal.SecurityIdentifier]).Value
}

function Copy-ProbeBinaries {
  param([Parameter(Mandatory)][string]$Destination)
  Get-ChildItem -LiteralPath $script:ProbeSourceDir -File |
    Where-Object { $_.Name -notin 'ProbeSetup.psm1', 'Invoke-E0aProbe.ps1' } |
    Copy-Item -Destination $Destination
}

function Start-E0aProbe {
  <# Creates the probe accounts, runs A and B, and always tears down. Returns the process exit code. #>
  [CmdletBinding()]
  param([string]$ReaderAccount)

  $resultsDir = Join-Path $script:ResultsRoot (Get-Date -Format 'yyyyMMdd-HHmmss')
  $setup = New-ProbeResult -Run 'setup'
  $setupPath = Join-Path $resultsDir 'setup.json'

  $precheck = Test-ProbePrecheck
  $c1Outcome = if ($precheck.Ok) { 'pass' } else { 'fail' }
  Add-ProbeCheck -Result $setup -Id 'C1' -Name 'precheck' -Outcome $c1Outcome -Details @{
    elevated = $precheck.Elevated; seclogon = $precheck.SeclogonStatus }
  if (-not $precheck.Ok) {
    New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null
    Write-ProbeResult -Result $setup -Path $setupPath
    return 2
  }
  if (-not (Import-ProbeNative).Loaded) { Write-Host 'native helper failed to load'; return 3 }

  $preClean = Invoke-ProbeTeardown
  if (-not ($preClean.Steps | Where-Object { $_.Name -eq 'Verify' }).Ok) {
    Write-Host 'pre-clean could not verify a clean system, aborting'
    return 4
  }

  $exitCode = 0
  $ctx = $null
  try {
    New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null
    $ctlPassword = New-ProbePassword
    $agentPassword = New-ProbePassword
    $ctlSid = New-ProbeAccount -Name 'ac-probe-ctl' -Password $ctlPassword
    $agentSid = New-ProbeAccount -Name 'ac-probe-agent' -Password $agentPassword
    $manifestPath = Join-Path $resultsDir 'manifest.json'
    [ordered]@{ ctl_sid = $ctlSid; agent_sid = $agentSid; granted_rights = @() } |
      ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    $readerSid = Resolve-ProbeAccountSid -Name $ReaderAccount
    Set-ProbeFolders -Spec (Get-ProbeAclSpec -ReaderSid $readerSid -CtlSid $ctlSid -AgentSid $agentSid)
    $binDir = Join-Path $script:ProbeRoot 'bin'
    Copy-ProbeBinaries -Destination $binDir

    $ctx = [pscustomobject]@{
      ResultsRoot   = $script:ResultsRoot
      ResultsDir    = $resultsDir
      ManifestPath  = $manifestPath
      BinDir        = $binDir
      CtlDir        = Join-Path $script:ProbeRoot 'ctl'
      ExchangeDir   = Join-Path $script:ProbeRoot 'exchange'
      CtlName       = 'ac-probe-ctl'
      AgentName     = 'ac-probe-agent'
      CtlSid        = $ctlSid
      AgentSid      = $agentSid
      CtlPassword   = $ctlPassword
      AgentPassword = $agentPassword
    }
    Write-ProbeResult -Result $setup -Path $setupPath
    $agentBatchRight = [bool](Get-ProbeBatchRight -Sid $agentSid)
    try {
      $runA = Invoke-ProbeRunA -Ctx $ctx
      $s0Outcome = if ($runA.NeededGrant -or $runA.Outcome -ne 'done') { 'finding' } else { 'pass' }
      Add-ProbeCheck -Result $setup -Id 'S0' -Name 'ctl_batch_logon_right' -Outcome $s0Outcome -Details @{
        ctl_batch_before         = $runA.BatchBefore
        ctl_batch_after_register = $runA.BatchAfterRegister
        needed_grant             = $runA.NeededGrant
        run_a_outcome            = $runA.Outcome
        run_a_stray_pids         = @($runA.StrayAfterRun)
        agent_batch_right        = $agentBatchRight
      }
    }
    catch {
      Add-ProbeCheck -Result $setup -Id 'S0' -Name 'ctl_batch_logon_right' -Outcome 'error' -Details @{
        error = $_.Exception.Message; agent_batch_right = $agentBatchRight }
      throw
    }
    finally { Write-ProbeResult -Result $setup -Path $setupPath }
    $runB = Invoke-ProbeRunB -Ctx $ctx
    $s0 = @($setup.checks) | Where-Object { $_.id -eq 'S0' }
    $s0.details.run_b_outcome = $runB.Outcome
    $s0.details.run_b_stray_pids = @($runB.StrayAfterRun)
    if ($runB.Outcome -ne 'done') { $s0.result = 'finding' }
    Write-ProbeResult -Result $setup -Path $setupPath
  }
  catch {
    Write-Host "probe failed: $($_.Exception.Message)"
    $exitCode = 1
  }
  finally {
    if ($ctx) {
      try {
        $scan = Test-SecretAbsent -Paths @($ctx.BinDir, $resultsDir, $script:ProbeRoot) `
          -Secrets @((ConvertFrom-ProbeSecureString $ctx.CtlPassword), (ConvertFrom-ProbeSecureString $ctx.AgentPassword))
        $s1Outcome = if ($scan.Clean) { 'pass' } else { 'fail' }
        Add-ProbeCheck -Result $setup -Id 'S1' -Name 'secret_absent_on_disk' -Outcome $s1Outcome -Details @{
          hits = @($scan.Hits); unscanned = @($scan.Unscanned) }
      }
      catch {
        Add-ProbeCheck -Result $setup -Id 'S1' -Name 'secret_absent_on_disk' -Outcome 'error' -Details @{ error = $_.Exception.Message }
      }
      Write-ProbeResult -Result $setup -Path $setupPath
    }
    $finalTeardown = Invoke-ProbeTeardown
  }

  $verify = $finalTeardown.Steps | Where-Object { $_.Name -eq 'Verify' }
  $t0Outcome = if ($verify.Ok) { 'pass' } else { 'fail' }
  Add-ProbeCheck -Result $setup -Id 'T0' -Name 'teardown_verify' -Outcome $t0Outcome -Details @{ error = $verify.Error }
  Write-ProbeResult -Result $setup -Path $setupPath
  if (-not $verify.Ok) { $exitCode = 1 }
  Write-Host "results: $resultsDir"
  $exitCode
}

Export-ModuleMember -Function Test-ProbePrecheck, Get-ProbeAclSpec, Set-ProbeFolders, New-ProbeAccount, Invoke-ProbeRunA, Invoke-ProbeRunB, Start-E0aProbe
