param([switch]$Publish, [switch]$Test, [string]$OutputDirectory = 'release')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = 'dotnet' }
else { $env:DOTNET_ROOT = Split-Path -Parent $taskDotnet }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$taskProject = Join-Path $taskRoot 'src\PerfCompass\PerfCompass.csproj'
$taskPublishRoot = [System.IO.Path]::GetFullPath($(if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $taskRoot $OutputDirectory }))
Push-Location $taskRoot
try {
    & $taskDotnet build $taskProject -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if ($Test) {
        $taskTest = Start-Process -FilePath (Join-Path $taskRoot 'src\PerfCompass\bin\Release\net10.0-windows\PerfCompass.exe') -ArgumentList '--self-test' -WindowStyle Hidden -PassThru -Wait
        Get-Content -LiteralPath (Join-Path $taskRoot 'artifacts\self-tests.txt')
        if ($taskTest.ExitCode -ne 0) { throw 'Tests failed' }
    }
    if ($Publish) {
        $taskRunning = Get-Process PerfCompass -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $taskPublishRoot 'PerfCompass.exe') }
        if ($taskRunning) { throw "目标发布包正在运行，请用 -OutputDirectory 指定独立目录：$taskPublishRoot" }
        $taskReadyPath = Join-Path $taskPublishRoot 'READY'
        if (Test-Path -LiteralPath $taskReadyPath) { [System.IO.File]::Delete($taskReadyPath) }
        & $taskDotnet publish $taskProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -o $taskPublishRoot
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
        Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\README.html') -Destination (Join-Path $taskPublishRoot 'README.html')
        & (Join-Path $taskRoot 'scripts\package-notices.ps1')
        $taskLicenseRoot = Join-Path $taskRoot 'licenses'
        foreach ($taskLicenseFile in Get-ChildItem -LiteralPath $taskLicenseRoot -File -Recurse) {
            $taskRelative = $taskLicenseFile.FullName.Substring($taskLicenseRoot.Length).TrimStart('\')
            $taskDestination = Join-Path (Join-Path $taskPublishRoot 'licenses') $taskRelative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $taskDestination) | Out-Null
            Copy-Item -LiteralPath $taskLicenseFile.FullName -Destination $taskDestination -Force
        }
        Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $taskPublishRoot 'THIRD-PARTY-NOTICES.md')
        Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination (Join-Path $taskPublishRoot 'README.md')
        Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\VALIDATION.md') -Destination (Join-Path $taskPublishRoot 'VALIDATION.md')
        Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\DEMAND_MODEL.md') -Destination (Join-Path $taskPublishRoot 'DEMAND_MODEL.md')
    }
} finally { Pop-Location }
