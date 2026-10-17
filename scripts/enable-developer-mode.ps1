#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
$before = Get-ItemProperty $path -ErrorAction SilentlyContinue
$data = Join-Path $env:LOCALAPPDATA 'CodeUsageMonitor'
New-Item -ItemType Directory -Force $data | Out-Null
@{ existed = ($null -ne $before.AllowDevelopmentWithoutDevLicense); value = $before.AllowDevelopmentWithoutDevLicense; timestamp = (Get-Date).ToUniversalTime().ToString('o') } |
    ConvertTo-Json | Set-Content (Join-Path $data ('developer-mode-before-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')) -Encoding UTF8
New-Item $path -Force | Out-Null
New-ItemProperty $path -Name AllowDevelopmentWithoutDevLicense -PropertyType DWord -Value 1 -Force | Out-Null
Write-Host 'Developer Mode enabled for local extension registration.'
