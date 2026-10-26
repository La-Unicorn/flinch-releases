Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
Write-Host "Root: $($root.Current.Name)"

$cond = [System.Windows.Automation.Condition]::TrueCondition
$children = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
Write-Host "Children count: $($children.Count)"
foreach ($child in $children | Select-Object -First 5) {
    Write-Host " - $($child.Current.Name) ($($child.Current.ControlType.ProgrammaticName))"
}
