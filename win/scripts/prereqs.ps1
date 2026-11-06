<#
.SYNOPSIS
  Git and .NET SDK detection and install for the Windows host.

.DESCRIPTION
  Dot-sourced by openmono.ps1 and bootstrap.ps1, like docker.ps1. Host
  PowerShell only; nothing here runs inside a container.

  Detection: `git --version` (Git 2.x), and `dotnet --list-sdks` for an SDK
  with the major version and minimum patch that win/global.json pins
  (10.0.403, rollForward latestMinor).

  Install when missing or too old, non-interactive:
    winget        Git.Git, and Microsoft.DotNet.SDK.<major> (Microsoft.DotNet.SDK.10).
    no winget     .NET: Microsoft's dotnet-install.ps1 (https://dot.net/v1/dotnet-install.ps1)
                  into %LOCALAPPDATA%\Microsoft\dotnet, no administrator needed.
                  Git: MinGit, the official minimal Git for Windows zip from the
                  latest release at https://github.com/git-for-windows/git/releases,
                  into %LOCALAPPDATA%\Programs\MinGit, no administrator needed.
  The fallback install directories are added to the user PATH.

  After an install, PATH in this session is rebuilt from the machine and
  user PATH, so later steps in the same run can call git and dotnet without
  a new shell or a reboot.

  Overrides: OPENMONO_DOTNET_INSTALL_SCRIPT_URL, OPENMONO_DOTNET_INSTALL_DIR,
  OPENMONO_GIT_RELEASE_API, OPENMONO_GIT_INSTALL_DIR. OPENMONO_PATH_REGISTRY_FILE
  (tests only) stands in for the machine and user PATH: one directory per line.
#>

$PrereqsWinDir = Split-Path -Parent $PSScriptRoot
$GitWingetId = 'Git.Git'
$MinGitVersion = [Version]'2.0.0'
$GitReleaseApi = 'https://api.github.com/repos/git-for-windows/git/releases/latest'
$DotnetInstallScriptUrl = 'https://dot.net/v1/dotnet-install.ps1'
$PrereqWingetAlreadyInstalled = @(-1978335189, -1978335135)
$PrereqWingetRebootRequired = @(-1978334967, -1978334966, 3010)

function Get-RequiredDotnetSdk {
  $globalJson = Join-Path $PrereqsWinDir 'global.json'
  return [Version]((Get-Content $globalJson -Raw | ConvertFrom-Json).sdk.version)
}

function Get-DotnetWingetId {
  return 'Microsoft.DotNet.SDK.' + (Get-RequiredDotnetSdk).Major
}

function Get-GitVersion {
  $ErrorActionPreference = 'Continue'
  if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $null }
  $out = (& git --version 2>$null | Out-String)
  $m = [regex]::Match($out, '(\d+)\.(\d+)\.(\d+)')
  if ($LASTEXITCODE -ne 0 -or -not $m.Success) { return $null }
  return [Version]$m.Value
}

# Every installed SDK, from `dotnet --list-sdks` ("10.0.403 [C:\Program Files\dotnet\sdk]").
function Get-DotnetSdks {
  $ErrorActionPreference = 'Continue'
  if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return @() }
  $sdks = New-Object System.Collections.Generic.List[Version]
  foreach ($line in @(& dotnet --list-sdks 2>$null)) {
    $m = [regex]::Match("$line", '^\s*(\d+\.\d+\.\d+)')
    if ($m.Success) { $sdks.Add([Version]$m.Groups[1].Value) }
  }
  return $sdks.ToArray()
}

# The newest installed SDK global.json accepts: same major, at least the pinned version.
function Get-UsableDotnetSdk {
  $required = Get-RequiredDotnetSdk
  $usable = @(@(Get-DotnetSdks) | Where-Object { $_.Major -eq $required.Major -and $_ -ge $required } | Sort-Object -Descending)
  if ($usable.Count -eq 0) { return $null }
  return $usable[0]
}

function Get-PersistedPathEntries {
  if ($env:OPENMONO_PATH_REGISTRY_FILE) {
    if (-not (Test-Path $env:OPENMONO_PATH_REGISTRY_FILE)) { return @() }
    return @(Get-Content $env:OPENMONO_PATH_REGISTRY_FILE | Where-Object { $_ })
  }
  $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
  $user = [Environment]::GetEnvironmentVariable('Path', 'User')
  return @("$machine;$user" -split ';' | Where-Object { $_ } | ForEach-Object { [Environment]::ExpandEnvironmentVariables($_) })
}

