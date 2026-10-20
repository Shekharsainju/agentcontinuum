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

Export-ModuleMember -Function Protect-E1Value
