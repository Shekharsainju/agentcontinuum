Set-StrictMode -Version 2.0

$script:TimestampPattern = '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})?$'
$script:EnumeratedPattern = '^[A-Za-z0-9_.:-]{1,40}$'

function Protect-E1Value {
  <# Returns a value safe to record: enumerated strings, timestamps, numbers and booleans as they are; any other string as its length only. #>
  param([AllowNull()]$Value)
  if ($null -eq $Value) { return $null }
  if ($Value -is [bool] -or $Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) { return $Value }
  if ($Value -is [datetime]) { return $Value.ToUniversalTime().ToString('o') }
  $text = [string]$Value
  if ($text -match $script:TimestampPattern) { return $text }
  if ($text -match $script:EnumeratedPattern) { return $text }
  @{ redacted = $true; length = $text.Length }
}

function Add-E1ShapeNode {
  param($Shape, [string]$Path, $Node)
  if ($Node -is [System.Management.Automation.PSCustomObject]) {
    foreach ($p in $Node.PSObject.Properties) {
      $child = if ($Path) { "$Path.$($p.Name)" } else { $p.Name }
      Add-E1ShapeNode -Shape $Shape -Path $child -Node $p.Value
    }
    return
  }
  if ($Node -is [array]) {
    $Shape["$Path[#]"] = $Node.Count
    # One element is enough to learn the schema; more would only add content.
    if ($Node.Count -gt 0) { Add-E1ShapeNode -Shape $Shape -Path "$Path[0]" -Node $Node[0] }
    return
  }
  $Shape[$Path] = Protect-E1Value $Node
}

function ConvertTo-E1Shape {
  <# Reduces a JSON document to its key paths, each with a protected leaf value. #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Json)
  $shape = [ordered]@{}
  try { $doc = ConvertFrom-Json -InputObject $Json -ErrorAction Stop }
  catch { $shape['_parse_error'] = $true; $shape['_length'] = $Json.Length; return $shape }
  Add-E1ShapeNode -Shape $shape -Path '' -Node $doc
  $shape
}

# Longer phrases first, so "not logged in" is not also counted as "logged in".
$script:TextCategories = @(
  'not logged in', 'logged in', 'claude max', 'claude pro', 'claude team', 'claude enterprise', 'claude.ai',
  'subscription', 'api key', 'apikey', 'api_key', 'oauth', 'anthropic console', 'console',
  'bedrock', 'vertex', 'organization', 'token', 'expired', 'login'
)

function Get-E1TextShape {
  <# Describes text output by length, line count and which fixed category words appear, never by its content. #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
  $rest = $Text.ToLowerInvariant()
  $found = New-Object System.Collections.Generic.List[string]
  foreach ($c in $script:TextCategories) {
    if ($rest.Contains($c)) { $found.Add($c); $rest = $rest.Replace($c, ' ') }
  }
  $lines = @($Text -split "`r?`n" | Where-Object { $_ -ne '' }).Count
  [ordered]@{ length = $Text.Length; lines = $lines; categories = $found.ToArray() }
}

function Get-E1KeyPaths {
  <# Lists the key paths of a JSON file. Values are never returned. #>
  param([Parameter(Mandatory)][string]$Path)
  if (-not (Test-Path -LiteralPath $Path)) { return @() }
  $shape = ConvertTo-E1Shape -Json (Get-Content -LiteralPath $Path -Raw)
  @($shape.Keys)
}

function Hide-E1Email {
  <# Replaces email addresses in a name, such as a Credential Manager target, with a placeholder. #>
  param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
  $Text -replace '[^\s@:=]+@[^\s@]+\.[A-Za-z]{2,}', '<email>'
}

function New-E1StartInfo {
  param([string]$Exe, [string]$Arguments, [string]$WorkingDirectory, [hashtable]$EnvOverrides, [string[]]$RemoveEnv)
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $Exe
  $psi.Arguments = $Arguments
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $psi.RedirectStandardInput = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
  $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
  if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
  foreach ($name in @($RemoveEnv)) { if ($name -and $psi.EnvironmentVariables.ContainsKey($name)) { $psi.EnvironmentVariables.Remove($name) } }
  if ($EnvOverrides) { foreach ($k in $EnvOverrides.Keys) { $psi.EnvironmentVariables[$k] = [string]$EnvOverrides[$k] } }
  $psi
}

function Stop-E1ProcessTree {
  param([Parameter(Mandatory)][int]$ProcessId)
  # Windows PowerShell's Process.Kill() cannot take the tree, and the CLI may have children.
  [void](& taskkill.exe /T /F /PID $ProcessId 2>&1)
}

function Invoke-E1Command {
  <# Runs a command with stdin closed, an optional environment change and a timeout. Returns its output for the caller to shape. #>
  param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$Arguments = '',
    [int]$TimeoutSeconds = 30,
    [string]$WorkingDirectory,
    [hashtable]$EnvOverrides,
    [string[]]$RemoveEnv
  )
  $psi = New-E1StartInfo -Exe $Exe -Arguments $Arguments -WorkingDirectory $WorkingDirectory -EnvOverrides $EnvOverrides -RemoveEnv $RemoveEnv
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $p = [Diagnostics.Process]::Start($psi)
  $p.StandardInput.Close()
  $out = $p.StandardOutput.ReadToEndAsync()
  $err = $p.StandardError.ReadToEndAsync()
  $timedOut = -not $p.WaitForExit($TimeoutSeconds * 1000)
  if ($timedOut) { Stop-E1ProcessTree -ProcessId $p.Id; [void]$p.WaitForExit(5000) }
  [void]$out.Wait(5000); [void]$err.Wait(5000)
  [ordered]@{
    exit_code = if ($timedOut) { $null } else { $p.ExitCode }
    stdout    = if ($out.IsCompleted) { $out.Result } else { '' }
    stderr    = if ($err.IsCompleted) { $err.Result } else { '' }
    timed_out = $timedOut
    seconds   = [math]::Round($sw.Elapsed.TotalSeconds, 1)
  }
}

Export-ModuleMember -Function Protect-E1Value, ConvertTo-E1Shape, Get-E1TextShape, Get-E1KeyPaths, Hide-E1Email, Invoke-E1Command
