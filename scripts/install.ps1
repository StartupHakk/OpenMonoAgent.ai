param(
    [string]$Workdir = (Get-Location).Path,
    [int]$AcpPort = 7475,
    [switch]$Run
)

$ErrorActionPreference = "Stop"

# This file lives in <repo>/scripts. The repo root is its parent, so the
# script finds the project no matter which directory it is launched from.
$repoRoot = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repoRoot "src/OpenMono.Cli/OpenMono.Cli.csproj"

if (-not (Test-Path $csproj)) {
    Write-Error "Could not find $csproj. scripts/install.ps1 must stay in the OpenMono repository's scripts directory."
    exit 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "The .NET SDK is required. Install .NET 10 from https://dotnet.microsoft.com/download, then re-run."
    exit 1
}

$sdkVersion = (& dotnet --version)
Write-Host "Found .NET SDK $sdkVersion"
# Oct 2026 OOB security release: .NET 10 runtime 10.0.14 / SDK 10.0.403.
$minSdkVersion = [version]"10.0.403"
try {
    $parsedVersion = [version]$sdkVersion
} catch {
    Write-Error "Could not parse .NET SDK version '$sdkVersion'. Install .NET 10 SDK 10.0.403 or newer from https://dotnet.microsoft.com/download, then re-run."
    exit 1
}
if ($parsedVersion -lt $minSdkVersion) {
    Write-Error "OpenMono needs .NET SDK 10.0.403 or newer (Oct 2026 security update). Found $sdkVersion. Install it from https://dotnet.microsoft.com/download, then re-run."
    exit 1
}

Write-Host "Building the OpenMono agent (Release)..."
& dotnet build $csproj -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit $LASTEXITCODE
}

if ($Run) {
    Write-Host "Starting the ACP agent on port $AcpPort for $Workdir ..."
    & dotnet run --project $csproj -c Release -- --workdir $Workdir --acp-only --acp-port $AcpPort
}
else {
    Write-Host ""
    Write-Host "Build complete. Start the agent for your project with:"
    Write-Host "  dotnet run --project `"$csproj`" -c Release -- --workdir `"$Workdir`" --acp-only --acp-port $AcpPort"
    Write-Host ""
    Write-Host "Then open the same folder in VS Code / Cursor and open the OpenMono Agent panel."
}
