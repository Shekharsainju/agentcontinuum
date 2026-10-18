Set-StrictMode -Version 2.0

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

Export-ModuleMember -Function Test-ProbePrecheck, Get-ProbeAclSpec, Set-ProbeFolders
