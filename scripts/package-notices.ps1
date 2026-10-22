param([string]$DotnetRoot)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $DotnetRoot) {
    $DotnetRoot = Join-Path $taskRoot '.tools\dotnet'
    if (-not (Test-Path -LiteralPath (Join-Path $DotnetRoot 'dotnet.exe'))) {
        $DotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
    }
}
$taskAssets = Get-Content -LiteralPath (Join-Path $taskRoot 'src\PerfCompass\obj\project.assets.json') -Encoding UTF8 -Raw | ConvertFrom-Json
$taskLicenseRoot = Join-Path $taskRoot 'licenses'
New-Item -ItemType Directory -Force -Path $taskLicenseRoot | Out-Null
$taskRows = foreach ($taskLibrary in $taskAssets.libraries.PSObject.Properties) {
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
        [PSCustomObject]@{Package=$taskLibrary.Name;Authors=[string]$taskMeta.authors;License=[string]$taskMeta.license.InnerText;LicenseType=[string]$taskMeta.license.type;Project=[string]$taskMeta.projectUrl;Repository=[string]$taskMeta.repository.url;Commit=[string]$taskMeta.repository.commit}
    }
}
$taskRows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskLicenseRoot 'dependency-inventory.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $DotnetRoot 'LICENSE.txt') -Destination (Join-Path $taskLicenseRoot 'dotnet-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $DotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $taskLicenseRoot 'dotnet-ThirdPartyNotices.txt')
