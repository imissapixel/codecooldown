[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')][string]$Architecture = 'x64',
    [switch]$Test,
    [switch]$TestWsl,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\package' }
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$metadata = Get-ChildItem (Join-Path $sdkRoot 'Platforms\UAP') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$metadata) { throw 'Install a Windows SDK (19041 or newer) with UAP metadata before building.' }
$runtime = if ($Architecture -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$properties = @("-p:Platform=$Architecture", "-p:CsWinRTWindowsMetadata=$($metadata.Name)", '-p:WindowsPackageType=None', '-p:AppxGeneratePriEnabled=false', '-p:IncludeProjectPriFile=false')
if ($Test -or $TestWsl) {
    if ($Architecture -ne 'x64') { throw 'The fixture runner currently targets x64.' }
    $testArgs = @()
    if ($TestWsl) { $testArgs += @('--', '--wsl') }
    & $dotnet run --project (Join-Path $root 'extension\CodeUsageMonitor.Tests\CodeUsageMonitor.Tests.csproj') @properties -r $runtime -c Release @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Windows fixture tests failed.' }
}
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
& $dotnet publish (Join-Path $root 'extension\CodeUsageMonitor\CodeUsageMonitor.csproj') @properties -r $runtime -c Release -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Extension publish failed.' }
$manifest = Get-Content (Join-Path $root 'extension\CodeUsageMonitor\Package.appxmanifest') -Raw
$manifest = $manifest.Replace('ProcessorArchitecture="x64"', "ProcessorArchitecture=`"$($Architecture.ToLowerInvariant())`"")
Set-Content (Join-Path $OutputDirectory 'AppxManifest.xml') $manifest -Encoding UTF8
New-Item -ItemType Directory -Force (Join-Path $OutputDirectory 'Public') | Out-Null
Copy-Item (Join-Path $root 'LICENSE') $OutputDirectory
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $OutputDirectory
Write-Host "Package layout ready: $OutputDirectory"
Write-Host 'Deploy with scripts\install.ps1. Building alone does not register the extension.'
