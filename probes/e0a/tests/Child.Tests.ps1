$child = "$PSScriptRoot\..\Child.ps1"
function Run($mode, $extra = '') { Start-Process powershell.exe "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$child`" -Exchange `"$TestDrive`" -Mode $mode $extra" -PassThru -WindowStyle Hidden }

Describe 'Child' {
  AfterEach {
    # Breakaway starts a grandchild via CreateProcessW directly (not a Start-Process child), so
    # killing $p above never touches it. Clean it up here by PID from breakaway.json, and verify.
    $bPath = "$TestDrive\breakaway.json"
    if (Test-Path $bPath) {
      try {
        $b = Get-Content $bPath -Raw | ConvertFrom-Json
        if ($b.pid) {
          Stop-Process -Id $b.pid -Force -ErrorAction SilentlyContinue
        }
      } catch {}
    }
  }

  It 'heartbeats every ~250 ms' {
    $p = Run Heartbeat; Start-Sleep 3; $p.Kill()
    (Get-Content "$TestDrive\heartbeat.txt").Count | Should BeGreaterThan 7
  }
  It 'reports breakaway outcome' {
    $p = Run Breakaway; Start-Sleep 4; $p.Kill()
    $b = Get-Content "$TestDrive\breakaway.json" -Raw | ConvertFrom-Json
    ($b.PSObject.Properties.Name -contains 'ok') | Should Be $true
  }
  It 'isolation writes six entries' {
    $p = Run Isolation "-HarnessPid $PID -CtlDir `"$TestDrive\ctl`" -ResultsDir `"$TestDrive\res`""; Start-Sleep 4; $p.Kill()
    (Get-Content "$TestDrive\isolation.json" -Raw | ConvertFrom-Json).Count | Should Be 6
  }
}
