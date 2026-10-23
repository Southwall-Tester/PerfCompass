param([Parameter(Mandatory = $true)][string]$RunDirectory)

# Windows PowerShell 5.1 compatible. Reads fixed local artifacts only; no command,
# executable, environment setting, or path taken from JSON is ever executed/opened.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$taskRoot = [IO.Path]::GetFullPath($RunDirectory).TrimEnd('\', '/')
if (-not [IO.Directory]::Exists($taskRoot)) { throw 'RunDirectory must be an existing recording directory.' }
$taskInvariant = [Globalization.CultureInfo]::InvariantCulture
$taskUtf8 = New-Object Text.UTF8Encoding($false, $true)

function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}
function Get-Number($Value) {
    if ($null -eq $Value -or $Value -is [bool]) { return $null }
    $number = 0.0
    if (-not [double]::TryParse([string]$Value, [Globalization.NumberStyles]::Float, $taskInvariant, [ref]$number) -or
        [double]::IsNaN($number) -or [double]::IsInfinity($number)) { return $null }
    return $number
}
function Get-Percent($Value) {
    $number = Get-Number $Value
    if ($null -ne $number -and $number -ge 0 -and $number -le 100) { return $number }
    return $null
}
function Get-SmiPercent($Value) {
    if ($null -eq $Value) { return $null }
    $text = ([string]$Value).Trim()
    # NVIDIA-SMI CSV includes units unless nounits was requested. Only a bare
    # numeric value or the percent unit is accepted; N/A and other units stay null.
    if ($text -notmatch '^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?\s*%?$') { return $null }
    return Get-Percent ($text -replace '\s*%$', '')
}
function Get-Utc($Value) {
    if ($Value -is [DateTimeOffset]) { return $Value.ToUniversalTime() }
    if ($Value -is [DateTime]) { return ([DateTimeOffset]$Value).ToUniversalTime() }
    if ([string]$Value -notmatch '(Z|[+-]\d{2}:\d{2})$') { return $null }
    $stamp = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse([string]$Value, $taskInvariant, [Globalization.DateTimeStyles]::None, [ref]$stamp)) {
        return $stamp.ToUniversalTime()
    }
    return $null
}
function Get-Iso($Value) { if ($null -eq $Value) { return $null }; return $Value.ToString('o', $taskInvariant) }
function Assert-PlainPath([string]$Path) {
    $cursor = $Path
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Artifact paths must not traverse links or junctions.' }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}
function Read-FixedInput([string]$RelativePath) {
    $path = [IO.Path]::GetFullPath((Join-Path $taskRoot $RelativePath))
    if (-not $path.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixed input leaves RunDirectory.' }
    Assert-PlainPath $path
    # Hash the same bytes that are parsed, rather than a second read of a file
    # which might have changed. Finished recording artifacts should be used.
    $bytes = [IO.File]::ReadAllBytes($path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    return [pscustomobject]@{ Path = $RelativePath.Replace('\', '/'); Sha256 = $hash; Bytes = $bytes.LongLength; Text = $taskUtf8.GetString($bytes).TrimStart([char]0xfeff) }
}
function Split-CsvLine([string]$Line) {
    $fields = New-Object 'Collections.Generic.List[string]'
    $field = New-Object Text.StringBuilder
    $quoted = $false
    for ($i = 0; $i -lt $Line.Length; $i++) {
        $character = $Line[$i]
        if ($character -eq '"') {
            if ($quoted -and $i + 1 -lt $Line.Length -and $Line[$i + 1] -eq '"') { [void]$field.Append('"'); $i++ }
            else { $quoted = -not $quoted }
        }
        elseif ($character -eq ',' -and -not $quoted) { $fields.Add($field.ToString().Trim()); [void]$field.Clear() }
        else { [void]$field.Append($character) }
    }
    if ($quoted) { throw 'Unclosed CSV quotation.' }
    $fields.Add($field.ToString().Trim())
    return $fields.ToArray()
}
function Get-WeightedStats([object[]]$Readings) {
    $count = $Readings.Count; $seconds = 0.0; $covered = 0.0; $weighted = 0.0; $complete = 0
    foreach ($reading in $Readings) {
        $seconds += $reading.Seconds; $covered += $reading.CoveredSeconds; $weighted += $reading.Value * $reading.Seconds
        if ($reading.Complete) { $complete++ }
    }
    $minimum = $null; $maximum = $null; $mean = $null; $p95 = $null
    if ($count -gt 0) {
        $ordered = @($Readings | Sort-Object Value)
        $minimum = $ordered[0].Value; $maximum = $ordered[-1].Value
        if ($count -ge 2 -and $seconds -gt 0) {
            $mean = [Math]::Max($minimum, [Math]::Min($maximum, $weighted / $seconds))
            $cumulative = 0.0
            foreach ($reading in $ordered) { $cumulative += $reading.Seconds; if ($cumulative -ge $seconds * .95) { $p95 = $reading.Value; break } }
            if ($null -eq $p95) { $p95 = $maximum }
        }
    }
    return [pscustomobject][ordered]@{
        Status = $(if ($count -ge 2) { 'review-required' } else { 'insufficient' })
        Count = $count; EffectiveSeconds = $seconds; CoveredSeconds = $covered
        Mean = $mean; P95 = $p95; Min = $minimum; Max = $maximum
        CompleteCount = $complete; PartialCount = $count - $complete
    }
}
function Get-GpuMetric([object[]]$Rows, [string]$Kind) {
    $readings = New-Object 'Collections.Generic.List[object]'
    $sourceNames = New-Object 'Collections.Generic.List[string]'
    $sourceScopes = New-Object 'Collections.Generic.List[object]'
    $exposed = 0; $invalid = 0
    foreach ($row in $Rows) {
        $sample = $row.Sample; $entry = $null
        $version = [string](Get-Field $sample 'GpuEngineVersion')
        $observed = $null; $covered = $null; $value = $null; $complete = $false
        if ($Kind -eq 'legacy-core-or-3d') {
            $exposed++; $value = Get-Percent (Get-Field $sample 'Gpu')
            $rawDuration = Get-Field $sample 'GpuCoverage'
            $observed = $(if ($null -eq $rawDuration) { $row.Seconds } else { Get-Number $rawDuration })
            $covered = $observed
            $complete = $null -ne $observed -and $observed -ge $row.Seconds - 0.0000001
            $source = [string](Get-Field $sample 'GpuMetricSource')
            $sources = @(); if (-not [string]::IsNullOrWhiteSpace($source)) { $sources = @($source) }
        }
        else {
            $entries = @(Get-Field $sample 'GpuEngines' | Where-Object { (Get-Field $_ 'Kind') -ceq $Kind })
            if ($entries.Count -eq 0) { continue }
            $exposed++
            if ($entries.Count -ne 1 -or $version -notin @('gpu-engines-1.0', 'gpu-engines-1.1')) { $invalid++; continue }
            $entry = $entries[0]; $value = Get-Percent (Get-Field $entry 'Load')
            $rawComplete = Get-Field $entry 'Complete'
            if ($rawComplete -isnot [bool]) { $invalid++; continue }
            $complete = $rawComplete
            $sources = @(Get-Field $entry 'Sources' | Where-Object { $null -ne $_ })
            if ($sources.Count -eq 0 -or @($sources | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) { $invalid++; continue }
            $rawDuration = Get-Field $entry 'ObservedSeconds'
            $observed = $(if ($null -eq $rawDuration) { $row.Seconds } else { Get-Number $rawDuration })
            $rawCovered = Get-Field $entry 'CoverageSeconds'
            $covered = $(if ($null -eq $rawCovered) { if ($complete) { $observed } else { 0.0 } } else { Get-Number $rawCovered })
        }
        # An exposed sensor with no value is still a named missing source. Do not
        # erase its provenance merely because it contributes no weighted reading.
        $signature = [ordered]@{ Version = $version; Sources = @($sources | Sort-Object -CaseSensitive) } | ConvertTo-Json -Compress -Depth 4
        foreach ($sourceName in $sources) { $sourceNames.Add($sourceName) }
        $sourceScopes.Add([pscustomobject]@{ Signature = $signature; Version = $version; Sources = $sources })
        if ($null -eq $value) { continue }
        if ($null -eq $observed -or $observed -le 0 -or $observed -gt $row.Seconds + 0.0000001 -or
            $null -eq $covered -or $covered -lt 0 -or $covered -gt $observed + 0.0000001 -or
            ($complete -and $covered + 0.0000001 -lt $observed)) { $invalid++; continue }
        $readings.Add([pscustomobject]@{
            Value = $value; Seconds = [Math]::Min($row.Seconds, $observed); CoveredSeconds = [Math]::Min($row.Seconds, [Math]::Min($observed, $covered))
            Complete = $complete; Signature = $signature; Version = $version; Sources = $sources
        })
    }
    $values = $readings.ToArray(); $stats = Get-WeightedStats $values
    $groups = @($values | Group-Object Signature -CaseSensitive | ForEach-Object {
        $groupValues = @($_.Group)
        [pscustomobject]@{ Version = $groupValues[0].Version; Sources = @($groupValues[0].Sources); Statistics = Get-WeightedStats $groupValues }
    })
    $exposedScopes = @($sourceScopes.ToArray() | Group-Object Signature -CaseSensitive | ForEach-Object {
        $scopeRows = @($_.Group)
        [pscustomobject]@{ Version = $scopeRows[0].Version; Sources = @($scopeRows[0].Sources); ExposedRows = $scopeRows.Count }
    })
    $mixed = $exposedScopes.Count -gt 1
    # Changing source sets are not one interchangeable load metric. Keep the
    # per-source distributions; pooled extrema remain observed clues only.
    if ($mixed) { $stats.Mean = $null; $stats.P95 = $null; if ($stats.Count -ge 2) { $stats.Status = 'mixed-sources' } }
    return [pscustomobject][ordered]@{
        Kind = $Kind; EligibleRows = $Rows.Count; ExposedRows = $exposed
        MissingOrInvalidRows = $Rows.Count - $values.Count; InvalidRows = $invalid
        Sources = @($sourceNames.ToArray() | Sort-Object -Unique -CaseSensitive)
        MixedSources = $mixed; Statistics = $stats; ExposedSourceScopes = $exposedScopes; BySource = $groups
    }
}
function Get-SmiMetric([object[]]$Rows, [string]$Property) {
    $numbers = @($Rows | ForEach-Object { Get-Percent (Get-Field $_ $Property) } | Where-Object { $null -ne $_ } | Sort-Object)
    $median = $null; $minimum = $null; $maximum = $null
    if ($numbers.Count -gt 0) {
        $minimum = $numbers[0]; $maximum = $numbers[-1]
        if ($numbers.Count -ge 2) {
            $middle = [int][Math]::Floor($numbers.Count / 2)
            $median = $(if ($numbers.Count % 2) { $numbers[$middle] } else { ($numbers[$middle - 1] + $numbers[$middle]) / 2 })
        }
    }
    return [pscustomobject][ordered]@{
        Status = $(if ($numbers.Count -ge 2) { 'review-required' } else { 'insufficient' })
        Count = $numbers.Count; MissingOrInvalidCount = $Rows.Count - $numbers.Count
        Min = $minimum; Median = $median; Max = $maximum
    }
}

$taskResultInput = Read-FixedInput 'result.json'
$taskTelemetryInput = Read-FixedInput 'data\validation\telemetry-cross-check.json'
$taskSmiInput = Read-FixedInput 'nvidia-smi-loop.stdout.log.received.jsonl'
$taskResult = $taskResultInput.Text | ConvertFrom-Json
$taskTelemetry = $taskTelemetryInput.Text | ConvertFrom-Json
$taskRawRows = @(Get-Field $taskTelemetry 'Rows' | Where-Object { $null -ne $_ })
$taskRows = New-Object 'Collections.Generic.List[object]'
$taskIssues = New-Object 'Collections.Generic.List[object]'
$taskInvalidInterval = 0; $taskZeroDuration = 0; $taskTimingIssueRows = 0
$taskIssueKinds = @{}; $taskValidTimes = New-Object 'Collections.Generic.List[DateTimeOffset]'
for ($rowIndex = 0; $rowIndex -lt $taskRawRows.Count; $rowIndex++) {
    $pair = $taskRawRows[$rowIndex]; $sample = Get-Field $pair 'Sample'
    $seconds = Get-Number (Get-Field $sample 'Seconds'); $time = Get-Utc (Get-Field $sample 'Time')
    $collectorMs = Get-Number (Get-Field $pair 'CollectorMilliseconds')
    $timingIssues = @(@('SamplingIssue', 'TimingIssue') | ForEach-Object { Get-Field $sample $_ } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique)
    if ($timingIssues.Count -gt 0) {
        $taskTimingIssueRows++
        foreach ($issue in $timingIssues) { $taskIssueKinds[[string]$issue] = 1 + [int]$taskIssueKinds[[string]$issue] }
    }
    if ($null -ne $seconds -and $seconds -le 0) { $taskZeroDuration++ }
    if ($null -eq $time -or $null -eq $seconds -or $seconds -le 0 -or $seconds -gt 15 -or
        $null -eq $collectorMs -or $collectorMs -lt 0 -or $timingIssues.Count -gt 0) { $taskInvalidInterval++; continue }
    try { $start = $time.AddSeconds(-$seconds); $end = $time.AddMilliseconds($collectorMs) }
    catch { $taskInvalidInterval++; continue }
    $taskRows.Add([pscustomobject]@{ Index = $rowIndex; Sample = $sample; Time = $time; Start = $start; End = $end; Seconds = $seconds })
    $taskValidTimes.Add($time)
}

$taskRequiredHeaders = @('timestamp', 'uuid', 'pci.bus_id', 'utilization.gpu', 'utilization.encoder', 'utilization.decoder', 'utilization.memory', 'memory.used', 'memory.total', 'temperature.gpu')
$taskHeader = $null; $taskSmiRows = New-Object 'Collections.Generic.List[object]'
$taskSmiLineNumber = 0; $taskSmiMalformed = 0; $taskSmiHeaders = 0
foreach ($line in ($taskSmiInput.Text -split '\r?\n')) {
    $taskSmiLineNumber++
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    try {
        $received = $line | ConvertFrom-Json
        $columns = @(Split-CsvLine ([string](Get-Field $received 'Line')))
        $names = @($columns | ForEach-Object { ($_ -replace '\s*\[[^\]]*\]\s*$', '').Trim().ToLowerInvariant() })
        if ($names -contains 'timestamp' -and $names -contains 'uuid') {
            if ($names.Count -ne 10 -or @($names | Select-Object -Unique).Count -ne 10 -or
                @($taskRequiredHeaders | Where-Object { $names -notcontains $_ }).Count -gt 0) { throw 'Unexpected NVIDIA-SMI header.' }
            $taskHeader = @{}; for ($i = 0; $i -lt $names.Count; $i++) { $taskHeader[$names[$i]] = $i }
            $taskSmiHeaders++; continue
        }
        if ($null -eq $taskHeader -or $columns.Count -ne 10) { throw 'Missing header or unexpected NVIDIA-SMI column count.' }
        $receivedUtc = Get-Utc (Get-Field $received 'ReceivedUtc')
        if ($null -eq $receivedUtc) { throw 'Invalid receipt timestamp.' }
        $taskSmiRows.Add([pscustomobject]@{
            ReceivedUtc = $receivedUtc; ApiTimestamp = $columns[$taskHeader['timestamp']]
            Gpu = Get-SmiPercent $columns[$taskHeader['utilization.gpu']]
            Encoder = Get-SmiPercent $columns[$taskHeader['utilization.encoder']]
            Decoder = Get-SmiPercent $columns[$taskHeader['utilization.decoder']]
        })
    }
    catch {
        $taskSmiMalformed++
        $taskIssues.Add([pscustomobject]@{ Input = $taskSmiInput.Path; Line = $taskSmiLineNumber; Reason = 'Unusable NVIDIA-SMI receipt/CSV row; original retained.' })
    }
}

$taskPhases = New-Object 'Collections.Generic.List[object]'
$taskRawPhases = @(Get-Field $taskResult 'Phases' | Where-Object { $null -ne $_ })
$taskProcesses = @(Get-Field $taskResult 'Processes' | Where-Object { $null -ne $_ })
foreach ($phase in $taskRawPhases) {
    $name = [string](Get-Field $phase 'Name')
    $start = Get-Utc (Get-Field $phase 'StartedUtc'); $end = Get-Utc (Get-Field $phase 'EndedUtc')
    $startElapsed = Get-Number (Get-Field $phase 'StartedElapsedSeconds'); $endElapsed = Get-Number (Get-Field $phase 'EndedElapsedSeconds')
    $clockDifference = $null; $windowStart = $null; $windowEnd = $null; $reason = ''; $status = 'insufficient'
    $core = $null; $engines = @(); $smiSummary = $null; $eligibleCount = 0
    if ($null -eq $start -or $null -eq $end -or $null -eq $startElapsed -or $null -eq $endElapsed -or $end -le $start -or $endElapsed -le $startElapsed) {
        $reason = 'Missing, reversed or invalid phase timestamps.'
    }
    else {
        $clockDifference = [Math]::Abs(($end - $start).TotalSeconds - ($endElapsed - $startElapsed))
        if ($clockDifference -gt .25) { $reason = 'UTC and elapsed phase durations differ by more than 0.25 seconds.'; $status = 'invalid-clock' }
        else {
            $windowStart = $start.AddSeconds(2); $windowEnd = $end.AddSeconds(-2)
            if ($name -in @('encode', 'decode')) {
                $role = $(if ($name -eq 'encode') { 'encode-nvenc' } else { 'decode-cuvid' })
                $processes = @($taskProcesses | Where-Object { (Get-Field $_ 'Role') -ceq $role })
                if ($processes.Count -ne 1) { $reason = 'No unique workload process record.' }
                else {
                    $firstMark = Get-Field $processes[0] 'FirstFrame'; $qMark = Get-Field $processes[0] 'QRequested'
                    $first = Get-Utc (Get-Field $firstMark 'Utc'); $q = Get-Utc (Get-Field $qMark 'Utc')
                    $firstElapsed = Get-Number (Get-Field $firstMark 'ElapsedSeconds'); $qElapsed = Get-Number (Get-Field $qMark 'ElapsedSeconds')
                    if ($null -eq $first -or $null -eq $q -or $first -lt $start -or $q -gt $end -or $q -le $first -or
                        $null -eq $firstElapsed -or $null -eq $qElapsed -or $qElapsed -le $firstElapsed) { $reason = 'Missing or out-of-phase first-frame/quit marks.' }
                    elseif ([Math]::Abs(($first - $start).TotalSeconds - ($firstElapsed - $startElapsed)) -gt .25 -or
                        [Math]::Abs(($q - $start).TotalSeconds - ($qElapsed - $startElapsed)) -gt .25) { $reason = 'Workload UTC and elapsed marks disagree.'; $status = 'invalid-clock' }
                    else {
                        if ($first.AddSeconds(2) -gt $windowStart) { $windowStart = $first.AddSeconds(2) }
                        if ($q.AddSeconds(-2) -lt $windowEnd) { $windowEnd = $q.AddSeconds(-2) }
                    }
                }
            }
            if ($reason.Length -eq 0 -and $windowEnd -le $windowStart) { $reason = 'No interval remains after trimming startup and drain.' }
            if ($reason.Length -eq 0) {
                $eligible = @($taskRows.ToArray() | Where-Object { $_.Start -ge $windowStart -and $_.End -le $windowEnd })
                $eligibleCount = $eligible.Count
                $core = Get-GpuMetric $eligible 'legacy-core-or-3d'
                $kinds = @('core', '3d', 'compute', 'video', 'encode', 'decode', 'processing', 'copy', 'render-compute', 'media')
                $kinds = @($kinds + @($eligible | ForEach-Object { Get-Field $_.Sample 'GpuEngines' } | ForEach-Object { Get-Field $_ 'Kind' } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }) | Sort-Object -Unique -CaseSensitive)
                $engines = @($kinds | ForEach-Object { Get-GpuMetric $eligible $_ })
                $smiRows = @($taskSmiRows.ToArray() | Where-Object { $_.ReceivedUtc -ge $windowStart -and $_.ReceivedUtc -le $windowEnd } | Sort-Object ReceivedUtc)
                $apiTimes = @($smiRows | ForEach-Object { $_.ApiTimestamp } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object)
                $smiSummary = [pscustomobject][ordered]@{
                    ReceiptRows = $smiRows.Count
                    FirstReceivedUtc = $(if ($smiRows.Count) { Get-Iso $smiRows[0].ReceivedUtc } else { $null })
                    LastReceivedUtc = $(if ($smiRows.Count) { Get-Iso $smiRows[-1].ReceivedUtc } else { $null })
                    ApiTimestampMinimum = $(if ($apiTimes.Count) { $apiTimes[0] } else { $null })
                    ApiTimestampMaximum = $(if ($apiTimes.Count) { $apiTimes[-1] } else { $null })
                    DistinctApiTimestampStrings = @($apiTimes | Select-Object -Unique).Count
                    ApiTimestampInterpretation = 'Raw API timestamp strings, lexical range only; no timezone, interval independence or freshness inferred.'
                    Gpu = Get-SmiMetric $smiRows 'Gpu'; Encoder = Get-SmiMetric $smiRows 'Encoder'; Decoder = Get-SmiMetric $smiRows 'Decoder'
                }
                $gpuEnough = $core.Statistics.Count -ge 2 -or @($engines | Where-Object { $_.Statistics.Count -ge 2 }).Count -gt 0
                $smiEnough = $smiSummary.Gpu.Count -ge 2 -or $smiSummary.Encoder.Count -ge 2 -or $smiSummary.Decoder.Count -ge 2
                if ($eligibleCount -ge 2 -and $gpuEnough -and $smiEnough) { $status = 'review-required' }
                else { $reason = 'Fewer than two usable readings on one or both acquisition paths; inspect each metric independently.' }
            }
        }
    }
    $taskPhases.Add([pscustomobject][ordered]@{
        Name = $name; RecordedPhaseStatus = Get-Field $phase 'Status'; Status = $status; Reason = $reason
        StartedUtc = Get-Iso $start; EndedUtc = Get-Iso $end; StartedElapsedSeconds = $startElapsed; EndedElapsedSeconds = $endElapsed
        UtcElapsedDifferenceSeconds = $clockDifference; WindowStartUtc = Get-Iso $windowStart; WindowEndUtc = Get-Iso $windowEnd
        IncludedTelemetryRows = $eligibleCount; Core = $core; Engines = $engines; NvidiaSmi = $smiSummary
    })
}

$taskMaximumGap = $null
$taskOrderedTimes = @($taskValidTimes.ToArray() | Sort-Object)
for ($i = 1; $i -lt $taskOrderedTimes.Count; $i++) {
    $gap = ($taskOrderedTimes[$i] - $taskOrderedTimes[$i - 1]).TotalSeconds
    if ($null -eq $taskMaximumGap -or $gap -gt $taskMaximumGap) { $taskMaximumGap = $gap }
}
$taskAnalysis = [ordered]@{
    Format = 'perfcompass-gpu-workload-summary-1.0'; GeneratedUtc = [DateTimeOffset]::UtcNow.ToString('o', $taskInvariant)
    Script = [ordered]@{ Name = 'summarize-gpu-workload.ps1'; Sha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant() }
    Inputs = @($taskResultInput, $taskTelemetryInput, $taskSmiInput | ForEach-Object { [pscustomobject]@{ Path = $_.Path; Sha256 = $_.Sha256; Bytes = $_.Bytes } })
    RunFacts = [ordered]@{
        Status = Get-Field $taskResult 'Status'; RecordingCompleted = Get-Field $taskResult 'RecordingCompleted'
        StartedUtc = Get-Field $taskResult 'StartedUtc'; FinishedUtc = Get-Field $taskResult 'FinishedUtc'
        ElapsedSeconds = Get-Field $taskResult 'ElapsedSeconds'; HadError = $null -ne (Get-Field $taskResult 'Error')
        CleanupErrorCount = @(Get-Field $taskResult 'CleanupErrors' | Where-Object { $null -ne $_ }).Count
        Version = Get-Field $taskTelemetry 'Version'; ExecutableSha256 = Get-Field $taskTelemetry 'ExecutableSha256'
        ManagedAssemblySha256 = Get-Field $taskTelemetry 'ManagedAssemblySha256'
        ProcessFacts = @($taskProcesses | ForEach-Object { [pscustomobject]@{
            Role = Get-Field $_ 'Role'; ExitCode = Get-Field $_ 'ExitCode'; Forced = Get-Field $_ 'Forced'
            ExpectedMonitorStop = Get-Field $_ 'ExpectedMonitorStop'; StopReason = Get-Field $_ 'StopReason'
            LaunchedUtc = Get-Field $_ 'LaunchedUtc'; ExitedUtc = Get-Field $_ 'ExitedUtc'
        } })
    }
    Sampling = [ordered]@{
        RawRows = $taskRawRows.Count; EligibleIntervalRows = $taskRows.Count; ExcludedIntervalRows = $taskInvalidInterval
        NonpositiveDurationRows = $taskZeroDuration; TimingIssueRows = $taskTimingIssueRows
        IssueKinds = @($taskIssueKinds.Keys | Sort-Object | ForEach-Object { [pscustomobject]@{ Issue = $_; Count = $taskIssueKinds[$_] } })
        MaximumUtcGapBetweenEligibleSampleTimesSeconds = $taskMaximumGap
        SmiReceiptRows = $taskSmiRows.Count; SmiHeaders = $taskSmiHeaders; SmiMalformedRows = $taskSmiMalformed
    }
    PhaseCounts = @($taskPhases.ToArray() | Group-Object Status | ForEach-Object { [pscustomobject]@{ Status = $_.Name; Count = $_.Count } })
    Phases = $taskPhases.ToArray(); Diagnostics = $taskIssues.ToArray()
    Method = [ordered]@{
        PhaseTrimSeconds = 2; MaximumUtcElapsedDisagreementSeconds = .25; MinimumMetricReadingsForSummary = 2
        Inclusion = 'Whole sample envelope [Sample.Time - Sample.Seconds, Sample.Time + CollectorMilliseconds/1000] inside the trimmed window. Nonpositive/invalid/>15-second durations and SamplingIssue/TimingIssue rows excluded. Active does not filter controlled-load observations.'
        Weights = 'Core uses GpuCoverage or Seconds. Engines use ObservedSeconds or Seconds only with valid Load; CoveredSeconds respects Complete and explicit coverage. Empirical P95 is the first sorted value reaching 95% of valid duration. Mixed source sets have separate distributions; no pooled mean/P95.'
        Missing = 'Null, N/A, invalid percentages and absent categories stay missing. Complete only describes exposed category sensors. It is not whole-GPU coverage or driver freshness. Single readings retain only observed min/max; mean/P95/median require two readings.'
        Reference = 'SMI rows use receipt UTC, not an assumed 500 ms sampling interval. API timestamps remain raw strings. Values are not independent physical ground truth, and receipt count does not prove independent fresh driver measurements.'
        Scope = 'Descriptive phase recording only. review-required is data availability, never pass. Phase status, process exit and RecordingCompleted are original facts, not GPU accuracy, purchase suitability, performance equivalence or long-term stability conclusions. Compare the named encode/decode metrics individually and preserve insufficient categories.'
        Privacy = 'Summary omits UUID, PCI identity, process arguments and local absolute paths. Original fixed inputs remain local and unchanged; source sensor names and input hashes are preserved.'
    }
}
$taskOutput = Join-Path $taskRoot 'analysis.json'
Assert-PlainPath $taskOutput
$taskTemporary = Join-Path $taskRoot ('analysis.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    $taskJson = $taskAnalysis | ConvertTo-Json -Depth 30
    $taskBytes = $taskUtf8.GetBytes($taskJson + "`r`n")
    $taskStream = New-Object IO.FileStream($taskTemporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $taskStream.Write($taskBytes, 0, $taskBytes.Length); $taskStream.Flush($true) }
    finally { $taskStream.Dispose() }
    if ([IO.File]::Exists($taskOutput)) { [IO.File]::Replace($taskTemporary, $taskOutput, $null) }
    else { [IO.File]::Move($taskTemporary, $taskOutput) }
}
finally { if ([IO.File]::Exists($taskTemporary)) { [IO.File]::Delete($taskTemporary) } }
Write-Output 'Wrote analysis.json. Descriptive summaries require review; no validation pass is inferred.'
