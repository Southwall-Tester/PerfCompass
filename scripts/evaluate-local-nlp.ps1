param([string[]]$CaseIds = @('11','12','18','23','24','27'), [int]$Port = 18109)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskRunner = Join-Path $taskRoot '.tools\nlp\llama-cpu\llama-server.exe'
$taskModel = Join-Path $taskRoot '.tools\nlp\Qwen3-4B-Q4_K_M.gguf'
if (-not (Test-Path -LiteralPath $taskRunner) -or -not (Test-Path -LiteralPath $taskModel)) { throw 'The isolated research runner and verified model are required in .tools/nlp.' }
$taskOutputRoot = Join-Path $taskRoot 'artifacts\nlp-model'
New-Item -ItemType Directory -Force -Path $taskOutputRoot | Out-Null
$taskCorpus = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'tests\nlp-cases.json') | ConvertFrom-Json
$taskSchema = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'tests\nlp-output.schema.json') | ConvertFrom-Json
$taskPrompt = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'tests\nlp-extraction-prompt.txt')
$taskEndpoint = "http://127.0.0.1:$Port"
$taskServer = Start-Process -FilePath $taskRunner -ArgumentList '-m',('"'+$taskModel+'"'),'--host','127.0.0.1','--port',$Port,'--alias','perfcompass-nlp','-t','4','-tb','4','-ngl','0','-c','8192','-np','1' -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOutputRoot 'server.stdout.log') -RedirectStandardError (Join-Path $taskOutputRoot 'server.stderr.log')
try {
    $taskHealthy = $false
    for ($taskAttempt = 0; $taskAttempt -lt 40; $taskAttempt++) {
        if ($taskServer.HasExited) { throw 'Research server exited. See artifacts/nlp-model/server.stderr.log.' }
        try { $taskHealth = Invoke-RestMethod -Uri "$taskEndpoint/health" -TimeoutSec 2; if ($taskHealth.status -eq 'ok') { $taskHealthy = $true; break } } catch { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $taskHealthy) { throw 'Research server did not become ready.' }
    foreach ($taskCase in $taskCorpus | Where-Object { $CaseIds -contains $_.id }) {
        $taskRequest = @{
            model='perfcompass-nlp'; stream=$false; temperature=0; max_tokens=1400
            chat_template_kwargs=@{enable_thinking=$false}
            response_format=@{type='json_schema';json_schema=@{name='computer_requirements';strict=$true;schema=$taskSchema}}
            messages=@(@{role='system';content=$taskPrompt},@{role='user';content=([pscustomobject]@{description=$taskCase.text} | ConvertTo-Json -Compress)})
        } | ConvertTo-Json -Depth 30 -Compress
        $taskTimer = [Diagnostics.Stopwatch]::StartNew()
        try {
            $taskReply = Invoke-RestMethod -Method Post -Uri "$taskEndpoint/v1/chat/completions" -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($taskRequest)) -TimeoutSec 180
            $taskContent = $taskReply.choices[0].message.content
            $taskParsed = $taskContent | ConvertFrom-Json
            $taskGrounded = @($taskParsed.facts | Where-Object { $_.evidence -and $taskCase.text.Contains([string]$_.evidence) }).Count
            $taskResult = [pscustomobject]@{Id=$taskCase.id;Group=$taskCase.group;Source=$taskCase.text;Model='Qwen3-4B-Q4_K_M';Runner='llama.cpp b11815 CPU 4 threads';Seconds=$taskTimer.Elapsed.TotalSeconds;FinishReason=$taskReply.choices[0].finish_reason;Facts=@($taskParsed.facts).Count;GroundedFacts=$taskGrounded;Extraction=$taskParsed;Usage=$taskReply.usage;Note='Source grounding and valid JSON do not establish semantic correctness. Manual review required.'}
        } catch { $taskResult = [pscustomobject]@{Id=$taskCase.id;Source=$taskCase.text;Seconds=$taskTimer.Elapsed.TotalSeconds;Error=$_.Exception.Message} }
        $taskResult | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath (Join-Path $taskOutputRoot ("case-"+$taskCase.id+'.json')) -Encoding UTF8
        Write-Output ("Finished case "+$taskCase.id+" in "+[Math]::Round($taskTimer.Elapsed.TotalSeconds,1)+" seconds")
    }
} finally {
    if (-not $taskServer.HasExited) { Stop-Process -Id $taskServer.Id }
}
