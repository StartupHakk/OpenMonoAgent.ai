<#
.SYNOPSIS
  Windows CI smoke test: drives win/openmono.ps1 end to end with fake tools.

.DESCRIPTION
  Builds FakeTool and stages it as foundry.exe, docker.exe, nvidia-smi.exe,
  winget.exe, and wsl.exe ahead of everything else on PATH (any real
  docker.exe directory is removed from PATH), starts a fake Foundry HTTP
  server on 127.0.0.1:7474, then runs four scenarios under the requested
  shell:

    docker-missing  `setup` with no Docker and no WSL: the model ladder is
                    checked against the (fake) Foundry catalog API, both
                    pages; WSL is enabled, Docker Desktop is installed with
                    winget and started, the engine is waited for, and the
                    agent image is built.
    docker-stopped  `setup` with Docker installed but the engine stopped:
                    nothing is installed, Docker Desktop is started and
                    waited for.
    agent           `start`, `agent`, `status`, `stop` with the engine stopped
                    at first. Asserts the docker invocation of the agent
                    launch: argument quoting (embedded quotes, a trailing
                    backslash, a workspace path with a space), the ACP
                    publish, and the compose variables.
    cpu-only        `start` with no NVIDIA GPU: qwen3.5-2b is served as its
                    generic CPU variant from the (fake) CPU catalog, and no
                    larger model is ever loaded.

  No GPU, Docker engine, WSL, or Foundry install is needed.
