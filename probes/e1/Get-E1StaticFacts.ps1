# Records Claude Code facts that cost no quota: version, install form, commands, auth status output shape,
# credential key names and settings key names. Values are never recorded.
[CmdletBinding()]
param([string]$Command = 'claude')

Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'E1Common.psm1') -Force

function Get-HelpFacts {
  param([string]$Text)
  $commands = New-Object System.Collections.Generic.List[string]
  $inCommands = $false
  foreach ($line in ($Text -split "`r?`n")) {
    if ($line -match '^Commands:') { $inCommands = $true; continue }
    if ($inCommands -and $line -match '^\S') { $inCommands = $false }
    # Command names sit at a two-space indent; wrapped description lines are indented further.
    if ($inCommands -and $line -match '^  ([a-z][a-z0-9-]*)(\s|$)') { $commands.Add($Matches[1]) }
  }
  [ordered]@{
    commands = $commands.ToArray()
    flags    = @([regex]::Matches($Text, '--[a-z][a-z0-9-]+') | ForEach-Object Value | Sort-Object -Unique)
  }
}

function Get-OutputShape {
  param([string]$Text)
  if ($Text.Trim().StartsWith('{')) { return [ordered]@{ json = ConvertTo-E1Shape -Json $Text } }
  [ordered]@{ text = Get-E1TextShape -Text $Text }
}

$cli = Get-E1Cli -Command $Command
# The auth answer must describe the stored login, not a key that happens to be in this shell.
$anthropicVars = @(Get-ChildItem Env: | Where-Object Name -like 'ANTHROPIC_*' | ForEach-Object Name)

$help = Get-HelpFacts -Text (Invoke-E1Command -Exe $cli.path -Arguments '--help').stdout
$authHelp = $null
if ($help.commands -contains 'auth') {
  $authHelp = Get-HelpFacts -Text (Invoke-E1Command -Exe $cli.path -Arguments 'auth --help').stdout
}

$authStatus = @()
foreach ($candidate in @('auth status', 'auth status --json')) {
  $r = Invoke-E1Command -Exe $cli.path -Arguments $candidate -TimeoutSeconds 30 -RemoveEnv $anthropicVars
  $authStatus += [ordered]@{
    args      = $candidate
    exit_code = $r.exit_code
    timed_out = $r.timed_out
    stdout    = Get-OutputShape -Text $r.stdout
    stderr    = Get-E1TextShape -Text $r.stderr
  }
}

$profileDir = $env:USERPROFILE
$credentialsPath = Join-Path $profileDir '.claude\.credentials.json'
$claudeJsonPath = Join-Path $profileDir '.claude.json'
$authKeyPattern = 'auth|key|oauth|account|token|login|subscription|billing|plan|org'
$claudeJsonTop = @()
if (Test-Path -LiteralPath $claudeJsonPath) {
  $claudeJsonTop = @(Get-E1KeyPaths -Path $claudeJsonPath | ForEach-Object { ($_ -split '[.\[]')[0] } | Sort-Object -Unique |
      Where-Object { $_ -match $authKeyPattern })
}

$settingsFiles = [ordered]@{
  managed    = Join-Path $env:ProgramData 'ClaudeCode\managed-settings.json'
  user       = Join-Path $profileDir '.claude\settings.json'
  user_local = Join-Path $profileDir '.claude\settings.local.json'
}
$settings = [ordered]@{}
foreach ($k in $settingsFiles.Keys) {
  $p = $settingsFiles[$k]
  $settings[$k] = [ordered]@{ exists = (Test-Path -LiteralPath $p); key_paths = @(Get-E1KeyPaths -Path $p) }
}

$credentialTargets = @(& cmdkey.exe /list 2>$null | Where-Object { $_ -match 'Target:' -and $_ -match 'claude' } |
    ForEach-Object { Hide-E1Email -Text ($_ -replace '^\s*Target:\s*', '') })

Write-E1Result -Name 'static' -Cli $cli -Data ([ordered]@{
    help                      = $help
    auth_help                 = $authHelp
    auth_status               = $authStatus
    shell_env_names           = @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(ANTHROPIC_|CLAUDE_)' } | ForEach-Object Name)
    credentials_file          = [ordered]@{ exists = (Test-Path -LiteralPath $credentialsPath); key_paths = @(Get-E1KeyPaths -Path $credentialsPath) }
    claude_json_auth_keys     = $claudeJsonTop
    claude_json_named_present = [ordered]@{
      primaryApiKey         = ($claudeJsonTop -contains 'primaryApiKey')
      oauthAccount          = ($claudeJsonTop -contains 'oauthAccount')
      customApiKeyResponses = ($claudeJsonTop -contains 'customApiKeyResponses')
    }
    settings                  = $settings
    credential_manager        = $credentialTargets
  })
Write-Output "static facts written for Claude Code $($cli.version)"
