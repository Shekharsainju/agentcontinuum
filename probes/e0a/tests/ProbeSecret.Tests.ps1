Import-Module "$PSScriptRoot\..\ProbeSecret.psm1" -Force
Import-Module "$PSScriptRoot\..\ProbeCommon.psm1" -Force

Describe 'Secret pipe' {
  $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

  It 'round-trips once' {
    $s = New-ProbePassword
    $p = New-ProbeSecretPipe $me $s
    (ConvertFrom-ProbeSecureString (Receive-ProbeSecret $p.Name)) | Should Be (ConvertFrom-ProbeSecureString $s)
  }
  It 'serves only one connection' {
    $p = New-ProbeSecretPipe $me (New-ProbePassword)
    Receive-ProbeSecret $p.Name | Out-Null
    { Receive-ProbeSecret $p.Name -TimeoutMs 1500 } | Should Throw
  }
  It 'denies a SID not on the DACL' {
    $p = New-ProbeSecretPipe 'S-1-5-32-546' (New-ProbePassword)
    { Receive-ProbeSecret $p.Name -TimeoutMs 1500 } | Should Throw
  }
  It 'refuses a squatted name and never serves the secret' {
    $name = 'ac-probe-squat-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $squat = New-Object IO.Pipes.NamedPipeServerStream($name, 'InOut', 4)
    try { { New-ProbeSecretPipe $me (New-ProbePassword) -Name $name } | Should Throw 'already in use' }
    finally { $squat.Dispose() }
  }
  It 'writes nothing to a client whose SID is not AllowedSid' {
    $p = New-ProbeSecretPipe 'S-1-5-32-546' (New-ProbePassword) -DaclSid $me
    { Receive-ProbeSecret $p.Name -TimeoutMs 3000 } | Should Throw
    (Receive-Job $p.Job -Wait) | Should Match '^client_sid_mismatch:'
  }
}
