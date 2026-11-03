<#
.SYNOPSIS  Stops and removes the Rasel API Windows service. The install and data directories are kept unless -RemoveFiles.
#>
[CmdletBinding()]
param([string]$ServiceName = 'RaselApi', [string]$InstallDir = 'C:\Rasel\api', [switch]$RemoveFiles)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')) { throw 'Run as Administrator.' }
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    & sc.exe stop $ServiceName | Out-Null; Start-Sleep 3
    & sc.exe delete $ServiceName | Out-Null
}
if ($RemoveFiles -and (Test-Path $InstallDir)) { Remove-Item -Recurse -Force $InstallDir }
Write-Host 'Removed (database and WhatsApp session data are never deleted by this script).'
