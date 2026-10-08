param(
  [string]$Version = "14.1.1",
  [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$url = "https://github.com/BurntSushi/ripgrep/releases/download/${Version}/ripgrep-${Version}-x86_64-pc-windows-msvc.zip"
if ([string]::IsNullOrWhiteSpace($OutDir)) {
  $OutDir = Join-Path $env:LOCALAPPDATA "OpenMono\bin\rg"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zip = Join-Path ([IO.Path]::GetTempPath()) "ripgrep.zip"
Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip
Expand-Archive -Path $zip -DestinationPath ([IO.Path]::GetTempPath() + "rg-extract") -Force
$found = Get-ChildItem ([IO.Path]::GetTempPath() + "rg-extract") -Recurse -Filter "rg.exe" | Select-Object -First 1
Copy-Item $found.FullName (Join-Path $OutDir "rg.exe") -Force
Write-Host "rg.exe ready at $OutDir\rg.exe"
