param([Parameter(Mandatory=$true)][string]$RunDirectory)
$ErrorActionPreference='Stop'
$run=[IO.Path]::GetFullPath($RunDirectory)
function WriteRecord([string]$name,[object]$value) {
    $target=Join-Path $run $name
    $temporary=$target+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [IO.File]::WriteAllText($temporary,($value|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
        # Each record is created once. Never replace an existing witness.
        [IO.File]::Move($temporary,$target)
    } finally { if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)} }
}
function NoLinks([string]$path) {
    for($cursor=$path;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Validation paths must not contain links.' }
    }
}
$child=$null; $childStarted=$false; $manifest=$null; $failure=$null
try {
    NoLinks $run
    $intent=Get-Content -LiteralPath (Join-Path $run 'intent.json') -Raw -Encoding UTF8|ConvertFrom-Json
    if($intent.Protocol -ne 'validation-supervision-1.0' -or [string]::IsNullOrWhiteSpace($intent.RunId)) { throw 'Unknown supervision intent.' }
    $seconds=0
    if(-not [int]::TryParse([string]$intent.RequestedSeconds,[ref]$seconds) -or $seconds -lt 10 -or $seconds -gt 604800){throw 'Invalid requested duration.'}
    $exe=Join-Path $run 'app/PerfCompass.exe'; $data=Join-Path $run 'data'
    NoLinks $exe; NoLinks $data
    if([IO.Path]::GetFullPath($intent.RunDirectory) -ne $run -or [IO.Path]::GetFullPath($intent.Executable) -ne $exe -or [IO.Path]::GetFullPath($intent.DataRoot) -ne $data) { throw 'Intent paths do not match this run.' }
    if((Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash -ne $intent.SupervisorSha256) { throw 'Frozen supervisor changed.' }
    if((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $intent.ExecutableSha256) { throw 'Frozen executable changed.' }
    if(-not [IO.Directory]::Exists($data)){throw 'Supervisor requires the original empty data directory.'}
    $entries=[IO.Directory]::EnumerateFileSystemEntries($data).GetEnumerator()
    try {$occupied=$entries.MoveNext()} finally {$entries.Dispose()}
    if($occupied){throw 'Supervisor requires the original empty data directory.'}
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$exe; $start.WorkingDirectory=$run; $start.UseShellExecute=$false
    $start.CreateNoWindow=$true; $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    # Only fixed validation arguments are executed; intent cannot supply commands.
    $start.Arguments='--soak-test --minimized --validation-seconds '+$seconds+' --data-dir "'+$data+'"'
    $child=New-Object Diagnostics.Process; $child.StartInfo=$start
    if(-not $child.Start()){throw 'Validation process did not start.'}
    $childStarted=$true; $handle=$child.Handle
    $manifest=[ordered]@{Protocol='validation-supervision-1.0';RunId=$intent.RunId;RunDirectory=$run;DataRoot=$data;Executable=$exe;ExecutableSha256=$intent.ExecutableSha256;Version=$intent.Version;RequestedSeconds=$seconds;ProcessId=$child.Id;StartTimeUtc=$child.StartTime.ToUniversalTime().ToString('O');RequireExitWitness=$true;SupervisorId=$PID;SupervisorSha256=$intent.SupervisorSha256}
    WriteRecord 'launch.json' $manifest
} catch {
    $failure=$_.Exception.GetType().Name+': '+$_.Exception.Message
    try { WriteRecord 'supervisor-error.json' @{Error=$failure;ChildStarted=$childStarted;Recorded=[DateTimeOffset]::UtcNow} } catch { [Console]::Error.WriteLine($_.Exception.Message) }
}
if($childStarted) {
    # Keep the process handle even if launch-record writing failed. Never kill or
    # restart the child to make an observation look complete.
    while(-not $child.WaitForExit(1000)){}
    $child.WaitForExit(); $child.Refresh()
    if($manifest -and $null -ne $child.ExitCode) {
        try {
            WriteRecord 'exit-witness.json' ([ordered]@{Protocol='validation-supervision-1.0';RunId=$manifest.RunId;ProcessId=$manifest.ProcessId;StartTimeUtc=$manifest.StartTimeUtc;ExecutableSha256=$manifest.ExecutableSha256;ExitCode=$child.ExitCode;ObservedExit=[DateTimeOffset]::UtcNow;Scope='OS process termination and exit only. Application checks remain independent. Files are not authenticated or power-loss recovery.'})
        } catch { $failure=$_.Exception.Message; [Console]::Error.WriteLine($failure) }
    } else { $failure='No complete process identity or exit code; no witness written.'; [Console]::Error.WriteLine($failure) }
    $child.Dispose()
}
if($failure){exit 1}
exit 0
