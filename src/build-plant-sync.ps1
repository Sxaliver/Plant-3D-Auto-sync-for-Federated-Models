param([switch]$Install)
$ErrorActionPreference = 'Stop'
chcp 65001 > $null
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$moduleRoot = Join-Path $PSScriptRoot 'Plant Model Sync'
& dotnet build (Join-Path $moduleRoot 'PlantModelSync.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Plant Model Sync 编译失败' }
$bundle = Join-Path $moduleRoot 'artifacts\Release-0.4.15\IWMF.PlantModelSync.bundle'
$buildId = (Get-FileHash -LiteralPath (Join-Path $moduleRoot 'bin\Release\net48\IWMF.PlantModelSync.dll')).Hash.Substring(0, 12)
$contents = Join-Path $bundle "Contents\$buildId"
New-Item -ItemType Directory -Path $contents -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $moduleRoot 'PackageContents.xml') -Destination $bundle -Force
$manifest = [xml](Get-Content -LiteralPath (Join-Path $bundle 'PackageContents.xml') -Encoding UTF8 -Raw)
$manifest.ApplicationPackage.Components.ComponentEntry.ModuleName = "./Contents/$buildId/IWMF.PlantModelSync.dll"
$writerSettings = [System.Xml.XmlWriterSettings]::new()
$writerSettings.Encoding = [System.Text.UTF8Encoding]::new($false)
$writerSettings.Indent = $true
$writer = [System.Xml.XmlWriter]::Create((Join-Path $bundle 'PackageContents.xml'), $writerSettings)
try { $manifest.Save($writer) } finally { $writer.Dispose() }
foreach ($name in @('IWMF.PlantModelSync.dll', 'Newtonsoft.Json.dll')) {
    Copy-Item -LiteralPath (Join-Path $moduleRoot "bin\Release\net48\$name") -Destination $contents -Force
}
Copy-Item -LiteralPath (Join-Path $moduleRoot 'README.md') -Destination $bundle -Force
$archive = Join-Path $moduleRoot 'artifacts\IWMF.PlantModelSync-0.4.15.zip'
Compress-Archive -Path $bundle -DestinationPath $archive -Force
if ($Install) {
    $target = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\IWMF.PlantModelSync.bundle'
    if (Test-Path -LiteralPath $target) {
        $backup = $target + '.backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
        Copy-Item -LiteralPath $target -Destination $backup -Recurse
    }
    New-Item -ItemType Directory -Path (Join-Path $target "Contents\$buildId") -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $bundle 'PackageContents.xml') -Destination $target -Force
    Copy-Item -LiteralPath (Join-Path $bundle 'README.md') -Destination $target -Force
    foreach ($name in @('IWMF.PlantModelSync.dll', 'Newtonsoft.Json.dll')) {
        $source = Join-Path $contents $name
        $destination = Join-Path $target "Contents\$buildId\$name"
        if (-not (Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }
        if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) { throw "安装哈希不符：$name" }
    }
    Write-Output "安装及 SHA-256 核验完成：$target"
}
Write-Output "插件包：$bundle"
Write-Output "安装包：$archive"
