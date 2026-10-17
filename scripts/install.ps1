[CmdletBinding()]
param(
    [string]$Distro = 'Ubuntu',
    [string]$LinuxUser = '',
    [string]$PackageDirectory = '',
    [switch]$SkipHelper
)
$ErrorActionPreference = 'Stop'
if (!$PackageDirectory) { $PackageDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\package' }
$source = (Resolve-Path $PackageDirectory).Path
if (!(Test-Path (Join-Path $source 'AppxManifest.xml'))) { throw 'Build the package first with scripts\build.ps1.' }
$data = Join-Path $env:LOCALAPPDATA 'CodeUsageMonitor'
[xml]$manifest = Get-Content (Join-Path $source 'AppxManifest.xml') -Raw
$installed = Get-AppxPackage CodeUsageMonitor
$nextVersion = [version]$manifest.Package.Identity.Version
if ($installed -and [version]$installed.Version -ge $nextVersion) {
    $oldVersion = [version]$installed.Version
    if ($oldVersion.Revision -ge 65535) { throw 'Increment the source package version before installing another development build.' }
    $nextVersion = [version]::new($oldVersion.Major, $oldVersion.Minor, $oldVersion.Build, $oldVersion.Revision + 1)
}
# Loaded shared fonts remain mapped by Windows. Use an immutable package folder;
# retaining previous folders also provides rollback without replacing mapped files.
do {
    $package = Join-Path $env:LOCALAPPDATA ('CodeUsageMonitorPackage-' + $nextVersion)
    if (!(Test-Path $package)) { break }
    if ($nextVersion.Revision -ge 65535) { throw 'No unused development package revision remains.' }
    $nextVersion = [version]::new($nextVersion.Major, $nextVersion.Minor, $nextVersion.Build, $nextVersion.Revision + 1)
} while ($true)
$manifest.Package.Identity.Version = $nextVersion.ToString()
New-Item -ItemType Directory -Force $data | Out-Null
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
[System.IO.Directory]::SetAccessControl($data, $acl)
# Stop the host before updates so its catalog cannot retain two version entries.
$hostProcesses = @(Get-Process Microsoft.CmdPal.UI -ErrorAction SilentlyContinue)
if ($hostProcesses.Count) { $hostProcesses | Stop-Process }
Get-Process CodeUsageMonitor -ErrorAction SilentlyContinue | Stop-Process
try {
    New-Item -ItemType Directory -Force $package | Out-Null
    Copy-Item (Join-Path $source '*') $package -Recurse -Force
    $manifest.Save((Join-Path $package 'AppxManifest.xml'))
    if (!$SkipHelper) {
        $wslArgs = @('--distribution', $Distro)
        if ($LinuxUser) { $wslArgs += @('--user', $LinuxUser) }
        $installer = Join-Path $package 'Helper\install.py'
        $linuxInstaller = & wsl.exe @wslArgs --exec wslpath -u $installer
        if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the helper installer inside the selected WSL distribution.' }
        & wsl.exe @wslArgs --exec python3 $linuxInstaller.Trim()
        if ($LASTEXITCODE -ne 0) { throw 'WSL helper installation failed. Python 3.11+ is required.' }
        if (!(Test-Path (Join-Path $data 'profiles.json'))) {
            # Discovery returns metadata only. Credentials and tokens never cross this interface.
            $helperPath = $linuxInstaller.Trim()
            $helperSource = $helperPath.Substring(0, $helperPath.LastIndexOf('/')) + '/monitor.py'
            $discovery = '{"version":1,"operation":"discover"}' | & wsl.exe @wslArgs --exec python3 $helperSource
            if ($LASTEXITCODE -ne 0) { throw 'Profile discovery failed.' }
            $found = $discovery | ConvertFrom-Json
            if ($found.version -ne 1 -or $found.profiles.Count -lt 1) { throw 'Unsupported discovery response.' }
            foreach ($profile in $found.profiles) {
                $profile | Add-Member -NotePropertyName distro -NotePropertyValue $Distro -Force
                $profile | Add-Member -NotePropertyName user -NotePropertyValue $LinuxUser -Force
            }
            @{ version = 1; show_remaining = $true; start_wsl_on_open = $false; profiles = @($found.profiles) } |
                ConvertTo-Json -Depth 12 | Set-Content (Join-Path $data 'profiles.json') -Encoding UTF8
        }
    }
    # Register this user's package without changing developer-mode or trust policies.
    Add-AppxPackage -Register (Join-Path $package 'AppxManifest.xml') -ForceApplicationShutdown
}
finally {
    if ($hostProcesses.Count) { Start-Process 'x-cmdpal://' }
}
Get-AppxPackage CodeUsageMonitor | Select-Object Name, Version, InstallLocation
Write-Host 'Open Command Palette, run Reload, then open Code Cooldown.'
