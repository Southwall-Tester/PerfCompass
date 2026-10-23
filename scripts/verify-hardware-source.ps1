param([string]$VendorDirectory, [switch]$PassThru)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $VendorDirectory) { $VendorDirectory = Join-Path $taskRoot 'vendor\LibreHardwareMonitor' }
$taskVendor = [IO.Path]::GetFullPath($VendorDirectory).TrimEnd('\','/')
$taskUpstreamPath = Join-Path $taskVendor 'UPSTREAM.json'
$taskPatchPath = Join-Path $taskVendor 'PATCH-MANIFEST.json'
$taskUpstream = Get-Content -LiteralPath $taskUpstreamPath -Encoding UTF8 -Raw | ConvertFrom-Json
$taskPatch = Get-Content -LiteralPath $taskPatchPath -Encoding UTF8 -Raw | ConvertFrom-Json
if ($taskPatch.Format -ne 'perfcompass-hardware-patches-1.0' -or $taskPatch.UpstreamCommit -ne $taskUpstream.Commit) { throw 'Unsupported patch manifest or mismatched upstream commit.' }
if ((Get-FileHash -LiteralPath $taskUpstreamPath -Algorithm SHA256).Hash -ne $taskPatch.UpstreamManifestSha256) { throw 'UPSTREAM.json differs from the original pinned manifest.' }
if ($taskPatch.PatchedVersion -notmatch '^[0-9A-Za-z._-]+$') { throw 'Invalid patched library version.' }

function Assert-HardwareRelativePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains('\') -or [IO.Path]::IsPathRooted($Path) -or
        $Path -match '(^|/)(\.|\.\.|bin|obj)(/|$)' -or $Path.Contains(':')) { throw "Invalid source-manifest path: $Path" }
    $taskResolved = [IO.Path]::GetFullPath((Join-Path $taskVendor $Path))
    if (-not $taskResolved.StartsWith($taskVendor + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Source-manifest path leaves vendor directory: $Path" }
}
$taskOriginal = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
$taskModified = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
$taskAdded = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
foreach ($taskItem in $taskUpstream.Files) {
    Assert-HardwareRelativePath $taskItem.Path
    if ($taskOriginal.ContainsKey($taskItem.Path) -or $taskItem.Sha256 -notmatch '^[0-9a-fA-F]{64}$' -or $taskItem.Bytes -lt 0) { throw "Invalid upstream entry: $($taskItem.Path)" }
    $taskOriginal.Add($taskItem.Path, $taskItem)
}
foreach ($taskItem in $taskPatch.ModifiedFiles) {
    Assert-HardwareRelativePath $taskItem.Path
    if (-not $taskOriginal.ContainsKey($taskItem.Path) -or $taskModified.ContainsKey($taskItem.Path) -or [string]::IsNullOrWhiteSpace($taskItem.Reason)) { throw "Invalid allowed modification: $($taskItem.Path)" }
    $taskModified.Add($taskItem.Path, $taskItem)
}
foreach ($taskItem in $taskPatch.AddedFiles) {
    Assert-HardwareRelativePath $taskItem.Path
    if ($taskOriginal.ContainsKey($taskItem.Path) -or $taskAdded.ContainsKey($taskItem.Path) -or [string]::IsNullOrWhiteSpace($taskItem.Reason)) { throw "Invalid allowed addition: $($taskItem.Path)" }
    $taskAdded.Add($taskItem.Path, $taskItem)
}
$taskMetadata = @('UPSTREAM.json', 'PATCH-MANIFEST.json', 'PATCHES.md')
$taskSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$taskFiles = [Collections.Generic.List[object]]::new()
$taskDirectories = [Collections.Generic.Stack[string]]::new()
$taskDirectories.Push($taskVendor)
while ($taskDirectories.Count -gt 0) {
    $taskDirectory = $taskDirectories.Pop()
    foreach ($taskFile in @(Get-ChildItem -LiteralPath $taskDirectory -Force)) {
        if (($taskFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked source entry is not allowed: $($taskFile.FullName)" }
        if ($taskFile.PSIsContainer) {
            if ($taskFile.Name -notin @('bin','obj')) { $taskDirectories.Push($taskFile.FullName) }
            continue
        }
        $taskRelative = $taskFile.FullName.Substring($taskVendor.Length + 1).Replace('\','/')
        if (-not $taskSeen.Add($taskRelative)) { throw "Duplicate source entry: $taskRelative" }
        $taskHash = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $taskOrigin = 'metadata'
        if ($taskOriginal.ContainsKey($taskRelative)) {
            $taskBaseline = $taskOriginal[$taskRelative]
            $taskChanged = $taskHash -ne $taskBaseline.Sha256 -or $taskFile.Length -ne $taskBaseline.Bytes
            if ($taskChanged -and -not $taskModified.ContainsKey($taskRelative)) { throw "Unlisted upstream source modification: $taskRelative" }
            $taskOrigin = $(if ($taskChanged) { 'modified' } else { 'upstream' })
        } elseif ($taskAdded.ContainsKey($taskRelative)) { $taskOrigin = 'added' }
        elseif ($taskRelative -notin $taskMetadata) { throw "Unlisted vendor source addition: $taskRelative" }
        $taskFiles.Add([pscustomobject]@{ Path=$taskRelative; Bytes=$taskFile.Length; Sha256=$taskHash; Origin=$taskOrigin })
    }
}
foreach ($taskRequired in @($taskOriginal.Keys) + @($taskAdded.Keys) + $taskMetadata) {
    if (-not $taskSeen.Contains($taskRequired)) { throw "Required source file is missing: $taskRequired" }
}
[xml]$taskVersionProps = Get-Content -LiteralPath (Join-Path $taskVendor 'Directory.Build.props') -Encoding UTF8 -Raw
if ([string]$taskVersionProps.Project.PropertyGroup.Version -ne $taskPatch.PatchedVersion) { throw 'Project version differs from PATCH-MANIFEST.json.' }
$taskSorted = @($taskFiles | Sort-Object Path)
$taskCanonical = ($taskSorted | ForEach-Object { $_.Path + "`t" + $_.Bytes + "`t" + $_.Sha256 }) -join "`n"
$taskHasher = [Security.Cryptography.SHA256]::Create()
try { $taskSnapshot = [BitConverter]::ToString($taskHasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($taskCanonical))).Replace('-','').ToLowerInvariant() } finally { $taskHasher.Dispose() }
$taskResult = [pscustomobject]@{ VendorDirectory=$taskVendor; Repository=$taskUpstream.Repository; UpstreamCommit=$taskUpstream.Commit; UpstreamVersion=$taskUpstream.Version;
    PatchedVersion=$taskPatch.PatchedVersion; SourceSnapshotSha256=$taskSnapshot; FileCount=$taskSorted.Count;
    UnchangedFiles=@($taskSorted | Where-Object Origin -eq 'upstream').Count; ModifiedFiles=@($taskSorted | Where-Object Origin -eq 'modified').Count;
    AddedFiles=@($taskSorted | Where-Object Origin -eq 'added').Count; Files=$taskSorted }
if ($PassThru) { $taskResult }
else { $taskResult | Select-Object VendorDirectory,UpstreamCommit,PatchedVersion,FileCount,UnchangedFiles,ModifiedFiles,AddedFiles,SourceSnapshotSha256 }