function ConvertTo-PathKey([string]$Dir) {
  return $Dir.Trim().TrimEnd('\').ToLowerInvariant()
}

<#
  Rebuilds this session's PATH from the machine and user PATH that an
  installer just changed. Entries already on PATH keep their order and come
  first; new ones are appended. Returns the directories it added.
#>
function Update-SessionPath {
  $current = @($env:PATH -split ';' | Where-Object { $_ })
  $seen = New-Object 'System.Collections.Generic.HashSet[string]'
  foreach ($dir in $current) { $null = $seen.Add((ConvertTo-PathKey $dir)) }
  $added = New-Object System.Collections.Generic.List[string]
  foreach ($dir in Get-PersistedPathEntries) {
    if ($seen.Add((ConvertTo-PathKey $dir))) { $added.Add($dir) }
  }
  if ($added.Count -gt 0) {
    $env:PATH = (@($current) + @($added)) -join ';'
    Write-Host "[openmono] Refreshed PATH for this session (added: $($added -join ', '))."
  }
  return , $added.ToArray()
}

function Add-PathFront([string]$Dir) {
  $rest = @($env:PATH -split ';' | Where-Object { $_ -and (ConvertTo-PathKey $_) -ne (ConvertTo-PathKey $Dir) })
  $env:PATH = (@($Dir) + $rest) -join ';'
}

# Adds a directory to the user PATH for future shells, and to this session.
function Add-PersistedPathEntry([string]$Dir) {
  Add-PathFront $Dir
  if ($env:OPENMONO_PATH_REGISTRY_FILE) {
    Add-Content -Path $env:OPENMONO_PATH_REGISTRY_FILE -Value $Dir
    return
  }
  # Read and write the raw value so %VARIABLE% entries stay unexpanded (REG_EXPAND_SZ).
  $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
  try {
    $raw = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $entries = @($raw -split ';' | Where-Object { $_ })
    if ($entries | Where-Object { (ConvertTo-PathKey ([Environment]::ExpandEnvironmentVariables($_))) -eq (ConvertTo-PathKey $Dir) }) { return }
    $key.SetValue('Path', ((@($entries) + @($Dir)) -join ';'), [Microsoft.Win32.RegistryValueKind]::ExpandString)
  } finally {
    $key.Close()
  }
  # A no-op user variable write broadcasts WM_SETTINGCHANGE, so shells opened from
  # Explorer after this pick up the new PATH.
  [Environment]::SetEnvironmentVariable('OPENMONO_PATH_REFRESH', $null, 'User')
}

function Test-Winget {
  return [bool](Get-Command winget -ErrorAction SilentlyContinue)
}

# winget install, then winget upgrade when the package is there but older.
# Returns 'ok' or 'reboot'; throws with the exit code on failure.
function Invoke-WingetPackage([string]$Id, [string]$Name) {
  $ErrorActionPreference = 'Continue'
  Write-Host "[openmono] Installing $Name with winget ($Id); approve the administrator prompt if asked..."
  & winget install --id $Id -e --source winget --silent --accept-source-agreements --accept-package-agreements | Out-Host
  $code = $LASTEXITCODE
  if ($PrereqWingetAlreadyInstalled -contains $code) {
    & winget upgrade --id $Id -e --source winget --silent --accept-source-agreements --accept-package-agreements | Out-Host
    $code = 0
  }
  if ($PrereqWingetRebootRequired -contains $code) { return 'reboot' }
  if ($code -ne 0) { throw "winget could not install $Name ($Id, exit $code)." }
  return 'ok'
}

function Enable-Tls12 {
  try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
  } catch { }
}

function Get-PowerShellExe {
  return (Get-Process -Id $PID).Path
}

function Install-GitFallback {
  Enable-Tls12
  $api = if ($env:OPENMONO_GIT_RELEASE_API) { $env:OPENMONO_GIT_RELEASE_API } else { $GitReleaseApi }
  $dir = if ($env:OPENMONO_GIT_INSTALL_DIR) { $env:OPENMONO_GIT_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\MinGit' }
  $suffix = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { '64-bit' }
  Write-Host "[openmono] winget is not available. Installing MinGit (official Git for Windows) into $dir..."
  $release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'openmono-win' } -UseBasicParsing
  $asset = @($release.assets | Where-Object { $_.name -match "^MinGit-\d[\d.]*(\.windows\.\d+)?-$suffix\.zip$" }) | Select-Object -First 1
  if (-not $asset) { throw "No MinGit-*-$suffix.zip asset in the Git for Windows release at $api." }
  $zip = Join-Path ([IO.Path]::GetTempPath()) $asset.name
  Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing
  if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
  Expand-Archive -Path $zip -DestinationPath $dir -Force
  Remove-Item $zip -ErrorAction SilentlyContinue
  $cmd = Join-Path $dir 'cmd'
  if (-not (Test-Path (Join-Path $cmd 'git.exe'))) { throw "MinGit unpacked into $dir, but cmd\git.exe is missing." }
  Add-PersistedPathEntry $cmd
}

