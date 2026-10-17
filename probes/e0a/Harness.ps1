# Harness.ps1 - E0a probe harness. Self mode runs C3-C6 as the current standard user, with the
# child started by plain CreateProcess (no accounts, no elevation). A/B modes (later task) will
# additionally run C2, C7, C8 under ac-agent via Ctx.Launch backed by CreateProcessWithLogonW.
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidateSet('A', 'B', 'self')][string]$Run,
  [Parameter(Mandatory)][string]$OutDir,
  [Parameter(Mandatory)][string]$Exchange,
  [Parameter(Mandatory)][string]$BinDir,
  [string]$CtlDir,
  [string]$ResultsDir,
  [string]$PipeName   # reserved for A/B secret handoff to SubHarness.ps1; unused in self mode.
)

Set-StrictMode -Version 2.0

Import-Module (Join-Path $BinDir 'ProbeCommon.psm1') -Force
Import-Module (Join-Path $BinDir 'ProbeNative.psm1') -Force

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$result = New-ProbeResult -Run $Run
$native = Import-ProbeNative

if (-not $native.Loaded) {
  Add-ProbeCheck -Result $result -Id 'native' -Name 'native_load' -Outcome 'native_load_blocked' -Details @{ error = $native.Error }
  Write-ProbeResult -Result $result -Path (Join-Path $OutDir "results-$Run.json")
  return
}

Import-Module (Join-Path $BinDir 'HarnessChecks.psm1') -Force

New-Item -ItemType Directory -Force -Path $Exchange | Out-Null

# Current process's pseudo-handle works fine with IsProcessInJob.
$selfHandle = [System.Diagnostics.Process]::GetCurrentProcess().Handle
$result['harness_in_job_before'] = [AcProbe.Native]::IsInJob($selfHandle, [IntPtr]::Zero)

$ChildPath = Join-Path $BinDir 'Child.ps1'
$Launch = {
  param($Mode, $ExtraArgs, $Suspended)
  $cmd = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$ChildPath`" -Mode $Mode $ExtraArgs"
  [AcProbe.Native]::StartSelf($cmd, [bool]$Suspended, $false)
}.GetNewClosure()

$Ctx = @{
  Launch     = $Launch
  Exchange   = $Exchange
  BinDir     = $BinDir
  CtlDir     = $CtlDir
  ResultsDir = $ResultsDir
}

if ($Run -eq 'self') {
  Invoke-C3FreezeThaw -Result $result -Ctx $Ctx
  Invoke-C4Terminate -Result $result -Ctx $Ctx
  Invoke-C5KillOnClose -Result $result -Ctx $Ctx
  Invoke-C6Breakaway -Result $result -Ctx $Ctx
}
else {
  Invoke-C2LaunchAssign -Result $result -Ctx $Ctx
  Invoke-C3FreezeThaw -Result $result -Ctx $Ctx
  Invoke-C4Terminate -Result $result -Ctx $Ctx
  Invoke-C5KillOnClose -Result $result -Ctx $Ctx
  Invoke-C6Breakaway -Result $result -Ctx $Ctx
  Invoke-C7Escape -Result $result -Ctx $Ctx
  Invoke-C8Isolation -Result $result -Ctx $Ctx
}

Write-ProbeResult -Result $result -Path (Join-Path $OutDir "results-$Run.json")
