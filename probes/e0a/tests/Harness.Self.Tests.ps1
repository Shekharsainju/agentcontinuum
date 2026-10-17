$binDir = "$PSScriptRoot\.."

Describe 'Harness self mode' {

  It 'writes C3-C6 with outcomes and harness_in_job_before' {
    $outDir = "$TestDrive\out"
    $exchange = "$TestDrive\ex"
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$binDir\Harness.ps1" `
      -Run self -OutDir $outDir -Exchange $exchange -BinDir $binDir
    $j = Get-Content "$outDir\results-self.json" -Raw | ConvertFrom-Json
    ($j.checks.id -join ',') | Should Be 'C3,C3b,C4,C5,C6'
    $j.checks | ForEach-Object { $_.result | Should Be 'pass' }
    $j.harness_in_job_before | Should Not BeNullOrEmpty

    # Hygiene: no leftover probe children/sub-harnesses from this run.
    Start-Sleep -Milliseconds 500
    $leftovers = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
      Where-Object { $_.CommandLine -and ($_.CommandLine -like '*Child.ps1*' -or $_.CommandLine -like '*SubHarness.ps1*') }
    $leftovers | Should BeNullOrEmpty
  }
}

Describe 'Invoke-C3FreezeThaw thaws on exception mid-freeze' {

  # Import HarnessChecks.psm1 directly (per the brief), but do the real work -- running the check,
  # measuring whether the heartbeat resumes, and cleaning up -- inside its own child
  # powershell.exe, exactly like the Describe above does for the full harness.
  It 'records an error outcome and still resumes the child heartbeat' {
    $helper = "$TestDrive\run-c3-failafterfreeze.ps1"
    @'
param($BinDir, $Exchange, $ResultPath)
Import-Module (Join-Path $BinDir 'ProbeNative.psm1') -Force
Import-Module (Join-Path $BinDir 'ProbeCommon.psm1') -Force
Import-Module (Join-Path $BinDir 'HarnessChecks.psm1') -Force
$native = Import-ProbeNative
if (-not $native.Loaded) { exit 3 }
$childPath = Join-Path $BinDir 'Child.ps1'
$Launch = {
  param($Mode, $ExtraArgs, $Suspended)
  $cmd = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$childPath`" -Mode $Mode $ExtraArgs"
  [AcProbe.Native]::StartSelf($cmd, [bool]$Suspended, $false)
}.GetNewClosure()
$Ctx = @{ Launch = $Launch; Exchange = $Exchange; BinDir = $BinDir; CtlDir = ''; ResultsDir = '' }
$result = New-ProbeResult -Run 'self'
Invoke-C3FreezeThaw -Result $result -Ctx $Ctx -FailAfterFreeze
$c3 = $result.checks | Where-Object { $_.id -eq 'C3' }

# Measure resume from inside THIS process too -- not just set it up and let the Pester host
# process measure it, which proved unreliable in this sandbox (see Describe comment above).
$beatPath = Join-Path $Exchange 'C3\heartbeat.txt'
$before = @(Read-Heartbeat $beatPath).Count
Start-Sleep -Seconds 2
$after = @(Read-Heartbeat $beatPath).Count

if ($c3.details.child_pid) { try { [AcProbe.Native]::KillPid([int]$c3.details.child_pid) } catch {} }

[ordered]@{ outcome = $c3.result; resumed = ($after -gt $before); before = $before; after = $after } |
  ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding UTF8
'@ | Set-Content -LiteralPath $helper -Encoding UTF8

    $exchange = "$TestDrive\ex2"
    New-Item -ItemType Directory -Force -Path $exchange | Out-Null
    $resultPath = Join-Path $exchange 'c3-result.json'

    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $helper `
      -BinDir $binDir -Exchange $exchange -ResultPath $resultPath

    $j = Get-Content $resultPath -Raw | ConvertFrom-Json

    $j.outcome | Should Be 'error'
    $j.resumed | Should Be $true
  }
}
