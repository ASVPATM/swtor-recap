# Runs only on a disposable GitHub Windows runner. Never installs on a user's PC.
param([string]$PackageDir = 'artifacts/SwtorRecap-win-x64', [string]$Installer = 'artifacts/SwtorRecap-0.2.0-Setup.exe')
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or !$IsWindows) { throw 'Use a disposable Windows GitHub Actions runner.' }
$package = (Resolve-Path $PackageDir).Path
$setup = (Resolve-Path $Installer).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('swtor-package-' + [guid]::NewGuid().ToString('N'))
$installed = Join-Path $testRoot 'Installed App With Spaces'
$dataRoot = Join-Path $testRoot 'Isolated History'
$evidence = Join-Path (Get-Location) '.artifacts/package-smoke'
New-Item -ItemType Directory -Force -Path $dataRoot,$evidence | Out-Null
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RecapWindowProbe {
  [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint parameter, out Rect rect, uint flags);
}
'@
Add-Type -AssemblyName System.Drawing
function Require($condition, $message) { if (!$condition) { throw $message } }
function Request($command, $epoch = $null) {
    $normalized = [IO.Path]::GetFullPath($dataRoot).TrimEnd([IO.Path]::DirectorySeparatorChar).ToUpperInvariant()
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($normalized))).ToLowerInvariant()
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', ('SwtorRecap.v1.' + $hash.Substring(0,24)), [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)
        $request = @{protocolVersion=1;requestId=[guid]::NewGuid().ToString('N');command=$command;parameters=@{};expectedEpoch=$epoch} | ConvertTo-Json -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($request)
        $pipe.Write([BitConverter]::GetBytes([int]$bytes.Length),0,4); $pipe.Write($bytes,0,$bytes.Length); $pipe.Flush()
        $header = [byte[]]::new(4); $pipe.ReadExactly($header,0,4)
        $length = [BitConverter]::ToInt32($header,0); Require ($length -gt 0 -and $length -lt 1048576) 'Invalid response frame'
        $body = [byte[]]::new($length); $pipe.ReadExactly($body,0,$length)
        $response = [Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json
        Require $response.success $response.error
        return $response
    } finally { $pipe.Dispose() }
}
$desktop = $null
$agentIds = @()
$report = @{ install=$false; portableLaunch=$false; installedLaunch=$false; shortcut=$false; duplicateLaunch=$false; uninstall=$false; historyPreserved=$false }
try {
    $process = Start-Process $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="'+$installed+'"'),'/TASKS=') -PassThru -Wait
    Require ($process.ExitCode -eq 0) 'Installer failed'
    Require (Test-Path (Join-Path $installed 'SwtorRecap.exe')) 'Installed app missing'
    $report.install = $true
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'SWTOR Recap.lnk'
    Require (Test-Path $shortcut) 'Start menu shortcut missing'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    Require ($link.TargetPath -eq (Join-Path $installed 'SwtorRecap.exe')) 'Wrong shortcut target'
    Require ($link.IconLocation -like '*SwtorRecap.exe*') 'Shortcut icon missing'
    $report.shortcut = $true
    $icon = [Drawing.Icon]::ExtractAssociatedIcon((Join-Path $installed 'SwtorRecap.exe'))
    Require ($null -ne $icon) 'Executable icon missing'
    $icon.ToBitmap().Save((Join-Path $evidence 'windows-icon.png')); $icon.Dispose()
    foreach ($variant in @(@{name='portable';path=$package},@{name='installed';path=$installed})) {
        $desktop = Start-Process (Join-Path $variant.path 'SwtorRecap.exe') -ArgumentList @('--data-dir',('"'+$dataRoot+'"')) -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(40)
        $opened = $false
        do {
            Start-Sleep -Milliseconds 300
            $desktop.Refresh()
            Require (!$desktop.HasExited) 'Dashboard exited during startup'
            if ($desktop.MainWindowHandle -ne 0) {
                $rect = [RecapWindowProbe+Rect]::new()
                [void][RecapWindowProbe]::GetWindowRect($desktop.MainWindowHandle,[ref]$rect)
                if (($rect.Right-$rect.Left) -ge 740) { $opened=$true; break }
            }
        } while ([DateTime]::UtcNow -lt $deadline)
        Require $opened 'The loading window did not open the dashboard'
        $workArea = [RecapWindowProbe+Rect]::new()
        Require ([RecapWindowProbe]::SystemParametersInfo(0x30,0,[ref]$workArea,0)) 'Could not read desktop work area'
        Require ($rect.Left -ge $workArea.Left -and $rect.Top -ge $workArea.Top -and $rect.Right -le $workArea.Right -and $rect.Bottom -le $workArea.Bottom) 'Dashboard extends beyond the usable desktop'
        $report.windowFitsDesktop = $true
        $snapshot = Request 'Snapshot'
        Require ($snapshot.payload.parserVersion -eq '1.1.0') 'Wrong tracker version'
        Require ($null -eq $snapshot.payload.current) 'An idle app must not claim an active recording'
        Require (!$snapshot.payload.settings.startWithWindows) 'Isolated launch changed startup preference'
        $report[($variant.name+'Launch')] = $true
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($desktop.Id)" | Select-Object ProcessId,Name)
        $agentIds = @($children | Where-Object Name -eq 'SwtorRecap.Agent.exe' | ForEach-Object ProcessId)
        Require ($agentIds.Count -eq 1) 'Expected one tracker agent'
        Require (@($children | Where-Object Name -in @('cmd.exe','conhost.exe','powershell.exe')).Count -eq 0) 'Launch spawned a console'
        $again = Start-Process (Join-Path $variant.path 'SwtorRecap.exe') -ArgumentList @('--data-dir',('"'+$dataRoot+'"')) -PassThru
        Require ($again.WaitForExit(10000) -and $again.ExitCode -eq 0) 'Duplicate launch did not activate the existing dashboard'
        $report.duplicateLaunch = $true
        [void][RecapWindowProbe]::SetForegroundWindow($desktop.MainWindowHandle)
        Start-Sleep -Milliseconds 300
        $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size)
        $bitmap.Save((Join-Path $evidence ($variant.name+'-window.png')))
        $graphics.Dispose(); $bitmap.Dispose()
        Require ($desktop.CloseMainWindow()) 'Could not close dashboard'
        Require ($desktop.WaitForExit(10000)) 'Dashboard did not close'
        # Closing the dashboard must leave the background tracker available.
        $snapshot = Request 'Snapshot'
        try { $null = Request 'Quit' $snapshot.revision.historyEpoch } catch [System.IO.EndOfStreamException] { }
        foreach ($agentId in $agentIds) {
            $agent = Get-Process -Id $agentId -ErrorAction SilentlyContinue
            if ($agent) { Require ($agent.WaitForExit(10000)) 'Tracker did not quit' }
        }
        $desktop = $null; $agentIds = @()
    }
    $history = Join-Path $dataRoot 'recap.db'
    $before = (Get-FileHash $history -Algorithm SHA256).Hash
    $remove = Start-Process (Join-Path $installed 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -PassThru -Wait
    Require ($remove.ExitCode -eq 0) 'Uninstaller failed'
    Require (!(Test-Path (Join-Path $installed 'SwtorRecap.exe'))) 'Uninstaller left the application'
    Require (!(Test-Path $shortcut)) 'Uninstaller left the Start menu shortcut'
    $report.uninstall = $true
    Require ((Get-FileHash $history -Algorithm SHA256).Hash -eq $before) 'History changed during uninstall'
    $report.historyPreserved = $true
    $report.signature = (Get-AuthenticodeSignature $setup).Status.ToString()
    $report.ok = $true
} finally {
    if ($desktop -and !$desktop.HasExited) { Stop-Process -Id $desktop.Id -Force }
    foreach ($agentId in $agentIds) { Stop-Process -Id $agentId -Force -ErrorAction SilentlyContinue }
    $report | ConvertTo-Json | Set-Content (Join-Path $evidence 'result.json')
}
$report | ConvertTo-Json
