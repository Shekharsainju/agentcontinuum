Import-Module "$PSScriptRoot\..\HarnessChecks.psm1" -Force

Describe 'Get-C5ExchangeBase' {
  It 'uses the ctl folder when it is set, not the agent exchange' {
    Get-C5ExchangeBase -Ctx @{ Exchange = 'X:\exchange'; CtlDir = 'X:\ctl' } | Should Be 'X:\ctl'
  }
  It 'falls back to the exchange when no ctl folder is set' {
    Get-C5ExchangeBase -Ctx @{ Exchange = 'X:\exchange'; CtlDir = '' } | Should Be 'X:\exchange'
  }
  It 'falls back to the exchange when the ctl key is absent' {
    Get-C5ExchangeBase -Ctx @{ Exchange = 'X:\exchange' } | Should Be 'X:\exchange'
  }
}
