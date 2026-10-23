param([string]$DotnetRoot)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskHardware = & (Join-Path $PSScriptRoot 'verify-hardware-source.ps1') -PassThru
if (-not $DotnetRoot) {
    $DotnetRoot = Join-Path $taskRoot '.tools\dotnet'
    if (-not (Test-Path -LiteralPath (Join-Path $DotnetRoot 'dotnet.exe'))) {
        $DotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
    }
}
$taskAssets = Get-Content -LiteralPath (Join-Path $taskRoot 'src\PerfCompass\obj\project.assets.json') -Encoding UTF8 -Raw | ConvertFrom-Json
$taskLicenseRoot = Join-Path $taskRoot 'licenses'
New-Item -ItemType Directory -Force -Path $taskLicenseRoot | Out-Null
$taskRows = @(foreach ($taskLibrary in $taskAssets.libraries.PSObject.Properties) {
    if ($taskLibrary.Value.type -eq 'project') { continue }
    if ($taskLibrary.Value.type -ne 'package') { throw "Unsupported dependency asset type: $($taskLibrary.Name)" }
    $taskPackageDir = Join-Path (Join-Path $taskRoot '.tools\nuget') $taskLibrary.Value.path
    $taskSpec = Get-ChildItem -LiteralPath $taskPackageDir -Filter '*.nuspec' | Select-Object -First 1
    if ($taskSpec) {
        [xml]$taskXml = Get-Content -LiteralPath $taskSpec.FullName -Encoding UTF8 -Raw
        $taskMeta = $taskXml.package.metadata
        $taskTarget = Join-Path $taskLicenseRoot ($taskLibrary.Name.Replace('/','-'))
        New-Item -ItemType Directory -Force -Path $taskTarget | Out-Null
        Copy-Item -LiteralPath $taskSpec.FullName -Destination $taskTarget
        Get-ChildItem -LiteralPath $taskPackageDir -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|NOTICE|COPYING|ThirdPartyNotices)' } | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $taskTarget $_.Name) -Force
        }
        [PSCustomObject]@{Package=$taskLibrary.Name;Type='package';Authors=[string]$taskMeta.authors;License=[string]$taskMeta.license.InnerText;LicenseType=[string]$taskMeta.license.type;Project=[string]$taskMeta.projectUrl;Repository=[string]$taskMeta.repository.url;Commit=[string]$taskMeta.repository.commit}
    }
})

# Distribute the exact modified MPL source, including resources and the dependency
# lock. A project asset is never looked up in the NuGet package cache.
$taskHardwareTarget = Join-Path $taskLicenseRoot 'LibreHardwareMonitor-perfcompass'
New-Item -ItemType Directory -Force -Path $taskHardwareTarget | Out-Null
$taskSourceZipName = 'LibreHardwareMonitor-' + $taskHardware.PatchedVersion + '-source.zip'
$taskSourceZip = Join-Path $taskHardwareTarget $taskSourceZipName
$taskTemporary = $taskSourceZip + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
$taskGitCommitLines = @(& git -C $taskRoot rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or $taskGitCommitLines.Count -ne 1) { throw 'Cannot determine the PerfCompass checkout commit.' }
$taskGitCommit = ([string]$taskGitCommitLines[0]).Trim()
if ($taskGitCommit -notmatch '^[0-9a-fA-F]{40,64}$') { throw 'Invalid PerfCompass checkout commit.' }
$taskGitStatus = @(& git -C $taskRoot status --porcelain --untracked-files=all -- vendor/LibreHardwareMonitor 2>$null)
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine modified source checkout status.' }
$taskSourceInventory = [ordered]@{
    Format='perfcompass-hardware-source-1.0'; Repository=$taskHardware.Repository; UpstreamCommit=$taskHardware.UpstreamCommit;
    UpstreamVersion=$taskHardware.UpstreamVersion; PatchedVersion=$taskHardware.PatchedVersion;
    PerfCompassRepository='https://github.com/Southwall-Tester/PerfCompass'; PerfCompassCheckoutCommit=$taskGitCommit;
    SourceTreeDirty=($taskGitStatus.Count -gt 0); SourceSnapshotSha256=$taskHardware.SourceSnapshotSha256;
    Files=$taskHardware.Files
}
$taskUtf8 = [Text.UTF8Encoding]::new($false)
$taskSourceNuGet = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3"/></packageSources>
  <fallbackPackageFolders><clear/></fallbackPackageFolders>
  <config><add key="globalPackagesFolder" value=".packages"/></config>
