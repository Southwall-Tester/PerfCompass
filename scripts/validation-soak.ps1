param(
    [ValidateSet('Start','Status','Stop')][string]$Action = 'Status',
    [string]$RunDirectory = '',
    [string]$SourceDirectory = 'release-next',
    [ValidateRange(10,604800)][int]$Seconds = 28800
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
function FullPath([string]$value) {
    if ([IO.Path]::IsPathRooted($value)) { return [IO.Path]::GetFullPath($value) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $value))
}
function ReadSharedJson([string]$path) {
    if (-not [IO.File]::Exists($path)) { return $null }
    $stream = [IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { $reader=[IO.StreamReader]::new($stream); try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() } } finally { $stream.Dispose() }
}
function NoLinks([string]$path) {
    $cursor = $path
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Validation paths must not contain links.' }
        }
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
}
function MatchingProcess([object]$record,[string]$idName,[string]$pathName,[string]$startName) {
    if(-not $record){return $false}
    $processNumber=0
    if(-not [int]::TryParse([string]$record.$idName,[ref]$processNumber) -or $processNumber -le 0){throw 'Invalid recorded process ID; identity remains unknown.'}
    if([string]::IsNullOrWhiteSpace($record.$pathName) -or -not [IO.Path]::IsPathRooted($record.$pathName)){throw 'Missing recorded process path; identity remains unknown.'}
    $expectedPath=[IO.Path]::GetFullPath($record.$pathName)
    $time=if($record.$startName -is [datetime]){[DateTimeOffset]$record.$startName}else{[DateTimeOffset]::Parse([string]$record.$startName,[Globalization.CultureInfo]::InvariantCulture)}
    try {$candidate=[Diagnostics.Process]::GetProcessById($processNumber)} catch [ArgumentException] {return $false}
    try {
        if($candidate.HasExited){return $false}
        $candidatePath=$candidate.MainModule.FileName
        if([string]::IsNullOrWhiteSpace($candidatePath)){throw 'Process path unavailable; identity remains unknown.'}
        return [IO.Path]::GetFullPath($candidatePath) -eq $expectedPath -and $candidate.StartTime.ToUniversalTime().Ticks -eq $time.UtcDateTime.Ticks
    } catch {
        if($candidate.HasExited){return $false}
        throw # Inspection failure is not evidence that the recorded process ended.
    } finally {$candidate.Dispose()}
}
$latest = Join-Path $repoRoot 'artifacts/soak-latest.json'
if ($Action -eq 'Start') {
    $lockRoot=Split-Path -Parent $latest; NoLinks $lockRoot
    [IO.Directory]::CreateDirectory($lockRoot)|Out-Null
    $lockPath=Join-Path $lockRoot 'validation-start.lock'; NoLinks $lockPath
    $startLock=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
    if (Test-Path -LiteralPath $latest) {
        $previousRun=ReadSharedJson $latest
        if ($previousRun) {
            $previousState=& $PSCommandPath -Action Status -RunDirectory $previousRun.RunDirectory
            if ($previousState.ProcessLive -or $previousState.SupervisorLive) { throw ('A soak run or its supervisor is already live: '+$previousRun.RunDirectory) }
        }
    }
    $source=FullPath $SourceDirectory; $sourceExe=Join-Path $source 'PerfCompass.exe'; NoLinks $sourceExe
    if (-not (Test-Path -LiteralPath (Join-Path $source 'READY'))) { throw 'Source distribution is not marked READY.' }
    $version=(Get-Item -LiteralPath $sourceExe).VersionInfo.ProductVersion
    if ([version]($version.Split('+')[0]) -lt [version]'0.8.10') { throw 'Soak validation requires PerfCompass 0.8.10 or newer.' }
    if (-not $RunDirectory) { $RunDirectory=Join-Path $repoRoot ('artifacts/soak-'+(Get-Date -Format yyyyMMdd-HHmmss)+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)) }
    $runRoot=FullPath $RunDirectory; NoLinks $runRoot
    if (Test-Path -LiteralPath $runRoot) { throw 'Run directory must not already exist.' }
    $appRoot=Join-Path $runRoot 'app'; $dataRoot=Join-Path $runRoot 'data'
    [IO.Directory]::CreateDirectory($appRoot) | Out-Null; [IO.Directory]::CreateDirectory($dataRoot) | Out-Null
    # Freeze the runnable EXE outside release-next so future builds cannot overwrite a live run.
    Copy-Item -LiteralPath $sourceExe -Destination (Join-Path $appRoot 'PerfCompass.exe')
    foreach ($name in @('README.html','THIRD-PARTY-NOTICES.md','licenses')) {
        $entry=Join-Path $source $name
        if (Test-Path -LiteralPath $entry) { Copy-Item -LiteralPath $entry -Destination (Join-Path $appRoot $name) -Recurse }
    }
    $exe=Join-Path $appRoot 'PerfCompass.exe'; $hash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    if ($hash -ne (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash) { throw 'Frozen executable differs from source.' }
    $supervisorScript=Join-Path $runRoot 'supervise.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'validation-supervisor.ps1') -Destination $supervisorScript
    $intent=[ordered]@{Protocol='validation-supervision-1.0';RunId=[Guid]::NewGuid().ToString('N');RunDirectory=$runRoot;DataRoot=$dataRoot;Executable=$exe;ExecutableSha256=$hash;Version=$version;RequestedSeconds=$Seconds;SupervisorSha256=(Get-FileHash -LiteralPath $supervisorScript -Algorithm SHA256).Hash}
    [IO.File]::WriteAllText((Join-Path $runRoot 'intent.json'),($intent|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($latest,(@{RunDirectory=$runRoot} | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $hostExe=Join-Path $PSHOME $(if($PSVersionTable.PSEdition -eq 'Core'){'pwsh.exe'}else{'powershell.exe'})
    $supervisor=Start-Process -FilePath $hostExe -ArgumentList ('-NoProfile -File "'+$supervisorScript+'" -RunDirectory "'+$runRoot+'"') -WorkingDirectory $runRoot -WindowStyle Hidden -RedirectStandardError (Join-Path $runRoot 'supervisor-stderr.txt') -RedirectStandardOutput (Join-Path $runRoot 'supervisor-stdout.txt') -PassThru
    $supervisorHandle=$supervisor.Handle
    [IO.File]::WriteAllText((Join-Path $runRoot 'supervisor-launch.json'),(@{ProcessId=$supervisor.Id;Executable=$hostExe;StartTimeUtc=$supervisor.StartTime.ToUniversalTime().ToString('O')}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $waiting=[Diagnostics.Stopwatch]::StartNew()
    do {
        $manifest=ReadSharedJson (Join-Path $runRoot 'launch.json')
        if($manifest){break}
        $failure=ReadSharedJson (Join-Path $runRoot 'supervisor-error.json')
        if($failure){throw ('Supervisor reported: '+$failure.Error+'; original run retained: '+$runRoot)}
        if($supervisor.HasExited){throw ('Supervisor ended before launch was recorded; inspect '+$runRoot)}
        Start-Sleep -Milliseconds 100
    } while($waiting.Elapsed.TotalSeconds -lt 10)
    if(-not $manifest){throw ('Launch observation expired; inspect the same run and supervisor, do not restart: '+$runRoot)}
    $manifest
    return
    } finally { $startLock.Dispose() }
}
if (-not $RunDirectory) {
    $pointer=ReadSharedJson $latest
    if (-not $pointer) { throw 'Specify -RunDirectory or start a run first.' }
    $RunDirectory=$pointer.RunDirectory
}
$runRoot=FullPath $RunDirectory; NoLinks $runRoot; $manifest=ReadSharedJson (Join-Path $runRoot 'launch.json')
$supervisorRecord=ReadSharedJson (Join-Path $runRoot 'supervisor-launch.json')
$supervisorLive=MatchingProcess $supervisorRecord 'ProcessId' 'Executable' 'StartTimeUtc'
if (-not $manifest) {
    if($Action -eq 'Stop'){throw 'No recorded child identity; no stop request written.'}
    $failure=ReadSharedJson (Join-Path $runRoot 'supervisor-error.json')
    [pscustomobject]@{RunDirectory=$runRoot;ProcessId=$null;ProcessLive=$false;SupervisorLive=$supervisorLive;State=$(if($supervisorLive){'starting'}elseif($failure){'start-failed'}else{'missing-launch-manifest'});ChecksPassed=$false;ApplicationChecksPassed=$false;ExitWitnessStatus='unavailable';ExitCode=$null;Error=$failure}
    return
}
$dataRoot=Join-Path $runRoot 'data'; $exe=Join-Path $runRoot 'app/PerfCompass.exe'
if ((FullPath $manifest.DataRoot) -ne $dataRoot -or (FullPath $manifest.Executable) -ne $exe) { throw 'Manifest paths do not match this run directory.' }
NoLinks $dataRoot; NoLinks $exe
$live=MatchingProcess $manifest 'ProcessId' 'Executable' 'StartTimeUtc'
$binaryMatches=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -eq $manifest.ExecutableSha256
$validation=Join-Path $dataRoot 'validation'; $started=ReadSharedJson (Join-Path $validation 'run.json')
$result=ReadSharedJson (Join-Path $validation 'result.json'); $progress=ReadSharedJson (Join-Path $validation 'progress.json')
if ($Action -eq 'Stop') {
    if (-not $live -or -not $binaryMatches -or -not $started) { throw 'The expected run is not live and initialized; no stop file was written.' }
    [IO.File]::WriteAllText((Join-Path $validation 'stop.request'),'Stop requested '+[DateTimeOffset]::UtcNow.ToString('O'),[Text.UTF8Encoding]::new($false))
}
$contextMatches=$result -and $result.ExecutableSha256 -eq $manifest.ExecutableSha256
$witness=ReadSharedJson (Join-Path $runRoot 'exit-witness.json')
$validExit=$witness -and ($witness.ExitCode -is [int] -or $witness.ExitCode -is [long]) -and $witness.ExitCode -ge [int]::MinValue -and $witness.ExitCode -le [int]::MaxValue
$witnessMatches=$validExit -and $witness.ProcessId -eq $manifest.ProcessId -and [string]$witness.StartTimeUtc -eq [string]$manifest.StartTimeUtc -and $witness.ExecutableSha256 -eq $manifest.ExecutableSha256 -and (-not $manifest.RunId -or $witness.RunId -eq $manifest.RunId)
$witnessState=if($witnessMatches){'matched'}elseif($witness){'identity-mismatch'}elseif($live -or $supervisorLive){'pending'}else{'missing'}
$applicationPassed=$binaryMatches -and $contextMatches -and $result.ChecksPassed -is [bool] -and $result.ChecksPassed -eq $true -and $result.Status -eq 'completed' -and $result.RequestedSeconds -eq $manifest.RequestedSeconds
[pscustomobject]@{ RunDirectory=$runRoot; ProcessId=$manifest.ProcessId; ProcessLive=$live; BinaryMatches=$binaryMatches; StopRequested=($Action -eq 'Stop');
    SupervisorLive=$supervisorLive;ApplicationChecksPassed=$applicationPassed;ExitWitnessStatus=$witnessState;ExitCode=$(if($witnessMatches){$witness.ExitCode}else{$null});RequireExitWitness=($manifest.RequireExitWitness -eq $true);
    State=$(if($result -and $contextMatches){$result.Status}elseif($live -and $started){'running'}elseif($live){'starting'}else{'missing-terminal-result'});
    ChecksPassed=($applicationPassed -and -not $live -and $witnessMatches -and $witness.ExitCode -eq 0); Progress=$progress; Result=$result }
