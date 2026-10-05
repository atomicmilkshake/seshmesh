# SeshMesh conversion-dialog acceptance test (UIA, Windows-only).
#
# Drives the actual Release app the user launches:
#   1. selects the first conversation row
#   2. opens Resume With -> Grok Build
#   3. verifies the conversion preview dialog renders (stats, options, warnings, resume command, repo)
#   4. toggles Enrich and confirms the dry run re-runs (+2 synthetic context messages)
#   5. unchecks "Launch after converting" (write-only path)
#   6. clicks Convert and confirms the app log reports a verified write
#   7. confirms the resume command lands on the clipboard and the dialog closes
#
# GROK_HOME is redirected to a temp directory for the duration of the run, so the
# converted session is written there (never the user's live ~/.grok store) and the
# directory is deleted afterwards. The app log entry for the conversion includes the
# read-back verification result, which this script requires to be "passed".
#
# Usage:  pwsh -File scripts\test-conversion-ui.ps1   (exit 0 = pass)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase | Out-Null
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SeshMeshUiMouse {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, int e);
  public static void Click(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(150);
    mouse_event(0x0002, 0, 0, 0, 0); System.Threading.Thread.Sleep(80);
    mouse_event(0x0004, 0, 0, 0, 0);
  }
}
"@

$AE = [System.Windows.Automation.AutomationElement]
$CW = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$steps = @(); $ok = $false
function Step($msg) { $script:steps += $msg; Write-Host "STEP: $msg" }

$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot 'src\Casr.App\bin\Release\net8.0-windows\Casr.App.exe'
if (-not (Test-Path $exe)) { throw "Release exe not found: $exe — run scripts\Build-Release.ps1 first" }

$grokHome = Join-Path $env:TEMP ("seshmesh-e2e-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $grokHome -Force | Out-Null
$env:GROK_HOME = $grokHome

$log = Join-Path $env:LOCALAPPDATA 'Casr\logs\casr_debug.log'
if (Test-Path $log) { $logLen = (Get-Item $log).Length } else { $logLen = 0 }
$proc = Start-Process -FilePath $exe -PassThru
$pidCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)

function NewLogText {
    if (-not (Test-Path $script:log)) { return '' }
    $fs = [System.IO.File]::Open($script:log, 'Open', 'Read', 'ReadWrite'); $fs.Seek($script:logLen, 'Begin') | Out-Null
    $sr = New-Object System.IO.StreamReader($fs); $t = $sr.ReadToEnd(); $sr.Close(); $fs.Close(); return $t
}
function FindByType($root, $type) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function FindControlForText($root, $textLike, $controlType) {
    $textCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    foreach ($t in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
        if ($t.Current.Name -like $textLike) {
            $node = $t
            for ($i = 0; $i -lt 8 -and $node; $i++) {
                if ($node.Current.ControlType -eq $controlType) { return $node }
                $node = $CW.GetParent($node)
            }
        }
    }
    return $null
}
function FindPopupAgentButton($textLike) {
    foreach ($w in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $script:pidCond)) {
        $b = FindControlForText $w $textLike ([System.Windows.Automation.ControlType]::Button)
        if ($b) { return $b }
    }
    return $null
}
function FindDialogWindow {
    $winCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    foreach ($w in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $winCond)) {
        if ($w.Current.Name -like '*Convert Session*' -and $w.Current.ProcessId -eq $script:proc.Id) { return $w }
    }
    return $null
}
function DialogText($dialog) {
    $textCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $all = @(); foreach ($t in $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) { $all += $t.Current.Name }
    return ($all -join ' || ')
}

