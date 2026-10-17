Set-StrictMode -Version 2.0

$script:ProbeBinDir = $PSScriptRoot

function New-ProbeSecretPipe {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory, Position = 0)][string]$AllowedSid,
    [Parameter(Mandatory, Position = 1)][securestring]$Secret,
    [string]$Name,
    [string]$DaclSid
  )
  if (-not $Name) {
    $hex = -join (1..16 | ForEach-Object { '{0:x}' -f (Get-Random -Minimum 0 -Maximum 16) })
    $Name = "ac-probe-$hex"
  }
  if (-not $DaclSid) { $DaclSid = $AllowedSid }

  $sidObj = New-Object System.Security.Principal.SecurityIdentifier($DaclSid)
  $dacl = New-Object System.Security.AccessControl.RawAcl([System.Security.AccessControl.GenericAcl]::AclRevision, 1)
  $readMask = 0x120089  # FILE_GENERIC_READ: enough to connect and read, nothing more.
  $ace = New-Object System.Security.AccessControl.CommonAce(
    ([System.Security.AccessControl.AceFlags]::None),
    ([System.Security.AccessControl.AceQualifier]::AccessAllowed),
    $readMask, $sidObj, $false, $null)
  $dacl.InsertAce(0, $ace)
  $sd = New-Object System.Security.AccessControl.RawSecurityDescriptor(
    ([System.Security.AccessControl.ControlFlags]::DiscretionaryAclPresent -bor [System.Security.AccessControl.ControlFlags]::SelfRelative),
    $null, $null, $null, $dacl)
  $sdBytes = New-Object byte[] $sd.BinaryLength
  $sd.GetBinaryForm($sdBytes, 0)

  $readyFile = Join-Path ([IO.Path]::GetTempPath()) ('acpsp-' + [guid]::NewGuid().ToString('N') + '.ready')

  $job = Start-Job -ArgumentList $script:ProbeBinDir, $Name, $AllowedSid, $Secret, $sdBytes, $readyFile -ScriptBlock {
    param($BinDir, $Name, $AllowedSid, $Secret, $SdBytes, $ReadyFile)
    Set-StrictMode -Version 2.0
    Import-Module (Join-Path $BinDir 'ProbeNative.psm1') -Force
    Import-Module (Join-Path $BinDir 'ProbeCommon.psm1') -Force
    $null = Import-ProbeNative

    $open = [AcProbe.Native]::CreateFirstInstancePipe("\\.\pipe\$Name", $SdBytes)
    if ($null -eq $open.Handle -or $open.Handle.IsInvalid -or $open.Win32Error -ne 0) {
      Set-Content -LiteralPath $ReadyFile -Value "ERR:$($open.Win32Error)" -Encoding ASCII
      return
    }
    $server = New-Object System.IO.Pipes.NamedPipeServerStream(
      ([System.IO.Pipes.PipeDirection]::Out), $false, $true, $open.Handle)
    Set-Content -LiteralPath $ReadyFile -Value 'READY' -Encoding ASCII

    try {
      $server.WaitForConnection()

      $box = [hashtable]::Synchronized(@{})
      $worker = [System.IO.Pipes.PipeStreamImpersonationWorker]({
          $box['sid'] = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        }.GetNewClosure())
      $server.RunAsClient($worker)
      $clientSid = $box['sid']

      if ($clientSid -eq $AllowedSid) {
        $plain = ConvertFrom-ProbeSecureString $Secret
        $bytes = [System.Text.Encoding]::Unicode.GetBytes($plain)
        try {
          $server.Write($bytes, 0, $bytes.Length)
          $server.WaitForPipeDrain()
        }
        finally { [Array]::Clear($bytes, 0, $bytes.Length) }
        'delivered'
      }
      else {
        "client_sid_mismatch:$clientSid"
      }
    }
    finally {
      try { if ($server.IsConnected) { $server.Disconnect() } } catch {}
      try { $server.Dispose() } catch {}
    }
  }

  $deadline = [datetime]::UtcNow.AddSeconds(30)
  $signal = $null
  while ([datetime]::UtcNow -lt $deadline) {
    if (Test-Path -LiteralPath $readyFile) {
      $signal = (Get-Content -LiteralPath $readyFile -Raw -ErrorAction SilentlyContinue)
      if ($signal) { $signal = $signal.Trim(); break }
    }
    if ($job.State -eq 'Completed' -or $job.State -eq 'Failed') { break }
    Start-Sleep -Milliseconds 100
  }
  Remove-Item -LiteralPath $readyFile -Force -ErrorAction SilentlyContinue

  if ($signal -like 'ERR:*') {
    $code = $signal.Substring(4)
    Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    throw "pipe name already in use (win32 $code)"
  }
  if ($signal -ne 'READY') {
    Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    throw 'secret pipe server did not start'
  }
  [pscustomobject]@{ Name = $Name; Job = $job }
}

function Receive-ProbeSecret {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory, Position = 0)][string]$PipeName,
    [int]$TimeoutMs = 10000
  )
  $client = New-Object System.IO.Pipes.NamedPipeClientStream(
    '.', $PipeName, ([System.IO.Pipes.PipeDirection]::In),
    ([System.IO.Pipes.PipeOptions]::None),
    ([System.Security.Principal.TokenImpersonationLevel]::Identification))
  try {
    $client.Connect($TimeoutMs)
    $ms = New-Object System.IO.MemoryStream
    $buf = New-Object byte[] 512
    while (($n = $client.Read($buf, 0, $buf.Length)) -gt 0) { $ms.Write($buf, 0, $n) }
    $bytes = $ms.ToArray()
    if ($bytes.Length -eq 0) { throw 'no secret delivered' }
    $chars = [System.Text.Encoding]::Unicode.GetChars($bytes)
    try {
      $ss = New-Object System.Security.SecureString
      foreach ($c in $chars) { $ss.AppendChar($c) }
      $ss.MakeReadOnly()
      $ss
    }
    finally {
      [Array]::Clear($chars, 0, $chars.Length)
      [Array]::Clear($bytes, 0, $bytes.Length)
    }
  }
  finally { $client.Dispose() }
}

Export-ModuleMember -Function New-ProbeSecretPipe, Receive-ProbeSecret
