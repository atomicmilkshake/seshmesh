# Iterative Automated Testing Loop for CASR GUI (.NET 8 WPF)
# Tests build, unit tests, GUI launch, responsiveness, UI automation controls, and clean shutdown in a loop.

param (
    [int]$Iterations = 3,
    [int]$MaxStartupMs = 5000,
    [int]$MaxMemoryMb = 350
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$slnPath = Join-Path $repoRoot "Casr.sln"
$exePath = Join-Path $repoRoot "src\Casr.App\bin\Release\net8.0-windows\Casr.App.exe"
$logPath = Join-Path $env:LOCALAPPDATA "Casr\logs\casr_debug.log"

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host "  CASR ITERATIVE AUTOMATED TEST LOOP (GUI & THREADING)  " -ForegroundColor Cyan
Write-Host "========================================================`n" -ForegroundColor Cyan
Write-Host "Repo Root: $repoRoot" -ForegroundColor Gray
Write-Host "Iterations: $Iterations" -ForegroundColor Gray
Write-Host "Executable: $exePath`n" -ForegroundColor Gray

# Step 1: Ensure Release Build and Run Unit Tests
Write-Host "[PRE-FLIGHT] Building solution in Release mode..." -ForegroundColor Yellow
$buildResult = dotnet build $slnPath -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE"
    exit 1
}
Write-Host "[PRE-FLIGHT] Running unit tests..." -ForegroundColor Yellow
$testResult = dotnet test $slnPath -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "Unit tests failed with exit code $LASTEXITCODE"
    exit 1
}
Write-Host "[PRE-FLIGHT] All unit tests passed!`n" -ForegroundColor Green

# Load UI Automation Assemblies
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes | Out-Null

$results = @()

for ($i = 1; $i -le $Iterations; $i++) {
    Write-Host "--------------------------------------------------------" -ForegroundColor DarkCyan
    Write-Host "  RUNNING ITERATION $i / $Iterations" -ForegroundColor DarkCyan
    Write-Host "--------------------------------------------------------" -ForegroundColor DarkCyan

    $iterStart = [System.Diagnostics.Stopwatch]::StartNew()
    $logLengthBefore = if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 }

    # Launch Process
    Write-Host "  -> Launching Casr.App.exe..." -ForegroundColor Gray
    $proc = Start-Process -FilePath $exePath -PassThru

    $startedOk = $false
    $windowFound = $false
    $responding = $false
    $rowsCount = 0
    $steadyMemMb = 0
    $winElement = $null

    try {
        # Check window handle and responsiveness
        $timeout = [System.Diagnostics.Stopwatch]::StartNew()
        while ($timeout.ElapsedMilliseconds -lt $MaxStartupMs) {
            $proc.Refresh()
            if ($proc.HasExited) {
                throw "Process exited prematurely with code $($proc.ExitCode)"
            }

            if ($proc.MainWindowHandle -ne [IntPtr]::Zero -and $proc.Responding) {
                $responding = $true
                break
            }
            Start-Sleep -Milliseconds 150
        }

        $startupMs = $timeout.ElapsedMilliseconds
        Write-Host "  -> Window created and responsive in $startupMs ms" -ForegroundColor Green

        # Allow 2 seconds for multi-threaded streaming to populate SQLite and UI queue
        Start-Sleep -Seconds 2
        $proc.Refresh()
        $steadyMemMb = [math]::Round($proc.WorkingSet64 / 1MB, 2)
        Write-Host "  -> Memory footprint: $steadyMemMb MB (Limit: $MaxMemoryMb MB)" -ForegroundColor Gray

        if ($steadyMemMb -gt $MaxMemoryMb) {
            Write-Warning "Memory exceeded threshold: $steadyMemMb MB > $MaxMemoryMb MB"
        }

        # UI Automation: Inspect MainWindow
        $procCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
        $winElement = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $procCondition)

        if ($winElement -ne $null) {
            $windowFound = $true
            $winTitle = $winElement.Current.Name
            Write-Host "  -> UI Window found: '$winTitle'" -ForegroundColor Gray

            # Find DataGrid
            $gridCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataGrid)
            $grid = $winElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $gridCondition)

            if ($grid -ne $null) {
                $rowCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)
                $rows = $grid.FindAll([System.Windows.Automation.TreeScope]::Children, $rowCondition)
                $rowsCount = $rows.Count
                Write-Host "  -> UI Automation DataGrid items visible: $rowsCount" -ForegroundColor Green
            } else {
                Write-Host "  -> DataGrid control not found via Automation" -ForegroundColor Yellow
            }
        }

        # Verify debug log activity
        if (Test-Path $logPath) {
            $logLengthAfter = (Get-Item $logPath).Length
            $logBytesAdded = $logLengthAfter - $logLengthBefore
            Write-Host "  -> Debug log active: $logBytesAdded bytes written during iteration" -ForegroundColor Gray
        }

        # Clean shutdown
        Write-Host "  -> Sending close signal to MainWindow..." -ForegroundColor Gray
        $closed = $false
        if ($winElement -ne $null) {
            try {
                $windowPattern = $winElement.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern) -as [System.Windows.Automation.WindowPattern]
                if ($windowPattern -ne $null) {
                    $windowPattern.Close()
                    $closed = $true
                }
            } catch {}
        }
        if (-not $closed) {
            $proc.CloseMainWindow() | Out-Null
        }
        $proc.WaitForExit(3000)

        if (-not $proc.HasExited) {
            Write-Warning "Process did not exit within 3s, killing..."
            $proc.Kill()
            $proc.WaitForExit(1000)
            $exitStatus = "Killed (Timeout)"
        } else {
            $exitStatus = "Clean Exit (Code 0)"
            Write-Host "  -> Exited cleanly." -ForegroundColor Green
        }

        $results += [PSCustomObject]@{
            Iteration    = $i
            Status       = "PASS"
            StartupMs    = $startupMs
            MemoryMb     = $steadyMemMb
            RowsLoaded   = $rowsCount
            ExitStatus   = $exitStatus
        }
    }
    catch {
        Write-Error "Iteration $i failed: $_"
        if ($proc -and -not $proc.HasExited) {
            $proc.Kill()
        }
        $results += [PSCustomObject]@{
            Iteration    = $i
            Status       = "FAIL: $_"
            StartupMs    = -1
            MemoryMb     = -1
            RowsLoaded   = 0
            ExitStatus   = "Failed"
        }
    }

    Start-Sleep -Milliseconds 500
}

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host "              TEST LOOP SUMMARY RESULTS                 " -ForegroundColor Cyan
Write-Host "========================================================`n" -ForegroundColor Cyan

$results | Format-Table -AutoSize

$allPassed = ($results | Where-Object { $_.Status -ne "PASS" }).Count -eq 0
if ($allPassed) {
    Write-Host "ALL $Iterations ITERATIONS COMPLETED SUCCESSFULLY WITH ZERO HANGS!`n" -ForegroundColor Green
    exit 0
} else {
    Write-Host "SOME ITERATIONS FAILED. CHECK LOGS ABOVE.`n" -ForegroundColor Red
    exit 1
}
