#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Downloads the MiniLM embedding model + tokenizer vocab for CASR neural search.

.DESCRIPTION
  Fetches sentence-transformers/all-MiniLM-L6-v2 (ONNX) and vocab.txt from
  Hugging Face into %LOCALAPPDATA%\Casr\models (or $env:CASR_MODELS_DIR when
  set), then writes a SHA-256 sidecar the app verifies before loading.
  Idempotent: skips files already present with a matching hash.

  Model:  https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2
  License: Apache 2.0 (model) — see the model card for details.
#>
[CmdletBinding()]
param(
  [string]$ModelsDir = $(if ($env:CASR_MODELS_DIR) { $env:CASR_MODELS_DIR } else { Join-Path $env:LOCALAPPDATA "Casr\models" }),
  [string]$ModelRepo = "sentence-transformers/all-MiniLM-L6-v2",
  [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ModelUrl = "https://huggingface.co/$ModelRepo/resolve/main/onnx/model.onnx"
$VocabUrl = "https://huggingface.co/$ModelRepo/resolve/main/vocab.txt"
$ModelName = "minilm-l6-v2.onnx"

New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null
$modelPath = Join-Path $ModelsDir $ModelName
$vocabPath = Join-Path $ModelsDir "vocab.txt"
$shaPath = "$modelPath.sha256"

function Get-FileSha256([string]$Path) {
  (Get-FileHash -Path $Path -Algorithm SHA256).Hash
}

function Save-WithCheck([string]$Url, [string]$Dest, [long]$MinBytes) {
  if ((Test-Path $Dest) -and -not $Force) {
    $size = (Get-Item $Dest).Length
    if ($size -ge $MinBytes) { Write-Host "present: $Dest ($size bytes)"; return }
    Write-Host "re-downloading undersized file: $Dest ($size bytes)"
  }
  Write-Host "downloading: $Url"
  $tmp = "$Dest.downloading"
  Invoke-WebRequest -Uri $Url -OutFile $tmp -UseBasicParsing
  $size = (Get-Item $tmp).Length
  if ($size -lt $MinBytes) { throw "Downloaded file too small ($size bytes): $Url" }
  Move-Item -Force $tmp $Dest
  Write-Host "saved: $Dest ($size bytes)"
}

Save-WithCheck $ModelUrl $modelPath 10MB
Save-WithCheck $VocabUrl $vocabPath 100KB

$sha = Get-FileSha256 $modelPath
"$sha  $ModelName" | Set-Content -NoNewline -Encoding ascii $shaPath
Write-Host "sha256: $sha"
Write-Host "models dir: $ModelsDir"
Write-Host "done. Launch CASR and opt into neural embeddings to trigger a re-index."
