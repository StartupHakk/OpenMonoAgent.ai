<#
.SYNOPSIS
  Windows CI smoke test: drives win/openmono.ps1 end to end with fake tools.

.DESCRIPTION
  Builds FakeTool and stages it as foundry.exe, docker.exe, and nvidia-smi.exe
  ahead of everything else on PATH, starts a fake Foundry HTTP server on
  127.0.0.1:7474, then runs `openmono.ps1 start`, `agent`, `status`, and
  `stop` under the requested shell. Asserts the docker invocation the agent
  launch produced: argument quoting (embedded quotes, a trailing backslash,
  a workspace path with a space), the ACP publish, and the compose variables.
  No GPU, Docker engine, or Foundry install is needed.
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
$fakeBin = Join-Path $work 'fakebin'
$project = Join-Path $work 'my proj'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $fakeBin, $project | Out-Null

$fakeProject = Join-Path $root 'win\tests\OpenMono.Win.FakeTools\OpenMono.Win.FakeTools.csproj'
& dotnet build $fakeProject -c Release --nologo -v quiet -o (Join-Path $work 'faketool')
if ($LASTEXITCODE -ne 0) { throw 'FakeTool build failed.' }
Copy-Item (Join-Path $work 'faketool\*') $fakeBin -Recurse
foreach ($n in 'foundry', 'docker', 'nvidia-smi') {
  Copy-Item (Join-Path $fakeBin 'FakeTool.exe') (Join-Path $fakeBin "$n.exe")
}

$env:FAKE_LOG = Join-Path $work 'calls.log'
$env:FAKE_DOCKER_OUT = Join-Path $work 'docker.json'
$env:PATH = "$fakeBin;$env:PATH"

$server = Start-Process -FilePath (Join-Path $fakeBin 'FakeTool.exe') -ArgumentList 'server', '7474' -PassThru -WindowStyle Hidden
try {
  $up = $false
  for ($i = 0; $i -lt 50 -and -not $up; $i++) {
    try { Invoke-RestMethod -Uri 'http://127.0.0.1:7474/v1/models' -TimeoutSec 2 | Out-Null; $up = $true } catch { Start-Sleep -Milliseconds 200 }
  }
  if (-not $up) { throw 'Fake Foundry server did not come up.' }

  $state = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.openmono\win\inference.json'
  Remove-Item $state -ErrorAction SilentlyContinue

  # The tricky arguments live inside the wrapper so the outer shell hop
  # carries none; what is under test is openmono.ps1 -> docker.exe.
  $wrapper = Join-Path $work 'run.ps1'
  $launcher = Join-Path $root 'win\openmono.ps1'
  Set-Content -Path $wrapper -Encoding UTF8 -Value @"
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

  $exe = if ($Shell -eq 'pwsh') { 'pwsh' } else { 'powershell.exe' }
  Push-Location $project
  try {
    & $exe -NoProfile -ExecutionPolicy Bypass -File $wrapper
    $code = $LASTEXITCODE
  } finally {
    Pop-Location
  }
  if ($code -ne 0) { throw "openmono.ps1 run failed under $Shell (exit $code)." }

  $run = Get-Content $env:FAKE_DOCKER_OUT -Raw | ConvertFrom-Json
  $argv = @($run.args)
  $failures = @()
  $tail = $argv[($argv.Count - 5)..($argv.Count - 1)]
  $expectedTail = @('agent', '--acp-only', '-p', 'say "hi" now', 'C:\my dir\')
  for ($i = 0; $i -lt 5; $i++) {
    if ($tail[$i] -cne $expectedTail[$i]) { $failures += "agent arg $i was [$($tail[$i])], expected [$($expectedTail[$i])]" }
  }
  foreach ($want in @('run', '--rm', '127.0.0.1:7475:7475', 'HOST_ACP_PORT=7475', "HOST_WORKSPACE_PATH=$project", 'OPENMONO_MODEL=qwen3.8-27b-instruct-cuda-gpu:1')) {
    if ($argv -notcontains $want) { $failures += "docker args missing [$want]" }
  }
  if ($run.env.OPENMONO_ENDPOINT -ne 'http://host.docker.internal:7474') { $failures += "OPENMONO_ENDPOINT was [$($run.env.OPENMONO_ENDPOINT)]" }
  if ($run.env.WORKSPACE -ne ($project -replace '\\', '/')) { $failures += "WORKSPACE was [$($run.env.WORKSPACE)]" }
  if ($run.env.CTX_SIZE -ne '65536') { $failures += "CTX_SIZE was [$($run.env.CTX_SIZE)]" }
  if ($run.env.OPENMONO_VISION_ENABLED -ne '0') { $failures += "OPENMONO_VISION_ENABLED was [$($run.env.OPENMONO_VISION_ENABLED)]" }

  $calls = Get-Content $env:FAKE_LOG
  if (-not ($calls -contains 'foundry --version')) { $failures += 'foundry --version was never called' }
  if ($calls | Where-Object { $_ -like 'foundry server stop*' }) { $failures += 'stop stopped a daemon OpenMono did not start' }

  if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    Write-Host 'docker argv:'; $argv | ForEach-Object { Write-Host "  [$_]" }
    exit 1
  }
  Write-Host "Windows smoke test passed under $Shell."
} finally {
  if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
