$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $taskDotnet) { $env:DOTNET_ROOT = Split-Path -Parent $taskDotnet } else { $taskDotnet = 'dotnet' }
& $taskDotnet build (Join-Path $PSScriptRoot 'PerfCompass.ModelProbe\PerfCompass.ModelProbe.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed' }
$taskProbe = Join-Path $PSScriptRoot 'PerfCompass.ModelProbe\bin\Release\net10.0-windows\PerfCompass.ModelProbe.dll'
$taskData = Join-Path $taskRoot ('artifacts\probe-batch-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskData) | Out-Null
$taskCorpus = Join-Path $taskData 'cases.json'
[IO.File]::WriteAllText($taskCorpus, '[{"id":"good","text":"内存至少64GB"},{"id":"failure","text":"合成失败输入"}]', [Text.UTF8Encoding]::new($false))
$taskEnvelope = @{ choices = @(@{ finish_reason = 'stop'; message = @{ content = '{"facts":[{"subject":"内存","kind":"resource","polarity":"required","timeframe":"current","value":"至少64","unit":"GB","condition":"","evidence":"内存至少64GB"}],"questions":[]}' } }) } | ConvertTo-Json -Depth 12 -Compress
# A port chosen by the OS avoids collisions with user services; publish its value only after binding.
$taskPortFile = Join-Path $taskData 'port.txt'
$taskServer = Start-Job -ArgumentList $taskEnvelope,$taskPortFile -ScriptBlock {
    param($envelope, $portFile)
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    [IO.File]::WriteAllText($portFile, [string]$listener.LocalEndpoint.Port)
    try {
        for ($i=0; $i -lt 2; $i++) {
            $client = $listener.AcceptTcpClient()
            try {
                $stream=$client.GetStream(); $stream.ReadTimeout=15000
                $header=[Collections.Generic.List[byte]]::new()
                while ($true) {
                    $b=$stream.ReadByte(); if ($b -lt 0) { throw 'Request ended early' }; $header.Add([byte]$b)
                    if ($header.Count -gt 32768) { throw 'Request headers too large' }
                    if ($header.Count -ge 4 -and [Text.Encoding]::ASCII.GetString($header.ToArray()).EndsWith("`r`n`r`n")) { break }
                }
                $text=[Text.Encoding]::ASCII.GetString($header.ToArray())
                $length=[int]([regex]::Match($text,'(?im)^Content-Length:\s*(\d+)').Groups[1].Value)
                if ($length -le 0 -or $length -gt 60000) { throw 'Invalid body length' }
                $body=New-Object byte[] $length; $read=0
                while ($read -lt $length) { $n=$stream.Read($body,$read,$length-$read); if ($n -le 0) { throw 'Truncated body' }; $read+=$n }
                $request=[Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json
                if ($i -eq 0 -and $request.messages[-1].content -ne '内存至少64GB') { throw 'Probe did not use production raw-text request' }
                $payload=if ($i -eq 0) { $envelope } else { 'synthetic-sensitive-provider-body' }
                $bytes=[Text.Encoding]::UTF8.GetBytes($payload)
                $status=if ($i -eq 0) { '200 OK' } else { '503 Unavailable' }
                $response=[Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $status`r`nContent-Type: application/json`r`nContent-Length: $($bytes.Length)`r`nConnection: close`r`n`r`n")
                $stream.Write($response,0,$response.Length); $stream.Write($bytes,0,$bytes.Length); $stream.Flush()
            } finally { $client.Dispose() }
        }
        'PASS synthetic transport served both requests'
    } finally { $listener.Stop() }
}
try {
    for ($taskWait=0; $taskWait -lt 100 -and -not (Test-Path -LiteralPath $taskPortFile); $taskWait++) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $taskPortFile)) { throw 'Transport did not bind' }
    $taskPort=[int][IO.File]::ReadAllText($taskPortFile)
    $taskOutput=Join-Path $taskData 'output'
    & $taskDotnet $taskProbe --batch "http://127.0.0.1:$taskPort/chat" synthetic-fixture $taskCorpus $taskOutput all
    if ($LASTEXITCODE -ne 0) { throw 'Batch entry failed' }
    $taskRecords=Get-Content -LiteralPath (Join-Path $taskOutput 'results.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($taskRecords.Count -ne 2 -or $taskRecords[0].Outcome -ne 'received' -or $taskRecords[0].Validation[0].QuantityCandidates[0].Target -ne 64 -or $taskRecords[1].Outcome -ne 'failed') { throw 'Batch results did not preserve typed success and transport failure' }
    if ([IO.File]::ReadAllText((Join-Path $taskOutput 'results.json')).Contains('synthetic-sensitive-provider-body')) { throw 'Provider body leaked' }
    'PASS batch entry preserves candidates and isolated failure without leaking provider body'
    & $taskDotnet $taskProbe --batch https://unit.invalid/chat synthetic-fixture $taskCorpus (Join-Path $taskData 'remote') all
    if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath (Join-Path $taskData 'remote'))) { throw 'Remote research endpoint was accepted' }
    'PASS batch entry refuses non-loopback endpoints before creating output'
    & $taskDotnet $taskProbe --batch "http://127.0.0.1:$taskPort/chat" synthetic-fixture $taskCorpus $taskOutput all
    if ($LASTEXITCODE -ne 1) { throw 'Existing output was overwritten' }
    'PASS batch entry refuses to overwrite previous experiments'
    $taskServer | Wait-Job -Timeout 10 | Out-Null
    if ($taskServer.State -ne 'Completed') { throw 'Synthetic transport failed' }
    Receive-Job $taskServer
} finally {
    if ($taskServer.State -eq 'Running') { Stop-Job $taskServer }
    Remove-Job $taskServer -Force
}

$global:LASTEXITCODE = 0
