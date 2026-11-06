<#
.SYNOPSIS
  Checks NVIDIA GPU, VRAM, and CUDA/cuDNN DLL visibility for Foundry Local.

.DESCRIPTION
  Prints nvidia-smi output, total dedicated VRAM, and whether the CUDA and
  cuDNN DLL directories are on PATH. Foundry Local does not ship CUDA/cuDNN,
  so they must be installed separately per the Foundry Local release notes.
  Exit code 0 always; this script reports, the host decides.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

Write-Host '[openmono] --- GPU ---'
if (Get-Command 'nvidia-smi' -ErrorAction SilentlyContinue) {
  & nvidia-smi --query-gpu=name,memory.total --format=csv
} else {
  Write-Warning '[openmono] nvidia-smi not found. Install an NVIDIA driver for your GPU.'
}

Write-Host '[openmono] --- CUDA compiler (optional, driver check is nvidia-smi) ---'
if (Get-Command 'nvcc' -ErrorAction SilentlyContinue) {
  & nvcc --version | Select-Object -First 3
} else {
  Write-Host '[openmono] nvcc not on PATH (fine if the CUDA runtime DLLs below resolve).'
}

Write-Host '[openmono] --- CUDA/cuDNN DLLs on PATH ---'
$wanted = @('cublas64', 'cudnn64', 'nvcuda')
$pathDirs = ($env:PATH -split ';') | Where-Object { $_ -and (Test-Path $_) }
foreach ($prefix in $wanted) {
  $hit = $false
  foreach ($dir in $pathDirs) {
    try {
      if (Get-ChildItem -Path $dir -Filter "$prefix*.dll" -ErrorAction Stop | Select-Object -First 1) {
        Write-Host "[openmono] FOUND  $prefix*.dll in $dir"
        $hit = $true
        break
      }
    } catch { }
  }
  if (-not $hit) {
    Write-Warning "[openmono] MISSING $prefix*.dll on PATH. Install CUDA + cuDNN (matching Foundry Local 0.13.0+ release notes) and add their bin directories to PATH."
  }
}

Write-Host '[openmono] --- VRAM guidance ---'
Write-Host '[openmono] Qwen3.8-27B needs 24 GB dedicated VRAM (64K context; 32 GB+ for up to 262K).'
Write-Host '[openmono] Below 24 GB the host picks a smaller catalog model and tells you why.'