#>
param(
  [ValidateSet('pwsh', 'powershell')]
  [string]$Shell = 'pwsh'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$work = Join-Path $temp "omw-smoke-$Shell"
$toolsBin = Join-Path $work 'tools'
$dockerBin = Join-Path $work 'docker-cli'
$desktopDir = Join-Path $work 'docker-desktop'
$project = Join-Path $work 'my proj'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $toolsBin, $dockerBin, $desktopDir, $project | Out-Null

$fakeProject = Join-Path $root 'win\tests\OpenMono.Win.FakeTools\OpenMono.Win.FakeTools.csproj'
$fakeOut = Join-Path $work 'faketool'
& dotnet build $fakeProject -c Release --nologo -v quiet -o $fakeOut
if ($LASTEXITCODE -ne 0) { throw 'FakeTool build failed.' }

function Install-Fake([string]$Dir, [string[]]$Names) {
  Copy-Item (Join-Path $fakeOut '*') $Dir -Recurse -Force
  foreach ($n in $Names) { Copy-Item (Join-Path $fakeOut 'FakeTool.exe') (Join-Path $Dir "$n.exe") }
}
Install-Fake $toolsBin @('foundry', 'nvidia-smi', 'winget', 'wsl')
Install-Fake $dockerBin @('docker')
Install-Fake $desktopDir @('Docker Desktop')

$cleanPath = (@($env:PATH -split ';') | Where-Object { $_ -and -not (Test-Path (Join-Path $_ 'docker.exe')) }) -join ';'
$env:FAKE_DOCKER_OUT = Join-Path $work 'docker.json'
$env:OPENMONO_DOCKER_WAIT_SECONDS = '60'
$env:OPENMONO_FOUNDRY_CATALOG_URL = 'http://127.0.0.1:7474/api/models'
$env:FAKE_API_LOG = Join-Path $work 'catalog-api.log'
$env:FAKE_SERVED = Join-Path $work 'served-model.txt'
Remove-Item (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.openmono\win\foundry-catalog-*.json') -ErrorAction SilentlyContinue
$exe = if ($Shell -eq 'pwsh') { 'pwsh' } else { 'powershell.exe' }
$launcher = Join-Path $root 'win\openmono.ps1'
$state = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.openmono\win\inference.json'
$failures = New-Object System.Collections.Generic.List[string]

function Invoke-Scenario([string]$Name, [string]$Body, [hashtable]$Environment) {
  $log = Join-Path $work "calls-$Name.log"
  $env:FAKE_LOG = $log
  foreach ($key in $Environment.Keys) { [Environment]::SetEnvironmentVariable($key, $Environment[$key], 'Process') }
  Remove-Item $state -ErrorAction SilentlyContinue
  $wrapper = Join-Path $work "run-$Name.ps1"
  Set-Content -Path $wrapper -Encoding UTF8 -Value $Body
  Write-Host "=== scenario $Name ($Shell) ==="
  Push-Location $project
  try {
    & $exe -NoProfile -ExecutionPolicy Bypass -File $wrapper | Out-Host
    $code = $LASTEXITCODE
  } finally {
    Pop-Location
  }
  $calls = @()
  if (Test-Path $log) { $calls = @(Get-Content $log) }
  if ($code -ne 0) { $failures.Add("[$Name] exited $code") }
  return , $calls
}

function Assert-Called([string]$Name, [object[]]$Calls, [string]$Pattern) {
  if (-not ($Calls | Where-Object { $_ -like $Pattern })) { $failures.Add("[$Name] expected a call like [$Pattern]") }
}

function Assert-NotCalled([string]$Name, [object[]]$Calls, [string]$Pattern) {
  if ($Calls | Where-Object { $_ -like $Pattern }) { $failures.Add("[$Name] unexpected call like [$Pattern]") }
}

$server = Start-Process -FilePath (Join-Path $toolsBin 'FakeTool.exe') -ArgumentList 'server', '7474' -PassThru -WindowStyle Hidden
try {
  $up = $false
  for ($i = 0; $i -lt 50 -and -not $up; $i++) {
    try { Invoke-RestMethod -Uri 'http://127.0.0.1:7474/v1/models' -TimeoutSec 2 | Out-Null; $up = $true } catch { Start-Sleep -Milliseconds 200 }
  }
  if (-not $up) { throw 'Fake Foundry server did not come up.' }

  $setupBody = @"
& '$launcher' setup
exit `$LASTEXITCODE
"@

  # Docker absent: no docker.exe anywhere on PATH, an empty install directory,
  # and WSL not enabled yet.
  $missingDir = Join-Path $work 'docker-desktop-fresh'
  $calls = Invoke-Scenario 'docker-missing' $setupBody @{
    PATH = "$toolsBin;$cleanPath"
    OPENMONO_DOCKER_DESKTOP_DIR = $missingDir
    FAKE_DOCKER_ENGINE = (Join-Path $work 'engine-missing')
    FAKE_WSL_STATE = (Join-Path $work 'wsl-installed')
  }
  Assert-Called 'docker-missing' $calls 'wsl --install --no-distribution*'
  Assert-Called 'docker-missing' $calls 'winget install --id Docker.DockerDesktop -e --accept-source-agreements --accept-package-agreements*'
  Assert-Called 'docker-missing' $calls 'docker desktop*'
  Assert-Called 'docker-missing' $calls 'docker compose version*'
  Assert-Called 'docker-missing' $calls 'docker compose -f * build agent*'
  if (@($calls | Where-Object { $_ -like 'docker info*' }).Count -lt 2) { $failures.Add('[docker-missing] engine was not re-checked after starting Docker Desktop') }
  $api = @()
  if (Test-Path $env:FAKE_API_LOG) { $api = @(Get-Content $env:FAKE_API_LOG) }
  if (-not ($api | Where-Object { $_ -like '*CUDAExecutionProvider*' })) { $failures.Add('[docker-missing] setup never queried the Foundry catalog API') }
  if (-not ($api | Where-Object { $_ -like '*"continuationToken":"fake-page-2"*' })) { $failures.Add('[docker-missing] setup did not follow the catalog continuationToken') }

  # Docker installed, engine stopped: start and wait, never reinstall.
  $calls = Invoke-Scenario 'docker-stopped' $setupBody @{
    PATH = "$dockerBin;$toolsBin;$cleanPath"
    OPENMONO_DOCKER_DESKTOP_DIR = $desktopDir
    FAKE_DOCKER_ENGINE = (Join-Path $work 'engine-stopped')
    FAKE_WSL_STATE = $null
  }
  Assert-NotCalled 'docker-stopped' $calls 'winget install --id Docker.DockerDesktop*'
  Assert-NotCalled 'docker-stopped' $calls 'wsl --install*'
  Assert-Called 'docker-stopped' $calls 'docker desktop*'
  Assert-Called 'docker-stopped' $calls 'docker compose -f * build agent*'

  # The tricky arguments live inside the wrapper so the outer shell hop
  # carries none; what is under test is openmono.ps1 -> docker.exe.
  $agentBody = @"
& '$launcher' start
if (`$LASTEXITCODE -ne 0) { exit 10 }
& '$launcher' agent --acp-only -p 'say "hi" now' 'C:\my dir\'
if (`$LASTEXITCODE -ne 0) { exit 11 }
& '$launcher' status
if (`$LASTEXITCODE -ne 0) { exit 12 }
& '$launcher' stop
if (`$LASTEXITCODE -ne 0) { exit 13 }
exit 0
"@
  Remove-Item $env:FAKE_DOCKER_OUT -ErrorAction SilentlyContinue
  $calls = Invoke-Scenario 'agent' $agentBody @{
    PATH = "$dockerBin;$toolsBin;$cleanPath"
    OPENMONO_DOCKER_DESKTOP_DIR = $desktopDir
    FAKE_DOCKER_ENGINE = (Join-Path $work 'engine-agent')
    FAKE_WSL_STATE = $null
  }
  Assert-Called 'agent' $calls 'docker desktop*'
  Assert-Called 'agent' $calls 'foundry --version*'
  Assert-NotCalled 'agent' $calls 'foundry server stop*'
  Assert-NotCalled 'agent' $calls 'winget *'

  $argv = @()
  if (-not (Test-Path $env:FAKE_DOCKER_OUT)) {
    $failures.Add('[agent] openmono.ps1 agent never ran docker compose run')
  } else {
    $run = Get-Content $env:FAKE_DOCKER_OUT -Raw | ConvertFrom-Json
    $argv = @($run.args)
    $tail = $argv[($argv.Count - 5)..($argv.Count - 1)]
    $expectedTail = @('agent', '--acp-only', '-p', 'say "hi" now', 'C:\my dir\')
    for ($i = 0; $i -lt 5; $i++) {
      if ($tail[$i] -cne $expectedTail[$i]) { $failures.Add("[agent] agent arg $i was [$($tail[$i])], expected [$($expectedTail[$i])]") }
    }
    foreach ($want in @('run', '--rm', '127.0.0.1:7475:7475', 'HOST_ACP_PORT=7475', "HOST_WORKSPACE_PATH=$project", 'OPENMONO_MODEL=qwen3.8-27b-cuda-gpu:1')) {
      if ($argv -notcontains $want) { $failures.Add("[agent] docker args missing [$want]") }
    }
    if ($run.env.OPENMONO_ENDPOINT -ne 'http://host.docker.internal:7474') { $failures.Add("[agent] OPENMONO_ENDPOINT was [$($run.env.OPENMONO_ENDPOINT)]") }
    if ($run.env.WORKSPACE -ne ($project -replace '\\', '/')) { $failures.Add("[agent] WORKSPACE was [$($run.env.WORKSPACE)]") }
    if ($run.env.CTX_SIZE -ne '65536') { $failures.Add("[agent] CTX_SIZE was [$($run.env.CTX_SIZE)]") }
    if ($run.env.OPENMONO_VISION_ENABLED -ne '0') { $failures.Add("[agent] OPENMONO_VISION_ENABLED was [$($run.env.OPENMONO_VISION_ENABLED)]") }
  }

  # No NVIDIA GPU: the CPU rung only, loaded by its catalog variant id.
  $cpuBody = @"
& '$launcher' start
exit `$LASTEXITCODE
"@
  $calls = Invoke-Scenario 'cpu-only' $cpuBody @{
    PATH = "$dockerBin;$toolsBin;$cleanPath"
    OPENMONO_DOCKER_DESKTOP_DIR = $desktopDir
    FAKE_DOCKER_ENGINE = $null
    FAKE_WSL_STATE = $null
    FAKE_NO_GPU = '1'
  }
  Assert-Called 'cpu-only' $calls 'foundry model load qwen3.5-2b-generic-cpu:3*'
  foreach ($big in @('qwen3.8-27b', 'qwen3.5-9b', 'qwen3.5-4b')) { Assert-NotCalled 'cpu-only' $calls "foundry model load $big*" }
  if (-not (Test-Path $state)) {
    $failures.Add('[cpu-only] start wrote no inference state')
  } else {
    $inference = Get-Content $state -Raw | ConvertFrom-Json
    if ($inference.model_alias -ne 'qwen3.5-2b') { $failures.Add("[cpu-only] model_alias was [$($inference.model_alias)]") }
    if ($inference.served_model_id -ne 'qwen3.5-2b-generic-cpu:3') { $failures.Add("[cpu-only] served_model_id was [$($inference.served_model_id)]") }
  }

  if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    if ($argv.Count -gt 0) { Write-Host 'docker argv:'; $argv | ForEach-Object { Write-Host "  [$_]" } }
    Get-ChildItem $work -Filter 'calls-*.log' | ForEach-Object { Write-Host "--- $($_.Name)"; Get-Content $_.FullName | ForEach-Object { Write-Host "  $_" } }
    exit 1
  }
  Write-Host "Windows smoke test passed under $Shell (docker-missing, docker-stopped, agent, cpu-only)."
} finally {
  if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
