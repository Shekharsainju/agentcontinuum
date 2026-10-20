Import-Module "$PSScriptRoot\..\E1Common.psm1" -Force

Describe 'Protect-E1Value' {
  It 'keeps enumerated values' { Protect-E1Value 'none' | Should Be 'none' }
  It 'keeps timestamps' { Protect-E1Value '2026-10-20T05:00:00.123+11:00' | Should Be '2026-10-20T05:00:00.123+11:00' }
  It 'keeps numbers and booleans' {
    Protect-E1Value 1760936400 | Should Be 1760936400
    Protect-E1Value $true | Should Be $true
  }
  It 'keeps null' { Protect-E1Value $null | Should Be $null }
  It 'redacts emails' { (Protect-E1Value 'me@example.com').redacted | Should Be $true }
  It 'redacts tokens by length' { (Protect-E1Value ('sk-ant-' + ('x' * 60))).length | Should Be 67 }
  It 'redacts free text with spaces' { (Protect-E1Value 'Reply with OK').redacted | Should Be $true }
  It 'redacts paths' { (Protect-E1Value 'C:\Users\me\.claude\x.jsonl').redacted | Should Be $true }
}

Describe 'ConvertTo-E1Shape' {
  It 'never emits a sentinel from message content' {
    $j = '{"type":"assistant","message":{"content":[{"text":"SENTINEL-7f3a in a sentence"}]}}'
    (ConvertTo-E1Shape $j | ConvertTo-Json -Depth 8) | Should Not Match 'SENTINEL-7f3a'
  }
  It 'keeps key paths' { (ConvertTo-E1Shape '{"a":{"b":1}}').Keys -contains 'a.b' | Should Be $true }
  It 'keeps enumerated leaf values' { (ConvertTo-E1Shape '{"apiKeySource":"none"}')['apiKeySource'] | Should Be 'none' }
  It 'records array count and first element shape' {
    $s = ConvertTo-E1Shape '{"xs":[{"k":1},{"k":2},{"k":3}]}'
    $s['xs[#]'] | Should Be 3
    $s['xs[0].k'] | Should Be 1
    $s.Keys -contains 'xs[1].k' | Should Be $false
  }
  It 'returns a parse marker for invalid JSON' { (ConvertTo-E1Shape '{"a":')['_parse_error'] | Should Be $true }
}

Describe 'Get-E1TextShape' {
  $s = Get-E1TextShape "Logged in as me@example.com`nPlan: Claude Max"
  It 'finds category words' {
    $s.categories -contains 'logged in' | Should Be $true
    $s.categories -contains 'claude max' | Should Be $true
  }
  It 'counts lines and length' { $s.lines | Should Be 2; $s.length | Should Be 44 }
  It 'never carries the text' { ($s | ConvertTo-Json) | Should Not Match 'example' }
  It 'distinguishes not logged in' { (Get-E1TextShape 'Not logged in').categories -contains 'not logged in' | Should Be $true }
}

Describe 'Get-E1KeyPaths' {
  It 'lists key paths without values' {
    Set-Content -LiteralPath "$TestDrive\c.json" -Value '{"claudeAiOauth":{"accessToken":"SENTINEL-k3y","expiresAt":1}}'
    $k = Get-E1KeyPaths -Path "$TestDrive\c.json"
    $k -contains 'claudeAiOauth.accessToken' | Should Be $true
    ($k -join ',') | Should Not Match 'SENTINEL'
  }
  It 'returns nothing for a missing file' { @(Get-E1KeyPaths -Path "$TestDrive\none.json").Count | Should Be 0 }
}

Describe 'Hide-E1Email' {
  It 'replaces email addresses' { Hide-E1Email 'Claude Code me@example.com' | Should Be 'Claude Code <email>' }
  It 'leaves other text alone' { Hide-E1Email 'Claude Code-credentials' | Should Be 'Claude Code-credentials' }
}

