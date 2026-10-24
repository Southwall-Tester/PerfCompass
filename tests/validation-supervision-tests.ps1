param([Parameter(Mandatory=$true)][string]$SourceDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$source=[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($SourceDirectory)){$SourceDirectory}else{Join-Path $repo $SourceDirectory}))
$originalExe=Join-Path $source 'PerfCompass.exe'
$root=Join-Path $repo ('artifacts/validation-supervision-test-'+[Guid]::NewGuid().ToString('N'))
$scripts=Join-Path $root 'scripts'; [IO.Directory]::CreateDirectory($scripts)|Out-Null
foreach($name in @('validation-soak.ps1','validation-supervisor.ps1')) {
    Copy-Item -LiteralPath (Join-Path $repo ('scripts/'+$name)) -Destination (Join-Path $scripts $name)
    if((Get-FileHash (Join-Path $scripts $name)).Hash -ne (Get-FileHash (Join-Path $repo ('scripts/'+$name))).Hash){throw 'Tested script copy differs from source'}
}
$manager=Join-Path $scripts 'validation-soak.ps1'
$results=New-Object 'Collections.Generic.List[string]'
function Require([bool]$ok,[string]$message) {
    $line=$(if($ok){'PASS '}else{'FAIL '})+$message; $results.Add($line)
    [IO.File]::AppendAllText((Join-Path $root 'results.txt'),$line+"`n")
    Write-Output $line
    if(-not $ok){throw $message}
}
function Json([string]$path){Get-Content -LiteralPath $path -Raw -Encoding UTF8|ConvertFrom-Json}
function WriteJson([string]$path,[object]$data){[IO.File]::WriteAllText($path,($data|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))}
function WaitTerminal([string]$run) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do {
        $s=& $manager -Action Status -RunDirectory $run
        if(-not $s.ProcessLive -and -not $s.SupervisorLive){return $s}
        if($clock.Elapsed.TotalSeconds -gt 90){throw ('Observation expired; inspect same test run, not restarted: '+$run)}
        Start-Sleep -Milliseconds 250
    }while($true)
}
function FixtureSource([string]$name,[string]$exe) {
    $dir=Join-Path $root $name; [IO.Directory]::CreateDirectory($dir)|Out-Null
    Copy-Item -LiteralPath $exe -Destination (Join-Path $dir 'PerfCompass.exe')
    [IO.File]::WriteAllText((Join-Path $dir 'READY'),'Isolated validation-tool fixture only; not distribution approval.')
    return $dir
}
$globalLatest=Join-Path $repo 'artifacts/soak-latest.json'
$globalBefore=if(Test-Path $globalLatest){(Get-FileHash $globalLatest).Hash}else{$null}
$real=FixtureSource 'source-real' $originalExe
WriteJson (Join-Path $root 'scope.json') @{Scope='Copied exact manager and supervisor scripts on an isolated tool workspace. Real 20-second monitor run and explicit stop; fast-exit native whoami fixture is not PerfCompass telemetry. Later metadata fixtures are synthetic, never executed. No production data or active soak directory is changed.';ExecutableSha256=(Get-FileHash $originalExe).Hash}
[IO.File]::WriteAllText((Join-Path $repo 'artifacts/validation-supervision-test-latest.json'),(@{RunDirectory=$root}|ConvertTo-Json))
try {
    $m=& $manager -Action Start -SourceDirectory $real -Seconds 20
    $s=& $manager -Action Status -RunDirectory $m.RunDirectory
    Require ($s.ProcessLive -and $s.SupervisorLive -and -not $s.ChecksPassed -and $s.ExitWitnessStatus -eq 'pending') 'Started real monitor and supervisor are live; no early successful exit claimed'
    Require ((Get-FileHash (Join-Path $m.RunDirectory 'supervise.ps1')).Hash -eq $m.SupervisorSha256) 'Run freezes the actual supervisor bytes alongside the EXE'
    $rejected=$false; $extra=Join-Path $root 'must-not-start'
    try {& $manager -Action Start -SourceDirectory $real -RunDirectory $extra -Seconds 10|Out-Null} catch {$rejected=$_.Exception.Message -like '*already live*'}
    Require ($rejected -and -not (Test-Path $extra)) 'A live monitor or supervisor prevents a second run without creating its target'
    $s=WaitTerminal $m.RunDirectory
    WriteJson (Join-Path $root 'real-status.json') $s
    Require ($s.ApplicationChecksPassed -and $s.ChecksPassed -and $s.ExitWitnessStatus -eq 'matched' -and $s.ExitCode -eq 0) 'Real short validation completes with independent matching OS exit code zero'
    Require ($s.Result.ExecutableSha256 -eq (Get-FileHash $originalExe).Hash -and $s.Result.RequestedSeconds -eq 20) 'Real result retains the exact published EXE and requested duration'

    $stopRun=& $manager -Action Start -SourceDirectory $real -Seconds 60
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path (Join-Path $stopRun.DataRoot 'validation/run.json'))) {
        if($clock.Elapsed.TotalSeconds -gt 25){throw 'Real validation did not initialize for stop check'}
        Start-Sleep -Milliseconds 100
    }
    & $manager -Action Stop -RunDirectory $stopRun.RunDirectory|Out-Null
    $stopped=WaitTerminal $stopRun.RunDirectory
    WriteJson (Join-Path $root 'stopped-status.json') $stopped
    Require ($stopped.State -eq 'stopped' -and -not $stopped.ChecksPassed -and $stopped.ExitWitnessStatus -eq 'matched' -and $stopped.ExitCode -eq 2) 'Orderly early stop preserves stopped result and captured exit two rather than completing'

    $fast=FixtureSource 'source-fast-native-fixture' (Join-Path $env:WINDIR 'System32/whoami.exe')
    $fastRun=& $manager -Action Start -SourceDirectory $fast -Seconds 10
    $fastStatus=WaitTerminal $fastRun.RunDirectory
    WriteJson (Join-Path $root 'fast-native-fixture-status.json') $fastStatus
    Require ($fastStatus.ExitWitnessStatus -eq 'matched' -and $null -ne $fastStatus.ExitCode -and $fastStatus.ExitCode -ne 0 -and -not $fastStatus.ChecksPassed) 'Fast-exiting native fixture keeps its nonzero OS code even before parent launch polling finishes'
    Require ($fastStatus.State -eq 'missing-terminal-result' -and -not $fastStatus.ApplicationChecksPassed) 'Process exit without a monitor result remains an incomplete validation'

    $starting=Join-Path $root 'synthetic-supervisor-only'; [IO.Directory]::CreateDirectory($starting)|Out-Null
    $self=[Diagnostics.Process]::GetCurrentProcess()
    $supervisorRecord=@{ProcessId=$PID;Executable=$self.MainModule.FileName;StartTimeUtc=$self.StartTime.ToUniversalTime().ToString('O')}
    WriteJson (Join-Path $starting 'supervisor-launch.json') $supervisorRecord
    WriteJson (Join-Path $root 'artifacts/soak-latest.json') @{RunDirectory=$starting}
    $startingStatus=& $manager -Action Status -RunDirectory $starting
    Require ($startingStatus.SupervisorLive -and -not $startingStatus.ProcessLive -and $startingStatus.State -eq 'starting') 'A supervisor identity remains observable before the child manifest exists (synthetic metadata)'
    $canonicalHost=$supervisorRecord.Executable
    $supervisorRecord.Executable=Join-Path ([IO.Path]::GetDirectoryName($canonicalHost)) ('.\'+[IO.Path]::GetFileName($canonicalHost))
    WriteJson (Join-Path $starting 'supervisor-launch.json') $supervisorRecord
    Require ((& $manager -Action Status -RunDirectory $starting).SupervisorLive) 'Equivalent path spelling cannot hide the live supervisor identity'
    $supervisorRecord.Executable=''; WriteJson (Join-Path $starting 'supervisor-launch.json') $supervisorRecord
    $missingPathRejected=$false
    try {& $manager -Action Status -RunDirectory $starting|Out-Null} catch {$missingPathRejected=$true}
    Require $missingPathRejected 'Missing recorded path remains unknown rather than proving the process stopped'
    $supervisorRecord.Executable=$canonicalHost; WriteJson (Join-Path $starting 'supervisor-launch.json') $supervisorRecord
    $blocked=$false; $second=Join-Path $root 'must-not-restart-supervisor'
    try {& $manager -Action Start -SourceDirectory $real -RunDirectory $second -Seconds 10|Out-Null} catch {$blocked=$_.Exception.Message -like '*already live*'}
    Require ($blocked -and -not (Test-Path $second)) 'A live supervisor alone blocks duplicate start without inventing a child terminal state'
    $supervisorRecord.StartTimeUtc='unknown'; WriteJson (Join-Path $starting 'supervisor-launch.json') $supervisorRecord
    $unknownRejected=$false
    try {& $manager -Action Start -SourceDirectory $real -RunDirectory $second -Seconds 10|Out-Null} catch {$unknownRejected=$true}
    Require ($unknownRejected -and -not (Test-Path $second)) 'Unreadable recorded identity is rejected rather than treated as a stopped process'
    $self.Dispose()

    $blockedRoot=Join-Path $root 'blocked-data-supervisor'; $blockedApp=Join-Path $blockedRoot 'app'; $blockedData=Join-Path $blockedRoot 'data'
    [IO.Directory]::CreateDirectory($blockedApp)|Out-Null; [IO.Directory]::CreateDirectory($blockedData)|Out-Null
    Copy-Item -LiteralPath (Join-Path $fast 'PerfCompass.exe') -Destination (Join-Path $blockedApp 'PerfCompass.exe')
    $frozenSupervisor=Join-Path $blockedRoot 'supervise.ps1'; Copy-Item -LiteralPath (Join-Path $scripts 'validation-supervisor.ps1') -Destination $frozenSupervisor
    [IO.File]::WriteAllText((Join-Path $blockedData 'sentinel.txt'),'preserve exact bytes')
    $intent=@{Protocol='validation-supervision-1.0';RunId=[Guid]::NewGuid().ToString('N');RunDirectory=$blockedRoot;Executable=(Join-Path $blockedApp 'PerfCompass.exe');DataRoot=$blockedData;ExecutableSha256=(Get-FileHash (Join-Path $blockedApp 'PerfCompass.exe')).Hash;RequestedSeconds=10;SupervisorSha256=(Get-FileHash $frozenSupervisor).Hash}
    WriteJson (Join-Path $blockedRoot 'intent.json') $intent
    $hostExe=Join-Path $PSHOME $(if($PSVersionTable.PSEdition -eq 'Core'){'pwsh.exe'}else{'powershell.exe'})
    $guard=Start-Process -FilePath $hostExe -ArgumentList ('-NoProfile -File "'+$frozenSupervisor+'" -RunDirectory "'+$blockedRoot+'"') -WindowStyle Hidden -PassThru
    $guardHandle=$guard.Handle
    if(-not $guard.WaitForExit(15000)){throw 'Supervisor guard observation expired; inspect same process'}
    $guard.WaitForExit();$guard.Refresh()
    $guardError=Json (Join-Path $blockedRoot 'supervisor-error.json')
    Require ($guard.ExitCode -eq 1 -and $guardError.ChildStarted -eq $false -and -not (Test-Path (Join-Path $blockedRoot 'launch.json')) -and [IO.File]::ReadAllText((Join-Path $blockedData 'sentinel.txt')) -eq 'preserve exact bytes') 'Nonempty data is refused before executing a child; sentinel and failure remain intact'
    WriteJson (Join-Path $root 'artifacts/soak-latest.json') @{RunDirectory=$fastRun.RunDirectory}

    # Tiny metadata fixture: never run this file or use it as telemetry evidence.
    $fixture=Join-Path $root 'synthetic-status'; $app=Join-Path $fixture 'app'; $validation=Join-Path $fixture 'data/validation'
    [IO.Directory]::CreateDirectory($app)|Out-Null; [IO.Directory]::CreateDirectory($validation)|Out-Null
    $fakeExe=Join-Path $app 'PerfCompass.exe'; [IO.File]::WriteAllText($fakeExe,'Synthetic status bytes; never executable')
    $id=[Guid]::NewGuid().ToString('N'); $hash=(Get-FileHash $fakeExe).Hash
    $manifest=@{RunDirectory=$fixture;DataRoot=(Join-Path $fixture 'data');Executable=$fakeExe;ExecutableSha256=$hash;ProcessId=2147483647;StartTimeUtc='2026-01-01T00:00:00.0000000Z';RunId=$id;RequireExitWitness=$true;RequestedSeconds=20}
    $witness=@{RunId=$id;ProcessId=2147483647;StartTimeUtc=$manifest.StartTimeUtc;ExecutableSha256=$hash;ExitCode=0}
    $result=@{Status='completed';ChecksPassed=$true;ExecutableSha256=$hash;RequestedSeconds=20}
    WriteJson (Join-Path $fixture 'launch.json') $manifest; WriteJson (Join-Path $validation 'result.json') $result
    $status=& $manager -Action Status -RunDirectory $fixture
    Require ($status.ApplicationChecksPassed -and -not $status.ChecksPassed -and $null -eq $status.ExitCode -and $status.ExitWitnessStatus -eq 'missing') 'A passing result declaration cannot supply a missing OS exit witness'
    foreach($change in @(@{RunId='different'},@{ProcessId=123},@{StartTimeUtc='2026-01-02T00:00:00Z'},@{ExecutableSha256='different'},@{ExitCode=$false},@{ExitCode='0'},@{ExitCode=2147483648})) {
        $bad=$witness.Clone(); foreach($key in $change.Keys){$bad[$key]=$change[$key]}
        WriteJson (Join-Path $fixture 'exit-witness.json') $bad
        $status=& $manager -Action Status -RunDirectory $fixture
        Require (-not $status.ChecksPassed -and $null -eq $status.ExitCode -and $status.ExitWitnessStatus -eq 'identity-mismatch') ('Rejects mismatched or ill-typed witness '+($change.Keys -join ','))
    }
    WriteJson (Join-Path $fixture 'exit-witness.json') $witness
    $result.ChecksPassed='false'; WriteJson (Join-Path $validation 'result.json') $result
    Require (-not (& $manager -Action Status -RunDirectory $fixture).ChecksPassed) 'String false cannot be interpreted as a passing Boolean result'
    $result.ChecksPassed=$true; $result.RequestedSeconds=10; WriteJson (Join-Path $validation 'result.json') $result
    Require (-not (& $manager -Action Status -RunDirectory $fixture).ChecksPassed) 'A different requested duration cannot pass the current run'
    $result.RequestedSeconds=20; WriteJson (Join-Path $validation 'result.json') $result
    $witness.ExitCode=17; WriteJson (Join-Path $fixture 'exit-witness.json') $witness
    Require (-not (& $manager -Action Status -RunDirectory $fixture).ChecksPassed) 'Nonzero OS exit overrides a passing application declaration'
    $manifest.Remove('RequireExitWitness');$manifest.Remove('RunId'); WriteJson (Join-Path $fixture 'launch.json') $manifest
    Move-Item -LiteralPath (Join-Path $fixture 'exit-witness.json') -Destination (Join-Path $fixture 'exit-witness.fixture-held.json')
    $legacy=& $manager -Action Status -RunDirectory $fixture
    Require ($legacy.ApplicationChecksPassed -and -not $legacy.ChecksPassed -and $legacy.ExitWitnessStatus -eq 'missing') 'Legacy passing declarations remain readable without inventing a missing exit code'
    $globalAfter=if(Test-Path $globalLatest){(Get-FileHash $globalLatest).Hash}else{$null}
    Require ($globalBefore -eq $globalAfter) 'Tests preserve the actual workspace active-soak pointer'
} catch {
    $results.Add('FAIL '+$_.Exception.Message); Write-Error -Message $_.Exception.Message -ErrorAction Continue
    [IO.File]::AppendAllText((Join-Path $root 'results.txt'),'FAIL '+$_+"`n")
} finally {
    $failed=@($results|Where-Object {$_ -like 'FAIL *'}).Count
    WriteJson (Join-Path $root 'summary.json') @{Passed=@($results|Where-Object {$_ -like 'PASS *'}).Count;Failed=$failed;Scope='Real monitor short/stop flows, native fast-exit fixture, synthetic metadata checks are distinct. No long-term stability claim.'}
    $evidence=Join-Path $root 'evidence'; [IO.Directory]::CreateDirectory($evidence)|Out-Null
    foreach($name in @('results.txt','scope.json','summary.json','real-status.json','stopped-status.json','fast-native-fixture-status.json')) {
        $file=Join-Path $root $name; if(Test-Path $file){Copy-Item -LiteralPath $file -Destination (Join-Path $evidence $name)}
    }
    WriteJson (Join-Path $evidence 'script-sha256.json') @{
        Manager=(Get-FileHash (Join-Path $scripts 'validation-soak.ps1')).Hash
        Supervisor=(Get-FileHash (Join-Path $scripts 'validation-supervisor.ps1')).Hash
    }
}
if($failed){exit 1}
exit 0
