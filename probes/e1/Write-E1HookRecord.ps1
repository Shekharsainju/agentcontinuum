# Claude Code hook command for the limit capture: appends the shape of the hook payload, never its values.
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Hook,
  [Parameter(Mandatory)][string]$Out
)

Set-StrictMode -Version 2.0
Import-Module (Join-Path $PSScriptRoot 'E1Common.psm1') -Force

# A failing hook must never change how the run ends, so every error is swallowed.
try {
  $raw = [Console]::In.ReadToEnd()
  $record = [ordered]@{ hook = $Hook; at_utc = [datetime]::UtcNow.ToString('o'); shape = (ConvertTo-E1Shape -Json $raw) }
  Add-Content -LiteralPath $Out -Value ($record | ConvertTo-Json -Depth 8 -Compress) -Encoding UTF8
}
catch {}
exit 0
