param([Parameter(Mandatory=$true)][int]$ProcessId,[int]$Seconds=600,[string]$Output='swtor-recap-resource-measurement.json')
$ErrorActionPreference='Stop'
$samples=@()
$process=Get-Process -Id $ProcessId
$started=Get-Date
$initialCpu=$process.TotalProcessorTime.TotalSeconds
$previousCpu=$initialCpu
$previousTime=$started
for($i=0;$i -lt $Seconds;$i++) {
    Start-Sleep -Seconds 1
    $process=Get-Process -Id $ProcessId
    $now=Get-Date
    $cpu=$process.TotalProcessorTime.TotalSeconds
    $samples+=@{elapsed=($now-$started).TotalSeconds;oneCoreCpuPercent=100*($cpu-$previousCpu)/($now-$previousTime).TotalSeconds;privateBytes=$process.PrivateMemorySize64;workingSet=$process.WorkingSet64;handles=$process.HandleCount;threads=$process.Threads.Count}
    $previousCpu=$cpu;$previousTime=$now
}
$values=@($samples | ForEach-Object {$_.oneCoreCpuPercent} | Sort-Object)
$mean=100*($previousCpu-$initialCpu)/($previousTime-$started).TotalSeconds
@{os=[Environment]::OSVersion.VersionString;logicalProcessors=[Environment]::ProcessorCount;processId=$ProcessId;seconds=($previousTime-$started).TotalSeconds;meanOneCoreCpuPercent=$mean;meanMachineCpuPercent=$mean/[Environment]::ProcessorCount;p95OneCoreCpuPercent=$values[[Math]::Min($values.Count-1,[int]($values.Count*.95))];p99OneCoreCpuPercent=$values[[Math]::Min($values.Count-1,[int]($values.Count*.99))];samples=$samples;note='Resource measurements only. Compare equivalent logging-on SWTOR trials with tracker closed/open using PresentMon separately.'} | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 $Output
