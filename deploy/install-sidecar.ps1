<#
.SYNOPSIS  Runs the sidecar at every boot as a Scheduled Task (SYSTEM), no third-party service wrapper needed.
.NOTES     node.exe is not a Windows-service-aware process, so sc.exe cannot host it; a boot task with restart-on-failure is the
           built-in alternative. To get a real service use NSSM instead: nssm install RaselSidecar node.exe src\server.js
           (set AppDirectory and AppEnvironmentExtra with the same variables). Run `npm install` in -SidecarDir first.
#>
[CmdletBinding()]
param(
    [string]$TaskName   = 'RaselSidecar',
    [string]$SidecarDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'sidecar'),
    [string]$SessionDir = 'C:\Rasel\session',
    [int]$Port          = 5001,
    [Parameter(Mandatory)][string]$SharedToken,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')) { throw 'Run as Administrator.' }
if ($Uninstall) { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue; Write-Host 'Task removed (session data kept).'; return }
if ($SharedToken.Length -lt 16) { throw 'SharedToken must be at least 16 characters.' }
$node = (Get-Command node.exe).Source
New-Item -ItemType Directory -Force $SessionDir | Out-Null
$cmd = "`$env:PORT='$Port'; `$env:HOST='127.0.0.1'; `$env:SESSION_DIR='$SessionDir'; `$env:SHARED_TOKEN='$SharedToken'; Set-Location '$SidecarDir'; & '$node' src\server.js"
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -Command `"$cmd`""
$settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger (New-ScheduledTaskTrigger -AtStartup) -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Write-Host "Started. Scan the QR at http://127.0.0.1:$Port/Qr (send header X-Bridge-Token). Set WhatsApp__Bridge__BaseUrl=http://127.0.0.1:$Port and WhatsApp__Bridge__SharedToken in the API env file."
