# Captures how Claude Code signals a usage limit, from one tiny headless run started while you are already at a limit:
# which hooks fire and with which fields, the stream's last events, and whether the process exits on its own.
# Saves results\limit-<version>.json and prints a summary to paste back. Values are reduced to their shape.
[CmdletBinding()]
param(
  [string]$Command = 'claude',
  [string]$OutDir = (Join-Path $PSScriptRoot 'results'),
  [int]$IdleSeconds = 300
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'E1Common.psm1') -Force

$cli = Get-E1Cli -Command $Command
Write-Output "Claude Code $($cli.version)"

# PSModulePath is dropped so a Windows PowerShell hook started by the CLI does not load PowerShell 7 modules.
$dropEnv = @(Get-ChildItem Env: | Where-Object Name -like 'ANTHROPIC_*' | ForEach-Object Name) + @('PSModulePath')
$work = Join-Path $env:TEMP ('ac-e1-real-limit-' + [guid]::NewGuid().ToString('N'))
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

function Select-Shape {
  param($Shape)
  # Token counts and per-model usage are long and say nothing about the limit.
  $kept = [ordered]@{}
  foreach ($k in $Shape.Keys) { if ($k -notmatch '^(usage|modelUsage|permission_denials)') { $kept[$k] = $Shape[$k] } }
  $kept
}

$exitCode = 0
try {
  $sid = [guid]::NewGuid().ToString()
  $runArgs = "-p `"Reply with the single word OK.`" --session-id $sid --settings `"$settingsPath`" --output-format stream-json --verbose --max-turns 1"
  Write-Output 'Sending one short request. This can take up to 5 minutes.'
  $run = Invoke-E1StreamRun -Exe $cli.path -Arguments $runArgs -WorkingDirectory $work -RemoveEnv $dropEnv -ExitGraceSeconds 120 -IdleSeconds $IdleSeconds -MaxSeconds 900

  $hookRecords = @()
  if (Test-Path -LiteralPath $hookOut) {
    $hookRecords = @(Get-Content -LiteralPath $hookOut | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object {
        $_.shape.PSObject.Properties['session_id'] -and $_.shape.session_id -eq $sid })
  }
  $shapes = @($run.events | ForEach-Object { $_.shape })
  $result = @($shapes | Where-Object { $_.Contains('type') -and $_['type'] -eq 'result' }) | Select-Object -Last 1
  $succeeded = $run.exited_by_itself -and $run.exit_code -eq 0 -and $null -ne $result -and
    $result.Contains('is_error') -and $result['is_error'] -eq $false

  if ($succeeded) {
    Write-Output 'Not at a limit: the request went through. Nothing was saved.'
    Write-Output 'Run this again when Claude Code tells you that you have hit your usage limit.'
    $exitCode = 2
  }
  else {
    $name = "limit-$($cli.version)"
    Write-E1Result -Name $name -Cli $cli -Dir $OutDir -Data ([ordered]@{
        session_id_matched = ($hookRecords.Count -gt 0)
        run                = $run
        hook_records       = $hookRecords
      })
    $summary = [ordered]@{
      version               = $cli.version
      exit_code             = $run.exit_code
      exited_by_itself      = $run.exited_by_itself
      killed_reason         = $run.killed_reason
      seconds_final_to_exit = $run.seconds_final_to_exit
      stderr                = $run.stderr
      event_types           = @($shapes | ForEach-Object { "$($_['type'])/$(if ($_.Contains('subtype')) { $_['subtype'] })" })
      rate_limit_events     = @($shapes | Where-Object { $_.Contains('type') -and $_['type'] -eq 'rate_limit_event' })
      result                = if ($null -ne $result) { Select-Shape $result } else { $null }
      hooks                 = @($hookRecords | ForEach-Object { [ordered]@{ hook = $_.hook; shape = $_.shape } })
    }
    Write-Output "Saved probes\e1\results\$name.json"
    Write-Output ''
    Write-Output '----- copy from here -----'
    Write-Output ($summary | ConvertTo-Json -Depth 8)
    Write-Output '----- to here -----'
  }
}
finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
exit $exitCode
