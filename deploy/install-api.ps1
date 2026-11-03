<#
.SYNOPSIS  Publishes Rasel.Api and installs it as a Windows service (needs an elevated PowerShell and the .NET 10 SDK).
.EXAMPLE   .\install-api.ps1 -EnvFile .\api.env -InstallDir C:\Rasel\api -DataDir C:\Rasel\data
.NOTES     EnvFile lines are KEY=VALUE using .NET configuration keys with "__" (e.g. Security__OtpPepper=...).
           The service runs as LocalService; the data directory is granted to it. Values are stored in the service's
           Environment registry value (readable by administrators only).
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'RaselApi',
    [string]$InstallDir  = 'C:\Rasel\api',
    [string]$DataDir     = 'C:\Rasel\data',
    [int]$Port           = 5114,
    [Parameter(Mandatory)][string]$EnvFile
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')) { throw 'Run as Administrator.' }
$repo = Split-Path -Parent $PSScriptRoot
$vars = @{}
Get-Content $EnvFile | Where-Object { $_ -match '^\s*[^#\s][^=]*=' } | ForEach-Object { $k, $v = $_ -split '=', 2; $vars[$k.Trim()] = $v.Trim() }
foreach ($required in 'Security__OtpPepper', 'Security__TokenSecret', 'Seed__DevApiKey') { if (-not $vars[$required]) { throw "$required missing in $EnvFile" } }
$vars['ASPNETCORE_ENVIRONMENT'] = 'Production'
$vars['ASPNETCORE_URLS'] = "http://127.0.0.1:$Port"
$vars['Database__Path'] = Join-Path $DataDir 'rasel.db'

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) { & sc.exe stop $ServiceName | Out-Null; Start-Sleep 3 }
dotnet publish "$repo\src\Rasel.Api\Rasel.Api.csproj" -c Release -o $InstallDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
New-Item -ItemType Directory -Force $DataDir | Out-Null
& icacls $DataDir /grant 'NT AUTHORITY\LocalService:(OI)(CI)M' | Out-Null

if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    & sc.exe create $ServiceName binPath= "`"$InstallDir\Rasel.Api.exe`"" start= delayed-auto obj= 'NT AUTHORITY\LocalService' DisplayName= 'Rasel WhatsApp gateway' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'sc create failed' }
}
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
$multi = [string[]]($vars.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Type MultiString -Value $multi
& sc.exe start $ServiceName | Out-Null
Write-Host "Installed. Check: curl http://127.0.0.1:$Port/health/ready"
