[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
$scope = [System.Windows.Automation.TreeScope]::Descendants
function Window {
    $process = Get-Process 'Microsoft.CmdPal.UI' | Select-Object -First 1
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
    $script:window = $windows | Where-Object { $_.Current.Name -ne 'PowerDock' } | Select-Object -First 1
    if (!$script:window) { throw 'Command Palette window is unavailable.' }
}
function Find([string]$name = '', [string]$id = '', [string]$type = '') {
    $all = $script:window.FindAll($scope, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $all) {
        if (($name -eq '' -or $element.Current.Name -eq $name) -and ($id -eq '' -or $element.Current.AutomationId -eq $id) -and
            ($type -eq '' -or $element.Current.ControlType.ProgrammaticName -eq $type)) { return $element }
    }
    return $null
}
function Click($element) {
    if (!$element) { throw 'Required UI element was not found.' }
    $pattern = $null
    if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and
        $element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.Select()
        Start-Sleep -Milliseconds 150
        $primary = Find -id 'PrimaryCommandButton'
        if ($primary) {
            $primary.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            return
        }
    }
    if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
    if ($element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pattern)) { $pattern.ScrollIntoView() }
    if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select() }
    $element.SetFocus()
    if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) { [System.Windows.Forms.SendKeys]::SendWait(' ') }
    else { [System.Windows.Forms.SendKeys]::SendWait('{ENTER}') }
}
function Search([string]$text) {
    $edit = Find -id 'MainSearchBox'
    if (!$edit) { throw 'Search field missing.' }
    $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
    Start-Sleep -Milliseconds 600
}
function Back { Click (Find -id 'BackButton'); Start-Sleep -Milliseconds 250 }
function Rows {
    @($script:window.FindAll($scope, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and $_.Current.IsEnabled } |
        ForEach-Object { $_.Current.Name })
}
function Require([bool]$test, [string]$description) { if (!$test) { throw $description }; Write-Host "PASS $description" }

try { Window }
catch {
    Start-Process 'x-cmdpal://'
    for ($i = 0; $i -lt 50; $i++) {
        Start-Sleep -Milliseconds 100
        try { Window; break } catch { if ($i -eq 49) { throw } }
    }
}
# Return to the top-level list if the host restored a previous page.
for ($i = 0; $i -lt 5 -and (Find -id 'BackButton'); $i++) { Back }
# Expectations come from this machine's own profiles; nothing user-specific is hard-coded.
$profiles = @((Get-Content (Join-Path $env:LOCALAPPDATA 'CodeUsageMonitor\profiles.json') -Raw | ConvertFrom-Json).profiles | Where-Object { $_.enabled })
Require ($profiles.Count -gt 0) 'At least one enabled profile is configured'
$first = $profiles[0].display_name
$codex = @($profiles | Where-Object { $_.provider -eq 'codex' }) | Select-Object -First 1
Search 'Code Cooldown'
$matches = @(Rows | Where-Object { $_ -eq 'Code Cooldown' })
Require ($matches.Count -eq 1) 'One Code Cooldown command is searchable'
$watch = [System.Diagnostics.Stopwatch]::StartNew()
Click (Find -name 'Code Cooldown' -type 'ControlType.ListItem')
for ($i = 0; $i -lt 100 -and !(Find -name $first -type 'ControlType.ListItem'); $i++) { Start-Sleep -Milliseconds 10 }
$watch.Stop()
$rows = Rows
foreach ($name in $profiles.display_name) { Require ($rows -contains $name) "Profile $name appears" }
Write-Host ('MEASURE host_cached_open_ms=' + $watch.Elapsed.TotalMilliseconds.ToString('F2', [Globalization.CultureInfo]::InvariantCulture))
Require ($watch.ElapsedMilliseconds -lt 500) 'Cached host opening is under 500 ms'
if ($codex) {
    Search 'Codex subscription'
    $codexRows = @(Rows)
    Require ($codexRows -contains $codex.display_name -and !($codexRows -contains $first -and $profiles[0].provider -ne 'codex')) 'Provider search isolates Codex profiles'
    Search $codex.display_name
    Click (Find -name $codex.display_name -type 'ControlType.ListItem'); Start-Sleep -Milliseconds 300
    $texts = @($script:window.FindAll($scope, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join ' '
    Require ($texts.Contains('remaining') -and $texts.Contains('Scope:') -and $texts.Contains('Source:')) 'Codex detail shows allowance and provenance'
    $cachePath = Join-Path $env:LOCALAPPDATA 'CodeUsageMonitor\usage-cache.json'
    $before = (Get-Content $cachePath -Raw | ConvertFrom-Json).($codex.id).last_attempt.fetched_at
    Click (Find -name 'Refresh profile' -type 'ControlType.Button')
    for ($i = 0; $i -lt 100; $i++) {
        Start-Sleep -Milliseconds 100
        $after = (Get-Content $cachePath -Raw | ConvertFrom-Json).($codex.id).last_attempt.fetched_at
        if ($after -ne $before) { break }
    }
    Require ($after -ne $before) 'Refresh profile updates the persisted attempt'
    Back
}
Search 'Configure profiles'; Click (Find -name 'Configure profiles' -type 'ControlType.ListItem'); Start-Sleep -Milliseconds 300
Click (Find -name $first -type 'ControlType.ListItem'); Start-Sleep -Milliseconds 300
Require ($null -ne (Find -name 'Credential file path (read only)' -type 'ControlType.Edit')) 'Profile source is editable as a path reference'
Require ($null -ne (Find -name 'Profile availability' -type 'ControlType.CheckBox')) 'Profile toggle has a specific accessible label'
Click (Find -name 'Save' -type 'ControlType.Button'); Start-Sleep -Milliseconds 500
Require ($null -ne (Find -name 'Add profile' -type 'ControlType.ListItem')) 'Profile form saves and returns to configuration'
Click (Find -name 'Display and refresh preferences' -type 'ControlType.ListItem'); Start-Sleep -Milliseconds 300
Require ($null -ne (Find -name 'Allowance display' -type 'ControlType.CheckBox')) 'Display preferences render native toggles'
Click (Find -name 'Save' -type 'ControlType.Button'); Start-Sleep -Milliseconds 300
Click (Find -name 'Add profile' -type 'ControlType.ListItem'); Start-Sleep -Milliseconds 300
Require ((Rows).Count -eq 2) 'Add profile offers Claude and Codex'
Back; Back
Search ''
Write-Host 'RESULT native PowerToys smoke passed'
