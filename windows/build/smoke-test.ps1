param(
  [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$windows = Join-Path $root "windows"

& dotnet run --project (Join-Path $windows "src\OpenMono.SmokeTest\OpenMono.SmokeTest.csproj") -c $Configuration
exit $LASTEXITCODE
