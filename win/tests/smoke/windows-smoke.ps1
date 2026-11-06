<#
.SYNOPSIS
  Windows CI smoke test: drives win/openmono.ps1 end to end with fake tools.

.DESCRIPTION
  Builds FakeTool and stages it as docker.exe, nvidia-smi.exe, winget.exe,
  and wsl.exe ahead of everything else on PATH (any real docker.exe
  directory is removed from PATH), packs it as llama-server.exe into fake
  llama.cpp Windows release zips, starts a fake download server on
  127.0.0.1:7480 (llama.cpp release feed and zips, GGUF files, installers),
  then runs these scenarios under the requested shell:

    docker-missing  `setup` with no Docker and no WSL: WSL is enabled, Docker
                    Desktop is installed with winget and started, the engine
                    is waited for, and the agent image is built; llama.cpp
                    (the CUDA build for the fake driver's CUDA 12.8) and the
                    24 GB rung's GGUF and projector are downloaded and
                    llama-server is started on 127.0.0.1:7474.
    docker-stopped  `setup` with Docker installed but the engine stopped:
                    nothing is installed, Docker Desktop is started and
                    waited for. Git and the .NET SDK are present, so neither
                    is installed.
    prereqs-winget  `setup` with no git and no dotnet on PATH: winget
                    installs Git.Git and Microsoft.DotNet.SDK.10 (they land
                    only on the persisted PATH, as with a real installer),
                    the session PATH is refreshed, and setup carries on to
                    the launcher build and the agent image.
    prereqs-fallback  The same without winget: MinGit from the (fake) Git for
                    Windows release and dotnet-install.ps1 -Channel 10.0.
    agent           `start`, `agent`, `status`, `stop` with the engine stopped
                    at first. Asserts the docker invocation of the agent
                    launch: argument quoting (embedded quotes, a trailing
                    backslash, a workspace path with a space), the ACP
                    publish, and the compose variables (model alias, vision
                    context), and that `stop` ends llama-server.
    cpu-only        `start` with no NVIDIA GPU: the llama.cpp CPU build serves
                    Qwen3.5-2B, and no larger model is ever downloaded.
    foreign-server  `start` while another llama-server, serving a model of
                    its own, holds 127.0.0.1:7474: start fails and says so,
                    and no second server is started.
    foreign-adopt   `start` while another llama-server already serves this
                    machine's model: it is adopted, and neither `stop` nor
                    `stop --force` ends it.
    cuda-crash      `start` when the CUDA build exits while loading: the
                    Vulkan build is installed and serves the same model, and
                    the output says why.
    cuda-no-device  `start` when the CUDA build lists no CUDA device (its
                    ggml-cuda.dll did not load): it is never started and
                    Vulkan serves the model.
    no-nvidia-smi   `start` with no nvidia-smi.exe on PATH: no NVIDIA GPU is
                    assumed and the CPU build serves the 2B.
    bad-download    `start` with an empty models folder while every GGUF
                    download returns 404: start fails with the URL and the
                    status, no llama-server starts, no model file is left.
    restart         `start` twice with LLAMA_THREADS changed in between: the
                    first llama-server is replaced, never doubled.
    commands        `models`, `models --json`, `status`, `doctor`, `diag`,
                    `help`, and an unknown command.
    agent-crlf      `agent` when the agent image was built from CRLF files
                    (its entrypoint has \r): the image is rebuilt with LF
                    before the container starts.
    line-endings    Repair-ContainerLineEndings on a CRLF copy of docker/:
                    every text file ends up LF, binaries are left alone.

  No GPU, Docker engine, WSL, llama.cpp, or model download is needed.
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
Install-Fake $toolsBin @('nvidia-smi', 'winget', 'wsl')
Install-Fake $dockerBin @('docker')
Install-Fake $desktopDir @('Docker Desktop')
$toolsNoWinget = Join-Path $work 'tools-no-winget'
New-Item -ItemType Directory -Force -Path $toolsNoWinget | Out-Null
Install-Fake $toolsNoWinget @('nvidia-smi', 'wsl')

# The (fake) llama.cpp Windows zips: llama-server.exe at the zip root, as in
# the real release, and a CUDA runtime zip with the DLLs beside it.
$llamaSrc = Join-Path $work 'llama-src'
New-Item -ItemType Directory -Force -Path $llamaSrc | Out-Null
Install-Fake $llamaSrc @('llama-server')
$env:FAKE_LLAMA_ZIP = Join-Path $work 'llama.zip'
Compress-Archive -Path (Join-Path $llamaSrc '*') -DestinationPath $env:FAKE_LLAMA_ZIP -Force
$cudartSrc = Join-Path $work 'cudart-src'
New-Item -ItemType Directory -Force -Path $cudartSrc | Out-Null
Set-Content -Path (Join-Path $cudartSrc 'cudart64_12.dll') -Value 'fake'
$env:FAKE_CUDART_ZIP = Join-Path $work 'cudart.zip'
Compress-Archive -Path (Join-Path $cudartSrc '*') -DestinationPath $env:FAKE_CUDART_ZIP -Force
$foreignBin = Join-Path $work 'foreign-llama'
New-Item -ItemType Directory -Force -Path $foreignBin | Out-Null
Install-Fake $foreignBin @('llama-server')

# The (fake) MinGit release zip: cmd\git.exe, as in the real MinGit layout.
$minGitSrc = Join-Path $work 'mingit-src'
New-Item -ItemType Directory -Force -Path (Join-Path $minGitSrc 'cmd') | Out-Null
Install-Fake (Join-Path $minGitSrc 'cmd') @('git')
$env:FAKE_MINGIT_ZIP = Join-Path $work 'MinGit.zip'
Compress-Archive -Path (Join-Path $minGitSrc '*') -DestinationPath $env:FAKE_MINGIT_ZIP -Force
$realDotnetDir = Split-Path -Parent (Get-Command dotnet).Source

$cleanPath = (@($env:PATH -split ';') | Where-Object { $_ -and -not (Test-Path (Join-Path $_ 'docker.exe')) }) -join ';'
# A machine without Git, the .NET SDK, or winget.
$barePath = (@($cleanPath -split ';') | Where-Object {
    $dir = $_
    -not (@('git.exe', 'dotnet.exe', 'winget.exe') | Where-Object { Test-Path (Join-Path $dir $_) })
  }) -join ';'
$env:FAKE_DOCKER_OUT = Join-Path $work 'docker.json'
$env:OPENMONO_DOCKER_WAIT_SECONDS = '60'
$env:OPENMONO_LLAMA_CPP_RELEASES_URL = 'http://127.0.0.1:7480/llama-releases'
$env:OPENMONO_MODEL_MIRROR = 'http://127.0.0.1:7480'
$env:OPENMONO_LLAMA_CPP_DIR = Join-Path $work 'llama.cpp'
$env:OPENMONO_MODELS_DIR = Join-Path $work 'models'
Remove-Item (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.openmono\win\agent-image-lf.txt') -ErrorAction SilentlyContinue
$exe = if ($Shell -eq 'pwsh') { 'pwsh' } else { 'powershell.exe' }
$launcher = Join-Path $root 'win\openmono.ps1'
$state = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.openmono\win\inference.json'
$failures = New-Object System.Collections.Generic.List[string]

# Every llama-server here is a FakeTool copy under $work; none may outlive a scenario.
function Stop-FakeLlama {
  Get-Process -Name 'llama-server' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($work, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force -ErrorAction SilentlyContinue
}

# The download server keeps the FAKE_LOG it started with; its lines are
# added to each scenario's calls.
$serverLog = Join-Path $work 'server.log'
$serverControl = Join-Path $work 'server-control.txt'

# A wrapper body that runs one openmono.ps1 command expected to fail, prints
# its stdout and stderr (the reason goes to stderr), and exits 0 only if the
# command failed.
function New-ExpectFailBody([string]$Command, [string]$Name, [string]$Before = '') {
  $out = Join-Path $work "$Name.out"
  $err = Join-Path $work "$Name.err"
  return @"
$Before
`$p = Start-Process -FilePath '$exe' -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File "$launcher" $Command' -RedirectStandardOutput '$out' -RedirectStandardError '$err' -NoNewWindow -Wait -PassThru
Get-Content '$out', '$err'
if (`$p.ExitCode -eq 0) { Write-Host '$Command succeeded but should have failed'; exit 30 }
exit 0
"@
}

# A copy of $Base with $Extra's keys set (PowerShell's + throws on a shared key).
function Merge-Env([hashtable]$Base, [hashtable]$Extra) {
  $merged = @{}
  foreach ($k in $Base.Keys) { $merged[$k] = $Base[$k] }
  foreach ($k in $Extra.Keys) { $merged[$k] = $Extra[$k] }
  return $merged
}

function Get-OwnLlama {
  return @(Get-Process -Name 'llama-server' -ErrorAction SilentlyContinue |
      Where-Object { $_.Path -and $_.Path.StartsWith($env:OPENMONO_LLAMA_CPP_DIR, [StringComparison]::OrdinalIgnoreCase) })
}

function Invoke-Scenario([string]$Name, [string]$Body, [hashtable]$Environment) {
  Stop-FakeLlama
  Remove-Item $serverLog -ErrorAction SilentlyContinue
  $log = Join-Path $work "calls-$Name.log"
  $env:FAKE_LOG = $log
  foreach ($key in $Environment.Keys) { [Environment]::SetEnvironmentVariable($key, $Environment[$key], 'Process') }
  Remove-Item $state -ErrorAction SilentlyContinue
  $wrapper = Join-Path $work "run-$Name.ps1"
  Set-Content -Path $wrapper -Encoding UTF8 -Value $Body
  Write-Host "=== scenario $Name ($Shell) ==="
  Push-Location $project
  try {
    & $exe -NoProfile -ExecutionPolicy Bypass -File $wrapper | Tee-Object -FilePath (Join-Path $work "output-$Name.log") | Out-Host
    $code = $LASTEXITCODE
  } finally {
    Pop-Location
  }
  $calls = @()
  if (Test-Path $log) { $calls = @(Get-Content $log) }
  if (Test-Path $serverLog) {
    $served = @(Get-Content $serverLog)
    Add-Content -Path $log -Value $served
    $calls += $served
  }
  if ($code -ne 0) { $failures.Add("[$Name] exited $code") }
  return , $calls
}

function Assert-Called([string]$Name, [object[]]$Calls, [string]$Pattern) {
  if (-not ($Calls | Where-Object { $_ -like $Pattern })) { $failures.Add("[$Name] expected a call like [$Pattern]") }
}

function Get-Output([string]$Name) {
  return (Get-Content (Join-Path $work "output-$Name.log") -Raw)
}

# Index of the first call matching $Pattern, or -1.
function Get-CallIndex([object[]]$Calls, [string]$Pattern) {
  for ($i = 0; $i -lt $Calls.Count; $i++) { if ($Calls[$i] -like $Pattern) { return $i } }
  return -1
}

function Assert-NotCalled([string]$Name, [object[]]$Calls, [string]$Pattern) {
  if ($Calls | Where-Object { $_ -like $Pattern }) { $failures.Add("[$Name] unexpected call like [$Pattern]") }
}

$env:FAKE_LOG = $serverLog
$env:FAKE_SERVER_CONTROL = $serverControl
$server = Start-Process -FilePath (Join-Path $toolsBin 'FakeTool.exe') -ArgumentList 'server', '7480' -PassThru -WindowStyle Hidden
try {
  $up = $false
  for ($i = 0; $i -lt 50 -and -not $up; $i++) {
    try { Invoke-RestMethod -Uri 'http://127.0.0.1:7480/llama-releases' -TimeoutSec 2 | Out-Null; $up = $true } catch { Start-Sleep -Milliseconds 200 }
  }
  if (-not $up) { throw 'Fake download server did not come up.' }

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
  Assert-Called 'docker-missing' $calls 'download llama-b9000-bin-win-cuda-12.4-x64.zip*'
  Assert-Called 'docker-missing' $calls 'download cudart-llama-bin-win-cuda-12.4-x64.zip*'
  Assert-Called 'docker-missing' $calls 'download /models/Qwen3.8-27B-UD-Q4_K_M.gguf*'
  Assert-Called 'docker-missing' $calls 'download /models/mmproj-qwen3.8-27B-F16.gguf*'
  Assert-Called 'docker-missing' $calls 'llama-server build b9000-cuda-12.4*'
  Assert-Called 'docker-missing' $calls 'llama-server --model *Qwen3.8-27B-UD-Q4_K_M.gguf --alias Qwen3.8-27B-UD-Q4_K_M --host 127.0.0.1 --port 7474 *--n-gpu-layers 99 *'
  if ((Get-CallIndex $calls 'llama-server build*') -lt (Get-CallIndex $calls 'docker compose -f * build agent*')) { $failures.Add('[docker-missing] llama-server started before the agent image was built') }
  Assert-NotCalled 'docker-missing' $calls 'download /models/Qwen3.5-*'

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
  Assert-Called 'docker-stopped' $calls 'llama-server build b9000-cuda-12.4*'
  Assert-NotCalled 'docker-stopped' $calls 'download *'
  Assert-NotCalled 'docker-stopped' $calls 'winget install --id Git.Git*'
  Assert-NotCalled 'docker-stopped' $calls 'winget install --id Microsoft.DotNet.SDK*'
  if ((Get-Output 'docker-stopped') -notmatch 'Git \d+\.\d+\.\d+ found\.') { $failures.Add('[docker-stopped] setup did not report the installed Git') }
  if ((Get-Output 'docker-stopped') -notmatch '\.NET SDK 10\.\d+\.\d+ found') { $failures.Add('[docker-stopped] setup did not report the installed .NET SDK') }

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
  Assert-Called 'agent' $calls 'llama-server build b9000-cuda-12.4*'
  if (@($calls | Where-Object { $_ -like 'llama-server build*' }).Count -ne 1) { $failures.Add('[agent] agent did not reuse the llama-server that start began') }
  if (Get-Process -Name 'llama-server' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($work, [StringComparison]::OrdinalIgnoreCase) }) { $failures.Add('[agent] stop left llama-server running') }
  Assert-NotCalled 'agent' $calls 'winget *'
  Assert-Called 'agent' $calls 'docker run --rm --entrypoint od openmono-agent:latest*'
  Assert-NotCalled 'agent' $calls 'docker compose -f * build agent*'

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
    foreach ($want in @('run', '--rm', '127.0.0.1:7475:7475', 'HOST_ACP_PORT=7475', "HOST_WORKSPACE_PATH=$project", 'OPENMONO_MODEL=Qwen3.8-27B-UD-Q4_K_M')) {
      if ($argv -notcontains $want) { $failures.Add("[agent] docker args missing [$want]") }
    }
    if ($argv | Where-Object { $_ -like 'OPENMONO_LLM_BACKEND=*' }) { $failures.Add('[agent] docker args still name an LLM backend') }
    if ($run.env.OPENMONO_ENDPOINT -ne 'http://host.docker.internal:7474') { $failures.Add("[agent] OPENMONO_ENDPOINT was [$($run.env.OPENMONO_ENDPOINT)]") }
    if ($run.env.WORKSPACE -ne ($project -replace '\\', '/')) { $failures.Add("[agent] WORKSPACE was [$($run.env.WORKSPACE)]") }
    if ($run.env.CTX_SIZE -ne '172032') { $failures.Add("[agent] CTX_SIZE was [$($run.env.CTX_SIZE)]") }
    if ($run.env.OPENMONO_VISION_ENABLED -ne '1') { $failures.Add("[agent] OPENMONO_VISION_ENABLED was [$($run.env.OPENMONO_VISION_ENABLED)]") }
  }

  # No NVIDIA GPU: the llama.cpp CPU build and the CPU rung only.
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
  Assert-Called 'cpu-only' $calls 'download llama-b9000-bin-win-cpu-x64.zip*'
  Assert-Called 'cpu-only' $calls 'download /models/Qwen3.5-2B-UD-Q4_K_XL.gguf*'
  Assert-Called 'cpu-only' $calls 'llama-server build b9000-cpu*'
  Assert-Called 'cpu-only' $calls 'llama-server --model *Qwen3.5-2B-UD-Q4_K_XL.gguf --alias Qwen3.5-2B-UD-Q4_K_XL *--threads-batch *'
  Assert-NotCalled 'cpu-only' $calls 'llama-server *--n-gpu-layers*'
  foreach ($big in @('Qwen3.8-27B', 'Qwen3.5-9B', 'Qwen3.5-4B')) { Assert-NotCalled 'cpu-only' $calls "download /models/$big*" }
  if (-not (Test-Path $state)) {
    $failures.Add('[cpu-only] start wrote no inference state')
  } else {
    $inference = Get-Content $state -Raw | ConvertFrom-Json
    if ($inference.model_alias -ne 'Qwen3.5-2B-UD-Q4_K_XL') { $failures.Add("[cpu-only] model_alias was [$($inference.model_alias)]") }
    if ($inference.accel -ne 'cpu') { $failures.Add("[cpu-only] accel was [$($inference.accel)]") }
  }

  # Someone else's llama-server, serving its own model, already holds 7474:
  # start fails with the reason and never starts a second server.
  $foreignExe = Join-Path $foreignBin 'llama-server.exe'
  $foreignBody = @"
`$foreign = Start-Process -FilePath '$foreignExe' -ArgumentList '--port 7474 --alias Llama-3.3-70B-Instruct-Q4_K_M' -PassThru -WindowStyle Hidden
for (`$i = 0; `$i -lt 50; `$i++) {
  try { Invoke-RestMethod -Uri 'http://127.0.0.1:7474/health' -TimeoutSec 2 | Out-Null; break } catch { Start-Sleep -Milliseconds 200 }
}
`$out = Join-Path '$work' 'foreign-start.out'
`$err = Join-Path '$work' 'foreign-start.err'
`$p = Start-Process -FilePath '$exe' -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File "$launcher" start' -RedirectStandardOutput `$out -RedirectStandardError `$err -NoNewWindow -Wait -PassThru
Get-Content `$out, `$err
`$alive = -not `$foreign.HasExited
Stop-Process -Id `$foreign.Id -Force -ErrorAction SilentlyContinue
if (`$p.ExitCode -eq 0) { Write-Host 'start succeeded against a foreign server'; exit 30 }
if (-not `$alive) { Write-Host 'the foreign server was stopped'; exit 31 }
exit 0
"@
  $calls = Invoke-Scenario 'foreign-server' $foreignBody @{
    PATH = "$dockerBin;$toolsBin;$cleanPath"
    OPENMONO_DOCKER_DESKTOP_DIR = $desktopDir
    FAKE_NO_GPU = $null
  }
  Assert-NotCalled 'foreign-server' $calls 'llama-server build b9000*'
  $out = Get-Output 'foreign-server'
  foreach ($want in @('Port 7474 is taken by a server OpenMono did not start', 'Llama-3.3-70B-Instruct-Q4_K_M')) {
    if (-not $out.Contains($want)) { $failures.Add("[foreign-server] output missing [$want]") }
  }

  $gpuEnv = @{
    PATH = "$dockerBin;$toolsBin;$cleanPath"
    OPENMONO_DOCKER_DESKTOP_DIR = $desktopDir
    FAKE_DOCKER_ENGINE = $null
    FAKE_WSL_STATE = $null
    FAKE_NO_GPU = $null
  }

  # Someone else's llama-server already serves this machine's model: adopted,
  # and stop (even --force) leaves it alone.
  $adoptBody = @"
