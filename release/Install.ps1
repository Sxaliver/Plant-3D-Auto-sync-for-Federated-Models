param([switch]$ForceReportSettings)
$ErrorActionPreference = 'Stop'
chcp 65001 > $null
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
# 安装时不关闭任何宿主；新版由用户保存并重启 Plant 后加载。
$bundleSource = Join-Path $PSScriptRoot 'IWMF.PlantModelSync.bundle'
if (-not (Test-Path -LiteralPath $bundleSource)) { throw 'Missing plugin bundle.' }
$bundleTarget = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\IWMF.PlantModelSync.bundle'
if (Test-Path -LiteralPath $bundleTarget) {
    Copy-Item -LiteralPath $bundleTarget -Destination ($bundleTarget + '.backup-' + (Get-Date -Format yyyyMMdd-HHmmss)) -Recurse
}
New-Item -ItemType Directory -Path $bundleTarget -Force | Out-Null
Copy-Item -Path (Join-Path $bundleSource '*') -Destination $bundleTarget -Recurse -Force
$storage = Join-Path $env:LOCALAPPDATA 'IWMF\PlantModelSync'
$templateTarget = Join-Path $storage 'Logs\Template'
$compilerTarget = Join-Path $storage 'Tools\Tectonic\0.17.0'
New-Item -ItemType Directory -Path $templateTarget,$compilerTarget -Force | Out-Null
# 自定义模板默认不覆盖；安装包模板无公司标识与合同数据。
$templateFile = Join-Path $templateTarget 'P3D_COBie_Guide_Template.tex'
if (-not (Test-Path -LiteralPath $templateFile)) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Template\P3D_COBie_Guide_Template.tex') -Destination $templateFile
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Tools\tectonic.exe') -Destination $compilerTarget -Force
$reportSettings = Join-Path $storage 'report-settings.json'
if ((Test-Path -LiteralPath $reportSettings) -and -not $ForceReportSettings) {
    Write-Output 'Existing report-settings.json preserved. Use -ForceReportSettings to replace it with a backup.'
} else {
    if (Test-Path -LiteralPath $reportSettings) { Copy-Item -LiteralPath $reportSettings -Destination ($reportSettings + '.backup-' + (Get-Date -Format yyyyMMdd-HHmmss)) }
    $config = @{ TemplateDirectory=$templateTarget; CompilerPath=(Join-Path $compilerTarget 'tectonic.exe'); TimeoutSeconds=300 } | ConvertTo-Json
    [IO.File]::WriteAllText($reportSettings,$config,[Text.UTF8Encoding]::new($false))
}
Write-Output 'Installed. Save work and restart Plant 3D 2024, then run SYNC. Configure your own folders and insertion coordinates.'
Write-Output 'First PDF compilation needs Internet access to download the Tectonic TeX bundle. No APS Client ID is required.'
