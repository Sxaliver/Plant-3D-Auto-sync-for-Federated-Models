param([string]$TectonicPath)
$ErrorActionPreference = 'Stop'
chcp 65001 > $null
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
if (-not (Test-Path -LiteralPath $TectonicPath -PathType Leaf)) { throw 'Supply the official Tectonic 0.17.0 Windows x64 executable with -TectonicPath.' }
& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$staging = Join-Path $PSScriptRoot 'artifacts\AutoSync-0.4.21-Windows-x64'
if (Test-Path -LiteralPath $staging) { throw 'Release staging already exists. Choose a fresh checkout to avoid stale files.' }
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src\Plant Model Sync\artifacts\Release-0.4.21\IWMF.PlantModelSync.bundle') -Destination $staging -Recurse
Copy-Item -Path (Join-Path $PSScriptRoot 'release\*') -Destination $staging -Recurse
Copy-Item -LiteralPath $TectonicPath -Destination (Join-Path $staging 'Tools\tectonic.exe')
# 发布包携带清单，安装者可自行验证，源仓库不提交二进制编译结果。
$hashes = Get-ChildItem -LiteralPath $staging -Recurse -File | Get-FileHash -Algorithm SHA256 | Select-Object @{Name='File';Expression={$_.Path.Substring($staging.Length+1)}},Hash
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'SHA256.json') -Encoding UTF8
$zip = Join-Path $PSScriptRoot 'artifacts\AutoSync-0.4.21-Windows-x64.zip'
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
Write-Output $zip
