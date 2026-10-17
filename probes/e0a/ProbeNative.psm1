# ProbeNative.psm1 - loads the inline C# native interop layer (AcProbe.Native) for the E0a probe.
Set-StrictMode -Version 2.0

function Import-ProbeNative {
  # Never throws: a compile/load failure is returned as Loaded=$false (native_load_blocked).
  [CmdletBinding()]
  param([string]$SourcePath)
  $isDefault = -not $PSBoundParameters.ContainsKey('SourcePath')
  if ($isDefault) { $SourcePath = Join-Path $PSScriptRoot 'Native.cs' }
  # Add-Type types cannot be unloaded; do not re-add the real type once loaded.
  if ($isDefault -and ('AcProbe.Native' -as [type])) {
    return [pscustomobject]@{ Loaded = $true; Error = '' }
  }
  try {
    Add-Type -Path ([string]$SourcePath) -ErrorAction Stop
    [pscustomobject]@{ Loaded = $true; Error = '' }
  }
  catch {
    [pscustomobject]@{ Loaded = $false; Error = [string]$_.Exception.Message }
  }
}

Export-ModuleMember -Function Import-ProbeNative
