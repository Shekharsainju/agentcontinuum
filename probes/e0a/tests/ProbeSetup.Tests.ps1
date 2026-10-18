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
    Assert-MockCalled -ModuleName ProbeSetup Add-LocalGroupMember -Times 1 -ParameterFilter { $SID -eq 'S-1-5-32-545' -and "$Member" -eq 'ac-probe-ctl' }
  }
}

function New-TestCtx {
  [pscustomobject]@{
    ResultsRoot   = 'C:\ProgramData\ac-probe-results'
    ResultsDir    = 'C:\ProgramData\ac-probe-results\20260101-000000'
    ManifestPath  = 'C:\ProgramData\ac-probe-results\20260101-000000\manifest.json'
    BinDir        = 'C:\ProgramData\ac-probe\bin'
    CtlDir        = 'C:\ProgramData\ac-probe\ctl'
    ExchangeDir   = 'C:\ProgramData\ac-probe\exchange'
    CtlName       = 'ac-probe-ctl'
    AgentName     = 'ac-probe-agent'
    CtlSid        = 'S-C'
    AgentSid      = 'S-A'
    CtlPassword   = (New-ProbePassword)
    AgentPassword = (New-ProbePassword)
  }
}

function New-FakeProcess([bool]$Exits) {
  $p = [pscustomobject]@{ Killed = $false; ExitCode = 0; Exits = $Exits }
  $p | Add-Member ScriptMethod WaitForExit { param($ms) $this.Exits }
  $p | Add-Member ScriptMethod Kill { $this.Killed = $true }
  $p
}

Describe 'Invoke-ProbeRunB' {
  BeforeEach {
    Mock -ModuleName ProbeSetup New-ProbeSecretPipe { [pscustomobject]@{ Name = 'pipe-b'; Job = $null } }
    Mock -ModuleName ProbeSetup Get-ProbeStrayProcess { @() }
    Mock -ModuleName ProbeSetup Remove-Job {}
  }
  It 'starts the harness as ctl through secondary logon with a profile' {
    $global:probeStartInfo = $null
    Mock -ModuleName ProbeSetup Start-ProbeProcess { $global:probeStartInfo = $StartInfo; New-FakeProcess $true }
    $r = Invoke-ProbeRunB -Ctx (New-TestCtx)
    $r.Outcome | Should Be 'done'
    $psi = $global:probeStartInfo
    $psi.UserName | Should Be 'ac-probe-ctl'
    $psi.Domain | Should Be '.'
    $psi.LoadUserProfile | Should Be $true
    $psi.UseShellExecute | Should Be $false
    ($psi.Password -is [securestring]) | Should Be $true
    $psi.Arguments | Should Match '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File'
    $psi.Arguments | Should Match '-Run B'
    $psi.Arguments | Should Match '-PipeName pipe-b'
  }
  It 'kills the process and reports timeout when it does not exit' {
    $fake = New-FakeProcess $false
    $global:probeFake = $fake
    Mock -ModuleName ProbeSetup Start-ProbeProcess { $global:probeFake }
    (Invoke-ProbeRunB -Ctx (New-TestCtx)).Outcome | Should Be 'timeout'
    $fake.Killed | Should Be $true
  }
  It 'reports start_failed when the launch throws' {
    Mock -ModuleName ProbeSetup Start-ProbeProcess { throw 'logon failure' }
    (Invoke-ProbeRunB -Ctx (New-TestCtx)).Outcome | Should Be 'start_failed'
  }
  It 'reports stray processes left after the run' {
    Mock -ModuleName ProbeSetup Start-ProbeProcess { New-FakeProcess $true }
    Mock -ModuleName ProbeSetup Get-ProbeStrayProcess { @(4242) }
    (Invoke-ProbeRunB -Ctx (New-TestCtx)).StrayAfterRun | Should Be 4242
  }
}
