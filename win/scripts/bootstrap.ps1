<#
.SYNOPSIS
  First-run bootstrap for OpenMono on Windows (Foundry Local backend).

.DESCRIPTION
  Checks for the Foundry Local CLI (0.13.0+), installs it with winget when
  missing, verifies NVIDIA GPU and CUDA/cuDNN visibility, and prints the next
  steps (model download, server start). Idempotent: safe to re-run.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$MinFoundry = [Version]'0.13.0'
$TargetModel = 'qwen3.8-27b'

function Test-Command($Name) {
  return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Get-FoundryVersion {
  if (-not (Test-Command 'foundry')) { return $null }
  $out = & foundry --version 2>&1 | Out-String
  $m = [regex]::Match($out, '\d+\.\d+\.\d+')
  if (-not $m.Success) { return $null }
  return [Version]$m.Value
}

Write-Host '[openmono] Checking Foundry Local CLI...'
$version = Get-FoundryVersion
if ($null -eq $version) {
  Write-Host '[openmono] Foundry CLI not found. Installing with winget...'
  if (-not (Test-Command 'winget')) {
    Write-Error 'winget is not available. Install App Installer from the Microsoft Store, then re-run this script.'
  }
  & winget install --id Microsoft.FoundryLocal --accept-source-agreements --accept-package-agreements
  $version = Get-FoundryVersion
}

if ($null -eq $version) {
  Write-Error 'Foundry CLI still not found after install. Open a new terminal (PATH refresh) and re-run.'
}
Write-Host "[openmono] Foundry CLI version: $version"
if ($version -lt $MinFoundry) {
  Write-Warning "[openmono] Foundry CLI $version is older than required $MinFoundry. Run: winget upgrade Microsoft.FoundryLocal"
} else {
  Write-Host '[openmono] Foundry CLI version OK.'
}

Write-Host '[openmono] Checking NVIDIA GPU...'
& "$PSScriptRoot\check-cuda.ps1"

Write-Host ''
Write-Host '[openmono] Next steps:'
Write-Host "  1. foundry model load $TargetModel   (about 18 GB download on first run)"
Write-Host '  2. foundry server status             (note the local API URL)'
Write-Host '  3. Start the Windows host, which adopts or binds http://127.0.0.1:7474'
Write-Host ''
Write-Host '[openmono] Bootstrap complete.'
