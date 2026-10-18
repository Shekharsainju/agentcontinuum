Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'ProbeSecret.psm1')

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
    'C:\ProgramData\ac-probe\ctl'      = @($admins, @{ Sid = $CtlSid; Rights = 'Modify' })
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

Export-ModuleMember -Function Test-ProbePrecheck, Get-ProbeAclSpec, Set-ProbeFolders, New-ProbeAccount, Invoke-ProbeRunB
