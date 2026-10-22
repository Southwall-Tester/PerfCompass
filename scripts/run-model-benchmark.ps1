param(
    [string]$ModelMetadata = '.tools/nlp/model-source.json',
    [string]$OutputName = ('nlp-production-' + (Get-Date -Format 'yyyyMMdd-HHmmss')),
    [string]$CaseIds = 'all'
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $taskDotnet) { $env:DOTNET_ROOT = Split-Path -Parent $taskDotnet } else { $taskDotnet = 'dotnet' }
if ($OutputName -notmatch '^[a-zA-Z0-9_-]+$' -or $CaseIds -notmatch '^(all|[a-zA-Z0-9_-]+(,[a-zA-Z0-9_-]+)*)$') { throw 'Invalid output name or case selection.' }
$taskOutput = Join-Path (Join-Path $taskRoot 'artifacts') $OutputName
if (Test-Path -LiteralPath $taskOutput) { throw 'Choose a new experiment output directory.' }
$taskMetadataPath = [IO.Path]::GetFullPath((Join-Path $taskRoot $ModelMetadata))
$taskMetadata = Get-Content -LiteralPath $taskMetadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
$taskModelName = if ($taskMetadata.File) { [string]$taskMetadata.File } else { [string]$taskMetadata.file }
if (-not $taskModelName -or $taskModelName -ne [IO.Path]::GetFileName($taskModelName)) { throw 'Model metadata must name one local model file.' }
$taskModel = Join-Path (Split-Path -Parent $taskMetadataPath) $taskModelName
$taskExpected = if ($taskMetadata.Sha256) { [string]$taskMetadata.Sha256 } else { [string]$taskMetadata.sha256 }
if ($taskExpected -notmatch '^[A-Fa-f0-9]{64}$' -or -not (Test-Path -LiteralPath $taskModel)) { throw 'Verified model file and SHA256 metadata are required.' }
if ((Get-FileHash -LiteralPath $taskModel -Algorithm SHA256).Hash -ne $taskExpected) { throw 'Model SHA256 mismatch.' }
$taskRunner = Join-Path $taskRoot '.tools\nlp\llama-cpu\llama-server.exe'
if (-not (Test-Path -LiteralPath $taskRunner)) { throw 'The isolated research runner is missing.' }
& $taskDotnet build (Join-Path $taskRoot 'tests\PerfCompass.ModelProbe\PerfCompass.ModelProbe.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
$taskPortProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$taskPortProbe.Start(); $taskPort = $taskPortProbe.LocalEndpoint.Port; $taskPortProbe.Stop()
$taskEndpoint = "http://127.0.0.1:$taskPort"
$taskArgs = @('-m',('"'+$taskModel+'"'),'--host','127.0.0.1','--port',$taskPort,'--alias','perfcompass-research','-t','4','-tb','4','-ngl','0','-c','8192','-np','1')
$taskServer = Start-Process -FilePath $taskRunner -ArgumentList $taskArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOutput 'server.stdout.log') -RedirectStandardError (Join-Path $taskOutput 'server.stderr.log')
$taskManifest = [ordered]@{
    Notice='Synthetic inputs only. Real model inference; not semantic accuracy or purchasing validation.'
    Started=[DateTimeOffset]::UtcNow.ToString('O'); Model=$taskModelName; ModelSha256=$taskExpected
    RunnerSha256=(Get-FileHash -LiteralPath $taskRunner -Algorithm SHA256).Hash
    PromptSha256=(Get-FileHash -LiteralPath (Join-Path $taskRoot 'tests\nlp-extraction-prompt.txt') -Algorithm SHA256).Hash
    SchemaSha256=(Get-FileHash -LiteralPath (Join-Path $taskRoot 'tests\nlp-output.schema.json') -Algorithm SHA256).Hash
    CorpusSha256=(Get-FileHash -LiteralPath (Join-Path $taskRoot 'tests\nlp-cases.json') -Algorithm SHA256).Hash
    CaseIds=$CaseIds; ServerPid=$taskServer.Id; Port=$taskPort; Threads=4; GpuLayers=0; Context=8192
    ProductCommit=(& git -C $taskRoot rev-parse HEAD); Outcome='starting'
    WorkingTreeDirty=([bool](& git -C $taskRoot status --porcelain))
    ProbeSha256=(Get-FileHash -LiteralPath (Join-Path $taskRoot 'tests\PerfCompass.ModelProbe\bin\Release\net10.0-windows\PerfCompass.ModelProbe.dll') -Algorithm SHA256).Hash
    ProductAssemblySha256=(Get-FileHash -LiteralPath (Join-Path $taskRoot 'tests\PerfCompass.ModelProbe\bin\Release\net10.0-windows\PerfCompass.dll') -Algorithm SHA256).Hash
}
function Write-TaskManifest { [IO.File]::WriteAllText((Join-Path $taskOutput 'manifest.json'),($taskManifest | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false)) }
try {
    Write-TaskManifest
    $taskHealthy=$false
    for ($taskWait=0; $taskWait -lt 60; $taskWait++) {
        if ($taskServer.HasExited) { throw 'Research server exited during startup.' }
        try { if ((Invoke-RestMethod -Uri "$taskEndpoint/health" -TimeoutSec 2).status -eq 'ok') { $taskHealthy=$true; break } } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $taskHealthy) { throw 'Research server did not become ready.' }
    $taskManifest.Outcome='running'; Write-TaskManifest
    Write-Output ("Running verified synthetic benchmark: server PID " + $taskServer.Id + ', model ' + $taskModelName)
    $taskProbe = Join-Path $taskRoot 'tests\PerfCompass.ModelProbe\bin\Release\net10.0-windows\PerfCompass.ModelProbe.dll'
    & $taskDotnet $taskProbe --batch "$taskEndpoint/v1/chat/completions" perfcompass-research (Join-Path $taskRoot 'tests\nlp-cases.json') (Join-Path $taskOutput 'cases') $CaseIds
    if ($LASTEXITCODE -ne 0) { throw 'Batch probe did not complete; inspect per-case records.' }
    $taskManifest.Outcome='completed'
} catch { $taskManifest.Outcome='failed'; $taskManifest.ErrorType=$_.Exception.GetType().Name; throw }
finally {
    try {
        # Process-owned metrics cover startup and inference, not whole-system load or commit.
        try {
            $taskServer.Refresh()
            $taskManifest.ServerPeakWorkingSetBytes=$taskServer.PeakWorkingSet64
            $taskManifest.ServerCpuSeconds=$taskServer.TotalProcessorTime.TotalSeconds
            $taskManifest.LogicalProcessors=[Environment]::ProcessorCount
        } catch { $taskManifest.ResourceMetricsErrorType=$_.Exception.GetType().Name }
        if (-not $taskServer.HasExited) { $taskServer.Kill(); $taskServer.WaitForExit(10000) | Out-Null }
        if (-not $taskServer.HasExited) { $taskManifest.Outcome='cleanup-failed'; throw 'Owned research server did not exit; its PID remains in the manifest.' }
    } finally {
        $taskManifest.ServerExited=$taskServer.HasExited
        $taskManifest.Finished=[DateTimeOffset]::UtcNow.ToString('O'); Write-TaskManifest
    }
}
$global:LASTEXITCODE=0