</configuration>
'@
$taskNuGetBytes = $taskUtf8.GetBytes($taskSourceNuGet)
$taskHasher = [Security.Cryptography.SHA256]::Create()
try { $taskNuGetHash = [BitConverter]::ToString($taskHasher.ComputeHash($taskNuGetBytes)).Replace('-','').ToLowerInvariant() } finally { $taskHasher.Dispose() }
$taskSourceInventory.GeneratedFiles = @([pscustomobject]@{Path='NuGet.Config';Bytes=$taskNuGetBytes.Length;Sha256=$taskNuGetHash})
$taskInventoryBytes = $taskUtf8.GetBytes(($taskSourceInventory | ConvertTo-Json -Depth 8))
$taskExtras = @([pscustomobject]@{Name='source-inventory.json';Bytes=$taskInventoryBytes}, [pscustomobject]@{Name='NuGet.Config';Bytes=$taskNuGetBytes})
Add-Type -AssemblyName System.IO.Compression
$taskZipStream = [IO.File]::Open($taskTemporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $taskArchive = [IO.Compression.ZipArchive]::new($taskZipStream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($taskFile in $taskHardware.Files) {
            $taskPath = Join-Path $taskHardware.VendorDirectory $taskFile.Path
            $taskInput = [IO.File]::Open($taskPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                $taskHasher = [Security.Cryptography.SHA256]::Create()
                try { $taskHash = [BitConverter]::ToString($taskHasher.ComputeHash($taskInput)).Replace('-','').ToLowerInvariant() } finally { $taskHasher.Dispose() }
                if ($taskHash -ne $taskFile.Sha256 -or $taskInput.Length -ne $taskFile.Bytes) { throw "Source changed during packaging: $($taskFile.Path)" }
                $taskInput.Position = 0
                $taskEntry = $taskArchive.CreateEntry($taskFile.Path, [IO.Compression.CompressionLevel]::Optimal)
                $taskEntry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
                $taskOutput = $taskEntry.Open()
                try { $taskInput.CopyTo($taskOutput) } finally { $taskOutput.Dispose() }
            } finally { $taskInput.Dispose() }
        }
        foreach ($taskExtra in $taskExtras) {
            $taskEntry = $taskArchive.CreateEntry($taskExtra.Name, [IO.Compression.CompressionLevel]::Optimal)
            $taskEntry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            $taskOutput = $taskEntry.Open()
            try { $taskOutput.Write($taskExtra.Bytes, 0, $taskExtra.Bytes.Length) } finally { $taskOutput.Dispose() }
        }
    } finally { $taskArchive.Dispose() }
} finally { $taskZipStream.Dispose() }