`$foreign = Start-Process -FilePath '$foreignExe' -ArgumentList '--port 7474 --alias Qwen3.8-27B-UD-Q4_K_M' -PassThru -WindowStyle Hidden
for (`$i = 0; `$i -lt 50; `$i++) {
  try { Invoke-RestMethod -Uri 'http://127.0.0.1:7474/health' -TimeoutSec 2 | Out-Null; break } catch { Start-Sleep -Milliseconds 200 }
}
try {
  & '$launcher' start
  if (`$LASTEXITCODE -ne 0) { exit 10 }
  & '$launcher' stop
  if (`$LASTEXITCODE -ne 0) { exit 11 }
  & '$launcher' stop --force
  if (`$foreign.HasExited) { Write-Host 'stop ended a server OpenMono did not start'; exit 12 }
  exit 0
} finally {
  Stop-Process -Id `$foreign.Id -Force -ErrorAction SilentlyContinue
}
"@
  $calls = Invoke-Scenario 'foreign-adopt' $adoptBody $gpuEnv
  Assert-NotCalled 'foreign-adopt' $calls 'llama-server build b9000*'
  if (-not (Get-Output 'foreign-adopt').Contains('adopting it')) { $failures.Add('[foreign-adopt] start did not say it adopted the server') }

  # The CUDA build exits while loading: Vulkan serves the same model.
  $calls = Invoke-Scenario 'cuda-crash' $cpuBody (Merge-Env $gpuEnv @{ FAKE_LLAMA_CRASH = 'cuda' })
  $env:FAKE_LLAMA_CRASH = $null
  Assert-Called 'cuda-crash' $calls 'llama-server build b9000-cuda-12.4*'
  Assert-Called 'cuda-crash' $calls 'download llama-b9000-bin-win-vulkan-x64.zip*'
  Assert-Called 'cuda-crash' $calls 'llama-server build b9000-vulkan*'
  Assert-Called 'cuda-crash' $calls 'llama-server --model *Qwen3.8-27B-UD-Q4_K_M.gguf *--n-gpu-layers 99 *'
  $out = Get-Output 'cuda-crash'
  foreach ($want in @('b9000-cuda-12.4 sees CUDA0: Fake RTX 4090', 'WARNING: the llama.cpp CUDA build failed', 'forward compatibility was attempted', 'llama.cpp b9000-vulkan')) {
    if (-not $out.Contains($want)) { $failures.Add("[cuda-crash] output missing [$want]") }
  }
  if ((Test-Path $state) -and ((Get-Content $state -Raw | ConvertFrom-Json).accel -ne 'vulkan')) { $failures.Add('[cuda-crash] the state does not record the Vulkan build') }

  # The CUDA build lists no CUDA device (ggml-cuda.dll did not load): it is
  # never started, and Vulkan serves the model.
  $calls = Invoke-Scenario 'cuda-no-device' $cpuBody (Merge-Env $gpuEnv @{ FAKE_LLAMA_NO_DEVICE = 'cuda' })
  $env:FAKE_LLAMA_NO_DEVICE = $null
  Assert-Called 'cuda-no-device' $calls 'llama-server list-devices b9000-cuda-12.4*'
  Assert-NotCalled 'cuda-no-device' $calls 'llama-server build b9000-cuda-12.4*'
  Assert-Called 'cuda-no-device' $calls 'llama-server build b9000-vulkan*'
  $out = Get-Output 'cuda-no-device'
  foreach ($want in @('sees no CUDA device', 'Falling back to the next backend', 'b9000-vulkan sees Vulkan0: Fake RTX 4090')) {
    if (-not $out.Contains($want)) { $failures.Add("[cuda-no-device] output missing [$want]") }
  }

  # No nvidia-smi.exe anywhere on PATH.
  $toolsNoSmi = Join-Path $work 'tools-no-smi'
  New-Item -ItemType Directory -Force -Path $toolsNoSmi | Out-Null
  Install-Fake $toolsNoSmi @('winget', 'wsl')
  $noSmiPath = (@($cleanPath -split ';') | Where-Object { $_ -and -not (Test-Path (Join-Path $_ 'nvidia-smi.exe')) }) -join ';'
  $calls = Invoke-Scenario 'no-nvidia-smi' $cpuBody (Merge-Env $gpuEnv @{ PATH = "$dockerBin;$toolsNoSmi;$noSmiPath" })
  Assert-NotCalled 'no-nvidia-smi' $calls 'nvidia-smi*'
  Assert-Called 'no-nvidia-smi' $calls 'llama-server build b9000-cpu*'
  Assert-Called 'no-nvidia-smi' $calls 'llama-server --model *Qwen3.5-2B-UD-Q4_K_XL.gguf *'

  # Every GGUF download fails with 404 on an empty models folder.
  $emptyModels = Join-Path $work 'models-empty'
  New-Item -ItemType Directory -Force -Path $emptyModels | Out-Null
  Set-Content -Path $serverControl -Value 'models=404'
  $calls = Invoke-Scenario 'bad-download' (New-ExpectFailBody 'start' 'bad-download') (Merge-Env $gpuEnv @{ OPENMONO_MODELS_DIR = $emptyModels })
  Remove-Item $serverControl -ErrorAction SilentlyContinue
  $env:OPENMONO_MODELS_DIR = Join-Path $work 'models'
  Assert-Called 'bad-download' $calls 'download /models/Qwen3.8-27B-UD-Q4_K_M.gguf*'
  Assert-NotCalled 'bad-download' $calls 'llama-server build*'
  $out = Get-Output 'bad-download'
  foreach ($want in @('Model download failed', '/models/Qwen3.8-27B-UD-Q4_K_M.gguf returned 404', 'Run the same command again')) {
    if (-not $out.Contains($want)) { $failures.Add("[bad-download] output missing [$want]") }
  }
  if (@(Get-ChildItem $emptyModels -Filter '*.gguf').Count -ne 0) { $failures.Add('[bad-download] a model file was left behind') }

  # A changed setting replaces our llama-server; there is never a second one.
  $restartBody = @"
& '$launcher' start
if (`$LASTEXITCODE -ne 0) { exit 10 }
`$env:LLAMA_THREADS = '3'
& '$launcher' start
if (`$LASTEXITCODE -ne 0) { exit 11 }
`$env:LLAMA_THREADS = `$null
exit 0
"@
  $calls = Invoke-Scenario 'restart' $restartBody $gpuEnv
  if (@($calls | Where-Object { $_ -like 'llama-server build b9000*' }).Count -ne 2) { $failures.Add('[restart] expected exactly two llama-server starts') }
  Assert-Called 'restart' $calls 'llama-server * --threads 3 *'
  $running = Get-OwnLlama
  if ($running.Count -ne 1) { $failures.Add("[restart] $($running.Count) llama-server processes running, expected 1") }

  # Every user command once.
  $diagDir = Join-Path $work 'diag'
  New-Item -ItemType Directory -Force -Path $diagDir | Out-Null
  $commandsBody = @"
& '$launcher' start
if (`$LASTEXITCODE -ne 0) { exit 10 }
& '$launcher' models
if (`$LASTEXITCODE -ne 0) { exit 11 }
`$json = (& '$launcher' models --json | Out-String) | ConvertFrom-Json
if (`$LASTEXITCODE -ne 0) { exit 12 }
if (`$json.pick -ne 'Qwen3.8-27B-UD-Q4_K_M') { Write-Host "models --json pick was `$(`$json.pick)"; exit 13 }
if (@(`$json.ladder).Count -ne 6) { Write-Host "models --json has `$(@(`$json.ladder).Count) rungs"; exit 14 }
if (@(`$json.backends) -join ',' -ne 'cuda,vulkan,cpu') { Write-Host "backends were `$(@(`$json.backends) -join ',')"; exit 15 }
& '$launcher' status
if (`$LASTEXITCODE -ne 0) { exit 16 }
& '$launcher' doctor
& '$launcher' diag --out '$diagDir\bundle.zip'
if (`$LASTEXITCODE -ne 0) { exit 17 }
& '$launcher' help
if (`$LASTEXITCODE -ne 0) { exit 18 }
& '$launcher' frobnicate
if (`$LASTEXITCODE -ne 2) { Write-Host "unknown command exited `$LASTEXITCODE"; exit 19 }
& '$launcher' stop
exit `$LASTEXITCODE
"@
  $calls = Invoke-Scenario 'commands' $commandsBody $gpuEnv
  $out = Get-Output 'commands'
  foreach ($want in @('Windows model ladder', 'This machine (NVIDIA GeForce RTX 4090, 24 GB): Qwen3.8-27B-UD-Q4_K_M', 'Inference: http://127.0.0.1:7474', '[ ok ] llama.cpp', 'llama-server stopped')) {
    if (-not $out.Contains($want)) { $failures.Add("[commands] output missing [$want]") }
  }
  $zip = @(Get-ChildItem $diagDir -Filter '*.zip' -Recurse)
  if ($zip.Count -eq 0) {
    $failures.Add('[commands] diag wrote no zip')
  } else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip[0].FullName)
    try { $entries = @($archive.Entries | ForEach-Object { $_.Name }) } finally { $archive.Dispose() }
    foreach ($want in @('llama-cpp.txt', 'llama-server-log.txt', 'doctor.txt', 'inference.json', 'nvidia-smi.txt')) {
      if ($entries -notcontains $want) { $failures.Add("[commands] diag zip missing $want") }
    }
  }
  if ((Get-OwnLlama).Count -ne 0) { $failures.Add('[commands] stop left llama-server running') }

  # No Git, no .NET SDK: winget installs both, then setup carries on in the
  # same session. git.exe is only reachable through the refreshed PATH.
  $wingetGit = Join-Path $work 'git-winget\cmd'
  $wingetDotnet = Join-Path $work 'dotnet-winget'
  New-Item -ItemType Junction -Path $wingetDotnet -Target $realDotnetDir | Out-Null
  $registry = Join-Path $work 'persisted-path-winget.txt'
  New-Item -ItemType File -Force -Path $registry | Out-Null
  $calls = Invoke-Scenario 'prereqs-winget' $setupBody @{
    PATH = "$dockerBin;$toolsBin;$barePath"
    OPENMONO_DOCKER_DESKTOP_DIR = $desktopDir
    FAKE_DOCKER_ENGINE = $null
    FAKE_WSL_STATE = $null
    FAKE_NO_GPU = $null
    OPENMONO_PATH_REGISTRY_FILE = $registry
    FAKE_GIT_DIR = $wingetGit
    FAKE_DOTNET_DIR = $wingetDotnet
  }
  $wingetFlags = '-e --source winget --silent --accept-source-agreements --accept-package-agreements'
  Assert-Called 'prereqs-winget' $calls "winget install --id Git.Git $wingetFlags*"
  Assert-Called 'prereqs-winget' $calls "winget install --id Microsoft.DotNet.SDK.10 $wingetFlags*"
  if ((Get-CallIndex $calls 'git --version*') -lt (Get-CallIndex $calls 'winget install --id Git.Git*')) { $failures.Add('[prereqs-winget] git was not run from the refreshed PATH after the install') }
  Assert-Called 'prereqs-winget' $calls 'docker compose -f * build agent*'
  $out = Get-Output 'prereqs-winget'
  foreach ($want in @("Refreshed PATH for this session (added: $wingetGit)", "Refreshed PATH for this session (added: $wingetDotnet)", 'Git 2.51.0 installed; available in this session', 'installed; available in this session')) {
    if (-not $out.Contains($want)) { $failures.Add("[prereqs-winget] output missing [$want]") }
  }

  # No winget either: the official installers, user-local, no administrator.
  $fallbackGit = Join-Path $work 'mingit'
  $fallbackDotnet = Join-Path $work 'dotnet-user'
  $registry = Join-Path $work 'persisted-path-fallback.txt'
  New-Item -ItemType File -Force -Path $registry | Out-Null
  $calls = Invoke-Scenario 'prereqs-fallback' $setupBody @{
    PATH = "$dockerBin;$toolsNoWinget;$barePath"
    OPENMONO_DOCKER_DESKTOP_DIR = $desktopDir
    OPENMONO_PATH_REGISTRY_FILE = $registry
    OPENMONO_GIT_RELEASE_API = 'http://127.0.0.1:7480/git-release'
    OPENMONO_GIT_INSTALL_DIR = $fallbackGit
    OPENMONO_DOTNET_INSTALL_SCRIPT_URL = 'http://127.0.0.1:7480/dotnet-install.ps1'
    OPENMONO_DOTNET_INSTALL_DIR = $fallbackDotnet
    FAKE_GIT_DIR = $null
    FAKE_DOTNET_DIR = $realDotnetDir
  }
  Assert-NotCalled 'prereqs-fallback' $calls 'winget *'
  Assert-Called 'prereqs-fallback' $calls "dotnet-install -Channel 10.0 -InstallDir $fallbackDotnet -NoPath:True"
  Assert-Called 'prereqs-fallback' $calls 'git --version*'
  Assert-Called 'prereqs-fallback' $calls 'docker compose -f * build agent*'
  $persisted = @(Get-Content $registry)
  foreach ($want in @((Join-Path $fallbackGit 'cmd'), $fallbackDotnet)) {
    if ($persisted -notcontains $want) { $failures.Add("[prereqs-fallback] [$want] was not added to the persisted PATH") }
  }
  foreach ($key in @('OPENMONO_PATH_REGISTRY_FILE', 'OPENMONO_GIT_RELEASE_API', 'OPENMONO_GIT_INSTALL_DIR', 'OPENMONO_DOTNET_INSTALL_SCRIPT_URL', 'OPENMONO_DOTNET_INSTALL_DIR', 'FAKE_DOTNET_DIR')) {
    [Environment]::SetEnvironmentVariable($key, $null, 'Process')
  }

  # An agent image built before .gitattributes, from CRLF files: rebuilt
  # with LF before the container is created.
  $crlfMarker = Join-Path $work 'image-crlf'
  Set-Content -Path $crlfMarker -Value 'crlf'
  # The agent scenario cached the LF image id; the rebuilt image must be probed again.
  Remove-Item (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.openmono\win\agent-image-lf.txt') -ErrorAction SilentlyContinue
  Remove-Item $env:FAKE_DOCKER_OUT -ErrorAction SilentlyContinue
  $crlfBody = @"
& '$launcher' start
if (`$LASTEXITCODE -ne 0) { exit 10 }
& '$launcher' agent --acp-only
if (`$LASTEXITCODE -ne 0) { exit 11 }
& '$launcher' stop
exit `$LASTEXITCODE
"@
  $calls = Invoke-Scenario 'agent-crlf' $crlfBody @{
    PATH = "$dockerBin;$toolsBin;$cleanPath"
    OPENMONO_DOCKER_DESKTOP_DIR = $desktopDir
    FAKE_IMAGE_CRLF = $crlfMarker
  }
  $rebuild = Get-CallIndex $calls 'docker compose -f * build agent*'
  $launch = Get-CallIndex $calls 'docker compose * run *'
  if ($rebuild -lt 0) { $failures.Add('[agent-crlf] the CRLF agent image was not rebuilt') }
  elseif ($launch -lt $rebuild) { $failures.Add('[agent-crlf] the agent container started before the rebuild') }
  if (@($calls | Where-Object { $_ -like 'docker run --rm --entrypoint od*' }).Count -lt 2) { $failures.Add('[agent-crlf] the rebuilt image was not re-checked') }
  if (-not (Test-Path $env:FAKE_DOCKER_OUT)) { $failures.Add('[agent-crlf] the agent never launched after the rebuild') }
  $env:FAKE_IMAGE_CRLF = $null

  # docker/ checked out with CRLF (core.autocrlf=true, no .gitattributes):
  # every file the containers run or parse is put back to LF.
  $crlfDocker = Join-Path $work 'docker-crlf'
  Copy-Item -Recurse (Join-Path $root 'docker') $crlfDocker
  $lineEndingsScript = Join-Path $root 'win\scripts\line-endings.ps1'
  $lineEndingsBody = @"
`$ErrorActionPreference = 'Stop'
. '$lineEndingsScript'
`$text = @(Get-ChildItem -LiteralPath '$crlfDocker' -Recurse -File)
foreach (`$f in `$text) {
  `$lf = [IO.File]::ReadAllText(`$f.FullName)
  [IO.File]::WriteAllText(`$f.FullName, (`$lf -replace "``r?``n", "``r``n"))
}
`$binary = Join-Path '$crlfDocker' 'blob.bin'
[IO.File]::WriteAllBytes(`$binary, [byte[]](0x00, 0x0D, 0x0A, 0x41))
`$withCr = @(`$text | Where-Object { [IO.File]::ReadAllBytes(`$_.FullName) -contains 13 })
if (`$withCr.Count -eq 0) { Write-Host 'the CRLF copy has no CR'; exit 19 }
`$changed = @(Repair-ContainerLineEndings '$crlfDocker')
if (`$changed.Count -ne `$withCr.Count) { Write-Host "`$(`$changed.Count) of `$(`$withCr.Count) CRLF files converted"; exit 20 }
foreach (`$f in `$text) {
  if ([IO.File]::ReadAllBytes(`$f.FullName) -contains 13) { Write-Host "CR left in `$(`$f.FullName)"; exit 21 }
}
if ((@([IO.File]::ReadAllBytes(`$binary)) -join ',') -ne '0,13,10,65') { Write-Host 'binary file was modified'; exit 22 }
`$first = ([IO.File]::ReadAllText((Join-Path '$crlfDocker' 'entrypoint.sh')) -split "``n")[0]
if (`$first -cne '#!/usr/bin/env bash') { Write-Host "entrypoint shebang is [`$first]"; exit 23 }
exit 0
"@
  $null = Invoke-Scenario 'line-endings' $lineEndingsBody @{}

  if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    if ($argv.Count -gt 0) { Write-Host 'docker argv:'; $argv | ForEach-Object { Write-Host "  [$_]" } }
    Get-ChildItem $work -Filter 'calls-*.log' | ForEach-Object { Write-Host "--- $($_.Name)"; Get-Content $_.FullName | ForEach-Object { Write-Host "  $_" } }
    exit 1
  }
  Write-Host "Windows smoke test passed under $Shell (docker-missing, docker-stopped, agent, cpu-only, foreign-server, foreign-adopt, cuda-crash, cuda-no-device, no-nvidia-smi, bad-download, restart, commands, prereqs-winget, prereqs-fallback, agent-crlf, line-endings)."
} finally {
  if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
  Stop-FakeLlama
}
