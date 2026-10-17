$binDir = "$PSScriptRoot\.."

Describe 'Harness self mode' {

  It 'writes C3-C6 with outcomes and harness_in_job_before' {
    $outDir = "$TestDrive\out"
    $exchange = "$TestDrive\ex"
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$binDir\Harness.ps1" `
      -Run self -OutDir $outDir -Exchange $exchange -BinDir $binDir
    $j = Get-Content "$outDir\results-self.json" -Raw | ConvertFrom-Json
    ($j.checks.id -join ',') | Should Be 'C3,C3b,C4,C5,C6'
    $j.checks | ForEach-Object { $_.result | Should Match '^(pass|fail|error)$' }
    $j.harness_in_job_before | Should Not BeNullOrEmpty

    # Hygiene: no leftover probe children/sub-harnesses from this run.
    Start-Sleep -Milliseconds 500
    $leftovers = Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
      Where-Object { $_.CommandLine -and ($_.CommandLine -like '*Child.ps1*' -or $_.CommandLine -like '*SubHarness.ps1*') }
    $leftovers | Should BeNullOrEmpty
  }
}

Describe 'Invoke-C3FreezeThaw thaws on exception mid-freeze' {

  Import-Module (Join-Path $binDir 'ProbeNative.psm1') -Force
  Import-Module (Join-Path $binDir 'ProbeCommon.psm1') -Force
  Import-Module (Join-Path $binDir 'HarnessChecks.psm1') -Force

  $native = Import-ProbeNative
  if (-not $native.Loaded) { throw "native_load_blocked: $($native.Error)" }

  $childPath = Join-Path $binDir 'Child.ps1'
  $Launch = {
    param($Mode, $ExtraArgs, $Suspended)
    $cmd = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$childPath`" -Mode $Mode $ExtraArgs"
    [AcProbe.Native]::StartSelf($cmd, [bool]$Suspended, $false)
  }.GetNewClosure()

  AfterEach {
    if ($script:result) {
      foreach ($c in $script:result.checks) {
        if ($c.details -and $c.details.child_pid) {
          try { [AcProbe.Native]::KillPid([int]$c.details.child_pid) } catch {}
        }
      }
    }
  }

  It 'records an error outcome and still resumes the child heartbeat' {
    $exchange = "$TestDrive\ex2"
    $Ctx = @{ Launch = $Launch; Exchange = $exchange; BinDir = $binDir; CtlDir = ''; ResultsDir = '' }
    $script:result = New-ProbeResult -Run 'self'

    Invoke-C3FreezeThaw -Result $script:result -Ctx $Ctx -FailAfterFreeze

    $c3 = $script:result.checks | Where-Object { $_.id -eq 'C3' }
    $c3.result | Should Be 'error'

    $beatPath = Join-Path $exchange 'C3\heartbeat.txt'
    $before = @(Read-Heartbeat $beatPath).Count
    Start-Sleep -Seconds 2
    $after = @(Read-Heartbeat $beatPath).Count
    $after | Should BeGreaterThan $before
  }
}
