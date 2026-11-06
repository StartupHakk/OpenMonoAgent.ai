<#
.SYNOPSIS
  Docker Desktop detection, install, and engine start for the Windows host.

.DESCRIPTION
  Dot-sourced by openmono.ps1 and bootstrap.ps1. The Windows counterpart of
  the Docker step in scripts/install_prereqs.sh (install when missing) and the
  bash launcher's _ensure_docker (start the engine and wait when it is down).
  The agent itself stays a Linux container; this only gets the engine ready.

  OPENMONO_DOCKER_DESKTOP_DIR overrides the Docker Desktop install directory
  and OPENMONO_DOCKER_WAIT_SECONDS the engine wait (120 s by default).
#>

$DockerWingetId = 'Docker.DockerDesktop'

# winget exit codes (APPINSTALLER_CLI_ERROR_*), as signed 32-bit integers.
$WingetAlreadyInstalled = @(-1978335189, -1978335135)
$WingetRebootRequired = @(-1978334967, -1978334966, 3010)

function Get-DockerDesktopDir {
  if ($env:OPENMONO_DOCKER_DESKTOP_DIR) { return $env:OPENMONO_DOCKER_DESKTOP_DIR }
  return Join-Path $env:ProgramFiles 'Docker\Docker'
}

function Get-DockerWaitSeconds {
  $seconds = 0
  if ([int]::TryParse([string]$env:OPENMONO_DOCKER_WAIT_SECONDS, [ref]$seconds) -and $seconds -gt 0) { return $seconds }
  return 120
}

# A fresh install adds docker.exe to the machine PATH, which this session has
# not picked up yet, so look in the install directory too.
function Find-DockerCli {
  if (Get-Command docker -ErrorAction SilentlyContinue) { return $true }
  $bin = Join-Path (Get-DockerDesktopDir) 'resources\bin'
  if (Test-Path (Join-Path $bin 'docker.exe')) {
    $env:PATH = "$bin;$env:PATH"
    return $true
  }
  return $false
}

function Test-DockerCompose {
  $ErrorActionPreference = 'Continue'
  & docker compose version *> $null
  return ($LASTEXITCODE -eq 0)
}

function Get-DockerEngineState {
  $ErrorActionPreference = 'Continue'
  $lines = @(& docker info --format '{{.OSType}}' 2>&1 | ForEach-Object { "$_" })
  $text = ($lines -join "`n").Trim()
  if ($LASTEXITCODE -eq 0) { return @{ Ready = $true; OSType = $text; Error = '' } }
  return @{ Ready = $false; OSType = ''; Error = $text }
}

function Test-Wsl {
  $ErrorActionPreference = 'Continue'
  if (-not (Get-Command wsl -ErrorAction SilentlyContinue)) { return $false }
  & wsl --status *> $null
  return ($LASTEXITCODE -eq 0)
}

function Test-Elevated {
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  return ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Docker Desktop's Linux engine runs on WSL 2. Returns $true when Windows must
# restart before WSL works.
function Install-Wsl {
  $ErrorActionPreference = 'Continue'
  $wsl = Get-Command wsl -ErrorAction SilentlyContinue
  if (-not $wsl) {
    Write-Host '[openmono] wsl.exe not found; Docker Desktop will try to enable WSL 2 itself.' -ForegroundColor Yellow
    return $true
  }
  Write-Host '[openmono] WSL 2 is not set up. Enabling it (wsl --install --no-distribution); approve the administrator prompt if asked.'
  if (Test-Elevated) {
    & $wsl.Source --install --no-distribution | Out-Host
  } else {
    Start-Process -FilePath $wsl.Source -ArgumentList '--install', '--no-distribution' -Verb RunAs -Wait | Out-Null
  }
  return (-not (Test-Wsl))
}

# Returns $true when Windows must restart before Docker Desktop can run.
function Install-DockerDesktop {
  $ErrorActionPreference = 'Continue'
  if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw 'Docker is not installed and winget is not available. Install App Installer from the Microsoft Store (or Docker Desktop from https://www.docker.com/products/docker-desktop/), then re-run: .\win\openmono.ps1 setup'
  }
  $reboot = $false
  if (-not (Test-Wsl)) { $reboot = Install-Wsl }

  Write-Host '[openmono] Docker not found. Installing Docker Desktop with winget; approve the administrator prompt if asked...'
  & winget install --id $DockerWingetId -e --accept-source-agreements --accept-package-agreements | Out-Host
  $code = $LASTEXITCODE
  if ($WingetRebootRequired -contains $code) {
    Write-Host '[openmono] Docker Desktop installed; Windows must restart to finish.' -ForegroundColor Yellow
    return $true
  }
  if ($code -ne 0 -and $WingetAlreadyInstalled -notcontains $code) {
    throw "winget could not install Docker Desktop (exit $code). Install it from https://www.docker.com/products/docker-desktop/, then re-run: .\win\openmono.ps1 setup"
  }
  Write-Host '[openmono] Docker Desktop installed.' -ForegroundColor Green
  return $reboot
}

function Start-DockerDesktop {
  $ErrorActionPreference = 'Continue'
  if (Get-Process -Name 'Docker Desktop' -ErrorAction SilentlyContinue) {
    Write-Host '[openmono] Docker Desktop is already running; waiting for its engine...'
    return $true
  }
  $exe = Join-Path (Get-DockerDesktopDir) 'Docker Desktop.exe'
  if (Test-Path $exe) {
    Write-Host '[openmono] Starting Docker Desktop...'
    Start-Process -FilePath $exe | Out-Null
    return $true
  }
  # Docker Desktop 4.37 and later can be started from its CLI wherever it lives.
  & docker desktop start *> $null
  return ($LASTEXITCODE -eq 0)
}

