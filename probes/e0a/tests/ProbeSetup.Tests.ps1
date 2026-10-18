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

Describe 'Get-ProbeAclSpec' {
  $s = Get-ProbeAclSpec -ReaderSid 'S-R' -CtlSid 'S-C' -AgentSid 'S-A'
  It 'gives agent nothing on ctl and results' {
    ($s['C:\ProgramData\ac-probe\ctl'].Sid -contains 'S-A') | Should Be $false
    ($s['C:\ProgramData\ac-probe-results'].Sid -contains 'S-A') | Should Be $false
  }
  It 'gives reader read-only on results' {
    ($s['C:\ProgramData\ac-probe-results'] | Where-Object Sid -eq 'S-R').Rights | Should Be 'ReadAndExecute'
  }
  It 'gives agent modify on exchange, ctl read' {
    ($s['C:\ProgramData\ac-probe\exchange'] | Where-Object Sid -eq 'S-A').Rights | Should Be 'Modify'
    ($s['C:\ProgramData\ac-probe\exchange'] | Where-Object Sid -eq 'S-C').Rights | Should Be 'Read'
  }
  It 'gives Admins full control everywhere' {
    foreach ($path in $s.Keys) {
      ($s[$path] | Where-Object Sid -eq 'S-1-5-32-544').Rights | Should Be 'FullControl'
    }
  }
  It 'lets agent read bin but not write it' {
    ($s['C:\ProgramData\ac-probe\bin'] | Where-Object Sid -eq 'S-A').Rights | Should Be 'ReadAndExecute'
  }
}

Describe 'Set-ProbeFolders' {
  BeforeEach {
    Mock -ModuleName ProbeSetup New-Item {}
    Mock -ModuleName ProbeSetup Set-Acl {}
  }
  $spec = @{
    'C:\ProgramData\ac-probe\ctl' = @(@{ Sid = 'S-1-5-32-544'; Rights = 'FullControl' }, @{ Sid = 'S-1-5-32-545'; Rights = 'Modify' })
    'C:\ProgramData\ac-probe'     = @(@{ Sid = 'S-1-5-32-544'; Rights = 'FullControl' })
  }
  It 'creates and protects every folder with no inherited rules' {
    Set-ProbeFolders -Spec $spec
    Assert-MockCalled -ModuleName ProbeSetup New-Item -Times 2
    Assert-MockCalled -ModuleName ProbeSetup Set-Acl -Times 2 -ParameterFilter { $AclObject.AreAccessRulesProtected }
    Assert-MockCalled -ModuleName ProbeSetup Set-Acl -Times 1 -ParameterFilter {
      $LiteralPath -eq 'C:\ProgramData\ac-probe\ctl' -and $AclObject.Access.Count -eq 2 }
  }
  It 'applies parents before children' {
    $global:probeApplyOrder = @()
    Mock -ModuleName ProbeSetup Set-Acl { $global:probeApplyOrder += $LiteralPath }
    Set-ProbeFolders -Spec $spec
    $global:probeApplyOrder[0] | Should Be 'C:\ProgramData\ac-probe'
  }
}

Describe 'New-ProbeAccount' {
  It 'adds to Users only, with description' {
    Mock -ModuleName ProbeSetup New-LocalUser { [pscustomobject]@{SID=[pscustomobject]@{Value='S-X'}} }
    Mock -ModuleName ProbeSetup Add-LocalGroupMember {}
    New-ProbeAccount 'ac-probe-ctl' (New-ProbePassword) | Should Be 'S-X'
    Assert-MockCalled -ModuleName ProbeSetup New-LocalUser -ParameterFilter { $Description -eq 'AgentContinuum E0a probe - safe to delete' -and $PasswordNeverExpires -and $UserMayNotChangePassword }
    Assert-MockCalled -ModuleName ProbeSetup Add-LocalGroupMember -Times 1 -ParameterFilter { $SID -eq 'S-1-5-32-545' -and $Member -eq 'ac-probe-ctl' }
  }
}
