param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$windows = Join-Path $root "windows"

# AMD/Intel (x64) hosts only. The WinUI packaging toolchain (self-contained
# Windows App SDK, XAML PRI generation) does not run on ARM64, so fail fast
# with a clear message instead of misleading compiler errors. Build gates are
# CI (windows-desktop workflow on windows-latest) and the Linux portable job.
$hostArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
if ($hostArch -ne [System.Runtime.InteropServices.Architecture]::X64) {
  throw "OpenMono for Windows requires an AMD/Intel (x64) build host. Current host: $hostArch. Push to the Windows branch and use the windows-desktop CI workflow instead."
}
$version = (Get-Content (Join-Path $windows "VERSION.windows")).Trim()
$env:OPENMONO_VERSION = $version

Write-Host "OpenMono for Windows build $version ($Configuration, $Runtime)"

& dotnet build (Join-Path $windows "OpenMono.Windows.sln") -c $Configuration
if ($LASTEXITCODE -ne 0) { exit 1 }

if (-not $SkipTests) {
  & dotnet test (Join-Path $windows "tests\OpenMono.Windows.Tests\OpenMono.Windows.Tests.csproj") -c $Configuration --no-build
  if ($LASTEXITCODE -ne 0) { exit 1 }
}

$publishDir = Join-Path $windows "publish\$Runtime"
& dotnet publish (Join-Path $windows "src\OpenMono.Desktop\OpenMono.Desktop.csproj") `
  -c $Configuration -r $Runtime --self-contained true `
  -o $publishDir /p:Version=$version
if ($LASTEXITCODE -ne 0) { exit 1 }

Copy-Item (Join-Path $windows "src\OpenMono.Models\models.json") $publishDir -Force
Copy-Item (Join-Path $windows "VERSION.windows") $publishDir -Force

# Optional signing: skipped when no cert is provided.
if ($env:WINDOWS_CERT_PATH -and (Test-Path $env:WINDOWS_CERT_PATH)) {
  Write-Host "Signing binaries..."
  $files = Get-ChildItem $publishDir -Include *.exe, *.dll -Recurse
  foreach ($f in $files) {
    & signtool sign /fd SHA256 /f $env:WINDOWS_CERT_PATH /p $env:WINDOWS_CERT_PASSWORD /tr http://timestamp.digicert.com /td SHA256 $f.FullName
  }
  if ($LASTEXITCODE -ne 0) { exit 1 }
} else {
  Write-Host "No signing cert provided (WINDOWS_CERT_PATH), skipping signing."
}

# Compile the installer when Inno Setup 6 is installed.
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (Test-Path $iscc) {
  & $iscc (Join-Path $windows "installer\inno\openmono.iss")
  if ($LASTEXITCODE -ne 0) { exit 1 }
  if ($env:WINDOWS_CERT_PATH -and (Test-Path $env:WINDOWS_CERT_PATH)) {
    & signtool sign /fd SHA256 /f $env:WINDOWS_CERT_PATH /p $env:WINDOWS_CERT_PASSWORD /tr http://timestamp.digicert.com /td SHA256 (Join-Path $windows "dist\OpenMonoSetup.exe")
  }
} else {
  Write-Host "Inno Setup 6 (ISCC.exe) not found, skipping setup.exe compile."
}

Write-Host "Build done."
