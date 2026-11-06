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
  Write-Host '[openmono] nvidia-smi not found: no NVIDIA GPU (or no driver). OpenMono runs qwen3.5-2b on the CPU.'
  Write-Host '[openmono] If this machine has an NVIDIA GPU, install its driver so the GPU ladder applies.'
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

# Keep in step with FoundryModelLadder.cs (a unit test compares them).
Write-Host '[openmono] --- VRAM guidance (one model per machine) ---'
Write-Host '[openmono]   24 GB and up  qwen3.8-27b   (always; 64K context, 32 GB+ for up to 262K)'
Write-Host '[openmono]   12 GB         qwen3.5-9b    (text and images)'
Write-Host '[openmono]   8 GB          qwen3.5-4b    (text and images)'
Write-Host '[openmono]   4 GB          qwen3.5-2b    (text and images)'
Write-Host '[openmono]   under 4 GB    qwen3.5-0.8b  (last resort)'
Write-Host '[openmono]   no GPU (CPU)  qwen3.5-2b    (generic CPU variant when Foundry offers one; never the larger models)'
Write-Host '[openmono] Aliases are checked against https://foundrylocal.ai/api/models before anything downloads.'
