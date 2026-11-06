<#
.SYNOPSIS
  OpenMono on Windows: host controls for the same OpenMono stack.

.DESCRIPTION
  The Windows counterpart of the bash `openmono` launcher. Same subcommands,
  same agent: the agent runs in its Linux Docker container exactly as on
  Mac/Linux. The one platform difference is inference: Foundry Local on the
  Windows host (127.0.0.1:7474) instead of llama.cpp, reached from the
  container through host.docker.internal.

  Commands:
    setup                  Install prerequisites (Foundry Local, Docker Desktop), build
                             the agent image, start inference
    start                  Start (or adopt) Foundry Local and load the model
    stop [flags]           Stop inference (default). Flags: --agent, --all, --force
    status                 Inference and agent container status
    agent [args]           Run the agent in its container (TUI by default)
                             --acp-only     run as an ACP server for the VS Code extension
                             --vision       session on a Foundry vision model (image input)
                             any other args are passed to the agent unchanged
    logs                   Follow the Foundry Local daemon logs
    doctor                 Check the machine (Docker, Foundry, GPU, CUDA, routes, ACP)
    diag [--out FILE.zip]  Write a redacted diagnostics bundle for bug reports
    help                   This text

  Runs on PowerShell 7 (pwsh.exe) or Windows PowerShell 5.1 (powershell.exe).
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$WinDir = $PSScriptRoot
$RepoDir = Split-Path -Parent $WinDir
$DockerDir = Join-Path $RepoDir 'docker'
$ComposeFile = Join-Path $DockerDir 'docker-compose.yml'
$LauncherProject = Join-Path (Join-Path (Join-Path $WinDir 'src') 'OpenMono.Win.Launcher') 'OpenMono.Win.Launcher.csproj'
$LauncherDll = Join-Path (Join-Path (Join-Path (Split-Path -Parent $LauncherProject) 'bin') 'Release') 'net10.0'
$LauncherDll = Join-Path $LauncherDll 'openmono-win.dll'
$UserHome = [Environment]::GetFolderPath('UserProfile')
$MinDotnet = [Version]'10.0.403'
. (Join-Path (Join-Path $WinDir 'scripts') 'docker.ps1')

function Write-Info([string]$Message) { Write-Host "[openmono] $Message" }
function Write-Ok([string]$Message) { Write-Host "[openmono] $Message" -ForegroundColor Green }
function Write-Warn([string]$Message) { Write-Host "[openmono] $Message" -ForegroundColor Yellow }
function Write-Err([string]$Message) { Write-Host "[openmono] $Message" -ForegroundColor Red }

