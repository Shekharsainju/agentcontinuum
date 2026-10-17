# Beacon.ps1 - escape-mode payload for the E0a probe (Windows PowerShell 5.1).
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Exchange
)

Set-StrictMode -Version 2.0

New-Item -ItemType Directory -Force -Path $Exchange | Out-Null
$ExchangeFull = (Resolve-Path -LiteralPath $Exchange).ProviderPath

$info = [ordered]@{
  pid        = $PID
  started_at = [datetime]::UtcNow.ToString('o')
}
$json = $info | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $ExchangeFull 'beacon.json'), $json, (New-Object Text.UTF8Encoding($false)))

$beatPath = Join-Path $ExchangeFull 'heartbeat-beacon.txt'
$deadline = [datetime]::UtcNow.AddSeconds(60)
while ([datetime]::UtcNow -lt $deadline) {
  Add-Content -LiteralPath $beatPath -Value ([datetime]::UtcNow.ToString('o'))
  Start-Sleep -Milliseconds 250
}
