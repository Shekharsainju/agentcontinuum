Import-Module "$PSScriptRoot\..\ProbeCommon.psm1" -Force
$t0 = [datetime]'2026-10-17T12:00:00Z'
function Beats($secs) { $secs | % { $t0.AddSeconds($_) } }

Describe 'New-ProbePassword' {
  It 'is 32 chars with all four classes' {
    $p = ConvertFrom-ProbeSecureString (New-ProbePassword)
    $p.Length | Should Be 32
    $p | Should Match '[A-Z]'; $p | Should Match '[a-z]'; $p | Should Match '\d'; $p | Should Match '[^A-Za-z0-9]'
  }
  It 'differs between calls' { (ConvertFrom-ProbeSecureString (New-ProbePassword)) | Should Not Be (ConvertFrom-ProbeSecureString (New-ProbePassword)) }
}
Describe 'Read-Heartbeat' {
  It 'skips a torn last line' {
    $f = "TestDrive:\hb.txt"; Set-Content $f @($t0.ToString('o'), $t0.AddSeconds(1).ToString('o'), '2026-10-17T12:0')
    (Read-Heartbeat (Resolve-Path $f)).Count | Should Be 2
  }
  It 'returns empty for missing file' { (Read-Heartbeat 'TestDrive:\nope.txt').Count | Should Be 0 }
}
Describe 'Get-FreezeVerdict' {  # freeze at t0+10, thaw at t0+16
  $f = $t0.AddSeconds(10); $th = $t0.AddSeconds(16)
  It 'passes a clean freeze'          { (Get-FreezeVerdict (Beats 9.75,10.0,10.3,17.0) $f $th).Result | Should Be 'pass' }
  It 'fails a beat during freeze'     { (Get-FreezeVerdict (Beats 9.75,11.0,17.0) $f $th).Result | Should Be 'fail' }
  It 'fails slow resume (2.5 s)'      { (Get-FreezeVerdict (Beats 9.75,18.5) $f $th).Result | Should Be 'fail' }
  It 'fails no beat after thaw'       { (Get-FreezeVerdict (Beats 9.75) $f $th).Result | Should Be 'fail' }
  It 'reports gap and resume seconds' { $v = Get-FreezeVerdict (Beats 10.3,17.0) $f $th; $v.GapSeconds | Should Be 6.7; $v.ResumeSeconds | Should Be 1 }
}
Describe 'Results' {
  It 'writes schema, edition and checks' {
    $r = New-ProbeResult -Run self; Add-ProbeCheck $r 'C3' 'freeze_thaw' 'pass' @{gap=6}
    Write-ProbeResult $r 'TestDrive:\r.json'
    $j = Get-Content 'TestDrive:\r.json' -Raw | ConvertFrom-Json
    $j.schema | Should Be 'ac-probe-e0a/1'; $j.edition | Should Be 'Windows 11 Home'
    $j.build | Should Match '^\d+\.\d+$'; $j.checks[0].result | Should Be 'pass'
  }
}
Describe 'Test-SecretAbsent' {
  It 'finds UTF-16 hit' { [IO.File]::WriteAllText("$TestDrive\x.bin",'..s3cret..',[Text.Encoding]::Unicode)
    (Test-SecretAbsent @("$TestDrive") @('s3cret')).Clean | Should Be $false }
  It 'clean when absent' { (Test-SecretAbsent @("$TestDrive\x.bin") @('other')).Clean | Should Be $true }
}
