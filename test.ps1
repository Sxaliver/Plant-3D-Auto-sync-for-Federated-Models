param([string]$EvidenceDirectory = (Join-Path $PSScriptRoot ('Demos\v_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))))
$ErrorActionPreference = 'Stop'
chcp 65001 > $null
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$tests = Join-Path $PSScriptRoot 'src\Plant Model Sync\Tests'
& dotnet build (Join-Path $tests 'WorkflowTests.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw '隔离测试编译失败。' }
& (Join-Path $tests 'bin\Release\net48\WorkflowTests.exe') $EvidenceDirectory
if ($LASTEXITCODE -ne 0) { throw '隔离测试失败；请查看错误输出。' }
