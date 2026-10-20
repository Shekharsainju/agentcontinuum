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
