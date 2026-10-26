param(
    [string] $Package = 'D:\AAAChatGPT\EndfieldCharacterTool-Acceptance',
    [Parameter(Mandatory)] [string] $Reports,
    [Parameter(Mandatory)] [string] $Runner
)
$ErrorActionPreference = 'Stop'
Add-Type -Path 'C:/Program Files/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12/UIAutomationClient.dll'
Add-Type -Path 'C:/Program Files/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12/UIAutomationTypes.dll'
function SettingsWindow {
    [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, '设置'))
}
function ScaleCombo {
    (SettingsWindow).FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SystemSettings_Display_Scaling_ItemSizeOverride_ComboBox'))
}
function CurrentScale {
    $combo = ScaleCombo
    ([System.Windows.Automation.SelectionPattern]$combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection()[0].Current.Name
}
function SetScale([string] $Name) {
    $combo = ScaleCombo
    ([System.Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
    $item = (SettingsWindow).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $item) { throw "系统没有提供缩放选项 $Name" }
    ([System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Seconds 2
}
$original = CurrentScale
if ($original -ne '100% (推荐)') { throw "当前不是预期100%，停止以免改变其他设置：$original" }
$record = @{ original = $original; tested = '150%'; restored = $false; actual200Available = $false; exitCode = -1 }
try {
    SetScale '150%'
    $record.selectionDuringTest = CurrentScale
    ([System.Windows.Automation.WindowPattern](SettingsWindow).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Minimized)
    & $Runner $Package $Reports real150
    $record.exitCode = $LASTEXITCODE
}
finally {
    # Minimized UWP windows can disappear from the automation tree. Reactivate
    # through the same Settings URI before locating fresh controls for restore.
    Start-Process explorer.exe -ArgumentList 'ms-settings:display' -WindowStyle Hidden
    Start-Sleep -Seconds 2
    SetScale $original
    $record.selectionAfterTest = CurrentScale
    $record.restored = $record.selectionAfterTest -eq $original
    $record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Reports 'display-scale-restoration.json') -Encoding utf8
}
if ($record.exitCode -ne 0 -or -not $record.restored) { throw '真实缩放验收未通过或缩放恢复失败。' }
