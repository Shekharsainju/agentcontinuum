# ProbeCommon.psm1 - pure logic shared by the E0a probe scripts (Windows PowerShell 5.1).
Set-StrictMode -Version 2.0

function ConvertFrom-ProbeSecureString {
  # For tests and the P/Invoke boundary only.
  [CmdletBinding()]
  param([Parameter(Mandatory, Position = 0)][securestring]$Secure)
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
  try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
  finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function New-ProbePassword {
  [CmdletBinding()]
  param([Parameter(Position = 0)][ValidateRange(8, 128)][int]$Length = 32)
  $classes = @(
    'ABCDEFGHIJKLMNOPQRSTUVWXYZ',
    'abcdefghijklmnopqrstuvwxyz',
    '0123456789',
    '!@#$%^&*()-_=+[]{}'
  )
  $all = -join $classes
  $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
  try {
    $buf = New-Object byte[] 1
    # Unbiased random index in [0, $max) by rejection sampling.
    $next = {
      param([int]$max)
      $limit = 256 - (256 % $max)
      do { $rng.GetBytes($buf) } while ($buf[0] -ge $limit)
      [int]($buf[0] % $max)
    }
    $chars = New-Object System.Collections.Generic.List[char]
    foreach ($c in $classes) { $chars.Add($c[(& $next $c.Length)]) }
    while ($chars.Count -lt $Length) { $chars.Add($all[(& $next $all.Length)]) }
    for ($i = $chars.Count - 1; $i -gt 0; $i--) {   # Fisher-Yates shuffle
      $j = & $next ($i + 1)
      $tmp = $chars[$i]; $chars[$i] = $chars[$j]; $chars[$j] = $tmp
    }
    $ss = New-Object System.Security.SecureString
    foreach ($ch in $chars) { $ss.AppendChar($ch) }
    $ss.MakeReadOnly()
    $ss
  }
  finally { $rng.Dispose() }
}

function Read-Heartbeat {
  [CmdletBinding()]
  param([Parameter(Mandatory, Position = 0)]$Path)
  $p = $PSCmdlet.GetUnresolvedProviderPathFromPSPath([string]$Path)
  if (-not [IO.File]::Exists($p)) { return , @() }
  $text = $null
  try {
    $fs = New-Object IO.FileStream($p, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { $sr = New-Object IO.StreamReader($fs); $text = $sr.ReadToEnd() } finally { $fs.Dispose() }
  } catch { return , @() }
  $parts = $text -split "`r?`n"
  # A concurrently appended file can be read mid-write; a final line with no trailing newline is
  # a torn partial write, so drop it and count only complete lines.
  if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) {
    if ($parts.Count -le 1) { $parts = @() } else { $parts = $parts[0..($parts.Count - 2)] }
  }
  $out = New-Object System.Collections.Generic.List[datetime]
  foreach ($line in $parts) {
    $l = $line.Trim()
    if ($l -eq '') { continue }
    $d = [datetime]::MinValue
    if ([datetime]::TryParseExact($l, 'o', [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind, [ref]$d)) { $out.Add($d.ToUniversalTime()) }
  }
  , $out.ToArray()
}

function Get-FreezeVerdict {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory, Position = 0)][AllowEmptyCollection()][datetime[]]$Beats,
    [Parameter(Mandatory, Position = 1)][datetime]$FreezeAt,
    [Parameter(Mandatory, Position = 2)][datetime]$ThawAt
  )
  $f = $FreezeAt.ToUniversalTime().AddSeconds(0.5)
  $th = $ThawAt.ToUniversalTime()
  $b = @($Beats | ForEach-Object { $_.ToUniversalTime() } | Sort-Object)
  $during = @($b | Where-Object { $_ -gt $f -and $_ -lt $th })
  $before = @($b | Where-Object { $_ -le $f })
  $after = @($b | Where-Object { $_ -ge $th })
  $gap = $null; $resume = $null
  $ok = ($during.Count -eq 0)
  if ($after.Count -gt 0) {
    $resume = ($after[0] - $th).TotalSeconds
    if ($resume -gt 2) { $ok = $false }
    if ($before.Count -gt 0) {
      $gap = ($after[0] - $before[-1]).TotalSeconds
      if ($gap -lt 5) { $ok = $false }
    } else { $ok = $false }
  } else { $ok = $false }
  [pscustomobject]@{
    Result            = $(if ($ok) { 'pass' } else { 'fail' })
    GapSeconds        = $(if ($null -ne $gap) { [math]::Round($gap, 1) } else { $null })
    ResumeSeconds     = $(if ($null -ne $resume) { [math]::Round($resume, 1) } else { $null })
    BeatsDuringFreeze = $during.Count
  }
}

