<#
.SYNOPSIS
    Builds CASR and creates a Desktop shortcut.
.DESCRIPTION
    Compiles Casr.App in Release mode via the shared scripts\Build-Release.ps1 (the same
    build run.ps1 uses — one build path, not two that drift apart), then creates CASR.lnk
    on the current user's Desktop pointing to the built executable with the lightning
    bolt icon. The shortcut's WorkingDirectory matches run.ps1's Start-Process working
    directory (the exe's folder).
    NOTE: the shortcut points at the exe on disk — it does NOT rebuild. After pulling or
    changing source, relaunch via run.cmd/run.ps1 (which rebuild first) or re-run this
    script; a stale shortcut opens stale code and the ReleaseBuildGuard test is the only
    thing that will tell you.
.EXAMPLE
    .\Create-Shortcut.ps1
    .\Create-Shortcut.ps1 -SkipBuild    # skip the dotnet build step (WARNING: bypasses the ReleaseBuildGuard staleness test)
#>
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$RepoRoot     = Split-Path $PSScriptRoot -Parent
$ProjectDir   = Join-Path $RepoRoot "src\Casr.App"
$ExePath      = Join-Path $ProjectDir "bin\Release\net8.0-windows\Casr.App.exe"
$IcoPath      = Join-Path $ProjectDir "casr.ico"
$Desktop      = [Environment]::GetFolderPath("Desktop")
$ShortcutPath = Join-Path $Desktop "CASR.lnk"

# Build via the shared script (same build run.ps1 performs before launching).
if (-not $SkipBuild) {
    & (Join-Path $RepoRoot "scripts\Build-Release.ps1")
}

if (-not (Test-Path $ExePath)) {
    throw "Executable not found: $ExePath`nRun without -SkipBuild to compile first."
}

# Create shortcut
Write-Host "Creating shortcut: $ShortcutPath" -ForegroundColor Cyan
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath       = $ExePath
# Agreement with run.ps1: both launch with the exe's folder as working directory.
$Shortcut.WorkingDirectory = Split-Path $ExePath
$Shortcut.IconLocation     = if (Test-Path $IcoPath) { "$IcoPath,0" } else { "$ExePath,0" }
$Shortcut.Description      = "SeshMesh - Cross-Agent Session Resumer"
$Shortcut.WindowStyle      = 1
$Shortcut.Save()

Write-Host "  Done! Shortcut created on Desktop." -ForegroundColor Green
Write-Host "  Target : $ExePath"
Write-Host "  Icon   : $IcoPath"
