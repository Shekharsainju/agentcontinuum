# Two one-turn headless runs: one on the stored login, one with an invalid dummy API key as the negative
# control. Records the shape of every stream event and the auth status output for both. Uses a little quota.
[CmdletBinding()]
param(
  [string]$Command = 'claude',
  [string]$AuthStatusArgs = 'auth status'
)

Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'E1Common.psm1') -Force

$cli = Get-E1Cli -Command $Command
# PSModulePath is dropped so a Windows PowerShell hook started by the CLI does not load PowerShell 7 modules.
$dropEnv = @(Get-ChildItem Env: | Where-Object Name -like 'ANTHROPIC_*' | ForEach-Object Name) + @('PSModulePath')
$dummyKey = @{ ANTHROPIC_API_KEY = 'sk-ant-e1-invalid-probe' }
$runArgs = '-p "Reply with the single word OK." --output-format stream-json --verbose --max-turns 1'

function Get-AuthShape {
  param([hashtable]$EnvOverrides)
  $r = Invoke-E1Command -Exe $cli.path -Arguments $AuthStatusArgs -TimeoutSeconds 30 -RemoveEnv $dropEnv -EnvOverrides $EnvOverrides
  [ordered]@{ exit_code = $r.exit_code; timed_out = $r.timed_out; stdout = (ConvertTo-E1Shape -Json $r.stdout); stderr = (Get-E1TextShape -Text $r.stderr) }
}

$work = Join-Path $env:TEMP ('ac-e1-init-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
  Write-Output 'run 1 of 2: stored login'
  $normal = [ordered]@{
    run         = Invoke-E1StreamRun -Exe $cli.path -Arguments $runArgs -WorkingDirectory $work -RemoveEnv $dropEnv -ExitGraceSeconds 60 -MaxSeconds 300
    auth_status = Get-AuthShape -EnvOverrides @{}
  }
  Write-Output 'run 2 of 2: invalid dummy API key'
  $withKey = [ordered]@{
    run         = Invoke-E1StreamRun -Exe $cli.path -Arguments $runArgs -WorkingDirectory $work -RemoveEnv $dropEnv -EnvOverrides $dummyKey -ExitGraceSeconds 60 -MaxSeconds 300
    auth_status = Get-AuthShape -EnvOverrides $dummyKey
  }
  Write-E1Result -Name 'init' -Cli $cli -Data ([ordered]@{
      removed_shell_env = $dropEnv
      stored_login      = $normal
      invalid_api_key   = $withKey
    })
  Write-Output 'init capture written to probes\e1\results\init.json'
}
finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
