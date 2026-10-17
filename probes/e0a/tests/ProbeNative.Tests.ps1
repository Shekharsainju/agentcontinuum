Import-Module "$PSScriptRoot\..\ProbeNative.psm1" -Force
Describe 'Import-ProbeNative' {
  It 'loads real source' { (Import-ProbeNative).Loaded | Should Be $true }
  It 'reports blocked instead of throwing' {
    Set-Content TestDrive:\bad.cs 'this is not C#'
    $r = Import-ProbeNative -SourcePath (Resolve-Path TestDrive:\bad.cs)
    $r.Loaded | Should Be $false; $r.Error | Should Not BeNullOrEmpty
  }
}
Describe 'JOBOBJECT_FREEZE_INFORMATION layout' {
  It 'marshals to 16 bytes' {
    Import-ProbeNative | Out-Null
    [AcProbe.Native]::FreezeInfoSize() | Should Be 16
  }
}
Describe 'Job basics (self, no elevation)' {
  It 'assigns a suspended self child and terminates it' {
    Import-ProbeNative | Out-Null
    $job = [AcProbe.Native]::CreateKillOnCloseJob()
    $p = [AcProbe.Native]::StartSelf('powershell.exe -NoProfile -Command Start-Sleep 30', $true, $false)
    [AcProbe.Native]::Assign($job, $p.Process) | Should Be $true
    [AcProbe.Native]::JobPids($job) -contains $p.Pid | Should Be $true
    [AcProbe.Native]::Resume($p.Thread) | Out-Null
    [AcProbe.Native]::TerminateJob($job) | Should Be $true
    Start-Sleep 1; [AcProbe.Native]::JobActive($job) | Should Be 0
  }
}
