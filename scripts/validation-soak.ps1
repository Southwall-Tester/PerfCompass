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
$latest = Join-Path $repoRoot 'artifacts/soak-latest.json'
if ($Action -eq 'Start') {
    if (Test-Path -LiteralPath $latest) {
        $previousRun=ReadSharedJson $latest
        if ($previousRun -and (Test-Path -LiteralPath (Join-Path $previousRun.RunDirectory 'launch.json'))) {
            $previousState=& $PSCommandPath -Action Status -RunDirectory $previousRun.RunDirectory
            if ($previousState.ProcessLive) { throw ('A soak run is already live: '+$previousRun.RunDirectory) }
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
    $soakProcess=Start-Process -FilePath $exe -ArgumentList @('--soak-test','--minimized','--validation-seconds',$Seconds,'--data-dir',('"'+$dataRoot+'"')) -WindowStyle Hidden -PassThru
    $manifest=[pscustomobject]@{ RunDirectory=$runRoot; DataRoot=$dataRoot; Executable=$exe; ExecutableSha256=$hash; Version=$version; RequestedSeconds=$Seconds;
        ProcessId=$soakProcess.Id; StartTimeUtc=$soakProcess.StartTime.ToUniversalTime().ToString('O') }
    [IO.File]::WriteAllText((Join-Path $runRoot 'launch.json'),($manifest | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($latest,(@{RunDirectory=$runRoot} | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $manifest
    return
}
if (-not $RunDirectory) {
    $pointer=ReadSharedJson $latest
    if (-not $pointer) { throw 'Specify -RunDirectory or start a run first.' }
    $RunDirectory=$pointer.RunDirectory
}
$runRoot=FullPath $RunDirectory; NoLinks $runRoot; $manifest=ReadSharedJson (Join-Path $runRoot 'launch.json')
if (-not $manifest) { throw 'Run launch manifest is missing.' }
$dataRoot=Join-Path $runRoot 'data'; $exe=Join-Path $runRoot 'app/PerfCompass.exe'
if ((FullPath $manifest.DataRoot) -ne $dataRoot -or (FullPath $manifest.Executable) -ne $exe) { throw 'Manifest paths do not match this run directory.' }
NoLinks $dataRoot; NoLinks $exe
$soakProcess=Get-Process -Id $manifest.ProcessId -ErrorAction SilentlyContinue
$live=$false
if ($soakProcess) {
    $recorded=if ($manifest.StartTimeUtc -is [datetime]) { [DateTimeOffset]$manifest.StartTimeUtc } else { [DateTimeOffset]::Parse($manifest.StartTimeUtc,[Globalization.CultureInfo]::InvariantCulture) }
    $live=$soakProcess.Path -eq $exe -and $soakProcess.StartTime.ToUniversalTime().Ticks -eq $recorded.UtcDateTime.Ticks
}
$binaryMatches=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -eq $manifest.ExecutableSha256
$validation=Join-Path $dataRoot 'validation'; $started=ReadSharedJson (Join-Path $validation 'run.json')
$result=ReadSharedJson (Join-Path $validation 'result.json'); $progress=ReadSharedJson (Join-Path $validation 'progress.json')
if ($Action -eq 'Stop') {
    if (-not $live -or -not $binaryMatches -or -not $started) { throw 'The expected run is not live and initialized; no stop file was written.' }
    [IO.File]::WriteAllText((Join-Path $validation 'stop.request'),'Stop requested '+[DateTimeOffset]::UtcNow.ToString('O'),[Text.UTF8Encoding]::new($false))
}
$contextMatches=$result -and $result.ExecutableSha256 -eq $manifest.ExecutableSha256
[pscustomobject]@{ RunDirectory=$runRoot; ProcessId=$manifest.ProcessId; ProcessLive=$live; BinaryMatches=$binaryMatches; StopRequested=($Action -eq 'Stop');
    State=$(if($result -and $contextMatches){$result.Status}elseif($live -and $started){'running'}elseif($live){'starting'}else{'missing-terminal-result'});
    ChecksPassed=($binaryMatches -and $contextMatches -and $result.ChecksPassed); Progress=$progress; Result=$result }
