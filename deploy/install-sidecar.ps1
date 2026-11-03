<#
.SYNOPSIS  Runs a Rasel sidecar (one WhatsApp number) at every boot as a Scheduled Task named RaselSidecar-<InstanceName>.
.DESCRIPTION
  One instance = one linked WhatsApp number = one task, one port, one session folder. Install several instances for several numbers.
    .\install-sidecar.ps1 -InstanceName main  -SharedToken <16+ chars>                  # port: first free from 5301
    .\install-sidecar.ps1 -InstanceName sales -SharedToken <16+ chars> -Port 5302
    .\install-sidecar.ps1 -List
    .\install-sidecar.ps1 -InstanceName sales -Uninstall                                 # session data is kept
  Refuses when the task name already exists or the port is listening, unless -Force. Port 5001 is refused always (Afzaz's bridge).
  -DryRun prints what would be done and changes nothing (no admin needed).
.NOTES     node.exe is not a Windows-service-aware process, so sc.exe cannot host it; a boot task with restart-on-failure is the
           built-in alternative. For a real service use NSSM instead (AppDirectory + AppEnvironmentExtra with the same variables).
           Run `npm ci` in -SidecarDir first.
#>
[CmdletBinding()]
param(
    [string]$InstanceName = 'main',
    [string]$SidecarDir   = (Join-Path (Split-Path -Parent $PSScriptRoot) 'sidecar'),
    [string]$SessionRoot  = 'C:\Rasel\sessions',
    [int]$Port            = 0,
    [string]$SharedToken,
    [switch]$Uninstall,
    [switch]$List,
    [switch]$Force,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$prefix = 'RaselSidecar-'
$minPort = 5301
$reservedPorts = @(5001)

function Test-Admin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator') }
function Test-PortListening([int]$p) { [bool](Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue) }
function Get-Instances { Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like "$prefix*" } }
function Get-InstancePort($task) { if ($task.Description -match 'Port=(\d+)') { [int]$Matches[1] } else { $null } }

if ($List) {
    $rows = @(Get-Instances | ForEach-Object {
        $p = Get-InstancePort $_
        [pscustomobject]@{
            Instance   = $_.TaskName.Substring($prefix.Length)
            State      = $_.State
            Port       = $p
            Listening  = if ($p) { Test-PortListening $p } else { $null }
            SessionDir = if ($_.Description -match 'SessionDir=([^;]+)') { $Matches[1] } else { '' }
        }
    })
    if ($rows.Count -eq 0) { Write-Host 'No Rasel sidecar instances installed.' } else { $rows | Format-Table -AutoSize }
    return
}

if ($InstanceName -notmatch '^[A-Za-z0-9_-]{1,32}$') { throw "InstanceName '$InstanceName' is invalid: use 1-32 characters from A-Z a-z 0-9 _ -" }
$taskName   = "$prefix$InstanceName"
$sessionDir = Join-Path $SessionRoot $InstanceName
if (-not $DryRun -and -not (Test-Admin)) { throw 'Run as Administrator.' }

if ($Uninstall) {
    if ($DryRun) { Write-Host "[dry-run] would remove task $taskName (session data in $sessionDir is kept)"; return }
    if (-not (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) { throw "Task $taskName does not exist. Use -List to see installed instances." }
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "Task $taskName removed. Session data kept in $sessionDir (it is the linked phone: delete it only to unlink)."
    return
}

if (-not $SharedToken -or $SharedToken.Length -lt 16) { throw 'SharedToken must be at least 16 characters.' }

$taken = @(Get-Instances | ForEach-Object { Get-InstancePort $_ } | Where-Object { $_ })
if ($Port -eq 0) {
    $Port = $minPort
    while (($taken -contains $Port) -or (Test-PortListening $Port) -or ($reservedPorts -contains $Port)) { $Port++ }
}
if ($Port -lt 1024 -or $Port -gt 65535) { throw "Port $Port is out of range (1024-65535)." }
if ($reservedPorts -contains $Port) { throw "Port $Port is reserved for the Afzaz bridge. Use $minPort or higher." }

if (-not $Force) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw "Task $taskName already exists. Choose another -InstanceName, or use -Force to replace it." }
    if (Test-PortListening $Port) { throw "Port $Port is already listening. Choose another -Port, or use -Force." }
    if ($taken -contains $Port) { throw "Port $Port is already assigned to another Rasel instance. Run -List." }
}

if ($DryRun) { Write-Host "[dry-run] would install task $taskName on port $Port with SessionDir $sessionDir"; return }

$node = (Get-Command node.exe).Source
New-Item -ItemType Directory -Force $sessionDir | Out-Null
$cmd = "`$env:PORT='$Port'; `$env:HOST='127.0.0.1'; `$env:SESSION_DIR='$sessionDir'; `$env:SHARED_TOKEN='$SharedToken'; Set-Location '$SidecarDir'; & '$node' src\server.js"
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -Command `"$cmd`""
$settings = New-ScheduledTaskSettingsSet -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable
Register-ScheduledTask -TaskName $taskName -Description "Rasel sidecar; Port=$Port; SessionDir=$sessionDir" -Action $action -Trigger (New-ScheduledTaskTrigger -AtStartup) -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Host "Instance '$InstanceName' started on port $Port. Scan the QR at http://127.0.0.1:$Port/Qr (header X-Bridge-Token). Point Rasel (WhatsApp__Bridge__BaseUrl=http://127.0.0.1:$Port, WhatsApp__Bridge__SharedToken) at it."
