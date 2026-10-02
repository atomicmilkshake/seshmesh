<#
.SYNOPSIS
    Rebuilds CASR in Release and launches it.
.DESCRIPTION
    The taskbar icon, run.cmd, the desktop shortcut and the README all point at the
    Release exe, while `dotnet build` and `dotnet test` default to Debug — a binary the
    user never opens. Rebuilding here on every launch is what guarantees the program you
    see matches the source that was just changed; skipping it is how you end up running
    two-day-old code and concluding "the fix didn't work".
    The actual build lives in scripts\Build-Release.ps1, shared with
    scripts\Create-Shortcut.ps1: one build path, not two that drift apart.
.PARAMETER SkipBuild
    Launch whatever is already on disk. Fast relaunch only — never use this to verify
    a code change. WARNING: -SkipBuild bypasses the ReleaseBuildGuard staleness test,
    so the exe you open may predate the current source.
.EXAMPLE
    .\run.ps1
.EXAMPLE
    .\run.ps1 -SkipBuild
#>
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$solution = Join-Path $PSScriptRoot "Casr.sln"
$exePath  = Join-Path $PSScriptRoot "src\Casr.App\bin\Release\net8.0-windows\Casr.App.exe"

if (!$SkipBuild) {
    & (Join-Path $PSScriptRoot "scripts\Build-Release.ps1")
}

if (!(Test-Path $exePath)) {
    Write-Host "Executable not found: $exePath`nRun without -SkipBuild to compile first." -ForegroundColor Red
    exit 1
}

# WorkingDirectory agreement: the desktop shortcut (scripts\Create-Shortcut.ps1) uses
# this same directory as its WorkingDirectory so launches behave identically.
$workDir = Split-Path $exePath
Start-Process $exePath -WorkingDirectory $workDir
