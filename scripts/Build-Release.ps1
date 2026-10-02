<#
.SYNOPSIS
    Builds CASR in Release. Single shared build path for run.ps1 and Create-Shortcut.ps1.
.DESCRIPTION
    The taskbar icon, run.cmd/run.ps1 and the desktop CASR.lnk all launch the Release
    exe, while `dotnet build`/`dotnet test` default to Debug — a binary the user never
    opens. Every launcher delegates here so there is exactly one build-then-launch path
    and they cannot drift apart. The build runs quiet; only on failure it re-runs with
    --verbosity minimal so the error is visible instead of swallowed.
    Exits non-zero (throws with -ErrorActionPreference Stop callers) on build failure:
    a failed build must never look like a successful launch of stale code.
#>
param()

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $RepoRoot "Casr.sln"

Write-Host "Building CASR (Release)..." -ForegroundColor Cyan
dotnet build $solution --configuration Release --verbosity quiet --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Quiet build failed (exit code $LASTEXITCODE) - re-running with --verbosity minimal to show errors:" -ForegroundColor Yellow
    dotnet build $solution --configuration Release --verbosity minimal --nologo
    throw "Release build failed (exit code $LASTEXITCODE) - not launching a stale exe."
}
Write-Host "  Build succeeded." -ForegroundColor Green
