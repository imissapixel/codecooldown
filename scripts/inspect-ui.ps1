[CmdletBinding()]
param(
    [ValidateSet('Inspect','Open','Search','Invoke','Screenshot')][string]$Action = 'Inspect',
    [ValidateSet('Palette','Dock')][string]$Surface = 'Palette',
    [string]$Text = '',
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if ($Action -eq 'Open') {
    Start-Process 'x-cmdpal://commands/CodeUsageMonitor/code-usage'
    Start-Sleep -Milliseconds 800
}
$process = Get-Process 'Microsoft.CmdPal.UI' -ErrorAction Stop | Select-Object -First 1
$condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
$windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
$window = $windows | Where-Object { if ($Surface -eq 'Dock') { $_.Current.Name -eq 'PowerDock' } else { $_.Current.Name -ne 'PowerDock' } } | Select-Object -First 1
if (!$window) { throw 'No visible Command Palette window. Open Command Palette first.' }
if ($Action -eq 'Search') {
    $editCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $edit = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
    if (!$edit) { throw 'No search field on this Command Palette page.' }
    $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
    Start-Sleep -Milliseconds 500
}
if ($Action -eq 'Invoke') {
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Text)
    $elements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nameCondition)
    $invoked = $false
    foreach ($element in $elements) {
        $pattern = $null
        if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); $invoked = $true; break }
        if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            $pattern.Select(); $element.SetFocus()
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); $invoked = $true; break
        }
        if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $element.Current.IsEnabled) {
            if ($element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pattern)) { $pattern.ScrollIntoView() }
            $element.SetFocus()
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.SendKeys]::SendWait(' '); $invoked = $true; break
        }
    }
    if (!$invoked) { throw "No invokable control named $Text." }
    Start-Sleep -Milliseconds 500
}
if ($Action -eq 'Screenshot') {
    if (!$OutputPath) { throw 'Pass -OutputPath for the cropped Command Palette screenshot.' }
    Add-Type -AssemblyName System.Drawing
    $rect = $window.Current.BoundingRectangle
    if ($rect.Width -le 0 -or $rect.Height -le 0) { throw 'Command Palette is not visible.' }
    $bitmap = New-Object System.Drawing.Bitmap([int]$rect.Width, [int]$rect.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bitmap.Size)
    $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    Write-Host "Saved $OutputPath"
}
$all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
@($all | ForEach-Object { @{ name = $_.Current.Name; type = $_.Current.ControlType.ProgrammaticName; id = $_.Current.AutomationId; enabled = $_.Current.IsEnabled } }) |
    ConvertTo-Json -Depth 4 -Compress
