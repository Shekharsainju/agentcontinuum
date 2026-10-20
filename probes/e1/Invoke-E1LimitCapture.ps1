# Repeats a short headless run until one ends without success, to capture how a usage-limit stop looks:
# which hooks fire and with which fields, the last stream event, and whether the process exits on its own.
# Spends the quota that is left, so run it only when you are near a limit and paid overage is off.
[CmdletBinding()]
param(
  [string]$Command = 'claude',
  [int]$MaxRuns = 200,
  [string]$Prompt = 'Write about 400 words on the history of the printing press.'
)

Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'E1Common.psm1') -Force

Write-Output 'This spends your remaining Claude quota until a run stops.'
Write-Output 'Paid overage (extra usage) must be OFF, or the runs may be billed instead of stopping.'
$answer = Read-Host "Type 'overage is off' to continue"
if ($answer -ne 'overage is off') { Write-Output 'Not confirmed. Nothing was run.'; exit 1 }

$cli = Get-E1Cli -Command $Command
# PSModulePath is dropped so a Windows PowerShell hook started by the CLI does not load PowerShell 7 modules.
$dropEnv = @(Get-ChildItem Env: | Where-Object Name -like 'ANTHROPIC_*' | ForEach-Object Name) + @('PSModulePath')
$work = Join-Path $env:TEMP ('ac-e1-limit-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$hookOut = Join-Path $work 'hooks.jsonl'
$recorder = Join-Path $PSScriptRoot 'Write-E1HookRecord.ps1'

$hooks = [ordered]@{}
foreach ($name in @('StopFailure', 'Stop', 'SessionEnd')) {
  $cmd = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$recorder`" -Hook $name -Out `"$hookOut`""
  $hooks[$name] = @(@{ hooks = @(@{ type = 'command'; command = $cmd }) })
}
$settingsPath = Join-Path $work 'settings.json'
[IO.File]::WriteAllText($settingsPath, (@{ hooks = $hooks } | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))

function Get-HookRecords {
  param([string]$SessionId)
  if (-not (Test-Path -LiteralPath $hookOut)) { return @() }
  @(Get-Content -LiteralPath $hookOut | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object {
      $_.shape.PSObject.Properties['session_id'] -and $_.shape.session_id -eq $SessionId })
}

$rows = New-Object System.Collections.Generic.List[object]
$stopping = $null
try {
  for ($i = 1; $i -le $MaxRuns; $i++) {
    # A fresh session per run keeps each run small and gives every hook record an unambiguous owner.
    $sid = [guid]::NewGuid().ToString()
    $runArgs = "-p `"$Prompt`" --session-id $sid --settings `"$settingsPath`" --output-format stream-json --verbose --max-turns 1"
    $r = Invoke-E1StreamRun -Exe $cli.path -Arguments $runArgs -WorkingDirectory $work -RemoveEnv $dropEnv -ExitGraceSeconds 120
    $last = if (@($r.events).Count) { $r.events[-1].shape } else { @{} }
    $subtype = if ($last.Contains('subtype')) { $last['subtype'] } else { $null }
    $isError = if ($last.Contains('is_error')) { $last['is_error'] } else { $null }
    $runHooks = Get-HookRecords -SessionId $sid
    $ok = $r.exited_by_itself -and $r.exit_code -eq 0 -and $subtype -eq 'success' -and $isError -ne $true
    $rows.Add([ordered]@{
        run = $i; ok = $ok; exit_code = $r.exit_code; exited_by_itself = $r.exited_by_itself; killed_reason = $r.killed_reason
        seconds_final_to_exit = $r.seconds_final_to_exit; result_subtype = $subtype; is_error = $isError
        hooks = @($runHooks | ForEach-Object hook)
      })
    Write-Output ("run {0}: {1}" -f $i, $(if ($ok) { 'ok' } else { 'stopped' }))
    if (-not $ok) {
      $stopping = [ordered]@{ run = $r; hook_records = $runHooks; session_id_matched = (@($runHooks).Count -gt 0) }
      break
    }
  }
  Write-E1Result -Name 'limit' -Cli $cli -Data ([ordered]@{
      max_runs      = $MaxRuns
      prompt_length = $Prompt.Length
      runs          = $rows.ToArray()
      stopping_run  = $stopping
      all_hooks     = @(if (Test-Path -LiteralPath $hookOut) { Get-Content -LiteralPath $hookOut | ForEach-Object { $_ | ConvertFrom-Json } })
    })
  Write-Output 'limit capture written to probes\e1\results\limit.json'
}
finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
