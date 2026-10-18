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

Export-ModuleMember -Function Test-ProbePrecheck
