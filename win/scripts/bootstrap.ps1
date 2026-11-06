<#
.SYNOPSIS
  First-run bootstrap for OpenMono on Windows (Foundry Local backend).

.DESCRIPTION
  Makes sure Git and the .NET SDK are installed (winget, or the official
  installers without winget; PATH is refreshed in this session), checks for
  the Foundry Local CLI (0.13.0+), installs it with winget when missing,
  verifies NVIDIA GPU and CUDA/cuDNN visibility, then makes sure
  Docker Desktop is installed (winget, with WSL 2) and its engine is running,
  and prints the next steps. Idempotent: safe to re-run.

  -SkipHostPrerequisites: openmono.ps1 setup has already done the Git and
  .NET step.
#>
param([switch]$SkipHostPrerequisites)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'docker.ps1')
. (Join-Path $PSScriptRoot 'prereqs.ps1')

$MinFoundry = [Version]'0.13.0'

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

if (-not $SkipHostPrerequisites) { Confirm-HostPrerequisites }

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

Write-Host '[openmono] Checking Docker (the agent runs in a Linux container)...'
Confirm-DockerReady -Install

Write-Host ''
Write-Host '[openmono] Prerequisites checked. Next:'
Write-Host '  .\win\openmono.ps1 catalog check   (the model this machine serves, checked against the Foundry catalog)'
Write-Host '  .\win\openmono.ps1 start   (serves it on http://127.0.0.1:7474; qwen3.8-27b on 24 GB+ is about 18 GB on first run, qwen3.5-2b on CPU about 2 GB)'
Write-Host '  .\win\openmono.ps1 agent   (the OpenMono agent in its Linux container)'
