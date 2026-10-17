# Child.ps1 - synthetic agent child process for the E0a probe (Windows PowerShell 5.1).
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Exchange,
  [Parameter(Mandatory)][ValidateSet('Heartbeat', 'Grandchildren', 'Breakaway', 'Escape', 'Isolation')][string]$Mode,
  [int]$HarnessPid,
  [string]$CtlDir,
  [string]$ResultsDir,
  [string]$BinDir,
  [string]$BeatFile = 'heartbeat.txt'
)

Set-StrictMode -Version 2.0

New-Item -ItemType Directory -Force -Path $Exchange | Out-Null
$ExchangeFull = (Resolve-Path -LiteralPath $Exchange).ProviderPath

function Write-ProbeJson {
  param([Parameter(Mandatory)]$Data, [Parameter(Mandatory)][string]$Path)
  $json = $Data | ConvertTo-Json -Depth 8
  [IO.File]::WriteAllText($Path, $json, (New-Object Text.UTF8Encoding($false)))
}

function Test-DirAccess {
  # Simulates an escaped agent probing read/write access to a harness-owned directory.
  param([string]$Dir, [ValidateSet('read', 'write')][string]$AccessMode)
  try {
    if ([string]::IsNullOrEmpty($Dir)) { throw 'no directory supplied' }
    if ($AccessMode -eq 'read') {
      Get-ChildItem -LiteralPath $Dir -Force -ErrorAction Stop | Out-Null
    }
    else {
      $probeFile = Join-Path $Dir ('probe-isolation-' + [guid]::NewGuid().ToString('N') + '.tmp')
      [IO.File]::WriteAllText($probeFile, 'probe')
      Remove-Item -LiteralPath $probeFile -Force -ErrorAction SilentlyContinue
    }
    [pscustomobject]@{ Ok = $true; Error = '' }
  }
  catch {
    [pscustomobject]@{ Ok = $false; Error = [string]$_.Exception.Message }
  }
}

function Write-Heartbeat {
  # Appends an ISO-8601 UTC timestamp every 250 ms until killed, or for up to 120 s.
  param([Parameter(Mandatory)][string]$Path, [int]$Seconds = 120)
  $deadline = [datetime]::UtcNow.AddSeconds($Seconds)
  while ([datetime]::UtcNow -lt $deadline) {
    Add-Content -LiteralPath $Path -Value ([datetime]::UtcNow.ToString('o'))
    Start-Sleep -Milliseconds 250
  }
}

Import-Module "$PSScriptRoot\ProbeNative.psm1" -Force

$needsNative = $Mode -in @('Breakaway', 'Isolation')
if ($needsNative) {
  $native = Import-ProbeNative
  if (-not $native.Loaded) {
    Write-ProbeJson -Data ([ordered]@{ result = 'native_load_blocked'; error = $native.Error }) `
      -Path (Join-Path $ExchangeFull 'child-native.json')
    exit 3
  }
}

switch ($Mode) {
  'Heartbeat' {
    # Nothing beyond the universal heartbeat loop below.
  }
  'Grandchildren' {
    foreach ($beat in @('heartbeat-g1.txt', 'heartbeat-g2.txt')) {
      $args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Exchange `"$ExchangeFull`" -Mode Heartbeat -BeatFile $beat"
      Start-Process powershell.exe $args -WindowStyle Hidden | Out-Null
    }
  }
  'Breakaway' {
    $cmdLine = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Exchange `"$ExchangeFull`" -Mode Heartbeat -BeatFile heartbeat-breakaway.txt"
    $result = [AcProbe.Native]::StartSelf($cmdLine, $false, $true)
    Write-ProbeJson -Data ([ordered]@{
        ok          = ($result.Win32Error -eq 0)
        win32_error = $result.Win32Error
        pid         = $result.Pid
      }) -Path (Join-Path $ExchangeFull 'breakaway.json')
  }
  'Escape' {
    $registered = $false; $registerError = ''
    $started = $false; $startError = ''
    try {
      $beaconScript = Join-Path $PSScriptRoot 'Beacon.ps1'
      $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType S4U
      $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$beaconScript`" -Exchange `"$ExchangeFull`""
      Register-ScheduledTask -TaskName 'ac-probe-agent-escape' -Principal $principal -Action $action -Force -ErrorAction Stop | Out-Null
      $registered = $true
    }
    catch {
      $registerError = [string]$_.Exception.Message
    }
    if ($registered) {
      try {
        Start-ScheduledTask -TaskName 'ac-probe-agent-escape' -ErrorAction Stop
        $started = $true
      }
      catch {
        $startError = [string]$_.Exception.Message
      }
    }
    Write-ProbeJson -Data ([ordered]@{
        registered     = $registered
        register_error = $registerError
        started        = $started
        start_error    = $startError
      }) -Path (Join-Path $ExchangeFull 'escape.json')
  }
  'Isolation' {
    $ctl = Test-DirAccess -Dir $CtlDir -AccessMode 'read'
    $ctlW = Test-DirAccess -Dir $CtlDir -AccessMode 'write'
    $res = Test-DirAccess -Dir $ResultsDir -AccessMode 'read'
    $resW = Test-DirAccess -Dir $ResultsDir -AccessMode 'write'

    $termOpen = [AcProbe.Native]::TryOpen($HarnessPid, 0x0001)
    $vmOpen = [AcProbe.Native]::TryOpen($HarnessPid, (0x0020 -bor 0x0008))

    $entries = @(
      [ordered]@{ name = 'ctl_read'; ok = $ctl.Ok; error = $ctl.Error }
      [ordered]@{ name = 'ctl_write'; ok = $ctlW.Ok; error = $ctlW.Error }
      [ordered]@{ name = 'results_read'; ok = $res.Ok; error = $res.Error }
      [ordered]@{ name = 'results_write'; ok = $resW.Ok; error = $resW.Error }
      [ordered]@{ name = 'open_harness_terminate'; ok = $termOpen.Ok; error = $(if ($termOpen.Ok) { '' } else { [string]$termOpen.Win32Error }) }
      [ordered]@{ name = 'open_harness_vmwrite'; ok = $vmOpen.Ok; error = $(if ($vmOpen.Ok) { '' } else { [string]$vmOpen.Win32Error }) }
    )
    Write-ProbeJson -Data $entries -Path (Join-Path $ExchangeFull 'isolation.json')
  }
}

Write-Heartbeat -Path (Join-Path $ExchangeFull $BeatFile)