function Get-ProbeOsInfo {
  [CmdletBinding()]
  param()
  $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
  $caption = (Get-CimInstance -ClassName Win32_OperatingSystem).Caption
  $disp = if ($cv.PSObject.Properties['DisplayVersion']) { [string]$cv.DisplayVersion } else { [string]$cv.ReleaseId }
  $ubr = if ($cv.PSObject.Properties['UBR']) { [string]$cv.UBR } else { '0' }
  $warn = $null
  if ($caption -notlike '*Windows 11 Home*') { $warn = "Expected 'Windows 11 Home' but found '$caption'" }
  @{
    os_caption      = $caption
    build           = "$($cv.CurrentBuild).$ubr"
    display_version = $disp
    edition_warning = $warn
  }
}

function New-ProbeResult {
  [CmdletBinding()]
  param([Parameter(Mandatory, Position = 0)][ValidateSet('A', 'B', 'self', 'setup')][string]$Run)
  $os = Get-ProbeOsInfo
  @{
    schema          = 'ac-probe-e0a/1'
    edition         = 'Windows 11 Home'
    run             = $Run
    os_caption      = $os.os_caption
    build           = $os.build
    display_version = $os.display_version
    edition_warning = $os.edition_warning
    started_utc     = [datetime]::UtcNow.ToString('o')
    checks          = @()
  }
}

function Add-ProbeCheck {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory, Position = 0)][hashtable]$Result,
    [Parameter(Mandatory, Position = 1)][string]$Id,
    [Parameter(Mandatory, Position = 2)][string]$Name,
    [Parameter(Mandatory, Position = 3)][ValidateSet('pass', 'fail', 'error', 'finding', 'native_load_blocked')][string]$Outcome,
    [Parameter(Position = 4)][hashtable]$Details = @{}
  )
  $Result['checks'] = @($Result['checks']) + @([ordered]@{ id = $Id; name = $Name; result = $Outcome; details = $Details })
}

function Write-ProbeResult {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory, Position = 0)][hashtable]$Result,
    [Parameter(Mandatory, Position = 1)][string]$Path
  )
  $p = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($Path)
  $json = $Result | ConvertTo-Json -Depth 8
  [IO.File]::WriteAllText($p, $json, (New-Object Text.UTF8Encoding($false)))
}

function Test-SecretAbsent {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory, Position = 0)][string[]]$Paths,
    [Parameter(Mandatory, Position = 1)][string[]]$Secrets
  )
  $latin1 = [Text.Encoding]::GetEncoding(28591)
  $needles = foreach ($s in $Secrets) {
    foreach ($enc in @(@('utf8', [Text.Encoding]::UTF8), @('utf16le', [Text.Encoding]::Unicode))) {
      [pscustomobject]@{ Enc = $enc[0]; Text = $latin1.GetString($enc[1].GetBytes($s)) }
    }
  }
  $hits = New-Object System.Collections.Generic.List[string]
  $unscanned = New-Object System.Collections.Generic.List[string]
  foreach ($root in $Paths) {
    $rp = $PSCmdlet.GetUnresolvedProviderPathFromPSPath($root)
    if (-not (Test-Path -LiteralPath $rp)) { $unscanned.Add($rp); continue }
    $gciErr = @()
    $files = @(Get-ChildItem -LiteralPath $rp -Recurse -Force -File -ErrorAction SilentlyContinue -ErrorVariable gciErr)
    foreach ($e in $gciErr) {
      $t = if ($e.TargetObject) { [string]$e.TargetObject } else { $rp }
      $unscanned.Add($t)
    }
    foreach ($file in $files) {
      try {
        $fs = New-Object IO.FileStream($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
          $bytes = New-Object byte[] $fs.Length
          $n = 0
          while ($n -lt $bytes.Length) {
            $r = $fs.Read($bytes, $n, $bytes.Length - $n)
            if ($r -le 0) { break }
            $n += $r
          }
        }
        finally { $fs.Dispose() }
      } catch { $unscanned.Add($file.FullName); continue }
      $hay = $latin1.GetString($bytes)
      foreach ($nd in $needles) {
        if ($hay.IndexOf($nd.Text, [StringComparison]::Ordinal) -ge 0) { $hits.Add("$($file.FullName) [$($nd.Enc)]") }
      }
    }
  }
  [pscustomobject]@{
    Clean     = ($hits.Count -eq 0 -and $unscanned.Count -eq 0)
    Hits      = $hits.ToArray()
    Unscanned = $unscanned.ToArray()
  }
}

Export-ModuleMember -Function New-ProbePassword, ConvertFrom-ProbeSecureString, Read-Heartbeat,
  Get-FreezeVerdict, New-ProbeResult, Add-ProbeCheck, Write-ProbeResult, Get-ProbeOsInfo, Test-SecretAbsent
