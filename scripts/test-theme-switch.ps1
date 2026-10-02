param(
    [string]$TargetTheme = "Nord"
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes | Out-Null

$exe = Join-Path $PSScriptRoot "..\src\Casr.App\bin\Release\net8.0-windows\Casr.App.exe"
$logPath = Join-Path $env:LOCALAPPDATA "Casr\logs\casr_debug.log"
$settingsPath = Join-Path $env:LOCALAPPDATA "Casr\settings.json"

Write-Host "Launching Casr.App.exe..."
$proc = Start-Process -FilePath $exe -PassThru

try {
    # Wait for window
    $timeout = [System.Diagnostics.Stopwatch]::StartNew()
    $winElement = $null
    while ($timeout.ElapsedMilliseconds -lt 5000) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
            $procCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
            $winElement = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $procCondition)
            if ($winElement -ne $null) { break }
        }
        Start-Sleep -Milliseconds 200
    }

    if ($winElement -eq $null) {
        throw "Failed to locate MainWindow via UI Automation"
    }

    Write-Host "MainWindow located: $($winElement.Current.Name)"
    Start-Sleep -Seconds 1

    # Find ComboBoxes
    $comboCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
    $combos = $winElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $comboCondition)
    Write-Host "Found $($combos.Count) ComboBoxes"

    $themeCombo = $null
    foreach ($c in $combos) {
        # Check tooltip or items
        $helpText = $c.Current.HelpText
        if ($helpText -like "*color scheme*" -or $helpText -like "*theme*") {
            $themeCombo = $c
            break
        }
    }

    if ($themeCombo -eq $null -and $combos.Count -gt 0) {
        # The first ComboBox in header row is the Theme picker
        $themeCombo = $combos[0]
    }

    if ($themeCombo -ne $null) {
        Write-Host "Found Theme ComboBox! Current selection: $($themeCombo.Current.Name)"
        
        # Expand combo box
        $expandPattern = $themeCombo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) -as [System.Windows.Automation.ExpandCollapsePattern]
        if ($expandPattern -ne $null) {
            $expandPattern.Expand()
            Start-Sleep -Milliseconds 300

            # Find list item matching TargetTheme
            $itemCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $TargetTheme)
            $item = $themeCombo.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $itemCondition)
            if ($item -ne $null) {
                $selectPattern = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern) -as [System.Windows.Automation.SelectionItemPattern]
                $selectPattern.Select()
                Write-Host "Successfully selected '$TargetTheme' via UI Automation!" -ForegroundColor Green
            } else {
                Write-Host "Could not find item '$TargetTheme' in dropdown" -ForegroundColor Yellow
            }

            $expandPattern.Collapse()
            Start-Sleep -Milliseconds 500
        }
    }

    # Verify settings.json
    $json = Get-Content $settingsPath -Raw
    Write-Host "Current settings.json content:"
    Write-Host $json

    # Close window cleanly
    $windowPattern = $winElement.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern) -as [System.Windows.Automation.WindowPattern]
    if ($windowPattern -ne $null) {
        $windowPattern.Close()
    } else {
        $proc.CloseMainWindow()
    }
    $proc.WaitForExit(3000)
    if (-not $proc.HasExited) { $proc.Kill() }

    Write-Host "Test completed cleanly." -ForegroundColor Green
}
catch {
    Write-Error "Test failed: $_"
    if ($proc -and -not $proc.HasExited) { $proc.Kill() }
    exit 1
}