function Switch-DockerLinuxEngine {
  $cli = Join-Path (Get-DockerDesktopDir) 'DockerCli.exe'
  if (-not (Test-Path $cli)) { return $false }
  Write-Host '[openmono] Docker Desktop is in Windows containers mode; switching to Linux containers...'
  & $cli -SwitchLinuxEngine | Out-Host
  return $true
}

# Polls until the engine answers with a Linux daemon, or the time runs out.
function Wait-DockerEngine([int]$Seconds) {
  Write-Host "[openmono] Waiting for the Docker engine (up to $Seconds s)" -NoNewline
  $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
  while ($true) {
    $state = Get-DockerEngineState
    if ($state.Ready -and $state.OSType -ne 'windows') { Write-Host ''; return $state }
    if ([DateTime]::UtcNow -ge $deadline) { Write-Host ''; return $state }
    Write-Host '.' -NoNewline
    Start-Sleep -Seconds 2
  }
}

function Write-DockerNextSteps([string[]]$Steps, [string]$Rerun) {
  Write-Host ''
  Write-Host '[openmono] Docker needs a few steps only you can finish:' -ForegroundColor Yellow
  $n = 1
  foreach ($step in $Steps) {
    Write-Host "  $n. $step"
    $n++
  }
  Write-Host "  $n. Then re-run: $Rerun"
  Write-Host ''
}

<#
  Makes sure the docker CLI, docker compose, and a running Linux engine are
  available. With -Install (setup), installs Docker Desktop when missing.
  Throws after printing next steps when something only the user can do is left.
#>
function Confirm-DockerReady {
  param(
    [switch]$Install,
    [string]$Rerun = '.\win\openmono.ps1 setup'
  )
  $freshInstall = $false
  $reboot = $false
  $steps = New-Object System.Collections.Generic.List[string]

  if (-not (Find-DockerCli)) {
    if (-not $Install) {
      throw 'Docker is not installed. Run: .\win\openmono.ps1 setup (installs Docker Desktop with winget)'
    }
    $reboot = Install-DockerDesktop
    $freshInstall = $true
    if (-not (Find-DockerCli)) {
      if ($reboot) { $steps.Add('Restart Windows to finish installing WSL 2 and Docker Desktop.') }
      $steps.Add('Open a new PowerShell window so PATH includes docker.')
      Write-DockerNextSteps $steps $Rerun
      throw 'Docker Desktop was installed but docker.exe is not reachable from this session yet.'
    }
  }

  $ErrorActionPreference = 'Continue'
  $version = (& docker --version 2>$null | Select-Object -First 1)
  $ErrorActionPreference = 'Stop'
  Write-Host "[openmono] Docker CLI: $version"

  if (-not (Test-DockerCompose)) {
    throw 'docker compose is not available. It ships with Docker Desktop; repair or reinstall it (winget install Docker.DockerDesktop), then re-run.'
  }

  if ($reboot) {
    $steps.Add('Restart Windows to finish installing WSL 2 and Docker Desktop.')
    $steps.Add('Start Docker Desktop from the Start menu and accept the Docker Subscription Service Agreement (first launch only).')
    Write-DockerNextSteps $steps $Rerun
    throw 'Docker Desktop is installed; Windows needs a restart before its engine can run.'
  }

  $state = Get-DockerEngineState
  $started = $false
  if (-not $state.Ready) {
    Write-Host '[openmono] Docker engine is not running.' -ForegroundColor Yellow
    $started = Start-DockerDesktop
    if ($started) { $state = Wait-DockerEngine (Get-DockerWaitSeconds) }
  }
  if ($state.Ready -and $state.OSType -eq 'windows') {
    if (Switch-DockerLinuxEngine) { $state = Wait-DockerEngine (Get-DockerWaitSeconds) }
  }

  if ($state.Ready -and $state.OSType -ne 'windows') {
    Write-Host '[openmono] Docker engine is running.' -ForegroundColor Green
    return
  }

  if ($state.Ready) {
    $steps.Add('Switch Docker Desktop to Linux containers: right-click its tray icon and choose "Switch to Linux containers".')
  } else {
    if ($state.Error) { Write-Host "[openmono] docker info: $($state.Error)" -ForegroundColor DarkGray }
    if (-not $started) { $steps.Add('Start Docker Desktop from the Start menu.') }
    if ($freshInstall) {
      $steps.Add('In the Docker Desktop window, accept the Docker Subscription Service Agreement (first launch only).')
      $steps.Add('If Docker Desktop asks you to sign out (docker-users group), sign out and back in.')
    }
    if ($state.Error -match 'Access is denied|permission denied') {
      $steps.Add("Your account cannot reach the Docker engine. Sign out and back in; if that does not help, run in an administrator terminal: net localgroup docker-users `"$env:USERNAME`" /add")
    }
    if ($state.Error -match 'WSL|wsl') {
      $steps.Add('Update WSL in an administrator terminal: wsl --update, then restart Docker Desktop.')
    }
    $steps.Add('Wait until Docker Desktop shows "Engine running".')
  }
  Write-DockerNextSteps $steps $Rerun
  throw 'The Docker engine is not ready.'
}
