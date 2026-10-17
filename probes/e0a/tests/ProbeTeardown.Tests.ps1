Import-Module "$PSScriptRoot\..\ProbeTeardown.psm1" -Force

Describe 'Invoke-ProbeTeardown' {
  BeforeEach {
    Mock -ModuleName ProbeTeardown Get-LocalUser { }
    Mock -ModuleName ProbeTeardown Get-CimInstance { }
    Mock -ModuleName ProbeTeardown Invoke-CimMethod { }
    Mock -ModuleName ProbeTeardown Get-ScheduledTask { }
    Mock -ModuleName ProbeTeardown Unregister-ScheduledTask { }
    Mock -ModuleName ProbeTeardown Stop-Process { }
    Mock -ModuleName ProbeTeardown Remove-LocalUser { }
    Mock -ModuleName ProbeTeardown Remove-Item { }
    Mock -ModuleName ProbeTeardown Remove-ProbeRight { }
    Mock -ModuleName ProbeTeardown Get-ProbeProfileListSid { }
  }

  It 'is a no-op success when nothing exists' {
    $r = Invoke-ProbeTeardown -ResultsRoot 'TestDrive:\'
    ($r.Steps.Ok -contains $false) | Should Be $false
  }

  It 'runs steps in fixed order' {
    $r = Invoke-ProbeTeardown -ResultsRoot 'TestDrive:\'
    ($r.Steps.Name -join ',') | Should Be 'StopProcesses,RemoveTasks,RevokeRights,DeleteProfiles,RemoveAccounts,RemoveProbeDir,Verify'
  }

  It 'deletes profiles via CIM, never Remove-Item under C:\Users' {
    Mock -ModuleName ProbeTeardown Get-CimInstance { [pscustomobject]@{ SID = 'S-1-5-21-x'; LocalPath = 'C:\Users\ac-probe-ctl' } } -ParameterFilter { $ClassName -eq 'Win32_UserProfile' }
    $r = Invoke-ProbeTeardown -ResultsRoot 'TestDrive:\'
    Assert-MockCalled -ModuleName ProbeTeardown Invoke-CimMethod -ParameterFilter { $MethodName -eq 'Delete' } -Times 1 -Scope It
    Assert-MockCalled -ModuleName ProbeTeardown Remove-Item -ParameterFilter { $Path -like 'C:\Users\*' } -Times 0 -Scope It
  }

  It 'retries a loaded profile 5 times then continues' {
    Mock -ModuleName ProbeTeardown Start-Sleep { }
    Mock -ModuleName ProbeTeardown Get-CimInstance { [pscustomobject]@{ SID = 'S-1-5-21-x'; LocalPath = 'C:\Users\ac-probe-ctl' } } -ParameterFilter { $ClassName -eq 'Win32_UserProfile' }
    Mock -ModuleName ProbeTeardown Invoke-CimMethod { throw 'profile in use' }
    $r = Invoke-ProbeTeardown -ResultsRoot 'TestDrive:\'
    (($r.Steps | Where-Object Name -eq 'DeleteProfiles').Ok) | Should Be $false
    Assert-MockCalled -ModuleName ProbeTeardown Invoke-CimMethod -Times 5 -Scope It
    (($r.Steps | Where-Object Name -eq 'RemoveAccounts')) | Should Not BeNullOrEmpty
  }

  It 'revokes rights listed in a manifest even when the account is gone' {
    $root = "$TestDrive\res"
    New-Item -ItemType Directory -Force -Path "$root\stamp" | Out-Null
    @{ ctl_sid = 'S-C'; agent_sid = 'S-A'; granted_rights = @(@{ sid = 'S-C'; right = 'SeBatchLogonRight' }) } |
      ConvertTo-Json | Set-Content -LiteralPath "$root\stamp\manifest.json"
    $r = Invoke-ProbeTeardown -ResultsRoot $root
    Assert-MockCalled -ModuleName ProbeTeardown Remove-ProbeRight -ParameterFilter { $Sid -eq 'S-C' -and $Right -eq 'SeBatchLogonRight' } -Times 1 -Scope It
  }
}