function Test-Command([string]$Name) {
  return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

# Windows PowerShell 5.1 passes native arguments without escaping embedded
# quotes or trailing backslashes; PowerShell 7.3+ does it correctly.
function ConvertTo-NativeArgument([string]$Value) {
  if ($PSVersionTable.PSVersion.Major -ge 7) { return $Value }
  $escaped = [regex]::Replace($Value, '(\\*)"', { param($m) ($m.Groups[1].Value * 2) + '\"' })
  if ($escaped -match '\s' -and $escaped.EndsWith('\')) {
    $escaped = [regex]::Replace($escaped, '(\\+)$', { param($m) $m.Groups[1].Value * 2 })
  }
  return $escaped
}

function Assert-Dotnet {
  if (-not (Test-Command 'dotnet')) {
    throw ".NET SDK $MinDotnet or newer is required (same as install.ps1). Install .NET 10 from https://dotnet.microsoft.com/download"
  }
  $raw = (& dotnet --version) | Select-Object -First 1
  $parsed = $null
  if (-not [Version]::TryParse(($raw -replace '-.*$', ''), [ref]$parsed) -or $parsed -lt $MinDotnet) {
    throw "OpenMono needs .NET SDK $MinDotnet or newer. Found '$raw'."
  }
}

function Build-Launcher {
  Assert-Dotnet
  Write-Info 'Building openmono-win (host launcher)...'
  & dotnet build $LauncherProject -c Release --nologo -v quiet | Out-Host
  if ($LASTEXITCODE -ne 0) { throw 'openmono-win build failed.' }
}

function Invoke-Launcher {
  if (-not (Test-Path $LauncherDll)) { Build-Launcher }
  $native = @($args | ForEach-Object { ConvertTo-NativeArgument ([string]$_) })
  & dotnet $LauncherDll @native
}

# Like the bash launcher's _ensure_docker: start the engine and wait rather
# than fail. Installing Docker is left to setup.
function Assert-Docker {
  $rerun = '.\win\openmono.ps1 ' + $command
  Confirm-DockerReady -Rerun $rerun
}

# Host directories the compose file bind-mounts. Created up front so Docker
# never creates them itself (as directories owned by the VM), and so
# host-bridge.json stays a file, as the bash launcher guarantees.
function Initialize-HostDirs {
  $openmono = Join-Path $UserHome '.openmono'
  New-Item -ItemType Directory -Force -Path $openmono | Out-Null
  New-Item -ItemType Directory -Force -Path (Join-Path $UserHome '.code-review-graph') | Out-Null
  $bridge = Join-Path $openmono 'host-bridge.json'
  if (-not (Test-Path $bridge)) { New-Item -ItemType File -Path $bridge | Out-Null }
}

function Invoke-Setup {
  Write-Info 'Setting up OpenMono for Windows (Foundry Local inference, Linux agent container).'
  Assert-Dotnet
  Build-Launcher
  & (Join-Path (Join-Path $WinDir 'scripts') 'bootstrap.ps1')
  Assert-Docker
  Initialize-HostDirs
  Write-Info 'Building the agent image (docker compose build agent)...'
  & docker compose -f $ComposeFile build agent
  if ($LASTEXITCODE -ne 0) { throw 'Agent image build failed.' }
  Invoke-Start
  Write-Host ''
  Invoke-Launcher doctor
  Write-Host ''
  Write-Ok 'Setup complete. Run: .\win\openmono.ps1 agent'
}

function Invoke-Start {
  Write-Info 'Starting inference (Foundry Local)...'
  Invoke-Launcher inference up
  if ($LASTEXITCODE -ne 0) { throw 'Inference did not start. Run: .\win\openmono.ps1 doctor' }
}

function Invoke-Stop([string[]]$Flags) {
  $stopInference = $true
  $stopAgent = $false
  $force = $false
  foreach ($flag in $Flags) {
    switch ($flag) {
      '--inference' { }
      '--llama' { }
      '--agent' { $stopAgent = $true; $stopInference = $false }
      '--all' { $stopAgent = $true; $stopInference = $true }
      '--force' { $force = $true }
      default { throw "Unknown flag for stop: $flag (use --inference, --agent, --all, --force)" }
    }
  }
  if ($Flags -contains '--inference' -or $Flags -contains '--llama') { $stopInference = $true }

  if ($stopInference) {
    if ($force) { Invoke-Launcher inference down --force } else { Invoke-Launcher inference down }
  }

  # Live agent sessions are only removed on request, as on Linux: stopping
  # them would kill someone's running `openmono agent` in another terminal.
  if ($stopAgent) {
    Assert-Docker
    $ids = @(& docker ps -aq --filter 'name=^openmono-agent$')
    $ids += @(& docker ps -aq --filter 'label=com.docker.compose.service=agent' --filter 'label=com.docker.compose.oneoff=True')
    $ids = @($ids | Where-Object { $_ } | Select-Object -Unique)
    if ($ids.Count -gt 0) {
      & docker rm -f @ids | Out-Null
      Write-Ok "Removed $($ids.Count) agent container(s)."
    } else {
      Write-Info 'No agent containers to remove.'
    }
  }
}

function Invoke-Status {
  Invoke-Launcher inference status
  if (Test-Command 'docker') {
    & docker ps --filter 'label=com.docker.compose.service=agent' --format 'agent container: {{.Names}}  {{.Status}}  {{.Ports}}'
  }
}

function Invoke-Agent([string[]]$AgentArgs) {
  Assert-Docker
  Initialize-HostDirs

  $planArgs = @('agent-plan', '--workdir', (Get-Location).Path, '--compose', $ComposeFile)
  $forward = New-Object System.Collections.Generic.List[string]
  for ($i = 0; $i -lt $AgentArgs.Count; $i++) {
    $a = $AgentArgs[$i]
    if ($a -eq '--vision') { $planArgs += '--vision'; continue }
    if ($a -eq '--host') { throw 'Host mode (--host) is not available on Windows yet; the agent runs sandboxed.' }
    if ($a -eq '--sandbox') { continue }
    $forward.Add($a)
  }
  if ($env:OPENMONO_NO_GIT_AUTH -eq '1') { $planArgs += '--no-git-auth' }

  $env:OPENMONO_WIN_AGENT_ARGS = ConvertTo-Json -InputObject @($forward.ToArray()) -Compress
  try {
    $json = (Invoke-Launcher @planArgs) | Out-String
    $planExit = $LASTEXITCODE
  } finally {
    Remove-Item Env:OPENMONO_WIN_AGENT_ARGS -ErrorAction SilentlyContinue
  }
  if ($planExit -ne 0) { throw 'Could not prepare the agent run. Run: .\win\openmono.ps1 doctor' }

  $plan = $json | ConvertFrom-Json
  foreach ($note in @($plan.notes)) { if ($note) { Write-Warn $note } }
  if ($plan.acp_port) {
    Write-Info "ACP on http://127.0.0.1:$($plan.acp_port) (VS Code extension: OpenMono Agent panel)."
  }

  # Compose interpolation variables, set for this process only, the way the
  # bash launcher exports them before `docker compose run`.
  $saved = @{}
  foreach ($p in $plan.environment.PSObject.Properties) {
    $saved[$p.Name] = [Environment]::GetEnvironmentVariable($p.Name, 'Process')
    [Environment]::SetEnvironmentVariable($p.Name, [string]$p.Value, 'Process')
  }
  try {
    $dockerArgs = @($plan.docker_args | ForEach-Object { ConvertTo-NativeArgument ([string]$_) })
    & docker @dockerArgs
    $code = $LASTEXITCODE
  } finally {
    foreach ($name in $saved.Keys) {
      [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
    }
  }
  exit $code
}

function Invoke-Logs {
  if (-not (Test-Command 'foundry')) { throw 'Foundry Local CLI not found. Run: .\win\openmono.ps1 setup' }
  & foundry server logs --follow
}

function Show-Help {
  $help = Get-Help $PSCommandPath -Full
  Write-Host ($help.description | Out-String).Trim()
}

$command = 'help'
$rest = @()
if ($args.Count -gt 0) {
  $command = [string]$args[0]
  if ($args.Count -gt 1) { $rest = @($args[1..($args.Count - 1)] | ForEach-Object { [string]$_ }) }
}

try {
  switch ($command) {
    'setup' { Invoke-Setup; exit 0 }
    'start' { Invoke-Start }
    'stop' { Invoke-Stop $rest }
    'status' { Invoke-Status }
    'agent' { Invoke-Agent $rest }
    'logs' { Invoke-Logs }
    'doctor' { Invoke-Launcher doctor --workdir (Get-Location).Path; exit $LASTEXITCODE }
    'diag' { Invoke-Launcher diag --workdir (Get-Location).Path @rest; exit $LASTEXITCODE }
    'help' { Show-Help }
    '--help' { Show-Help }
    '-h' { Show-Help }
    default { Write-Err "Unknown command: $command"; Show-Help; exit 2 }
  }
} catch {
  Write-Err $_.Exception.Message
  exit 1
}
