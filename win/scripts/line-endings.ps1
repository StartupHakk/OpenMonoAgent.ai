<#
.SYNOPSIS
  Keeps files that run inside the Linux agent container on LF line endings.

.DESCRIPTION
  Dot-sourced by openmono.ps1. A Windows checkout made before .gitattributes
  pinned eol=lf (or with core.autocrlf=true) has CRLF in docker/, and
  Dockerfile.agent copies docker/entrypoint.sh into the image as its
  ENTRYPOINT, which then fails with "/usr/bin/env: 'bash\r': No such file or
  directory". Before the image is built or run, every text file under docker/
  (the build inputs and the bind-mounted configs) is rewritten with LF, and an
  agent image already built from CRLF files is rebuilt.
#>

$AgentImageName = 'openmono-agent:latest'
$AgentEntrypointInImage = '/usr/local/bin/openmono-entrypoint.sh'

# Rewrites CRLF (and a stray trailing CR) as LF in place. Files with a NUL
# byte are binary and left alone. Returns the paths it changed.
function Repair-LineEndings([string[]]$Paths) {
  $changed = New-Object System.Collections.Generic.List[string]
  foreach ($path in $Paths) {
    $bytes = [IO.File]::ReadAllBytes($path)
    if ([Array]::IndexOf($bytes, [byte]13) -lt 0 -or [Array]::IndexOf($bytes, [byte]0) -ge 0) { continue }
    $out = New-Object -TypeName 'System.Collections.Generic.List[byte]' -ArgumentList $bytes.Length
    for ($i = 0; $i -lt $bytes.Length; $i++) {
      $b = $bytes[$i]
      if ($b -eq 13 -and ($i + 1 -eq $bytes.Length -or $bytes[$i + 1] -eq 10)) { continue }
      $out.Add($b)
    }
    if ($out.Count -eq $bytes.Length) { continue }
    [IO.File]::WriteAllBytes($path, $out.ToArray())
    $changed.Add($path)
  }
  return , $changed.ToArray()
}

function Repair-ContainerLineEndings([string]$DockerDir) {
  $files = @(Get-ChildItem -Path $DockerDir -Recurse -File -Force | ForEach-Object { $_.FullName })
  $changed = Repair-LineEndings $files
  if ($changed.Count -gt 0) {
    Write-Host "[openmono] Converted $($changed.Count) container file(s) under docker\ from CRLF to LF:" -ForegroundColor Yellow
    foreach ($f in $changed) { Write-Host "  $f" }
  }
  return $changed
}

function Get-LineEndingCacheFile {
  return Join-Path ([Environment]::GetFolderPath('UserProfile')) '.openmono\win\agent-image-lf.txt'
}

# $true when the agent image is missing (compose builds it from the repaired
# tree) or its entrypoint has no CR. Cached per image id, so this costs one
# `docker image inspect` per launch once an image has passed.
function Test-AgentImageLineEndings {
  $ErrorActionPreference = 'Continue'
  # Collect all output before picking a line: Select-Object -First in the
  # pipeline stops docker early on Windows PowerShell 5.1 and loses its exit code.
  $inspect = @(& docker image inspect $AgentImageName --format '{{.Id}}' 2>$null)
  if ($LASTEXITCODE -ne 0 -or $inspect.Count -eq 0) { return $true }
  $id = "$($inspect[0])".Trim()
  if (-not $id) { return $true }
  $cache = Get-LineEndingCacheFile
  if ((Test-Path $cache) -and ((Get-Content $cache -Raw).Trim() -eq $id)) { return $true }

  # od prints a carriage return as the two characters \r, which survives
  # PowerShell's line splitting of native output.
  $dump = (& docker run --rm --entrypoint od $AgentImageName -An -c -N 512 $AgentEntrypointInImage 2>&1 | Out-String)
  if ($LASTEXITCODE -ne 0) { return $true }
  if ($dump.Contains('\r')) { return $false }
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $cache) | Out-Null
  Set-Content -Path $cache -Value $id -Encoding ASCII
  return $true
}

<#
  Repairs docker/ to LF, then rebuilds the agent image if the existing one
  was built from CRLF files. Throws if the rebuilt image still has CR.
#>
function Confirm-AgentImageLineEndings([string]$DockerDir, [string]$ComposeFile) {
  $null = Repair-ContainerLineEndings $DockerDir
  if (Test-AgentImageLineEndings) { return }

  Write-Host "[openmono] $AgentImageName was built from CRLF files (its entrypoint has Windows line endings). Rebuilding it with LF..." -ForegroundColor Yellow
  & docker compose -f $ComposeFile build agent | Out-Host
  if ($LASTEXITCODE -ne 0) { throw 'Agent image rebuild failed. Run: .\win\openmono.ps1 setup' }
  if (-not (Test-AgentImageLineEndings)) {
    throw "The rebuilt $AgentImageName still has CRLF in $AgentEntrypointInImage. Check that docker\entrypoint.sh in $DockerDir has LF line endings."
  }
  Write-Host '[openmono] Agent image rebuilt with LF line endings.' -ForegroundColor Green
}
