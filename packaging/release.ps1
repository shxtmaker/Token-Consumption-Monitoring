[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'TokenConsumptionMonitoring.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$projectVersion = [string]$project.Project.PropertyGroup.Version
if (-not $Version) { $Version = $projectVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $Version -ne $projectVersion) {
    throw "Release version '$Version' must match the numeric project version '$projectVersion'."
}

if (-not $IsccPath) {
    $compilerCommand = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $compilerCandidates = @(
        $(if ($compilerCommand) { $compilerCommand.Source }),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $IsccPath = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
        Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup 6 is required. Install it or pass -IsccPath with the ISCC.exe path.'
}
$compilerBanner = (& $IsccPath '/?' 2>&1 | Out-String)
if ($compilerBanner -notmatch 'Inno Setup 6 Command-Line Compiler') {
    throw 'This release script requires Inno Setup 6.'
}

$scriptPath = Join-Path $PSScriptRoot 'TokenConsumptionMonitoring-Setup.iss'
$installerScript = Get-Content -LiteralPath $scriptPath -Raw
$appIdentity = Get-Content -LiteralPath (Join-Path $repositoryRoot 'AppIdentity.cs') -Raw
if (-not $installerScript.Contains('#define ProductAppId "{{C5D7E9A1-4B62-4F38-9A07-8E1C3D6B2A54}"') -or
    -not $installerScript.Contains('#define ProductMutex "TokenConsumptionMonitoring_SingleInstance"') -or
    -not $appIdentity.Contains('public const string MutexName = ProductName + "_SingleInstance";') -or
    -not $appIdentity.Contains('public const string ProductName = "TokenConsumptionMonitoring";')) {
    throw 'Production installer identity does not match the application identity.'
}

$workRoot = Join-Path $repositoryRoot ('artifacts\packaging\{0}-{1}' -f $Version, [Guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $workRoot 'publish'
$packageDirectory = Join-Path $workRoot 'packages'
$distributionDirectory = Join-Path $repositoryRoot 'dist'
New-Item -ItemType Directory -Path $publishDirectory, $packageDirectory, $distributionDirectory -Force | Out-Null

& dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
$applicationPath = Join-Path $publishDirectory 'TokenConsumptionMonitoring.exe'
$binaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($applicationPath)
if ($binaryVersion.FileVersion -ne "$Version.0") {
    throw "Published executable version '$($binaryVersion.FileVersion)' does not match '$Version.0'."
}
$runtimeReadme = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'README-runtime.md') -Raw).
    Replace('@@VERSION@@', $Version)
Set-Content -LiteralPath (Join-Path $publishDirectory 'README.md') -Value $runtimeReadme -Encoding utf8

foreach ($packageMode in @('Setup', 'Upgrade')) {
    & $IsccPath "/DMyAppVersion=$Version" "/DPublishDir=$publishDirectory" `
        "/DPackageOutputDir=$packageDirectory" "/DPackageMode=$packageMode" $scriptPath
    if ($LASTEXITCODE -ne 0) { throw "$packageMode compiler failed with exit code $LASTEXITCODE." }
}

$portableFileName = "TokenConsumptionMonitoring-Portable-$Version.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($publishDirectory, (Join-Path $packageDirectory $portableFileName),
    [IO.Compression.CompressionLevel]::Optimal, $false)

$fileNames = @("TokenConsumptionMonitoring-Setup-$Version.exe", $portableFileName,
    "TokenConsumptionMonitoring-Upgrade-$Version.exe")
$manifest = foreach ($fileName in $fileNames) {
    $source = Join-Path $packageDirectory $fileName
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing package: $fileName" }
    $destination = [IO.Path]::GetFullPath((Join-Path $distributionDirectory $fileName))
    if (-not $destination.StartsWith($distributionDirectory + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid distribution path: $destination" }
    Copy-Item -LiteralPath $source -Destination $destination -Force
    [pscustomobject]@{
        File = $destination
        Bytes = (Get-Item -LiteralPath $destination).Length
        SHA256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    }
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workRoot 'release-manifest.json') -Encoding utf8
$manifest | Format-Table -AutoSize
Write-Host "Release packages: $distributionDirectory"
Write-Host "Build manifest: $(Join-Path $workRoot 'release-manifest.json')"
