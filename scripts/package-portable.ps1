param([string]$SourceDirectory = 'release-next', [string]$OutputPath = 'artifacts/PerfCompass-current-Windows-x64.zip')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskSource = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($SourceDirectory)) { $SourceDirectory } else { Join-Path $taskRoot $SourceDirectory }))
$taskOutput = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $taskRoot $OutputPath }))
if (-not $taskOutput.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $taskOutput -PathType Container)) { throw 'Output must be a ZIP file path.' }
if ($taskOutput.StartsWith($taskSource.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive output must be outside the source directory.' }
$taskExe = Join-Path $taskSource 'PerfCompass.exe'
if (-not (Test-Path -LiteralPath $taskExe -PathType Leaf)) { throw 'Published executable is missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $taskSource 'READY') -PathType Leaf)) { throw 'Distribution has not been marked READY after validation.' }
$taskFiles = @(Get-ChildItem -LiteralPath $taskSource -File -Recurse)
foreach ($taskFile in $taskFiles) {
    $taskRelative = $taskFile.FullName.Substring($taskSource.Length + 1).Replace('\','/')
    if ($taskRelative -match '(^|/)data/|\.(jsonl|gguf)$|(^|/)(errors\.log|settings\.json|comparison\.json)$') { throw "Private data or a model file cannot be packaged: $taskRelative" }
    if ($taskRelative -notmatch '^(docs/([^/]+\.(md|html)|experiments/[^/]+\.(json|txt)|images/[^/]+\.(png|jpg|svg))|licenses/.+|PerfCompass\.exe|(PerfCompass|LibreHardwareMonitorLib)\.pdb|READY|README\.(md|html)|THIRD-PARTY-NOTICES\.md|VALIDATION\.md|DEMAND_MODEL\.md)$') { throw "Unexpected distribution file: $taskRelative" }
    if (($taskFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked files cannot be packaged.' }
}
# Reparse directories could point outside the chosen distribution even if the leaves are ordinary files.
if (Get-ChildItem -LiteralPath $taskSource -Directory -Recurse | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }) { throw 'Linked directories cannot be packaged.' }
[IO.Directory]::CreateDirectory((Split-Path -Parent $taskOutput)) | Out-Null
$taskTemporary = $taskOutput + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
$taskZipStream = [IO.File]::Open($taskTemporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $taskArchive = [IO.Compression.ZipArchive]::new($taskZipStream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($taskFile in $taskFiles) {
            $taskRelative = $taskFile.FullName.Substring($taskSource.Length + 1).Replace('\','/')
            $taskEntry = $taskArchive.CreateEntry($taskRelative, [IO.Compression.CompressionLevel]::Optimal)
            $taskInput = [IO.File]::Open($taskFile.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            try { $taskEntryOutput = $taskEntry.Open(); try { $taskInput.CopyTo($taskEntryOutput) } finally { $taskEntryOutput.Dispose() } } finally { $taskInput.Dispose() }
        }
    } finally { $taskArchive.Dispose() }
} finally { $taskZipStream.Dispose() }
$taskSourceHash = (Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash
$taskReadStream = [IO.File]::OpenRead($taskTemporary)
try {
    $taskReadArchive = [IO.Compression.ZipArchive]::new($taskReadStream, [IO.Compression.ZipArchiveMode]::Read)
    try {
        $taskExeEntry = $taskReadArchive.GetEntry('PerfCompass.exe')
        if (-not $taskExeEntry) { throw 'Executable missing from archive.' }
        $taskEntryStream = $taskExeEntry.Open()
        try { $taskHasher = [Security.Cryptography.SHA256]::Create(); try { $taskEntryHash = [BitConverter]::ToString($taskHasher.ComputeHash($taskEntryStream)).Replace('-','') } finally { $taskHasher.Dispose() } } finally { $taskEntryStream.Dispose() }
        if ($taskEntryHash -ne $taskSourceHash) { throw 'Archived executable differs from the published file.' }
        $taskEntryCount = $taskReadArchive.Entries.Count
    } finally { $taskReadArchive.Dispose() }
} finally { $taskReadStream.Dispose() }
Move-Item -LiteralPath $taskTemporary -Destination $taskOutput -Force
$taskZipHash = (Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256).Hash
$taskChecksum = Join-Path (Split-Path -Parent $taskOutput) 'SHA256SUMS.txt'
[IO.File]::WriteAllText($taskChecksum, ($taskZipHash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($taskOutput) + [Environment]::NewLine + $taskSourceHash.ToLowerInvariant() + '  ' + $taskSource.Replace('\','/') + '/PerfCompass.exe' + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ Archive = $taskOutput; Bytes = (Get-Item -LiteralPath $taskOutput).Length; Entries = $taskEntryCount; ExecutableHashMatches = $true; Version = (Get-Item -LiteralPath $taskExe).VersionInfo.ProductVersion }
