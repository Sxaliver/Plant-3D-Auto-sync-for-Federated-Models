param([string]$ConfigPath=(Join-Path $PSScriptRoot 'weekly-config.json'),[ValidatePattern('^94\d{2}$')][string]$SystemCode,[switch]$ForceExport,[switch]$DryRun,[switch]$VisibleTest,[ValidateRange(1,240)][int]$MaxMinutes=90)
$ErrorActionPreference='Stop'
chcp 65001 > $null
$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$config=Get-Content -LiteralPath $ConfigPath -Encoding UTF8 -Raw | ConvertFrom-Json
$mutex=[Threading.Mutex]::new($false,'Local\IWMF_PlantModelSync_Weekly')
if(-not $mutex.WaitOne(0)){$mutex.Dispose();throw '已有周更启动器正在运行，未重复执行。'}
$worker=$null
try{
    $activeFile=Join-Path $config.Storage 'weekly-active.json'
    if(Test-Path -LiteralPath $activeFile){
        $active=Get-Content -LiteralPath $activeFile -Encoding UTF8 -Raw | ConvertFrom-Json
        $previous=Get-Process -Id $active.ProcessId -ErrorAction SilentlyContinue
        if($previous -and $previous.StartTime.ToUniversalTime().ToString('o') -eq $active.StartTimeUtc){throw ('前一周更进程仍在运行，未重复启动。PID='+$active.ProcessId)}
    }
    $runId='weekly-'+[DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
    $runDir=Join-Path (Join-Path $config.Storage 'WeeklyRuns') $runId
    New-Item -ItemType Directory -Path $runDir -Force | Out-Null
    Copy-Item -LiteralPath $config.SettingsPath -Destination (Join-Path $runDir 'settings-snapshot.json')
    $snapshot=Get-Content -LiteralPath (Join-Path $runDir 'settings-snapshot.json') -Encoding UTF8 -Raw|ConvertFrom-Json
    foreach($pair in @(@('ExpectedLocalModelsRoot','LocalModelsRoot'),@('ExpectedScopeFolder','ProjectScanFolder'),@('ExpectedConnectorRoot','ConnectorRoot'))){
        $expected=$config.($pair[0]);$actual=$snapshot.($pair[1])
        if($expected -and [IO.Path]::GetFullPath($expected).TrimEnd('\') -ne [IO.Path]::GetFullPath($actual).TrimEnd('\')){throw ('交互插件目录已改变，为避免周更操作其他项目已停止：'+$pair[1])}
    }
    $request=[ordered]@{SettingsPath=(Join-Path $runDir 'settings-snapshot.json');Storage=$config.Storage;OutputRoot=$config.OutputRoot;RunDirectory=$runDir;ProcessId=0;DryRun=[bool]$DryRun;LegacyConnectorRoots=@($config.LegacyConnectorRoots);TrustedRepairReceipts=@($config.TrustedRepairReceipts)}
    $request.ForceExport=[bool]$ForceExport
    $request.SystemCodes=@()
    $request.AssemblyFiles=@()
    if($SystemCode){
        $request.SystemCodes=@($SystemCode)
        $request.AssemblyFiles=@(Get-ChildItem -LiteralPath $snapshot.ProjectScanFolder -Filter '*.dwg' -File | Where-Object {$_.BaseName -match ('(?<!\d)'+[Regex]::Escape($SystemCode)+'(?!\d)')} | Select-Object -ExpandProperty FullName)
        if($request.AssemblyFiles.Count -eq 0){throw ('未找到系统总装：'+$SystemCode)}
        Write-Output ('SystemScope='+$SystemCode)
        Write-Output ($request.AssemblyFiles -join "`n")
    }
    $requestPath=Join-Path $runDir 'request.json'
    function SaveRequest{
        $temp=$requestPath+'.tmp'
        [IO.File]::WriteAllText($temp,($request|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temp -Destination $requestPath -Force
    }
    SaveRequest
    $manifestPath=Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\IWMF.PlantModelSync.bundle\PackageContents.xml'
    $manifest=[xml](Get-Content -LiteralPath $manifestPath -Encoding UTF8 -Raw)
    if([version]$manifest.ApplicationPackage.AppVersion -lt [version]'0.2.7'){throw '请先安装支持 PMSWEEKLYRUN 的0.2.7或更新版本。'}
    # 只在本次启动的进程加载插件，不向用户现有 Plant 发送任何命令。
    $dll=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($manifestPath)) $manifest.ApplicationPackage.Components.ComponentEntry.ModuleName))
    $script=Join-Path $runDir 'run.scr'
    $commands='_.NETLOAD' + "`n" + '"'+$dll.Replace('\','/')+'"'+"`nPMSWEEKLYRUN`n"
    [IO.File]::WriteAllText($script,$commands,[Text.UTF8Encoding]::new($false))
    $exe='C:\Program Files\Autodesk\AutoCAD 2024\acad.exe'
    $template=Join-Path $env:LOCALAPPDATA 'Autodesk\Autodesk AutoCAD Plant 3D 2024\R24.3\enu\Template\acadiso.dwt'
    if(-not (Test-Path -LiteralPath $template)){throw 'Plant 3D空白模板不存在。'}
    $arguments=@('/nologo','/product','PLNT3D','/language','en-US','/t',('"'+$template+'"'),'/b',('"'+$script+'"'))
    if($config.Engine -eq 'CoreConsole'){
        if(-not $runDir.Contains('\Demos\') -and -not $config.Storage.Contains('\Demos\')){throw 'CoreConsole仅用于隔离验证，不代替生产NWC导出。'}
        $exe='C:\Program Files\Autodesk\AutoCAD 2024\accoreconsole.exe'
        $arguments=@('/s',('"'+$script+'"'),'/l','en-US')
    }
    $oldRequest=$env:IWMF_PMS_WEEKLY_REQUEST
    try{
        $env:IWMF_PMS_WEEKLY_REQUEST=$requestPath
        if($VisibleTest){
            if(-not $config.Storage.Contains('\Demos\')){throw 'VisibleTest只允许已获授权的隔离验证。'}
            $worker=Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -WindowStyle Normal -PassThru
        }elseif($config.ShowWindow -and $config.Engine -ne 'CoreConsole'){
            # 用户已确认每周允许显示独立Plant窗口，完成后只关闭该独立进程。
            $worker=Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -WindowStyle Normal -PassThru
        }else{$worker=Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory ([IO.Path]::GetDirectoryName($exe)) -WindowStyle Hidden -PassThru}
    }finally{if($null -eq $oldRequest){Remove-Item Env:IWMF_PMS_WEEKLY_REQUEST -ErrorAction SilentlyContinue}else{$env:IWMF_PMS_WEEKLY_REQUEST=$oldRequest}}
    $request.ProcessId=$worker.Id;SaveRequest
    [IO.File]::WriteAllText($activeFile,(@{ProcessId=$worker.Id;StartTimeUtc=$worker.StartTime.ToUniversalTime().ToString('o');RunDirectory=$runDir}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Write-Output ('RunDirectory='+$runDir)
    Write-Output ('WorkerPID='+$worker.Id)
    $resultPath=Join-Path $runDir 'result.json'
    $deadline=[DateTime]::UtcNow.AddMinutes($MaxMinutes)
    $result=$null
    while([DateTime]::UtcNow -lt $deadline){
        if(Test-Path -LiteralPath $resultPath){try{$result=Get-Content -LiteralPath $resultPath -Encoding UTF8 -Raw|ConvertFrom-Json}catch{$result=$null};if($result.Completed){break}}
        $worker.Refresh();if($worker.HasExited){break}
        Start-Sleep -Seconds 2
    }
    if(-not $result -or -not $result.Completed){throw ('周更未完成或超时；未强制关闭任何CAD。请检查 '+$runDir)}
    $result | ConvertTo-Json -Depth 8
    # 仅在插件确认全部任务结束、宿主没有用户图纸时，清理本次进程。
    $shutdownPath=Join-Path $runDir 'shutdown.json'
    $shutdownDeadline=[DateTime]::UtcNow.AddSeconds(12)
    while([DateTime]::UtcNow -lt $shutdownDeadline){$worker.Refresh();if($worker.HasExited){break};Start-Sleep -Milliseconds 500}
    $worker.Refresh()
    if(-not $worker.HasExited){
        $shutdown=if(Test-Path -LiteralPath $shutdownPath){Get-Content -LiteralPath $shutdownPath -Encoding UTF8 -Raw|ConvertFrom-Json}else{$null}
        $owned=Get-CimInstance Win32_Process -Filter ('ProcessId='+$worker.Id)
        if($shutdown.SafeToTerminate -and $shutdown.ProcessId -eq $worker.Id -and $owned.ExecutablePath -eq $exe -and $owned.CommandLine.Contains($script)){
            Stop-Process -Id $worker.Id
            Write-Output ('已清理本次完成任务的空白宿主，PID='+$worker.Id)
        }else{throw ('任务结果已记录，但宿主含未确认窗口或尚未安全退出，未强制关闭：PID='+$worker.Id)}
    }
    if($result.Error){throw $result.Error}
}catch{
    if($runDir){
        $failure=[ordered]@{State='启动器失败 / 未完成';Completed=$true;Error=$_.Exception.ToString();RunDirectory=$runDir;ProcessId=$(if($worker){$worker.Id}else{0})}
        [IO.File]::WriteAllText((Join-Path $runDir 'launcher-error.json'),($failure|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
        $logDir=Join-Path $config.Storage 'Logs\Weekly';New-Item -ItemType Directory -Path $logDir -Force|Out-Null
        $log=Join-Path $logDir ($runId+'-launcher-error.json')
        $entry=@{Format='IWMF.OneClickUpdate.v1';State=$failure.State;Rows=@(@{Time=[DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd HH:mm:ss zzz');Operation='周更启动器';Local=$runDir;State='失败，待处理';Details=$failure.Error})}
        [IO.File]::WriteAllText($log,($entry|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
    }
    throw
}finally{$mutex.ReleaseMutex();$mutex.Dispose()}
