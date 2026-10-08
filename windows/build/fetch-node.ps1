param(
  [string]$Version = "22.14.0",
  [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
# Portable Node for MCP servers and language servers that need it.
$url = "https://nodejs.org/dist/v${Version}/node-v${Version}-win-x64.zip"
if ([string]::IsNullOrWhiteSpace($OutDir)) {
  $OutDir = Join-Path $env:LOCALAPPDATA "OpenMono\bin\node"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zip = Join-Path ([IO.Path]::GetTempPath()) "node.zip"
Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip
Expand-Archive -Path $zip -DestinationPath $OutDir -Force
Write-Host "Node ready under $OutDir"
