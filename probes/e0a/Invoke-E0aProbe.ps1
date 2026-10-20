#Requires -RunAsAdministrator
<#
.SYNOPSIS
Runs the E0a probe elevated and removes everything it created.
#>
[CmdletBinding()]
param([string]$ReaderAccount)

Import-Module (Join-Path $PSScriptRoot 'ProbeSetup.psm1') -Force
exit (Start-E0aProbe -ReaderAccount $ReaderAccount)
