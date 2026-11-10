# Isolated upgrade and mixed-version recovery. Only run on disposable CI Windows.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or !$IsWindows) { throw 'Use a disposable Windows GitHub Actions runner.' }
$package = (Resolve-Path 'artifacts/SwtorRecap-win-x64').Path
$setup = (Resolve-Path 'artifacts/SwtorRecap-0.3.0-Setup.exe').Path
$baseline = (Resolve-Path '.artifacts/update-baseline/SwtorRecap-0.2.0-Setup.exe').Path
if ((Get-FileHash $baseline -Algorithm SHA256).Hash -ne 'EB152A23A3CE4CE425F3254C951097466E1E14EB74165F9E519DD9272A6EBFD1') { throw 'Baseline checksum mismatch' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('swtor-update-' + [guid]::NewGuid().ToString('N'))
$installed = Join-Path $root 'Installed App'
$dataRoot = Join-Path $root 'History'
$work = Join-Path $root 'Update Files'
$evidence = Join-Path (Get-Location) '.artifacts/package-smoke/update'
New-Item -ItemType Directory -Force -Path $work,$dataRoot,$evidence | Out-Null
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
function Require($condition,$message) { if (!$condition) { throw $message } }
function Request($command,$epoch=$null,$parameters=@{}) {
    $normalized=[IO.Path]::GetFullPath($dataRoot).TrimEnd([IO.Path]::DirectorySeparatorChar).ToUpperInvariant()
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($normalized))).ToLowerInvariant()
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',('SwtorRecap.v1.'+$hash.Substring(0,24)),[IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)
        $bytes=[Text.Encoding]::UTF8.GetBytes((@{protocolVersion=1;requestId=[guid]::NewGuid().ToString('N');command=$command;parameters=$parameters;expectedEpoch=$epoch}|ConvertTo-Json -Depth 8 -Compress))
        $pipe.Write([BitConverter]::GetBytes([int]$bytes.Length),0,4);$pipe.Write($bytes,0,$bytes.Length);$pipe.Flush()
        $header=[byte[]]::new(4);$pipe.ReadExactly($header,0,4);$length=[BitConverter]::ToInt32($header,0)
        Require ($length -gt 0 -and $length -lt 1048576) 'Invalid frame'
        $body=[byte[]]::new($length);$pipe.ReadExactly($body,0,$length);$response=[Text.Encoding]::UTF8.GetString($body)|ConvertFrom-Json
        Require $response.success $response.error;return $response
    } finally { $pipe.Dispose() }
}
function Wait-Snapshot($version) {
    for($i=0;$i -lt 80;$i++) {
        try { $s=Request 'Snapshot';if(!$version -or $s.payload.appVersion -eq $version){return $s} } catch {}
        Start-Sleep -Milliseconds 250
    }
    throw 'Tracker did not become ready'
}
function Stop-Tracker {
    $s=Request 'Snapshot';try{$null=Request 'Quit' $s.revision.historyEpoch}catch [System.IO.EndOfStreamException]{}
    for($i=0;$i -lt 40;$i++){
        try{$f=[IO.File]::Open((Join-Path $dataRoot 'writer.lock'),'OpenOrCreate','ReadWrite','None');$f.Dispose();return}catch{Start-Sleep -Milliseconds 250}
    }
    throw 'Tracker did not stop'
}
$desktop=$null;$helper=$null;$report=@{ok=$false}
try {
    $old=Start-Process $baseline -ArgumentList @('/VERYSILENT','/NORESTART','/SP-',('/DIR="'+$installed+'"')) -Wait -PassThru
    Require ($old.ExitCode -eq 0) 'Baseline installation failed'
    & dotnet src/SwtorRecap.Tools/bin/Release/net10.0/SwtorRecap.Tools.dll seed-preview --data-dir $dataRoot | Out-Null
    Require ($LASTEXITCODE -eq 0) 'Synthetic history seeding failed'
    $desktop=Start-Process (Join-Path $installed 'SwtorRecap.exe') -ArgumentList @('--data-dir',('"'+$dataRoot+'"')) -PassThru
    $null=Wait-Snapshot $null
    for($i=0;$i -lt 60;$i++){$desktop.Refresh();if($desktop.MainWindowHandle -ne 0 -and $desktop.CloseMainWindow()){break};Start-Sleep -Milliseconds 250}
    Require ($desktop.WaitForExit(10000)) 'Old dashboard did not close'
    $desktop=Start-Process (Join-Path $package 'SwtorRecap.exe') -ArgumentList @('--data-dir',('"'+$dataRoot+'"')) -PassThru
    $button=$null
    for($i=0;$i -lt 80 -and !$button;$i++){
        $desktop.Refresh()
        if($desktop.MainWindowHandle -ne 0){
            $window=[Windows.Automation.AutomationElement]::FromHandle($desktop.MainWindowHandle)
            $button=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'startup-retry'))
        }
        if(!$button){Start-Sleep -Milliseconds 250}
    }
    Require ($button -and $button.Current.Name -eq 'Restart matching tracker') 'Mixed versions were not blocked'
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $snapshot=Wait-Snapshot '0.3.0';$report.mixedVersionRecovery=$true
    $before=Request 'Recap' $null @{year=2026}
    $backup=Request 'PrepareUpdate' $snapshot.revision.historyEpoch
    Require (Test-Path $backup.payload.path) 'Verified recovery backup missing'
    Copy-Item (Join-Path $package 'agent') (Join-Path $work 'helper') -Recurse
    $installerPath=Join-Path $work 'SwtorRecap-0.3.0-Setup.exe';Copy-Item $setup $installerPath
    $plan=@{release=@{version='0.3.0';url='https://github.com/ASVPATM/swtor-recap/releases/download/v0.3.0/SwtorRecap-0.3.0-Setup.exe';sha256=(Get-FileHash $installerPath -Algorithm SHA256).Hash;size=(Get-Item $installerPath).Length};currentVersion='0.2.0';installerPath=$installerPath;installDirectory=$installed;previousExecutable=(Join-Path $installed 'SwtorRecap.exe');parentPid=$desktop.Id;parentStartedTicks=$desktop.StartTime.ToUniversalTime().Ticks;backupPath=$backup.payload.path;dataRoot=$dataRoot;isolated=$true}
    $planPath=Join-Path $work 'install-plan.json';$plan|ConvertTo-Json -Depth 8|Set-Content $planPath
    $helper=Start-Process (Join-Path $work 'helper/SwtorRecap.Agent.exe') -ArgumentList @('--apply-update',('"'+$planPath+'"')) -PassThru
    for($i=0;$i -lt 40 -and !(Test-Path ($planPath+'.ready'));$i++){Start-Sleep -Milliseconds 250}
    Require (Test-Path ($planPath+'.ready')) 'Update helper did not start'
    Stop-Tracker
    $desktop.Refresh()
    Require ($desktop.CloseMainWindow()) 'Dashboard did not close for update';Require ($desktop.WaitForExit(10000)) 'Dashboard remained open'
    Require ($helper.WaitForExit(120000)) 'Installer helper did not finish'
    $result=Get-Content (Join-Path $work 'result.json') -Raw|ConvertFrom-Json;Require $result.success $result.error
    $report.installedUpgrade=$true
    $null=Wait-Snapshot '0.3.0';$after=Request 'Recap' $null @{year=2026}
    Require (($before.payload.totals|ConvertTo-Json -Depth 8 -Compress) -eq ($after.payload.totals|ConvertTo-Json -Depth 8 -Compress)) 'Upgrade changed recorded totals'
    $report.historyPreserved=$true;$report.backupVerified=$true;$report.reopened=$true
    $desktop=Get-Process SwtorRecap | Where-Object Path -eq (Join-Path $installed 'SwtorRecap.exe') | Select-Object -First 1
    Require ($null -ne $desktop) 'Updated dashboard not found';$null=$desktop.CloseMainWindow();$null=$desktop.WaitForExit(10000);Stop-Tracker
    # A modified download must never reach the installer; the old app is reopened.
    [IO.File]::WriteAllText($installerPath,'corrupt')
    $plan.parentPid=2147483647;$plan.parentStartedTicks=0;$plan|ConvertTo-Json -Depth 8|Set-Content $planPath
    $helper=Start-Process (Join-Path $work 'helper/SwtorRecap.Agent.exe') -ArgumentList @('--apply-update',('"'+$planPath+'"')) -PassThru -Wait
    $rejected=Get-Content (Join-Path $work 'result.json') -Raw|ConvertFrom-Json;Require (!$rejected.success -and $rejected.error -like '*verification failed*') 'Corrupt update was not rejected by checksum verification'
    $null=Wait-Snapshot '0.3.0';$report.corruptionRejected=$true
    $desktop=Get-Process SwtorRecap|Where-Object Path -eq (Join-Path $installed 'SwtorRecap.exe')|Select-Object -First 1
    $null=$desktop.CloseMainWindow();$null=$desktop.WaitForExit(10000);Stop-Tracker
    $uninstall=Start-Process (Join-Path $installed 'unins000.exe') -ArgumentList @('/VERYSILENT','/NORESTART') -Wait -PassThru
    Require ($uninstall.ExitCode -eq 0) 'Cleanup uninstall failed';$report.ok=$true
} finally {
    if($desktop -and !$desktop.HasExited){Stop-Process -Id $desktop.Id -Force}
    if($helper -and !$helper.HasExited){Stop-Process -Id $helper.Id -Force}
    $report|ConvertTo-Json|Set-Content (Join-Path $evidence 'result.json')
    if(Test-Path (Join-Path $work 'install.log')){Copy-Item (Join-Path $work 'install.log') $evidence}
}
$report|ConvertTo-Json
