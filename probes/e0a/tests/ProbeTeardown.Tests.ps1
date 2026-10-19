Import-Module "$PSScriptRoot\..\ProbeTeardown.psm1" -Force

Import-Module "$PSScriptRoot\..\ProbeNative.psm1" -Force

Describe 'Get-ProbeAccountRight' {
  It 'returns an empty array for a malformed SID without throwing' {
    Import-ProbeNative | Out-Null
    @(Get-ProbeAccountRight -Sid 'not-a-sid').Count | Should Be 0
  }
}

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
    Mock -ModuleName ProbeTeardown Remove-ProbeProfileInstance { }
    Mock -ModuleName ProbeTeardown Get-ProbeProfileListSid { }
    Mock -ModuleName ProbeTeardown Get-ProbeAccountRight { @() }
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
    Assert-MockCalled -ModuleName ProbeTeardown Remove-ProbeProfileInstance -Times 1 -Scope It
    Assert-MockCalled -ModuleName ProbeTeardown Remove-Item -ParameterFilter { $Path -like 'C:\Users\*' } -Times 0 -Scope It
  }

  It 'retries a loaded profile 5 times then continues' {
    Mock -ModuleName ProbeTeardown Start-Sleep { }
    Mock -ModuleName ProbeTeardown Get-CimInstance { [pscustomobject]@{ SID = 'S-1-5-21-x'; LocalPath = 'C:\Users\ac-probe-ctl' } } -ParameterFilter { $ClassName -eq 'Win32_UserProfile' }
    Mock -ModuleName ProbeTeardown Remove-ProbeProfileInstance { throw 'profile in use' }
    $r = Invoke-ProbeTeardown -ResultsRoot 'TestDrive:\'
    (($r.Steps | Where-Object Name -eq 'DeleteProfiles').Ok) | Should Be $false
    Assert-MockCalled -ModuleName ProbeTeardown Remove-ProbeProfileInstance -Times 5 -Scope It
    (($r.Steps | Where-Object Name -eq 'RemoveAccounts')) | Should Not BeNullOrEmpty
  }

  It 'unloads a loaded profile hive before deleting it' {
    Mock -ModuleName ProbeTeardown Get-CimInstance { [pscustomobject]@{ SID = 'S-1-5-21-x'; LocalPath = 'C:\Users\ac-probe-ctl'; Loaded = $true } } -ParameterFilter { $ClassName -eq 'Win32_UserProfile' }
    Mock -ModuleName ProbeTeardown Dismount-ProbeProfileHive { }
    Invoke-ProbeTeardown -ResultsRoot 'TestDrive:\' | Out-Null
    Assert-MockCalled -ModuleName ProbeTeardown Dismount-ProbeProfileHive -ParameterFilter { $Sid -eq 'S-1-5-21-x' } -Times 1 -Scope It
  }

  It 'does not unload a profile that is not loaded' {
    Mock -ModuleName ProbeTeardown Get-CimInstance { [pscustomobject]@{ SID = 'S-1-5-21-x'; LocalPath = 'C:\Users\ac-probe-ctl'; Loaded = $false } } -ParameterFilter { $ClassName -eq 'Win32_UserProfile' }
    Mock -ModuleName ProbeTeardown Dismount-ProbeProfileHive { }
    Invoke-ProbeTeardown -ResultsRoot 'TestDrive:\' | Out-Null
    Assert-MockCalled -ModuleName ProbeTeardown Dismount-ProbeProfileHive -Times 0 -Scope It
  }

  It 'strips the batch right from every discovered probe SID even without a manifest' {
    Mock -ModuleName ProbeTeardown Get-LocalUser {
      [pscustomobject]@{ SID = [pscustomobject]@{ Value = 'S-1-5-21-ctl' } },
      [pscustomobject]@{ SID = [pscustomobject]@{ Value = 'S-1-5-21-agent' } } }
    Invoke-ProbeTeardown -ResultsRoot 'TestDrive:\' | Out-Null
    Assert-MockCalled -ModuleName ProbeTeardown Remove-ProbeRight -ParameterFilter { $Sid -eq 'S-1-5-21-ctl' -and $Right -eq 'SeBatchLogonRight' } -Times 1 -Scope It
    Assert-MockCalled -ModuleName ProbeTeardown Remove-ProbeRight -ParameterFilter { $Sid -eq 'S-1-5-21-agent' -and $Right -eq 'SeBatchLogonRight' } -Times 1 -Scope It
  }

  It 'revokes rights listed in a manifest even when the account is gone' {
    $root = "$TestDrive\res"
    New-Item -ItemType Directory -Force -Path "$root\stamp" | Out-Null
    @{ ctl_sid = 'S-C'; agent_sid = 'S-A'; granted_rights = @(@{ sid = 'S-C'; right = 'SeBatchLogonRight' }) } |
      ConvertTo-Json | Set-Content -LiteralPath "$root\stamp\manifest.json"
    $r = Invoke-ProbeTeardown -ResultsRoot $root
    Assert-MockCalled -ModuleName ProbeTeardown Remove-ProbeRight -ParameterFilter { $Sid -eq 'S-C' -and $Right -eq 'SeBatchLogonRight' } -Times 1 -Scope It
  }

  It 'fails verification when a probe SID still holds an LSA right' {
    $root = "$TestDrive\rights"
    New-Item -ItemType Directory -Force -Path "$root\stamp" | Out-Null
    @{ ctl_sid = 'S-C'; agent_sid = 'S-A' } | ConvertTo-Json | Set-Content -LiteralPath "$root\stamp\manifest.json"
    Mock -ModuleName ProbeTeardown Get-ProbeAccountRight { @('SeBatchLogonRight') } -ParameterFilter { $Sid -eq 'S-C' }
    $r = Invoke-ProbeTeardown -ResultsRoot $root
    $verify = $r.Steps | Where-Object Name -eq 'Verify'
    $verify.Ok | Should Be $false
    $verify.Error | Should Match 'rights'
  }
}