function Install-DotnetFallback {
  Enable-Tls12
  $required = Get-RequiredDotnetSdk
  $url = if ($env:OPENMONO_DOTNET_INSTALL_SCRIPT_URL) { $env:OPENMONO_DOTNET_INSTALL_SCRIPT_URL } else { $DotnetInstallScriptUrl }
  $dir = if ($env:OPENMONO_DOTNET_INSTALL_DIR) { $env:OPENMONO_DOTNET_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet' }
  $channel = "$($required.Major).0"
  Write-Host "[openmono] winget is not available. Installing the .NET $channel SDK with dotnet-install.ps1 into $dir..."
  $script = Join-Path ([IO.Path]::GetTempPath()) 'openmono-dotnet-install.ps1'
  Invoke-WebRequest -Uri $url -OutFile $script -UseBasicParsing
  $ErrorActionPreference = 'Continue'
  & (Get-PowerShellExe) -NoProfile -ExecutionPolicy Bypass -File $script -Channel $channel -InstallDir $dir -NoPath | Out-Host
  $code = $LASTEXITCODE
  Remove-Item $script -ErrorAction SilentlyContinue
  if ($code -ne 0) { throw "dotnet-install.ps1 failed (exit $code)." }
  if (-not (Test-Path (Join-Path $dir 'dotnet.exe'))) { throw "dotnet-install.ps1 finished, but $dir\dotnet.exe is missing." }
  Add-PersistedPathEntry $dir
  $env:DOTNET_ROOT = $dir
  if (-not $env:OPENMONO_PATH_REGISTRY_FILE) { [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dir, 'User') }
}

# Known install locations, for when PATH has not caught up yet.
function Find-InstalledTool([string]$Exe, [string[]]$Dirs) {
  foreach ($dir in $Dirs) {
    if ($dir -and (Test-Path (Join-Path $dir $Exe))) {
      Add-PathFront $dir
      return $true
    }
  }
  return $false
}

function Confirm-Git {
  $version = Get-GitVersion
  if ($version -and $version -ge $MinGitVersion) {
    Write-Host "[openmono] Git $version found."
    return
  }
  if ($version) { Write-Host "[openmono] Git $version is older than $MinGitVersion; updating it." -ForegroundColor Yellow }

  $result = 'ok'
  if (Test-Winget) { $result = Invoke-WingetPackage $GitWingetId 'Git' } else { Install-GitFallback }
  $null = Update-SessionPath
  $version = Get-GitVersion
  if (-not ($version -and $version -ge $MinGitVersion)) {
    $gitDir = if ($env:OPENMONO_GIT_INSTALL_DIR) { Join-Path $env:OPENMONO_GIT_INSTALL_DIR 'cmd' } else { $null }
    if (Find-InstalledTool 'git.exe' @((Join-Path $env:ProgramFiles 'Git\cmd'), $gitDir)) { $version = Get-GitVersion }
  }
  if (-not ($version -and $version -ge $MinGitVersion)) {
    throw 'Git was installed but git is still not callable from this session. Open a new PowerShell window and re-run: .\win\openmono.ps1 setup'
  }
  Write-Host "[openmono] Git $version installed; available in this session." -ForegroundColor Green
  if ($result -eq 'reboot') { Write-Host '[openmono] The Git installer asked for a Windows restart; git already works here, so setup continues.' -ForegroundColor Yellow }
}

function Confirm-DotnetSdk {
  $required = Get-RequiredDotnetSdk
  $sdk = Get-UsableDotnetSdk
  if ($sdk) {
    Write-Host "[openmono] .NET SDK $sdk found (needs $($required.Major).x, $required or newer)."
    return
  }
  $found = @(Get-DotnetSdks)
  if ($found.Count -gt 0) {
    Write-Host "[openmono] .NET SDK $($found -join ', ') found, but OpenMono needs $($required.Major).x, $required or newer; installing it." -ForegroundColor Yellow
  }

  $result = 'ok'
  if (Test-Winget) { $result = Invoke-WingetPackage (Get-DotnetWingetId) ".NET $($required.Major) SDK" } else { Install-DotnetFallback }
  $null = Update-SessionPath
  $sdk = Get-UsableDotnetSdk
  if (-not $sdk) {
    $userDir = if ($env:OPENMONO_DOTNET_INSTALL_DIR) { $env:OPENMONO_DOTNET_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet' }
    if (Find-InstalledTool 'dotnet.exe' @((Join-Path $env:ProgramFiles 'dotnet'), $userDir)) { $sdk = Get-UsableDotnetSdk }
  }
  if (-not $sdk) {
    throw ".NET SDK $required was installed but dotnet does not list it from this session (found: $((@(Get-DotnetSdks)) -join ', ')). Open a new PowerShell window and re-run: .\win\openmono.ps1 setup"
  }
  Write-Host "[openmono] .NET SDK $sdk installed; available in this session." -ForegroundColor Green
  if ($result -eq 'reboot') { Write-Host '[openmono] The .NET installer asked for a Windows restart; dotnet already works here, so setup continues.' -ForegroundColor Yellow }
}

# Git and the .NET SDK, installed when missing. Throws with the reason when an
# install fails, so setup never continues into a missing git or dotnet.
function Confirm-HostPrerequisites {
  Write-Host '[openmono] Checking Git and the .NET SDK...'
  Confirm-Git
  Confirm-DotnetSdk
}