Describe 'Invoke-E1Command' {
  It 'captures stdout and the exit code' {
    $r = Invoke-E1Command -Exe 'cmd.exe' -Arguments '/c echo hi& exit 3' -TimeoutSeconds 10
    $r.stdout.Trim() | Should Be 'hi'
    $r.exit_code | Should Be 3
    $r.timed_out | Should Be $false
  }
  It 'kills a command that runs past the timeout' {
    $r = Invoke-E1Command -Exe 'powershell.exe' -Arguments '-NoProfile -Command Start-Sleep 20' -TimeoutSeconds 1
    $r.timed_out | Should Be $true
    $r.seconds -lt 10 | Should Be $true
  }
  It 'applies environment overrides' {
    (Invoke-E1Command -Exe 'cmd.exe' -Arguments '/c echo %E1_PROBE_VAR%' -TimeoutSeconds 10 -EnvOverrides @{ E1_PROBE_VAR = 'set1' }).stdout.Trim() | Should Be 'set1'
  }
  It 'removes named variables' {
    $env:E1_PROBE_GONE = 'present'
    try { (Invoke-E1Command -Exe 'cmd.exe' -Arguments '/c echo [%E1_PROBE_GONE%]' -TimeoutSeconds 10 -RemoveEnv @('E1_PROBE_GONE')).stdout.Trim() | Should Be '[%E1_PROBE_GONE%]' }
    finally { Remove-Item Env:\E1_PROBE_GONE }
  }
  It 'closes stdin so a reader sees end of input' {
    $r = Invoke-E1Command -Exe 'powershell.exe' -Arguments '-NoProfile -Command "[Console]::In.ReadToEnd().Length"' -TimeoutSeconds 10
    $r.timed_out | Should Be $false
    $r.stdout.Trim() | Should Be '0'
  }
}

Describe 'Invoke-E1StreamRun' {
  $emit = "$TestDrive\emit.ps1"
  Set-Content -LiteralPath $emit -Value @'
param([int]$LingerSeconds = 0)
[Console]::Out.WriteLine('{"type":"system","subtype":"init","apiKeySource":"none"}')
[Console]::Out.WriteLine('{"type":"result","subtype":"success","result":"SENTINEL-r3s text"}')
[Console]::Out.Flush()
if ($LingerSeconds) { Start-Sleep -Seconds $LingerSeconds }
'@
  It 'shapes each line and sees the process exit on its own' {
    $r = Invoke-E1StreamRun -Exe 'powershell.exe' -Arguments "-NoProfile -ExecutionPolicy Bypass -File `"$emit`"" -ExitGraceSeconds 30
    @($r.events).Count | Should Be 2
    $r.events[0].shape['apiKeySource'] | Should Be 'none'
    $r.final_seen | Should Be $true
    $r.exited_by_itself | Should Be $true
    ($r | ConvertTo-Json -Depth 8) | Should Not Match 'SENTINEL'
  }
  It 'kills a process that stays alive after the final event' {
    $r = Invoke-E1StreamRun -Exe 'powershell.exe' -Arguments "-NoProfile -ExecutionPolicy Bypass -File `"$emit`" -LingerSeconds 30" -ExitGraceSeconds 2
    $r.exited_by_itself | Should Be $false
    $r.seconds_final_to_exit -lt 15 | Should Be $true
  }
}

Describe 'Write-E1Result' {
  $cli = [ordered]@{ path = 'C:\x\claude.exe'; target = $null; version = '2.1.304' }
  It 'writes the schema, CLI identity and data' {
    Write-E1Result -Name 'static' -Data @{ answer = 1 } -Cli $cli -Dir "$TestDrive\res"
    $j = Get-Content "$TestDrive\res\static.json" -Raw | ConvertFrom-Json
    $j.schema | Should Be 'ac-probe-e1/1'
    $j.cli.version | Should Be '2.1.304'
    $j.data.answer | Should Be 1
  }
  It 'refuses to write an email address' {
    { Write-E1Result -Name 'leak' -Data @{ who = 'me@example.com' } -Cli $cli -Dir "$TestDrive\res" } | Should Throw
    Test-Path "$TestDrive\res\leak.json" | Should Be $false
  }
}
