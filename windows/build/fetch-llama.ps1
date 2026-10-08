param(
  [string]$Version = "b9070",
  [ValidateSet("cuda", "vulkan", "cpu")]
  [string]$Flavor = "cpu",
  [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"
$files = @{
  cuda   = "llama-${Version}-bin-win-cuda-12.4-x64.zip"
  vulkan = "llama-${Version}-bin-win-vulkan-x64.zip"
  cpu    = "llama-${Version}-bin-win-avx2-x64.zip"
}
$url = "https://github.com/ggml-org/llama.cpp/releases/download/${Version}/$($files[$Flavor])"
if ([string]::IsNullOrWhiteSpace($OutDir)) {
  $OutDir = Join-Path $env:LOCALAPPDATA "OpenMono\bin\llama-server\$Flavor"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zip = Join-Path ([IO.Path]::GetTempPath()) "llama-$Flavor.zip"
Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $zip
$exe = Join-Path $OutDir "llama-server.exe"
Expand-Archive -Path $zip -DestinationPath $OutDir -Force
$found = Get-ChildItem $OutDir -Recurse -Filter "llama-server.exe" | Select-Object -First 1
if ($found -and ($found.FullName -ne $exe)) {
  Copy-Item $found.FullName $exe -Force
}
# Ship CUDA runtime DLLs beside the binary when present in the archive.
Get-ChildItem $OutDir -Recurse -Include "cublas64_*.dll", "cudart64_*.dll", "vulkan-1.dll" |
  ForEach-Object { Copy-Item $_.FullName $OutDir -Force -ErrorAction SilentlyContinue }
Write-Host "llama-server ($Flavor) ready at $exe"
