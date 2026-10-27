param([switch]$Install)
$ErrorActionPreference = 'Stop'
chcp 65001 > $null
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
& (Join-Path $PSScriptRoot 'src\build-plant-sync.ps1') -Install:$Install
if ($LASTEXITCODE -ne 0) { throw '插件构建失败。' }
