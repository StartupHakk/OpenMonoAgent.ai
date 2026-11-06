<#
.SYNOPSIS
  Starts the OpenMono Windows stack: Foundry Local inference plus the Linux agent container.

.DESCRIPTION
  Host-side only. The agent itself stays in the Linux container (bash inside);
  this script manages the Windows host side in PowerShell:
    1. Verifies Docker Desktop is reachable.
    2. Verifies Foundry Local serves on http://127.0.0.1:7474 (adopt existing).
    3. Starts the agent container with OPENMONO_ENDPOINT mapped to
       http://host.docker.internal:7474 and ACP published on 127.0.0.1:7475.
  API keys travel in the child process environment, never on the command line.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

param(
  [string]$WorkDir = (Get-Location).Path,
  [string]$ComposeFile = (Join-Path $PSScriptRoot '..' '..' 'docker' 'docker-compose.yml'),
  [int]$AcpPort = 7475,
  [string]$Model = 'qwen3.8-27b',
  [string]$FoundryEndpoint = 'http://127.0.0.1:7474'
)

function Invoke-Docker($Arguments) {
  $proc = Start-Process -FilePath 'docker' -ArgumentList $Arguments -NoNewWindow -Wait -PassThru
  return $proc.ExitCode
}

Write-Host '[openmono] Checking Docker Desktop...'
& docker info 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
  Write-Error 'Docker Desktop is not reachable. Start Docker Desktop, then re-run this script.'
}

Write-Host '[openmono] Checking Foundry Local...'
try {
  $models = Invoke-RestMethod -Uri "$FoundryEndpoint/v1/models" -TimeoutSec 10
  Write-Host '[openmono] Foundry Local is serving. Adopting the existing server.'
} catch {
  Write-Error "Foundry Local is not serving at $FoundryEndpoint. Run: foundry model load $Model, then start the server (foundry server status for the URL)."
}

$containerEndpoint = $FoundryEndpoint -replace '127\.0\.0\.1|localhost', 'host.docker.internal'
Write-Host "[openmono] Container inference endpoint: $containerEndpoint"

$homeDir = [Environment]::GetFolderPath('UserProfile')
$envArgs = @(
  '-e', 'HOME=/home/agent',
  '-e', "WORKSPACE=$WorkDir",
  '-e', "OPENMONO_ENDPOINT=$containerEndpoint",
  '-e', "OPENMONO_MODEL=$Model",
  '-e', 'OPENMONO_HOST_OS=Windows',
  '-e', 'GIT_TERMINAL_PROMPT=0',
  '-e', "HOST_ACP_PORT=$AcpPort"
)

if ($env:OPENMONO_VISION_ENABLED) { $envArgs += @('-e', 'OPENMONO_VISION_ENABLED') }
if ($env:OPENMONO_API_KEY) { $envArgs += @('-e', 'OPENMONO_API_KEY') }

Write-Host '[openmono] Starting the agent container...'
$exit = Invoke-Docker (@(
  'compose', '-f', $ComposeFile, 'run', '-d', '--name', 'openmono-agent',
  '-p', "127.0.0.1:${AcpPort}:7475",
  '-v', "${WorkDir}:/workspace",
  '-v', "${homeDir}/.openmono:/home/agent/.openmono"
  ) + $envArgs + @('agent', '--acp-only', '--acp-port', '7475'))
if ($exit -ne 0) {
  Write-Error 'docker compose run failed. Is the Docker daemon running and the compose file present?'
}

Write-Host "[openmono] Agent ACP: http://127.0.0.1:${AcpPort} (discovery: http://127.0.0.1:${AcpPort}/api/v1/discovery)"
Write-Host '[openmono] Point the VS Code extension (StartupHakk.openmono-agent) at that URL.'
