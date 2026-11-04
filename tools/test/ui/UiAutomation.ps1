function Find-ProcessUiElement {
    param([int]$OwnerProcessId, [Windows.Automation.Condition]$Condition)
    # Enumerating the entire desktop's descendants can traverse unrelated large
    # document/browser trees until a timed QA window has already exited.
    $owner = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $OwnerProcessId)
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $owner)
    foreach ($window in $windows) {
        $element = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $Condition)
        if ($element) { return $element }
    }
    return $null
}
