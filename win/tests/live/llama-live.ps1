<#
.SYNOPSIS
  Live check of the Windows llama.cpp path: the real llama.cpp release, the
  real GGUF from Hugging Face, the real llama-server.exe, driven through
  win/openmono.ps1.

.DESCRIPTION
  Runs on any Windows machine (CI uses a GitHub windows-latest runner, which
  has no NVIDIA GPU, so it exercises the CPU build and the CPU rung,
  Qwen3.5-2B). On a GPU box it exercises that machine's backend and model.
  Checks, in order:

    start       `openmono.ps1 start` installs llama.cpp, downloads the GGUF
                and mmproj, starts llama-server; the state names the build,
                model and context, and the server listens on 127.0.0.1:7474
                only.
    reuse       a second `start` (from the other PowerShell when it exists)
                reuses the same llama-server process.
    status      `openmono.ps1 status` reports the server up with the model.
    chat        a plain chat completion answers.
    file-read   the agent's tool protocol: a read_file tool call comes back
                with a path, and after the tool result (role tool) the model
                answers with the secret word from the "file".
    vision      a 64x64 red PNG as an image_url part is accepted (the
                projector is loaded) and described as red.
    stop        `openmono.ps1 stop` ends llama-server and frees the port.

  Downloads land in OPENMONO_LLAMA_CPP_DIR and OPENMONO_MODELS_DIR when set
  (CI caches them), otherwise in the normal places. Exit code 0 means every
  check passed.

  -ApiOnly runs only chat, file-read and vision against a llama-server that
  is already running on -Endpoint (any OS, no openmono.ps1), for example one
  started by hand with the flags from `.\win\openmono.ps1 status`.

.EXAMPLE
  .\win\tests\live\llama-live.ps1
  .\win\tests\live\llama-live.ps1 -SkipVision -KeepServer
  pwsh win/tests/live/llama-live.ps1 -ApiOnly -Endpoint http://127.0.0.1:7474
#>
param(
  [switch]$SkipVision,
  [switch]$KeepServer,
  [switch]$ApiOnly,
  [string]$Endpoint = 'http://127.0.0.1:7474'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path (Join-Path (Join-Path $PSScriptRoot '..') '..') '..')).Path