try {
    $win = $null
    for ($i = 0; ($i -lt 60) -and ($null -eq $win); $i++) { Start-Sleep -Milliseconds 500; if (-not $proc.HasExited) { $win = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond) } }
    if (-not $win) { throw 'main window not found' }

    $grid = $null
    for ($i = 0; ($i -lt 120) -and ($null -eq $grid); $i++) { Start-Sleep -Milliseconds 500; $grid = FindByType $win ([System.Windows.Automation.ControlType]::DataGrid) }
    if (-not $grid) { throw 'data grid not found' }
    $rowCondition = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)
    $rows = $grid.FindAll([System.Windows.Automation.TreeScope]::Children, $rowCondition)
    if ($rows.Count -eq 0) { throw 'no conversation rows found' }
    $pt = $rows.Item(0).GetClickablePoint()
    [SeshMeshUiMouse]::Click([int]$pt.X, [int]$pt.Y)
    Start-Sleep -Seconds 3
    Step "selected first row (rows=$($rows.Count))"

    $idCond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'BtnResumeWithToggle')
    $toggle = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
    if (-not $toggle) { throw 'Resume With toggle not found' }
    $toggle.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 800

    $btn = $null
    for ($i = 0; ($i -lt 12) -and ($null -eq $btn); $i++) { $btn = FindPopupAgentButton '*Grok Build*'; if (-not $btn) { Start-Sleep -Milliseconds 400 } }
    if (-not $btn) { throw 'Grok Build popup entry not found' }
    $bp = $btn.GetClickablePoint()
    [SeshMeshUiMouse]::Click([int]$bp.X, [int]$bp.Y)

    $dialog = $null
    for ($i = 0; ($i -lt 60) -and ($null -eq $dialog); $i++) { Start-Sleep -Milliseconds 500; $dialog = FindDialogWindow }
    if (-not $dialog) { throw 'conversion preview dialog not found' }
    Start-Sleep -Seconds 2
    $dialog = FindDialogWindow
    if (-not $dialog) { throw 'conversion dialog vanished after settle' }
    Step 'dialog opened'

    $text = DialogText $dialog
    $hasStats = $text -match 'Messages:'
    $hasOptions = $text -match 'Max context tokens'
    $hasVerify = $text -match 'Verify written session'
    $hasEnrich = $text -match 'Add conversion context'
    $hasResumeCmd = $text -match 'grok .*resume'
    $hasRepo = $text -match 'casr @|Repository'
    Step "content: stats=$hasStats options=$hasOptions verify=$hasVerify enrich=$hasEnrich resumeCmd=$hasResumeCmd repo=$hasRepo"

    $enrich = FindControlForText $dialog '*Add conversion context*' ([System.Windows.Automation.ControlType]::CheckBox)
    if (-not $enrich) { throw 'enrich checkbox not found' }
    $enrich.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Seconds 2
    $after = DialogText $dialog
    $plus2 = $after -match '\+2 synthetic context messages'
    Step "enrich re-preview: +2=$plus2"

    $launch = FindControlForText $dialog '*Launch the target agent*' ([System.Windows.Automation.ControlType]::CheckBox)
    if (-not $launch) { throw 'launch checkbox not found' }
    $launch.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 800
    $convertOnly = (DialogText $dialog) -match 'Convert only'
    Step "write-only label: $convertOnly"

    $convert = FindControlForText $dialog 'Convert*' ([System.Windows.Automation.ControlType]::Button)
    if (-not $convert) { throw 'convert button not found' }
    $convert.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $convertedLine = $null
    for ($i = 0; ($i -lt 90) -and ($null -eq $convertedLine); $i++) {
        Start-Sleep -Milliseconds 500
        foreach ($line in ((NewLogText) -split "`n")) { if ($line -match '\[CONVERSION\] Converted:') { $convertedLine = $line.Trim() } }
    }
    $verifyPassed = $null -ne $convertedLine -and $convertedLine -match 'verification=passed'
    Step "convert log: $convertedLine"

    $files = @(Get-ChildItem $grokHome -Recurse -File -ErrorAction SilentlyContinue)
    $clip = ''
    for ($i = 0; $i -lt 10; $i++) {
        try { $clip = (Get-Clipboard -Raw -ErrorAction Stop); if ($clip -match 'grok .*--resume') { break } } catch { }
        Start-Sleep -Milliseconds 500
    }
    $clipHasResume = $clip -match 'grok .*--resume'
    $dialogGone = $null -eq (FindDialogWindow)
    $appAlive = -not $proc.HasExited
    Step "files=$($files.Count) clipboard=$clipHasResume dialogClosed=$dialogGone appAlive=$appAlive verifyPassed=$verifyPassed"

    $script:ok = $hasStats -and $hasOptions -and $hasVerify -and $hasEnrich -and $hasResumeCmd -and $hasRepo -and
                 $plus2 -and $convertOnly -and $verifyPassed -and ($files.Count -gt 0) -and $clipHasResume -and $dialogGone -and $appAlive
}
catch { Step "EXCEPTION: $($_.Exception.Message)" }
finally {
    try {
        if (-not $proc.HasExited) {
            $mainWin = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
            if ($mainWin) { try { $mainWin.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { } }
            Start-Sleep -Seconds 2
        }
        if (-not $proc.HasExited) { $proc.Kill() }
    } catch { }
    try { if (Test-Path $grokHome) { Remove-Item $grokHome -Recurse -Force } } catch { }
}

Write-Host ''
Write-Host ($(if ($script:ok) { 'CONVERSION UI TEST: PASS' } else { 'CONVERSION UI TEST: FAIL' }))
$script:steps | ForEach-Object { Write-Host "  $_" }
exit $(if ($script:ok) { 0 } else { 1 })
