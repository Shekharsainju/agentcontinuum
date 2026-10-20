Set-StrictMode -Version 2.0

$script:ProbeDir = 'C:\ProgramData\ac-probe'
$script:ProfileListKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'

function Remove-ProbeRight {
  param([Parameter(Mandatory)][string]$Sid, [Parameter(Mandatory)][string]$Right)
  [AcProbe.Native]::RemoveRight($Sid, $Right)
}

function Get-ProbeAccountRight {
  param([Parameter(Mandatory)][string]$Sid)
  @([AcProbe.Native]::AccountRights($Sid))
}

function Get-ProbeProfileListSid {
  @(Get-ChildItem -LiteralPath $script:ProfileListKey -ErrorAction SilentlyContinue |
    ForEach-Object { Split-Path -Leaf $_.Name })
}

function Get-ProbeManifest {
  param([string]$ResultsRoot)
  if (-not $ResultsRoot -or -not (Test-Path -LiteralPath $ResultsRoot)) { return @() }
  $out = @()
  foreach ($f in @(Get-ChildItem -LiteralPath $ResultsRoot -Recurse -Filter 'manifest.json' -ErrorAction SilentlyContinue)) {
    try { $out += (Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json) } catch {}
  }
  $out
}

function Get-ProbeSid {
  param([string]$ResultsRoot)
  $sids = New-Object System.Collections.Generic.List[string]
  foreach ($u in @(Get-LocalUser -Name 'ac-probe-*' -ErrorAction SilentlyContinue)) {
    if ($u.SID -and $u.SID.Value) { $sids.Add([string]$u.SID.Value) }
  }
  foreach ($m in (Get-ProbeManifest -ResultsRoot $ResultsRoot)) {
    if ($m.PSObject.Properties['ctl_sid'] -and $m.ctl_sid) { $sids.Add([string]$m.ctl_sid) }
    if ($m.PSObject.Properties['agent_sid'] -and $m.agent_sid) { $sids.Add([string]$m.agent_sid) }
  }
  @($sids | Select-Object -Unique)
}

function Stop-ProbeProcess {
  param([string[]]$Sids)
  $procs = @(Get-CimInstance -ClassName Win32_Process -ErrorAction SilentlyContinue | Where-Object {
      $owner = $null
      try { $owner = (Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid -ErrorAction SilentlyContinue).Sid } catch {}
      $owner -and ($Sids -contains $owner)
    })
  foreach ($p in $procs) { try { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue } catch {} }
}

function Remove-ProbeTask {
  foreach ($t in @(Get-ScheduledTask -TaskName 'ac-probe-*' -ErrorAction SilentlyContinue)) {
    Unregister-ScheduledTask -TaskName $t.TaskName -Confirm:$false -ErrorAction Stop
  }
}

function Revoke-ProbeRight {
  param([string]$ResultsRoot, [string[]]$Sids)
  $pairs = [ordered]@{}
  foreach ($s in @($Sids)) {
    if ($s) { $pairs["$s|SeBatchLogonRight"] = @{ Sid = [string]$s; Right = 'SeBatchLogonRight' } }
  }
  foreach ($m in (Get-ProbeManifest -ResultsRoot $ResultsRoot)) {
    if (-not $m.PSObject.Properties['granted_rights']) { continue }
    foreach ($g in @($m.granted_rights)) {
      if ($g.sid -and $g.right) { $pairs["$($g.sid)|$($g.right)"] = @{ Sid = [string]$g.sid; Right = [string]$g.right } }
    }
  }
  foreach ($p in $pairs.Values) { [void](Remove-ProbeRight -Sid $p.Sid -Right $p.Right) }
}

function Remove-ProbeProfileInstance {
  param([Parameter(Mandatory)]$Profile)
  # Win32_UserProfile has no Delete method; the WMI .Delete() is the intrinsic DeleteInstance,
  # which the CIM cmdlets expose only as Remove-CimInstance.
  Remove-CimInstance -InputObject $Profile -ErrorAction Stop
}

function Dismount-ProbeProfileHive {
  param([Parameter(Mandatory)][string]$Sid)
  # Only called for a profile reporting Loaded, which Windows refuses to delete. A just-ended batch
  # or secondary-logon session can leave the hive mounted under HKU for a while, so unloading it
  # first is a precaution; output is discarded because a failed unload just leaves the retry to fail.
  [void](& reg.exe unload "HKU\$Sid" 2>&1)
}

