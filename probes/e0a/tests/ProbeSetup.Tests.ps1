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

function global:New-FakeProcess([bool]$Exits) {
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

Describe 'Invoke-ProbeRunA' {
  BeforeEach {
    $global:probeInfoQueue = New-Object System.Collections.Queue
    $global:probeBatchQueue = New-Object System.Collections.Queue
    Mock -ModuleName ProbeSetup New-ProbeSecretPipe { [pscustomobject]@{ Name = 'pipe-a'; Job = $null } }
    Mock -ModuleName ProbeSetup Remove-Job {}
    Mock -ModuleName ProbeSetup Get-ProbeStrayProcess { @() }
    Mock -ModuleName ProbeSetup Start-Sleep {}
    Mock -ModuleName ProbeSetup Test-Path { $false }
    Mock -ModuleName ProbeSetup Register-ScheduledTask {}
    Mock -ModuleName ProbeSetup Start-ScheduledTask {}
    Mock -ModuleName ProbeSetup Stop-ScheduledTask {}
    Mock -ModuleName ProbeSetup Unregister-ScheduledTask {}
    Mock -ModuleName ProbeSetup Get-ScheduledTask { [pscustomobject]@{ State = 'Ready' } }
    Mock -ModuleName ProbeSetup Get-ScheduledTaskInfo { [pscustomobject]@{ LastTaskResult = [uint32]$global:probeInfoQueue.Dequeue() } }
    Mock -ModuleName ProbeSetup Get-ProbeBatchRight { [bool]$global:probeBatchQueue.Dequeue() }
    Mock -ModuleName ProbeSetup Add-ProbeBatchRight {}
    Mock -ModuleName ProbeSetup Add-ProbeManifestGrant {}
  }
  It 'registers a limited password-logon task as ctl and cleans it up' {
    $global:probeInfoQueue.Enqueue(0); $global:probeBatchQueue.Enqueue($true); $global:probeBatchQueue.Enqueue($true)
    $r = Invoke-ProbeRunA -Ctx (New-TestCtx)
    $r.Outcome | Should Be 'done'
    $r.BatchBefore | Should Be $true
    $r.NeededGrant | Should Be $false
    Assert-MockCalled -ModuleName ProbeSetup Register-ScheduledTask -Times 1 -ParameterFilter {
      $TaskName -eq 'ac-probe-ctl-runA' -and $User -eq 'ac-probe-ctl' -and $RunLevel -eq 'Limited' -and ($Password -is [string]) -and $Password.Length -gt 0 }
    Assert-MockCalled -ModuleName ProbeSetup Register-ScheduledTask -Times 1 -ParameterFilter { $Settings.ExecutionTimeLimit -eq 'PT5M' }
    Assert-MockCalled -ModuleName ProbeSetup Unregister-ScheduledTask -Times 1
    Assert-MockCalled -ModuleName ProbeSetup Add-ProbeBatchRight -Times 0 -Scope It
  }
  It 'grants batch right once and retries after a logon-type failure' {
    $global:probeInfoQueue.Enqueue(2147943785); $global:probeInfoQueue.Enqueue(0)
    $global:probeBatchQueue.Enqueue($false); $global:probeBatchQueue.Enqueue($false); $global:probeBatchQueue.Enqueue($false)
    $r = Invoke-ProbeRunA -Ctx (New-TestCtx)
    $r.Outcome | Should Be 'done'
    $r.BatchBefore | Should Be $false
    $r.BatchAfterRegister | Should Be $false
    $r.NeededGrant | Should Be $true
    Assert-MockCalled -ModuleName ProbeSetup Add-ProbeBatchRight -Times 1 -ParameterFilter { $Sid -eq 'S-C' }
    Assert-MockCalled -ModuleName ProbeSetup Add-ProbeManifestGrant -Times 1 -ParameterFilter { $Sid -eq 'S-C' -and $Right -eq 'SeBatchLogonRight' }
    Assert-MockCalled -ModuleName ProbeSetup Start-ScheduledTask -Times 2
    Assert-MockCalled -ModuleName ProbeSetup Unregister-ScheduledTask -Times 2
  }
  It 'does not grant when ctl already held the right' {
    $global:probeInfoQueue.Enqueue(2147943785)
    $global:probeBatchQueue.Enqueue($true); $global:probeBatchQueue.Enqueue($true)
    $r = Invoke-ProbeRunA -Ctx (New-TestCtx)
    $r.Outcome | Should Be 'start_failed'
    Assert-MockCalled -ModuleName ProbeSetup Add-ProbeBatchRight -Times 0 -Scope It
  }
  It 'reports timeout and stops the task when it never finishes' {
    Mock -ModuleName ProbeSetup Get-ScheduledTask { [pscustomobject]@{ State = 'Running' } }
    Mock -ModuleName ProbeSetup Get-ScheduledTaskInfo { [pscustomobject]@{ LastTaskResult = [uint32]267009 } }
    $global:probeBatchQueue.Enqueue($true); $global:probeBatchQueue.Enqueue($true)
    InModuleScope ProbeSetup { $script:RunTimeoutMs = 5 }
    try { $r = Invoke-ProbeRunA -Ctx (New-TestCtx) }
    finally { InModuleScope ProbeSetup { $script:RunTimeoutMs = 300000 } }
    $r.Outcome | Should Be 'timeout'
    Assert-MockCalled -ModuleName ProbeSetup Stop-ScheduledTask -Times 1
  }
  It 'treats an existing results file as completion' {
    Mock -ModuleName ProbeSetup Test-Path { $true }
    $global:probeBatchQueue.Enqueue($true); $global:probeBatchQueue.Enqueue($true)
    (Invoke-ProbeRunA -Ctx (New-TestCtx)).Outcome | Should Be 'done'
  }
}

Describe 'Start-E0aProbe' {
  BeforeEach {
    $global:probeVerifyOk = $true
    Mock -ModuleName ProbeSetup Test-ProbePrecheck { [pscustomobject]@{ Ok = $true; Elevated = $true; SeclogonStatus = 'Running' } }
    Mock -ModuleName ProbeSetup Import-ProbeNative { @{ Loaded = $true; Error = '' } }
    Mock -ModuleName ProbeSetup Invoke-ProbeTeardown { [pscustomobject]@{ Steps = @([pscustomobject]@{ Name = 'Verify'; Ok = $global:probeVerifyOk; Error = '' }) } }
    Mock -ModuleName ProbeSetup Resolve-ProbeAccountSid { 'S-R' }
    Mock -ModuleName ProbeSetup New-Item {}
    Mock -ModuleName ProbeSetup New-ProbeAccount { 'S-1-5-21-fake' }
    Mock -ModuleName ProbeSetup Set-ProbeFolders {}
    Mock -ModuleName ProbeSetup Copy-ProbeBinaries {}
    Mock -ModuleName ProbeSetup Write-ProbeResult {}
    Mock -ModuleName ProbeSetup Set-Content {}
    Mock -ModuleName ProbeSetup Test-SecretAbsent { [pscustomobject]@{ Clean = $true; Hits = @(); Unscanned = @() } }
    Mock -ModuleName ProbeSetup Get-ProbeBatchRight { $false }
    Mock -ModuleName ProbeSetup Write-Host {}
    $ok = [pscustomobject]@{ Outcome = 'done'; BatchBefore = $true; BatchAfterRegister = $true; NeededGrant = $false; StrayAfterRun = @() }
    $global:probeRunOk = $ok
    Mock -ModuleName ProbeSetup Invoke-ProbeRunA { $global:probeRunOk }
    Mock -ModuleName ProbeSetup Invoke-ProbeRunB { $global:probeRunOk }
  }
  It 'exits 2 without touching the system when precheck fails' {
    Mock -ModuleName ProbeSetup Test-ProbePrecheck { [pscustomobject]@{ Ok = $false; Elevated = $false; SeclogonStatus = 'Stopped' } }
    Start-E0aProbe | Should Be 2
    Assert-MockCalled -ModuleName ProbeSetup Invoke-ProbeTeardown -Times 0
    Assert-MockCalled -ModuleName ProbeSetup New-ProbeAccount -Times 0
  }
  It 'aborts before creating accounts when pre-clean cannot verify' {
    $global:probeVerifyOk = $false
    Start-E0aProbe | Should Not Be 0
    Assert-MockCalled -ModuleName ProbeSetup New-ProbeAccount -Times 0
    Assert-MockCalled -ModuleName ProbeSetup Invoke-ProbeRunA -Times 0
  }
  It 'always tears down when run A throws and skips run B' {
    Mock -ModuleName ProbeSetup Invoke-ProbeRunA { throw 'boom' }
    Start-E0aProbe | Should Not Be 0
    Assert-MockCalled -ModuleName ProbeSetup Invoke-ProbeTeardown -Times 2
    Assert-MockCalled -ModuleName ProbeSetup Invoke-ProbeRunB -Times 0
  }
  It 'runs A then B then tears down on the happy path' {
    Start-E0aProbe | Should Be 0
    Assert-MockCalled -ModuleName ProbeSetup New-ProbeAccount -Times 2
    Assert-MockCalled -ModuleName ProbeSetup Set-ProbeFolders -Times 1
    Assert-MockCalled -ModuleName ProbeSetup Invoke-ProbeRunA -Times 1
    Assert-MockCalled -ModuleName ProbeSetup Invoke-ProbeRunB -Times 1
    Assert-MockCalled -ModuleName ProbeSetup Invoke-ProbeTeardown -Times 2
  }
  It 'passes the reader account through to SID resolution' {
    Start-E0aProbe -ReaderAccount 'someone' | Out-Null
    Assert-MockCalled -ModuleName ProbeSetup Resolve-ProbeAccountSid -Times 1 -ParameterFilter { $Name -eq 'someone' }
  }
}

Describe 'Start-E0aProbe setup.json C1' {
  BeforeEach {
    Mock -ModuleName ProbeSetup Import-ProbeNative { @{ Loaded = $true; Error = '' } }
    Mock -ModuleName ProbeSetup Invoke-ProbeTeardown { [pscustomobject]@{ Steps = @([pscustomobject]@{ Name = 'Verify'; Ok = $true; Error = '' }) } }
    Mock -ModuleName ProbeSetup Resolve-ProbeAccountSid { 'S-R' }
    Mock -ModuleName ProbeSetup New-Item {}
    Mock -ModuleName ProbeSetup New-ProbeAccount { 'S-1-5-21-fake' }
    Mock -ModuleName ProbeSetup Set-ProbeFolders {}
    Mock -ModuleName ProbeSetup Copy-ProbeBinaries {}
    Mock -ModuleName ProbeSetup Write-ProbeResult {}
    Mock -ModuleName ProbeSetup Set-Content {}
    Mock -ModuleName ProbeSetup Test-SecretAbsent { [pscustomobject]@{ Clean = $true; Hits = @(); Unscanned = @() } }
    Mock -ModuleName ProbeSetup Get-ProbeBatchRight { $false }
    Mock -ModuleName ProbeSetup Write-Host {}
    $global:probeRunOk = [pscustomobject]@{ Outcome = 'done'; BatchBefore = $true; BatchAfterRegister = $true; NeededGrant = $false; StrayAfterRun = @() }
    Mock -ModuleName ProbeSetup Invoke-ProbeRunA { $global:probeRunOk }
    Mock -ModuleName ProbeSetup Invoke-ProbeRunB { $global:probeRunOk }
  }
  It 'records C1 fail in setup.json and exits 2 when precheck fails' {
    Mock -ModuleName ProbeSetup Test-ProbePrecheck { [pscustomobject]@{ Ok = $false; Elevated = $false; SeclogonStatus = 'Stopped' } }
    Start-E0aProbe | Should Be 2
    Assert-MockCalled -ModuleName ProbeSetup Write-ProbeResult -ParameterFilter {
      $Path -like '*\setup.json' -and (@($Result.checks) | Where-Object { $_.id -eq 'C1' }).result -eq 'fail' }
  }
  It 'records C1 pass in setup.json when precheck succeeds' {
    Mock -ModuleName ProbeSetup Test-ProbePrecheck { [pscustomobject]@{ Ok = $true; Elevated = $true; SeclogonStatus = 'Running' } }
    Start-E0aProbe | Should Be 0
    Assert-MockCalled -ModuleName ProbeSetup Write-ProbeResult -ParameterFilter {
      $Path -like '*\setup.json' -and (@($Result.checks) | Where-Object { $_.id -eq 'C1' }).result -eq 'pass' }
  }
}
