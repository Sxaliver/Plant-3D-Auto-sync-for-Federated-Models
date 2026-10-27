param(
    [Parameter(Mandatory=$true)][string]$LocalRoot,
    [Parameter(Mandatory=$true)][string]$CloudRoot,
    [string]$EvidenceDirectory,
    [string]$SettingsPath = (Join-Path $env:LOCALAPPDATA 'IWMF\PlantModelSync\settings.json'),
    [switch]$Apply,
    [switch]$PreserveStaleLocks
)
$ErrorActionPreference = 'Stop'
chcp 65001 > $null
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$modelRoot = [IO.Path]::GetFullPath($LocalRoot).TrimEnd('\')
$connectorRoot = [IO.Path]::GetFullPath($CloudRoot).TrimEnd('\')
if ($PreserveStaleLocks -and (Get-Process -Name acad,accoreconsole -ErrorAction SilentlyContinue)) { throw '有 CAD 进程运行，不能将锁文件判定为陈旧。' }
if (-not [IO.Directory]::Exists($modelRoot) -or -not [IO.Directory]::Exists($connectorRoot)) { throw '本地或云端根目录不存在。' }
$specs = @(
    @('OCF','114101-SIF-M3-M-9421_OCF'),
    @('AIR SYSTEM','114101-SIF-M3-M-9422_COMPRESSED AIR SYSTEM'),
    @('CENTRAL VACUUM','114101-SIF-M3-M-9423_CENTRAL VACUUM CLEANING'),
    @('GYPSUM DEWATERING SYSTEM','114101-SIF-M3-M-9424_GYPSUM DEWATERING SYSTEM'),
    @('COOLING WATER','114101-SIF-M3-M-9425_COOLING WATER SYSTEM'),
    @('LIME SILO','114101-SIF-M3-M-9426_LIME SILO SLURRY PREP SYSTEM'),
    @('PWWTP','114101-SIF-M3-M-9427_PWWTP'),
    @('CHEMICAL','114101-SIF-M3-M-9428_CHEMOCAL AREA EQUIPMENT'),
    @('SERVICE WATER','114101-SIF-M3-M-9429_SERVICE WATER SYSTEM'),
    @('MIC','114101-SIF-M3-M-9431_MICELLANEOUS'),
    @('FWTF','114101-FWTF-M3-MLP-9460_Piping Model'),
    @('114101-FIF-M3-9428_CHEMOCAL AREA','')
)
$steps = @($specs | ForEach-Object {
    $old = Join-Path $modelRoot $_[0]
    $leaf = if ($_[1]) { 'local_' + $_[1] } else { 'local_history_9428_114101-FIF-M3-9428_CHEMOCAL AREA' }
    [PSCustomObject]@{ OldName = $_[0]; CloudName = $_[1]; OldPath = $old; NewPath = (Join-Path $modelRoot $leaf); History = (-not [bool]$_[1]); State = 'planned' }
})
# 移动前逐一校验绝对路径、链接、目标占用及文件锁，不覆盖已有目录。
foreach ($step in $steps) {
    foreach ($path in @($step.OldPath, $step.NewPath)) {
        $resolved = [IO.Path]::GetFullPath($path)
        if (-not $resolved.StartsWith($modelRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetDirectoryName($resolved) -ne $modelRoot) { throw "目标不在指定根目录第一层：$resolved" }
    }
    if (-not [IO.Directory]::Exists($step.OldPath)) { throw "待迁移目录不存在：$($step.OldPath)" }
    if ((Get-Item -LiteralPath $step.OldPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "原目录已经是链接，拒绝重复迁移：$($step.OldPath)" }
    if (Test-Path -LiteralPath $step.NewPath) { throw "新目录已存在，不覆盖：$($step.NewPath)" }
    if ($step.CloudName -and -not [IO.Directory]::Exists((Join-Path $connectorRoot $step.CloudName))) { throw "云端系统目录不存在：$($step.CloudName)" }
    foreach ($file in Get-ChildItem -LiteralPath $step.OldPath -File -Recurse -Filter '*.dwg') {
        foreach ($lock in @([IO.Path]::ChangeExtension($file.FullName,'.dwl'), [IO.Path]::ChangeExtension($file.FullName,'.dwl2'))) {
            if (Test-Path -LiteralPath $lock) {
                if (-not $PreserveStaleLocks -or (Get-Item -LiteralPath $lock -Force).LastWriteTimeUtc -gt [DateTime]::UtcNow.AddDays(-1)) { throw "图纸有锁文件，请先关闭相关图纸：$lock" }
                Write-Output "保留历史锁文件，不删除：$lock"
            }
        }
        $handle = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $handle.Dispose()
    }
}
$steps | Select-Object OldName,@{n='NewName';e={[IO.Path]::GetFileName($_.NewPath)}},History | Format-Table -AutoSize
if (-not $Apply) { Write-Output '仅预览，未改名或写入。添加 -Apply 后执行。'; return }
if (-not $EvidenceDirectory) { throw '执行迁移必须指定独立 EvidenceDirectory，以保存恢复清单。' }
$auditRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath (Join-Path $auditRoot 'migration.json')) { throw '此迁移证据目录已有日志，请使用新目录。' }
New-Item -ItemType Directory -Path $auditRoot -Force | Out-Null
$encoding = [Text.UTF8Encoding]::new($false)
$records = @()
foreach ($step in $steps) {
    foreach ($file in Get-ChildItem -LiteralPath $step.OldPath -File -Recurse -Force) {
        $suffix = $file.FullName.Substring($step.OldPath.Length)
        $records += [PSCustomObject]@{ Before = $file.FullName; After = ($step.NewPath + $suffix); Sha256 = (Get-FileHash -LiteralPath $file.FullName).Hash }
    }
}
$assemblyDir = Join-Path $modelRoot '单系统总装文件'
$assemblyHashes = @(Get-ChildItem -LiteralPath $assemblyDir -File -Filter '*.dwg' | ForEach-Object { [PSCustomObject]@{Path=$_.FullName;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} })
$configBackup = Join-Path $auditRoot 'settings-before.json'
$hadConfig = Test-Path -LiteralPath $SettingsPath
if ($hadConfig) { Copy-Item -LiteralPath $SettingsPath -Destination $configBackup }
$receiptPath = Join-Path $auditRoot 'migration.json'
function Write-MigrationReceipt([string]$state,[string]$reason) {
    $receipt = [ordered]@{ state=$state;reason=$reason;recordedUtc=[DateTime]::UtcNow.ToString('o');localRoot=$modelRoot;cloudRoot=$connectorRoot;settings=$SettingsPath;settingsBackup=$configBackup;steps=$steps;files=$records;assemblies=$assemblyHashes }
    [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 20), $encoding)
}
Write-MigrationReceipt 'prepared' ''
try {
    foreach ($step in $steps) {
        Move-Item -LiteralPath $step.OldPath -Destination $step.NewPath
        $step.State = 'moved'; Write-MigrationReceipt 'running' ''
        New-Item -ItemType Junction -Path $step.OldPath -Target $step.NewPath | Out-Null
        $step.State = 'linked'; Write-MigrationReceipt 'running' ''
    }
    foreach ($record in $records) {
        if ((Get-FileHash -LiteralPath $record.After).Hash -ne $record.Sha256 -or (Get-FileHash -LiteralPath $record.Before).Hash -ne $record.Sha256) { throw "迁移后内容或兼容路径校验失败：$($record.After)" }
    }
    foreach ($record in $assemblyHashes) { if ((Get-FileHash -LiteralPath $record.Path).Hash -ne $record.Sha256) { throw "总装内容改变：$($record.Path)" } }
    $config = if ($hadConfig) { Get-Content -LiteralPath $SettingsPath -Encoding UTF8 -Raw | ConvertFrom-Json } else { [PSCustomObject]@{} }
    $aliases = @{}
    if ($config.PSObject.Properties.Name -contains 'LocalAliases') { foreach ($p in $config.LocalAliases.PSObject.Properties) { $aliases[$p.Name]=$p.Value } }
    foreach ($step in $steps) { $aliases[$step.OldPath]=$step.NewPath }
    foreach ($property in @{LocalAliases=$aliases;LocalModelsRoot=$modelRoot;ProjectScanFolder=$assemblyDir;ConnectorRoot=$connectorRoot}.GetEnumerator()) { $config | Add-Member -NotePropertyName $property.Key -NotePropertyValue $property.Value -Force }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($SettingsPath)) -Force | Out-Null
    $withBom = $false
    if ($hadConfig) { $bytes=[IO.File]::ReadAllBytes($SettingsPath); $withBom=$bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191 }
    $temp = $SettingsPath + '.migration.tmp'
    [IO.File]::WriteAllText($temp, ($config | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($withBom))
    if ($hadConfig) { [IO.File]::Replace($temp,$SettingsPath,(Join-Path $auditRoot 'settings-replaced.json')) } else { [IO.File]::Move($temp,$SettingsPath) }
    Write-MigrationReceipt 'verified' ''
    Write-Output "迁移及文件哈希核验完成；总装 DWG 未改写。清单：$receiptPath"
}
catch {
    $failure = $_.Exception.Message
    # 只删除已核实目标的兼容链接，不递归删除任何模型目录。
    foreach ($step in @($steps | Sort-Object { [array]::IndexOf($steps,$_) } -Descending)) {
        if ($step.State -eq 'linked') {
            $link = Get-Item -LiteralPath $step.OldPath -Force
            if (-not ($link.Attributes -band [IO.FileAttributes]::ReparsePoint) -or [IO.Path]::GetFullPath([string]$link.Target) -ne $step.NewPath) { throw "需手工恢复，兼容链接目标不符：$($step.OldPath)；原异常：$failure" }
            [IO.Directory]::Delete($step.OldPath)
        }
        if ($step.State -in @('linked','moved')) { Move-Item -LiteralPath $step.NewPath -Destination $step.OldPath; $step.State='restored' }
    }
    if ($hadConfig -and (Test-Path -LiteralPath $configBackup)) { Copy-Item -LiteralPath $configBackup -Destination $SettingsPath -Force }
    Write-MigrationReceipt 'rolled-back' $failure
    throw "迁移失败，目录已恢复：$failure"
}