function Remove-ProbeProfile {
  param([string[]]$Sids)
  $profiles = @(Get-CimInstance -ClassName Win32_UserProfile -ErrorAction SilentlyContinue | Where-Object {
      ($_.SID -and ($Sids -contains $_.SID)) -or ($_.LocalPath -like 'C:\Users\ac-probe-*')
    })
  $failures = @()
  foreach ($p in $profiles) {
    $loaded = ($p.PSObject.Properties['Loaded']) -and $p.Loaded
    $done = $false
    $lastError = ''
    for ($i = 0; $i -lt 5; $i++) {
      try {
        if ($loaded -and $p.SID) { Dismount-ProbeProfileHive -Sid ([string]$p.SID) }
        Remove-ProbeProfileInstance -Profile $p
        $done = $true; break
      }
      catch { $lastError = [string]$_.Exception.Message; Start-Sleep -Seconds 2 }
    }
    if (-not $done) { $failures += "$($p.LocalPath): $lastError" }
  }
  if ($failures.Count -gt 0) { throw "one or more probe profiles could not be deleted: $($failures -join '; ')" }
}

function Remove-ProbeAccount {
  foreach ($u in @(Get-LocalUser -Name 'ac-probe-*' -ErrorAction SilentlyContinue)) {
    Remove-LocalUser -SID $u.SID -ErrorAction Stop
  }
}

function Remove-ProbeDirectory {
  if (Test-Path -LiteralPath $script:ProbeDir) {
    Remove-Item -LiteralPath $script:ProbeDir -Recurse -Force -ErrorAction Stop
  }
}

function Remove-ProbeResult {
  param([string]$ResultsRoot)
  if ($ResultsRoot -and (Test-Path -LiteralPath $ResultsRoot)) {
    Remove-Item -LiteralPath $ResultsRoot -Recurse -Force -ErrorAction Stop
  }
}

function Assert-ProbeGone {
  param([string[]]$Sids)
  $remnants = @()
  if (@(Get-LocalUser -Name 'ac-probe-*' -ErrorAction SilentlyContinue).Count -gt 0) { $remnants += 'account' }
  if (@(Get-ScheduledTask -TaskName 'ac-probe-*' -ErrorAction SilentlyContinue).Count -gt 0) { $remnants += 'task' }
  $profiles = @(Get-CimInstance -ClassName Win32_UserProfile -ErrorAction SilentlyContinue |
    Where-Object { ($_.SID -and ($Sids -contains $_.SID)) -or ($_.LocalPath -like 'C:\Users\ac-probe-*') })
  if ($profiles.Count -gt 0) { $remnants += 'profile' }
  $regSids = @(Get-ProbeProfileListSid)
  if (@($regSids | Where-Object { $Sids -contains $_ }).Count -gt 0) { $remnants += 'profilelist' }
  # Fail on any leftover right, not only the SeBatchLogonRight teardown grants: an
  # unexpected right on a probe SID means something outside the probe's control, and a
  # loud verification failure is safer than silently leaving it.
  foreach ($s in @($Sids)) {
    if (@(Get-ProbeAccountRight -Sid $s).Count -gt 0) { $remnants += 'rights'; break }
  }
  if ($remnants.Count -gt 0) { throw "probe remnants remain: $($remnants -join ',')" }
}

function Invoke-ProbeTeardown {
  [CmdletBinding()]
  param(
    [string]$ResultsRoot = 'C:\ProgramData\ac-probe-results',
    [switch]$IncludeResults
  )
  $steps = New-Object System.Collections.Generic.List[object]
  $sids = @(Get-ProbeSid -ResultsRoot $ResultsRoot)

  $record = {
    param([string]$Name, [scriptblock]$Action)
    $ok = $true; $err = ''
    try { & $Action } catch { $ok = $false; $err = [string]$_.Exception.Message }
    $steps.Add([pscustomobject]@{ Name = $Name; Ok = $ok; Error = $err })
  }

  & $record 'StopProcesses' { Stop-ProbeProcess -Sids $sids }
  & $record 'RemoveTasks' { Remove-ProbeTask }
  & $record 'RevokeRights' { Revoke-ProbeRight -ResultsRoot $ResultsRoot -Sids $sids }
  & $record 'DeleteProfiles' { Remove-ProbeProfile -Sids $sids }
  & $record 'RemoveAccounts' { Remove-ProbeAccount }
  & $record 'RemoveProbeDir' { Remove-ProbeDirectory }
  if ($IncludeResults) { & $record 'RemoveResults' { Remove-ProbeResult -ResultsRoot $ResultsRoot } }
  & $record 'Verify' { Assert-ProbeGone -Sids $sids }

  [pscustomobject]@{ Steps = $steps.ToArray() }
}

Export-ModuleMember -Function Invoke-ProbeTeardown, Remove-ProbeRight, Get-ProbeAccountRight
