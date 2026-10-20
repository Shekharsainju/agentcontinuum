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

Export-ModuleMember -Function Protect-E1Value, ConvertTo-E1Shape
