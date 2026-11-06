<#
.SYNOPSIS
  First-run bootstrap for OpenMono on Windows (llama.cpp inference).

.DESCRIPTION
  Makes sure Git and the .NET SDK are installed (winget, or the official
  installers without winget; PATH is refreshed in this session), reports the
  NVIDIA GPU and the CUDA version its driver supports (that picks the
  llama.cpp build: CUDA, then Vulkan, then CPU), then makes sure Docker
  Desktop is installed (winget, with WSL 2) and its engine is running, and
  prints the next steps. Idempotent: safe to re-run.

  llama.cpp itself and the GGUF model are installed by `openmono.ps1 setup`
  (openmono-win inference up --update), the way scripts/install.sh pulls the
  llama-server image and downloads the model on Linux.

  -SkipHostPrerequisites: openmono.ps1 setup has already done the Git and
  .NET step.
#>
param([switch]$SkipHostPrerequisites)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'docker.ps1')
. (Join-Path $PSScriptRoot 'prereqs.ps1')

function Test-Command($Name) {
  return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

if (-not $SkipHostPrerequisites) { Confirm-HostPrerequisites }

Write-Host '[openmono] Checking NVIDIA GPU...'
$gpu = $null
if (Test-Command 'nvidia-smi') {
  $query = & nvidia-smi --query-gpu=name,memory.total --format=csv,noheader,nounits 2>$null
  if ($LASTEXITCODE -eq 0 -and $query) { $gpu = @($query)[0] }
}
if ($gpu) {
  $banner = & nvidia-smi 2>$null | Out-String
  $cuda = [regex]::Match($banner, 'CUDA Version:\s*(\d+\.\d+)')
  $parts = $gpu -split ','
  $gib = [math]::Round([double]$parts[1].Trim() / 1024)
  if ($cuda.Success) {
    Write-Host "[openmono] $($parts[0].Trim()), $gib GB VRAM; driver supports CUDA $($cuda.Groups[1].Value): llama.cpp CUDA build."
  } else {
    Write-Host "[openmono] $($parts[0].Trim()), $gib GB VRAM; the driver reports no CUDA version: llama.cpp Vulkan build. Updating the NVIDIA driver enables CUDA."
  }
} else {
  Write-Host '[openmono] No NVIDIA GPU found: llama.cpp CPU build with the CPU model (Qwen3.5-2B).'
}

Write-Host '[openmono] Checking Docker (the agent runs in a Linux container)...'
Confirm-DockerReady -Install

Write-Host ''
Write-Host '[openmono] Prerequisites checked. Next:'
Write-Host '  .\win\openmono.ps1 models   (the GGUF model ladder and the model this machine serves)'
Write-Host '  .\win\openmono.ps1 start    (installs llama.cpp and the model on first run, serves it on http://127.0.0.1:7474)'
Write-Host '  .\win\openmono.ps1 agent    (the OpenMono agent in its Linux container)'
