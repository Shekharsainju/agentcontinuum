Import-Module "$PSScriptRoot\..\ProbeCommon.psm1" -Force
Import-Module "$PSScriptRoot\..\ProbeSetup.psm1" -Force

Describe 'Test-ProbePrecheck' {
  It 'fails and does not start seclogon when stopped' {
    Mock -ModuleName ProbeSetup Test-ProbeElevated { $true }
    Mock -ModuleName ProbeSetup Get-Service { [pscustomobject]@{Status='Stopped'} }
    Mock -ModuleName ProbeSetup Start-Service {}
    $r = Test-ProbePrecheck
    $r.Ok | Should Be $false
    $r.SeclogonStatus | Should Be 'Stopped'
    Assert-MockCalled -ModuleName ProbeSetup Start-Service -Times 0
  }
  It 'fails when not elevated even if seclogon runs' {
    Mock -ModuleName ProbeSetup Test-ProbeElevated { $false }
    Mock -ModuleName ProbeSetup Get-Service { [pscustomobject]@{Status='Running'} }
    $r = Test-ProbePrecheck
    $r.Ok | Should Be $false
    $r.Elevated | Should Be $false
  }
  It 'passes when elevated and seclogon runs' {
    Mock -ModuleName ProbeSetup Test-ProbeElevated { $true }
    Mock -ModuleName ProbeSetup Get-Service { [pscustomobject]@{Status='Running'} }
    (Test-ProbePrecheck).Ok | Should Be $true
  }
}
