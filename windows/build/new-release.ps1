param(
  [string]$NewVersion = ""
)

$ErrorActionPreference = "Stop"
$windows = Split-Path $PSScriptRoot -Parent
$versionFile = Join-Path $windows "VERSION.windows"
$current = (Get-Content $versionFile).Trim()
if ([string]::IsNullOrWhiteSpace($NewVersion)) {
  Write-Host "Current windows version: $current"
  Write-Host "Usage: new-release.ps1 -NewVersion 1.0.0-preview.2"
  exit 0
}
$NewVersion.Trim() | Set-Content $versionFile -NoNewline
Write-Host "Bumped $current -> $NewVersion"