$launcher = Join-Path (Join-Path $root 'win') 'openmono.ps1'
$stateFile = Join-Path (Join-Path (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.openmono') 'win') 'inference.json'
$endpoint = $Endpoint.TrimEnd('/')
$failures = New-Object System.Collections.Generic.List[string]
$redPng = 'iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAT0lEQVR42u3PQQkAAAgEsAtx/ZMZxgi+hcEKLNO+FgEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQGBywKqxUDxqh7TUQAAAABJRU5ErkJggg=='

function Write-Step([string]$Text) { Write-Host "=== $Text" }

function Add-Failure([string]$Check, [string]$Text) {
  $failures.Add("[$Check] $Text")
  Write-Host "FAIL [$Check] $Text"
}

function Get-State {
  if (-not (Test-Path $stateFile)) { return $null }
  return Get-Content $stateFile -Raw | ConvertFrom-Json
}

function Invoke-Chat([hashtable]$Body, [string]$Check) {
  $Body.temperature = 0
  $json = ConvertTo-Json -InputObject $Body -Depth 12 -Compress
  $headers = @{}
  if ($env:LLAMA_API_KEY) { $headers['Authorization'] = "Bearer $($env:LLAMA_API_KEY)" }
  $started = Get-Date
  try {
    $reply = Invoke-RestMethod -Method Post -Uri "$endpoint/v1/chat/completions" -Headers $headers `
      -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 600
  } catch {
    Add-Failure $Check "chat completion failed: $($_.Exception.Message)"
    return $null
  }
  $seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
  $speed = ''
  if ($reply.PSObject.Properties['timings'] -and $reply.timings) {
    $speed = ", prompt $([math]::Round($reply.timings.prompt_per_second, 1)) tok/s, generation $([math]::Round($reply.timings.predicted_per_second, 1)) tok/s"
  }
  Write-Host "  $Check answered in $seconds s$speed"
  return $reply
}

function Get-Content0($Reply) {
  $content = $Reply.choices[0].message.content
  if ($null -eq $content) { return '' }
  return [string]$content
}

try {
  if ($ApiOnly) {
    $listed = Invoke-RestMethod -Uri "$endpoint/v1/models" -TimeoutSec 30
    $state = [pscustomobject]@{ model_alias = [string]@($listed.data)[0].id; vision_enabled = $true }
    Write-Host "  api-only against $endpoint, model $($state.model_alias)"
  } else {
  Write-Step "start ($($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion))"
  & $launcher start
  if ($LASTEXITCODE -ne 0) { throw "openmono.ps1 start exited $LASTEXITCODE" }
  $state = Get-State
  if (-not $state) { throw 'start wrote no inference state' }
  Write-Host "  build $($state.llama_build), model $($state.model_alias), context $($state.context_tokens), vision $($state.vision_enabled), pid $($state.server_pid)"
  if ($state.backend -ne 'llama') { Add-Failure 'start' "backend was $($state.backend)" }
  if ($state.endpoint -ne $endpoint) { Add-Failure 'start' "endpoint was $($state.endpoint)" }
  if (-not $state.started_by_us) { Add-Failure 'start' 'the server was adopted, not started; stop any other llama-server first' }
  $server = Get-Process -Id $state.server_pid -ErrorAction SilentlyContinue
  if (-not $server) {
    Add-Failure 'start' "no process with pid $($state.server_pid)"
  } elseif ($server.ProcessName -ne 'llama-server') {
    Add-Failure 'start' "pid $($state.server_pid) is $($server.ProcessName)"
  }
  $listen = @(Get-NetTCPConnection -LocalPort 7474 -State Listen -ErrorAction SilentlyContinue)
  if ($listen.Count -eq 0) { Add-Failure 'start' 'nothing listens on port 7474' }
  foreach ($l in $listen) {
    if ($l.LocalAddress -ne '127.0.0.1') { Add-Failure 'start' "port 7474 is also bound on $($l.LocalAddress)" }
  }
  $log = [string]$state.log_file
  if (-not (Test-Path $log) -or (Get-Item $log).Length -eq 0) { Add-Failure 'start' "llama-server wrote no log at $log" }
  $hasSmi = [bool](Get-Command nvidia-smi -ErrorAction SilentlyContinue)
  if ($hasSmi -and $state.accel -eq 'cpu') { Add-Failure 'start' 'nvidia-smi is present but the CPU build serves the model' }
  $device = @{ cuda = 'CUDA'; vulkan = 'Vulkan' }[[string]$state.accel]
  if ($device -and (Test-Path $log)) {
    # llama.cpp logs "using device CUDA0 (...)" when the model goes on the GPU.
    $used = Select-String -Path $log -Pattern "using device $device\d" -SimpleMatch:$false | Select-Object -Last 1
    if ($used) { Write-Host "  $($used.Line.Trim())" } else { Add-Failure 'start' "the $($state.accel) build did not load the model on a $device device (see $log)" }
  }

  Write-Step 'reuse'
  $other = if ($PSVersionTable.PSEdition -eq 'Core') { 'powershell.exe' } else { 'pwsh' }
  if (-not (Get-Command $other -ErrorAction SilentlyContinue)) { $other = $null }
  if ($other) {
    & $other -NoProfile -ExecutionPolicy Bypass -File $launcher start
  } else {
    & $launcher start
  }
  if ($LASTEXITCODE -ne 0) { Add-Failure 'reuse' "second start exited $LASTEXITCODE" }
  $again = Get-State
  if ($again.server_pid -ne $state.server_pid) { Add-Failure 'reuse' "second start replaced pid $($state.server_pid) with $($again.server_pid)" }
  if (@(Get-Process -Name 'llama-server' -ErrorAction SilentlyContinue).Count -ne 1) { Add-Failure 'reuse' 'more than one llama-server is running' }

  Write-Step 'status'
  $status = (& $launcher status | Out-String)
  Write-Host $status.Trim()
  if (-not $status.Contains("Inference: $endpoint up; models: ") -or -not $status.Contains([string]$state.model_alias)) {
    Add-Failure 'status' 'status did not report the server up with the model'
  }
  }

  $model = [string]$state.model_alias

  Write-Step 'chat'
  $reply = Invoke-Chat @{ model = $model; max_tokens = 32; messages = @(@{ role = 'user'; content = 'Reply with the single word: pong' }) } 'chat'
  if ($reply) {
    $text = Get-Content0 $reply
    Write-Host "  reply: $text"
    if ($text.Trim().Length -eq 0) { Add-Failure 'chat' 'empty reply' }
    if ($reply.model -and ($reply.model -notlike "$model*")) { Add-Failure 'chat' "reply came from model $($reply.model)" }
  }

  Write-Step 'file-read'
  $tools = @(@{
      type = 'function'
      function = @{
        name = 'read_file'
        description = 'Read a text file from the workspace and return its contents.'
        parameters = @{ type = 'object'; properties = @{ path = @{ type = 'string'; description = 'Path of the file' } }; required = @('path') }
      }
    })
  $ask = @{ role = 'user'; content = 'Use the read_file tool to read notes.txt, then tell me the secret word written in it.' }
  $first = Invoke-Chat @{ model = $model; max_tokens = 256; tools = $tools; tool_choice = 'required'; messages = @($ask) } 'file-read (tool call)'
  if ($first) {
    $message = $first.choices[0].message
    $calls = @()
    if ($message.PSObject.Properties['tool_calls'] -and $message.tool_calls) { $calls = @($message.tool_calls) }
    if ($calls.Count -eq 0) {
      Add-Failure 'file-read' "no tool call; reply: $(Get-Content0 $first)"
    } else {
      $call = $calls[0]
      $arguments = $call.function.arguments | ConvertFrom-Json
      Write-Host "  tool call: $($call.function.name)($($call.function.arguments))"
      if ($call.function.name -ne 'read_file') { Add-Failure 'file-read' "called $($call.function.name)" }
      if ([string]$arguments.path -notlike '*notes.txt*') { Add-Failure 'file-read' "path was $($arguments.path)" }
      $assistant = @{ role = 'assistant'; content = ''; tool_calls = @(@{ id = $call.id; type = 'function'; function = @{ name = $call.function.name; arguments = $call.function.arguments } }) }
      $result = @{ role = 'tool'; tool_call_id = $call.id; content = "notes.txt:`n1: Shopping list`n2: The secret word is BLUEBERRY.`n" }
      $second = Invoke-Chat @{ model = $model; max_tokens = 64; tools = $tools; messages = @($ask, $assistant, $result) } 'file-read (answer)'
      if ($second) {
        $text = Get-Content0 $second
        Write-Host "  reply: $text"
        if ($text -notmatch 'blueberry') { Add-Failure 'file-read' "the answer does not contain the secret word: $text" }
      }
    }
  }

  Write-Step 'vision'
  if ($SkipVision) {
    Write-Host '  skipped (-SkipVision)'
  } elseif (-not $state.vision_enabled) {
    Add-Failure 'vision' 'vision is off: the mmproj projector was not loaded'
  } else {
    $content = @(
      @{ type = 'text'; text = 'What color is this image? Answer with one word.' },
      @{ type = 'image_url'; image_url = @{ url = "data:image/png;base64,$redPng" } })
    $seen = Invoke-Chat @{ model = $model; max_tokens = 16; messages = @(@{ role = 'user'; content = $content }) } 'vision'
    if ($seen) {
      $text = Get-Content0 $seen
      Write-Host "  reply: $text"
      if ($text -notmatch 'red') { Add-Failure 'vision' "the model did not see a red image: $text" }
    }
  }
} catch {
  Add-Failure 'live' $_.Exception.Message
} finally {
  if (-not $KeepServer -and -not $ApiOnly) {
    Write-Step 'stop'
    & $launcher stop
    Start-Sleep -Seconds 2
    $left = @(Get-Process -Name 'llama-server' -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) { Add-Failure 'stop' "llama-server still running (pid $(($left | ForEach-Object { $_.Id }) -join ', '))" }
    if (@(Get-NetTCPConnection -LocalPort 7474 -State Listen -ErrorAction SilentlyContinue).Count -gt 0) { Add-Failure 'stop' 'port 7474 is still bound' }
    if (Test-Path $stateFile) { Add-Failure 'stop' 'the inference state was not removed' }
  }
}

if ($failures.Count -gt 0) {
  Write-Host ''
  $failures | ForEach-Object { Write-Host "FAIL: $_" }
  $logFile = Join-Path (Join-Path (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.openmono') 'win') 'llama-server.log'
  if (-not $ApiOnly -and (Test-Path $logFile)) { Write-Host '--- llama-server log (last 40 lines)'; Get-Content $logFile -Tail 40 | ForEach-Object { Write-Host "  $_" } }
  exit 1
}
Write-Host "Live llama.cpp check passed under $($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion)."
