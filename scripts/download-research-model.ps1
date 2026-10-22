param([string]$ModelMetadata = '.tools/nlp/model8b-source.json', [int]$ChunkMiB = 8, [ValidateRange(1,4)][int]$Connections = 1, [ValidateSet('official','mirror')][string]$Transport = 'official')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$taskAllowed = [IO.Path]::GetFullPath((Join-Path $taskRoot '.tools\nlp'))
$taskMetadataPath = [IO.Path]::GetFullPath((Join-Path $taskRoot $ModelMetadata))
if (-not $taskMetadataPath.StartsWith($taskAllowed+'\',[StringComparison]::OrdinalIgnoreCase) -or $ChunkMiB -lt 1 -or $ChunkMiB -gt 64) { throw 'Choose metadata inside .tools/nlp and a 1-64 MiB chunk size.' }
$taskMetadata = Get-Content -LiteralPath $taskMetadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($taskMetadata.Repository -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]*/[a-zA-Z0-9][a-zA-Z0-9._-]*$' -or $taskMetadata.File -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]*\.gguf$' -or $taskMetadata.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or [long]$taskMetadata.Bytes -le 0) { throw 'Invalid public model metadata.' }
$taskFinal = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $taskMetadataPath) $taskMetadata.File))
$taskPart = $taskFinal + '.part'
function Assert-TaskPath([string]$path) {
    if (-not [IO.Path]::GetFullPath($path).StartsWith($taskAllowed+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Research path escaped the workspace model folder.' }
    $cursor=[IO.Path]::GetFullPath($path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) { if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked model paths are not supported.' } }
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
}
Assert-TaskPath $taskFinal; Assert-TaskPath $taskPart
if (Test-Path -LiteralPath $taskFinal) {
    if ((Get-FileHash -LiteralPath $taskFinal -Algorithm SHA256).Hash -ne $taskMetadata.Sha256) { throw 'Existing final model does not match metadata.' }
    Write-Output 'Final model already verified.'; return
}
$taskHost=if ($Transport -eq 'mirror') { 'https://hf-mirror.com/' } else { 'https://huggingface.co/' }
$taskUrl=$taskHost+$taskMetadata.Repository+'/resolve/main/'+$taskMetadata.File+'?download=true'
$taskCurl=Join-Path $env:SystemRoot 'System32\curl.exe'
if (-not (Test-Path -LiteralPath $taskCurl)) { throw 'Windows system curl is required.' }
function New-TaskRange([long]$start,[long]$end) {
    $scratch=$taskPart+'.range-'+[Guid]::NewGuid().ToString('N')+'.tmp'
    foreach ($path in @($scratch,($scratch+'.headers'),($scratch+'.log'))) { Assert-TaskPath $path }
    return [pscustomobject]@{ Start=$start; End=$end; Bytes=$end-$start+1; Data=$scratch; Headers=$scratch+'.headers'; Log=$scratch+'.log'; Process=$null }
}
function Start-TaskRange($range) {
    $arguments=@('--silent','--show-error','--fail','--location','--connect-timeout','20','--max-time','120','--range',($range.Start.ToString()+'-'+$range.End.ToString()),'--dump-header',('"'+$range.Headers+'"'),'--output',('"'+$range.Data+'"'),$taskUrl)
    $range.Process=Start-Process -FilePath $taskCurl -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardError $range.Log
}
function Test-TaskRange($range) {
    if (-not (Test-Path -LiteralPath $range.Headers) -or -not (Test-Path -LiteralPath $range.Data)) { return $false }
    $matches=[regex]::Matches([IO.File]::ReadAllText($range.Headers),'(?im)^content-range:\s*bytes\s+(\d+)-(\d+)/(\d+)')
    if ($matches.Count -eq 0) { return $false }
    $last=$matches[$matches.Count-1]
    return [long]$last.Groups[1].Value -eq $range.Start -and [long]$last.Groups[2].Value -eq $range.End -and [long]$last.Groups[3].Value -eq [long]$taskMetadata.Bytes -and (Get-Item -LiteralPath $range.Data).Length -eq $range.Bytes
}
# Never launch while another downloader owns this prefix; an exclusive stream also prevents concurrent writers.
$taskStream=[IO.File]::Open($taskPart,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$taskStopped=$false
try {
    if ($taskStream.Length -gt [long]$taskMetadata.Bytes) { throw 'Existing prefix exceeds expected model size.' }
    while ($taskStream.Length -lt [long]$taskMetadata.Bytes) {
        if (Test-Path -LiteralPath ($taskPart+'.stop')) { $taskStopped=$true; break }
        $taskRanges=@()
        try {
            for ($taskIndex=0; $taskIndex -lt $Connections; $taskIndex++) {
                $taskStart=$taskStream.Length+[long]$taskIndex*$ChunkMiB*1048576
                if ($taskStart -ge [long]$taskMetadata.Bytes) { break }
                $taskEnd=[Math]::Min([long]$taskMetadata.Bytes-1,$taskStart+[long]$ChunkMiB*1048576-1)
                $taskRange=New-TaskRange $taskStart $taskEnd
                $taskRanges+=@($taskRange); Start-TaskRange $taskRange
            }
            foreach ($taskRange in $taskRanges) {
                $taskComplete=$false
                for ($taskAttempt=0; $taskAttempt -lt 3; $taskAttempt++) {
                    if (-not $taskRange.Process.WaitForExit(125000)) { $taskRange.Process.Kill(); $taskRange.Process.WaitForExit(10000) | Out-Null }
                    if (Test-TaskRange $taskRange) { $taskComplete=$true; break }
                    if ($taskAttempt -lt 2) { $taskRange.Process.Dispose(); Start-TaskRange $taskRange }
                }
                if (-not $taskComplete) { throw 'Range did not complete. Successful prefix retained.' }
                if ($taskStream.Length -ne $taskRange.Start) { throw 'Non-contiguous model range; prefix retained.' }
                $taskStream.Position=$taskStream.Length
                $taskInput=[IO.File]::OpenRead($taskRange.Data)
                try { $taskInput.CopyTo($taskStream); $taskStream.Flush($true) } finally { $taskInput.Dispose() }
                Write-Output ('Downloaded '+$taskStream.Length+' / '+$taskMetadata.Bytes+' bytes; final verification pending')
            }
        } finally {
            foreach ($taskRange in $taskRanges) {
                if ($taskRange.Process) {
                    if (-not $taskRange.Process.HasExited) { $taskRange.Process.Kill(); $taskRange.Process.WaitForExit(10000) | Out-Null }
                    $taskRange.Process.Dispose()
                }
                foreach ($path in @($taskRange.Data,$taskRange.Headers,$taskRange.Log)) {
                    if (Test-Path -LiteralPath $path) { Assert-TaskPath $path; Remove-Item -LiteralPath $path }
                }
            }
        }
    }
    if (-not $taskStopped) {
        $taskStream.Flush($true); $taskStream.Position=0
        $taskHasher=[Security.Cryptography.SHA256]::Create()
        try { $taskActual=[BitConverter]::ToString($taskHasher.ComputeHash($taskStream)).Replace('-','').ToLowerInvariant() } finally { $taskHasher.Dispose() }
        if ($taskActual -ne $taskMetadata.Sha256.ToLowerInvariant()) { throw 'Full model SHA256 mismatch. Prefix retained, not promoted to final model.' }
    }
} finally { $taskStream.Dispose() }
if ($taskStopped) { Write-Output 'Stopped at a flushed checkpoint; prefix retained.'; return }
Assert-TaskPath $taskPart; Assert-TaskPath $taskFinal
if (Test-Path -LiteralPath $taskFinal) { throw 'Final model appeared concurrently; prefix retained.' }
[IO.File]::Move($taskPart,$taskFinal)
Write-Output ('Verified model SHA256 '+$taskActual)
$global:LASTEXITCODE=0