# Check both the final working snapshot and the archived bytes before replacing a
# previously delivered source archive. A partial/changed package is not promoted.
$taskHardwareAfter = & (Join-Path $PSScriptRoot 'verify-hardware-source.ps1') -PassThru
if ($taskHardwareAfter.SourceSnapshotSha256 -ne $taskHardware.SourceSnapshotSha256) { throw 'Hardware source changed while its archive was being created.' }
$taskReadStream = [IO.File]::OpenRead($taskTemporary)
try {
    $taskReadArchive = [IO.Compression.ZipArchive]::new($taskReadStream, [IO.Compression.ZipArchiveMode]::Read)
    try {
        if ($taskReadArchive.Entries.Count -ne $taskHardware.FileCount + 2) { throw 'Unexpected source archive entry count.' }
        foreach ($taskFile in $taskHardware.Files) {
            $taskEntry = $taskReadArchive.GetEntry($taskFile.Path)
            if (-not $taskEntry -or $taskEntry.Length -ne $taskFile.Bytes) { throw "Source archive entry missing or truncated: $($taskFile.Path)" }
            $taskInput = $taskEntry.Open()
            try {
                $taskHasher = [Security.Cryptography.SHA256]::Create()
                try { $taskHash = [BitConverter]::ToString($taskHasher.ComputeHash($taskInput)).Replace('-','').ToLowerInvariant() } finally { $taskHasher.Dispose() }
                if ($taskHash -ne $taskFile.Sha256) { throw "Source archive hash mismatch: $($taskFile.Path)" }
            } finally { $taskInput.Dispose() }
        }
        foreach ($taskExtra in $taskExtras) {
            $taskEntry = $taskReadArchive.GetEntry($taskExtra.Name)
            if (-not $taskEntry -or $taskEntry.Length -ne $taskExtra.Bytes.Length) { throw "Generated source metadata missing: $($taskExtra.Name)" }
            $taskInput = $taskEntry.Open()
            try {
                $taskHasher = [Security.Cryptography.SHA256]::Create()
                try {
                    $taskArchivedHash = [BitConverter]::ToString($taskHasher.ComputeHash($taskInput))
                    $taskExpectedHash = [BitConverter]::ToString($taskHasher.ComputeHash($taskExtra.Bytes))
                    if ($taskArchivedHash -ne $taskExpectedHash) { throw "Generated source metadata hash mismatch: $($taskExtra.Name)" }
                } finally { $taskHasher.Dispose() }
            } finally { $taskInput.Dispose() }
        }
    } finally { $taskReadArchive.Dispose() }
} finally { $taskReadStream.Dispose() }
Move-Item -LiteralPath $taskTemporary -Destination $taskSourceZip -Force
$taskSourceZipHash = (Get-FileHash -LiteralPath $taskSourceZip -Algorithm SHA256).Hash.ToLowerInvariant()
$taskPackageInfo = [pscustomobject]@{
    Package=('LibreHardwareMonitorLib/' + $taskHardware.PatchedVersion); Type='project'; Authors='LibreHardwareMonitor contributors; PerfCompass contributors';
    License='MPL-2.0'; LicenseType='expression'; Project=$taskHardware.Repository; Repository=$taskHardware.Repository; Commit=$taskHardware.UpstreamCommit;
    UpstreamVersion=$taskHardware.UpstreamVersion; PatchedVersion=$taskHardware.PatchedVersion;
    PerfCompassRepository=$taskSourceInventory.PerfCompassRepository; PerfCompassCheckoutCommit=$taskGitCommit;
    SourceTreeDirty=$taskSourceInventory.SourceTreeDirty; SourceSnapshotSha256=$taskHardware.SourceSnapshotSha256;
    SourceZip=('LibreHardwareMonitor-perfcompass/' + $taskSourceZipName); SourceZipSha256=$taskSourceZipHash;
    SourceFiles=$taskHardware.FileCount
}
[IO.File]::WriteAllText((Join-Path $taskHardwareTarget 'source-package.json'), ($taskPackageInfo | ConvertTo-Json -Depth 5), $taskUtf8)
[IO.File]::WriteAllText((Join-Path $taskHardwareTarget 'source-inventory.json'), ($taskSourceInventory | ConvertTo-Json -Depth 8), $taskUtf8)
[IO.File]::WriteAllText((Join-Path $taskHardwareTarget 'SHA256SUMS.txt'), ($taskSourceZipHash + '  ' + $taskSourceZipName + [Environment]::NewLine), $taskUtf8)
foreach ($taskNotice in @('LICENSE','THIRD-PARTY-NOTICES.txt','UPSTREAM.json','PATCHES.md','PATCH-MANIFEST.json')) {
    Copy-Item -LiteralPath (Join-Path $taskHardware.VendorDirectory $taskNotice) -Destination (Join-Path $taskHardwareTarget $taskNotice) -Force
}
$taskRows += $taskPackageInfo
[IO.File]::WriteAllText((Join-Path $taskLicenseRoot 'dependency-inventory.json'), ($taskRows | ConvertTo-Json -Depth 5), $taskUtf8)
Copy-Item -LiteralPath (Join-Path $DotnetRoot 'LICENSE.txt') -Destination (Join-Path $taskLicenseRoot 'dotnet-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $DotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $taskLicenseRoot 'dotnet-ThirdPartyNotices.txt')
[pscustomobject]@{DependencyCount=$taskRows.Count; HardwareSourceArchive=$taskSourceZip; SourceFiles=$taskHardware.FileCount; SourceZipSha256=$taskSourceZipHash; SourceTreeDirty=$taskSourceInventory.SourceTreeDirty}
