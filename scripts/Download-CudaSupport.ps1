#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Downloads the CUDA support DLLs (cuDNN 9) for SeshMesh's optional GPU
  (CUDA) ONNX build into %LOCALAPPDATA%\Casr\cuda.

.DESCRIPTION
  The CUDA-enabled SeshMesh build (release asset *-cuda.zip, built with
  -p:CasrGpu=true) loads onnxruntime's CUDA execution provider, which needs the
  NVIDIA CUDA 13 runtime and cuDNN 9 at load time. The CUDA 13 toolkit bin
  directory must already be on PATH (standard CUDA Toolkit install); this
  script fetches cuDNN 9 from NVIDIA's redistributable wheels (no account
  required) and extracts the DLLs into the app's support directory, which
  SeshMesh prepends to its DLL search path right before the execution provider
  is created. The CPU build never needs this.

  Idempotent: skips when cudnn64_9.dll is already present, unless -Force.
#>
[CmdletBinding()]
param(
  [string]$CudaDir = $(if ($env:CASR_CUDA_DIR) { $env:CASR_CUDA_DIR } else { Join-Path $env:LOCALAPPDATA "Casr\cuda" }),
  [string]$Wheel = "nvidia-cudnn-cu13",
  [string]$Version = "",
  [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$cudnnDll = Join-Path $CudaDir "cudnn64_9.dll"
if ((Test-Path $cudnnDll) -and -not $Force) {
  Write-Host "present: $cudnnDll"
  Write-Host "cuda support dir: $CudaDir"
  Write-Host "done (nothing to do; use -Force to refresh)."
  exit 0
}

New-Item -ItemType Directory -Force -Path $CudaDir | Out-Null
$work = Join-Path $env:TEMP ("seshmesh-cudnn-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
  $pip = Get-Command pip -ErrorAction SilentlyContinue
  if ($pip) {
    $pipCmd = $pip.Source
    $pipArgs = @()
  } else {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) { throw "python/pip not found; install Python or fetch cuDNN 9 from NVIDIA manually into $CudaDir" }
    $pipCmd = $python.Source
    $pipArgs = @("-m", "pip")
  }

  $spec = if ([string]::IsNullOrWhiteSpace($Version)) { $Wheel } else { "$Wheel==$Version" }
  Write-Host "downloading: $spec (NVIDIA redistributable wheel)"
  & $pipCmd @pipArgs download --no-deps --only-binary :all: --dest $work $spec
  if ($LASTEXITCODE -ne 0) { throw "pip download failed (exit $LASTEXITCODE)" }

  $whl = Get-ChildItem $work -Filter *.whl | Select-Object -First 1
  if (-not $whl) { throw "no wheel downloaded into $work" }
  Write-Host ("extracting: " + $whl.Name + " (" + [math]::Round($whl.Length / 1MB, 1) + " MB)")

  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $extract = Join-Path $work "extracted"
  [System.IO.Compression.ZipFile]::ExtractToDirectory($whl.FullName, $extract)

  $binDir = Get-ChildItem $extract -Recurse -Directory -Filter bin |
            Where-Object { $_.FullName -match 'cudnn' } |
            Select-Object -First 1
  if (-not $binDir) { throw "cudnn bin directory not found inside " + $whl.Name }

  $copied = 0
  Get-ChildItem $binDir.FullName -Filter *.dll | ForEach-Object {
    if ($_.Name -like 'cudnn*') {
      Copy-Item $_.FullName (Join-Path $CudaDir $_.Name) -Force
      Write-Host ("  copied: " + $_.Name + " (" + [math]::Round($_.Length / 1MB, 1) + " MB)")
      $copied++
    }
  }

  if (-not (Test-Path $cudnnDll)) { throw "cudnn64_9.dll was not present in the wheel" }
  Write-Host "cuda support dir: $CudaDir ($copied dlls)"
  Write-Host "note: the CUDA 13 toolkit bin directory must be on PATH; the CPU build ignores this directory."
  Write-Host "done. Restart SeshMesh; the Neural toggle reports the provider it selected."
} finally {
  try { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}
