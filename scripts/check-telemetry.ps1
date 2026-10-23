param(
    [string]$SourceDirectory = 'release-next',
    [string]$RunDirectory = '',
    [ValidateRange(10,300)][int]$Seconds = 40,
    [ValidateRange(0,30)][int]$SingleCoreSeconds = 0,
    [ValidateRange(0,30)][int]$WorkerSeconds = 0,
    [string]$WorkerOutput = ''
)
$ErrorActionPreference = 'Stop'
if ($WorkerSeconds -gt 0) {
    # Only this helper process is pinned. No other process or system setting is changed.
    $workerProcess=[Diagnostics.Process]::GetCurrentProcess()
    $originalAffinity=$workerProcess.ProcessorAffinity
    try {
        $allowed=[Convert]::ToUInt64($workerProcess.ProcessorAffinity.ToInt64().ToString('X16'),16)
        $mask=[uint64]1; $processor=0
        while ($processor -lt 64 -and ($allowed -band $mask) -eq 0) { $mask=$mask -shl 1; $processor++ }
        if ($processor -ge 64) { throw 'No available logical processor.' }
        $signed=[BitConverter]::ToInt64([BitConverter]::GetBytes($mask),0)
        $workerProcess.ProcessorAffinity=[IntPtr]::new($signed)
        $started=[DateTimeOffset]::UtcNow; $watch=[Diagnostics.Stopwatch]::StartNew(); $sink=0.0
        while ($watch.Elapsed.TotalSeconds -lt $WorkerSeconds) { for($i=1;$i -le 2000;$i++) { $sink += [Math]::Sqrt($i) } }
        $metadata=@{ Started=$started.ToString('O'); Ended=[DateTimeOffset]::UtcNow.ToString('O'); ElapsedSeconds=$watch.Elapsed.TotalSeconds;
            LogicalProcessor=$processor; RequestedSeconds=$WorkerSeconds; Scope='One pinned helper process. Other programs and system affinity unchanged.' }
        [IO.File]::WriteAllText($WorkerOutput,($metadata | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    } finally { try { $workerProcess.ProcessorAffinity=$originalAffinity } finally { $workerProcess.Dispose() } }
    return
}
if ($SingleCoreSeconds -gt 0 -and $Seconds -lt $SingleCoreSeconds+12) { throw 'Allow at least 12 seconds outside the controlled CPU phase.' }
$repoRoot=Split-Path -Parent $PSScriptRoot
$source=if([IO.Path]::IsPathRooted($SourceDirectory)){[IO.Path]::GetFullPath($SourceDirectory)}else{[IO.Path]::GetFullPath((Join-Path $repoRoot $SourceDirectory))}
$exe=Join-Path $source 'PerfCompass.exe'
if (-not (Test-Path -LiteralPath (Join-Path $source 'READY'))) { throw 'Source distribution is not marked READY.' }
$version=(Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if ([version]($version.Split('+')[0]) -lt [version]'0.8.10') { throw 'Telemetry comparison requires PerfCompass 0.8.10 or newer.' }
if (-not $RunDirectory) { $RunDirectory=Join-Path $repoRoot ('artifacts/telemetry-check-'+(Get-Date -Format yyyyMMdd-HHmmss)+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)) }
$runRoot=[IO.Path]::GetFullPath($RunDirectory)
if (Test-Path -LiteralPath $runRoot) { throw 'Run directory must not already exist.' }
$dataRoot=Join-Path $runRoot 'data'; [IO.Directory]::CreateDirectory($dataRoot) | Out-Null
$hash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$probe=Start-Process -FilePath $exe -ArgumentList @('--telemetry-check','--validation-seconds',$Seconds,'--data-dir',('"'+$dataRoot+'"')) -WindowStyle Hidden -PassThru
$worker=$null; $timedOut=$false
try {
    if ($SingleCoreSeconds -gt 0) {
        Start-Sleep -Seconds 6
        $shell=(Get-Process -Id $PID).Path
        $worker=Start-Process -FilePath $shell -ArgumentList @('-NoProfile','-NonInteractive','-File',('"'+$PSCommandPath+'"'),'-WorkerSeconds',$SingleCoreSeconds,'-WorkerOutput',('"'+(Join-Path $runRoot 'worker.json')+'"')) -WindowStyle Hidden -PassThru
    }
    if (-not $probe.WaitForExit(($Seconds+45)*1000)) { $timedOut=$true; $probe.Kill(); $probe.WaitForExit() }
    if ($worker -and -not $worker.WaitForExit(35000)) { $worker.Kill(); $worker.WaitForExit() }
    $metadata=[pscustomobject]@{ Version=$version; ExecutableSha256=$hash; DataRoot=$dataRoot; RequestedSeconds=$Seconds; SingleCoreRequestedSeconds=$SingleCoreSeconds;
        ProbeExit=$probe.ExitCode; ProbeTimedOut=$timedOut; WorkerExit=$(if($worker){$worker.ExitCode}else{$null}); Report=Join-Path $dataRoot 'validation/telemetry-cross-check.json';
        Scope='Exit 0 means comparable readings were recorded. It is not an accuracy or purchase-recommendation certificate. The controlled phase is present only if worker.json exists and reports successful completion.' }
    [IO.File]::WriteAllText((Join-Path $runRoot 'experiment.json'),($metadata | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $metadata
} finally { $probe.Dispose(); if($worker){$worker.Dispose()} }
