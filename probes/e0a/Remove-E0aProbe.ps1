[CmdletBinding()]
param([switch]$IncludeResults)
Set-StrictMode -Version 2.0

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  Write-Host 'Remove-E0aProbe must run elevated.'
  exit 1
}

Import-Module (Join-Path $PSScriptRoot 'ProbeNative.psm1') -Force
$null = Import-ProbeNative
Import-Module (Join-Path $PSScriptRoot 'ProbeTeardown.psm1') -Force

$result = Invoke-ProbeTeardown -IncludeResults:$IncludeResults
foreach ($s in $result.Steps) {
  $status = if ($s.Ok) { 'ok' } else { 'FAILED' }
  $line = "$($s.Name): $status"
  if ($s.Error) { $line += " ($($s.Error))" }
  Write-Host $line
}

$verify = $result.Steps | Where-Object { $_.Name -eq 'Verify' }
if ($verify -and $verify.Ok) { exit 0 }
exit 1
