[CmdletBinding()]
param([switch]$DefaultSize)
$ErrorActionPreference = 'Stop'
$hostPackage = Get-AppxPackage Microsoft.CommandPalette
$extension = Get-AppxPackage CodeUsageMonitor
if (!$hostPackage -or !$extension) { throw 'Install Command Palette and Code Cooldown before pinning the HUD.' }
$settingsPath = Join-Path "$env:LOCALAPPDATA\Packages" ($hostPackage.PackageFamilyName + '\LocalState\settings.json')
if (!(Test-Path $settingsPath)) { throw 'Command Palette settings were not found.' }
$provider = $extension.PackageFamilyName + '!App!CodeUsageMonitor'
# Close only Command Palette so its in-memory settings cannot overwrite this targeted pin update.
# Subtitles stay hidden in the band; the host still shows them in each item's tooltip.
Get-Process Microsoft.CmdPal.UI -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 300
$raw = Get-Content $settingsPath -Raw
$settings = $raw | ConvertFrom-Json
if (!$settings.DockSettings) { throw 'Command Palette dock settings are unavailable.' }
$backup = $settingsPath + '.code-usage-before-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak'
[System.IO.File]::WriteAllText($backup, $raw)
$found = $false
foreach ($location in @('StartBands','CenterBands','EndBands')) {
    foreach ($band in $settings.DockSettings.$location) {
        if ($band.ProviderId -eq $provider -and $band.CommandId -in @('code-usage','code-usage-hud')) {
            $band.CommandId = 'code-usage-hud'; $band.ShowTitles = $true; $band.ShowSubtitles = $false; $found = $true
        }
    }
}
foreach ($monitor in $settings.DockSettings.MonitorConfigs) {
    foreach ($location in @('StartBands','CenterBands','EndBands')) {
        foreach ($band in $monitor.$location) {
            if ($band.ProviderId -eq $provider -and $band.CommandId -in @('code-usage','code-usage-hud')) {
                $band.CommandId = 'code-usage-hud'; $band.ShowTitles = $true; $band.ShowSubtitles = $false; $found = $true
            }
        }
    }
}
if (!$found) {
    $settings.DockSettings.CenterBands = @($settings.DockSettings.CenterBands) + @([pscustomobject]@{
        ProviderId = $provider; CommandId = 'code-usage-hud'; ShowTitles = $true; ShowSubtitles = $false
    })
}
$settings.EnableDock = $true
if ($DefaultSize) { $settings.DockSettings.DockSize = 'Default' }
$temp = $settingsPath + '.code-usage.tmp'
[System.IO.File]::WriteAllText($temp, ($settings | ConvertTo-Json -Depth 64), (New-Object System.Text.UTF8Encoding($false)))
Move-Item $temp $settingsPath -Force
Start-Process 'x-cmdpal://'
Write-Host 'Code Cooldown HUD pinned. Other dock items were preserved.'
if ($DefaultSize) { Write-Host 'Dock size set to Default for taller meters.' }
Write-Host "Previous settings: $backup"
