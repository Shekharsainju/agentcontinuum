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
