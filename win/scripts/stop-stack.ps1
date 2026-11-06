<#
.SYNOPSIS
  Stops the OpenMono Windows stack's agent container (Foundry Local keeps serving).

.DESCRIPTION
  Removes the openmono-agent container if present. Foundry Local is left
  running on purpose: other sessions may share it, and model reloads are slow.
  To stop Foundry too, run: foundry server stop.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

Write-Host '[openmono] Stopping the agent container...'
& docker rm -f openmono-agent 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
  Write-Host '[openmono] No openmono-agent container was running.'
} else {
  Write-Host '[openmono] Agent container removed. Foundry Local left running.'
}
