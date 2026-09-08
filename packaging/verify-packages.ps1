[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory,
    [string]$IsccPath = (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$payload = Join-Path $PublishDirectory 'TokenConsumptionMonitoring.exe'
$version = ([Diagnostics.FileVersionInfo]::GetVersionInfo($payload).FileVersion -replace '\.0$', '')
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'The payload must have a numeric release version.' }
$verificationId = [Guid]::NewGuid().ToString().ToUpperInvariant()
$testRoot = Join-Path $repositoryRoot "artifacts\packaging-verification\$verificationId"
$installDirectory = Join-Path $testRoot 'installed'
$packageDirectory = Join-Path $testRoot 'packages'
$logDirectory = Join-Path $testRoot 'logs'
New-Item -ItemType Directory -Path $packageDirectory, $logDirectory -Force | Out-Null
$testRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$verificationId}_is1"
$productionRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C5D7E9A1-4B62-4F38-9A07-8E1C3D6B2A54}_is1'
$results = [Collections.Generic.List[object]]::new()

function Get-ProductionRegistrationSnapshot {
    if (-not (Test-Path -LiteralPath $productionRegistryPath)) { return '' }
    $key = Get-Item -LiteralPath $productionRegistryPath
    try {
        $values = [ordered]@{}
        foreach ($name in ($key.GetValueNames() | Sort-Object)) { $values[$name] = $key.GetValue($name) }
        return ($values | ConvertTo-Json -Depth 5 -Compress)
    } finally { $key.Dispose() }
}
$productionRegistryBefore = Get-ProductionRegistrationSnapshot

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-TestUninstaller {
    $registration = Get-ItemProperty -LiteralPath $testRegistryPath
    $registeredDirectory = [IO.Path]::GetFullPath($registration.InstallLocation.TrimEnd('\'))
    $registeredUninstaller = [IO.Path]::GetFullPath($registration.UninstallString.Trim('"'))
    Assert-Condition ($registeredDirectory -eq $installDirectory -and
        $registeredUninstaller.StartsWith($installDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($registeredUninstaller) -match '^unins\d+\.exe$') `
        'Refusing to invoke an uninstaller outside the isolated verification installation.'
    return $registeredUninstaller
}

function Invoke-Installer([string]$Path, [string]$Case, [string[]]$AdditionalArguments = @(), [bool]$ExpectSuccess = $true) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $logDirectory "$Case.log")`"")
    $arguments += $AdditionalArguments
    $process = Start-Process -FilePath $Path -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(90000)) {
        Stop-Process -Id $process.Id -Force
        throw "Verification installer timed out: $Case"
    }
    $process.Refresh()
    $exitCode = $process.ExitCode
    Assert-Condition (($ExpectSuccess -and $exitCode -eq 0) -or (-not $ExpectSuccess -and $exitCode -ne 0)) `
        "$Case returned unexpected exit code $exitCode. See the verification log."
    $results.Add([pscustomobject]@{ Case = $Case; Passed = $true; ExitCode = $exitCode })
    Write-Host "$Case : passed (exit $exitCode)"
}

function Compile-TestPackage([string]$Mode, [string]$PackageVersion, [string]$SourceDirectory) {
    & $IsccPath /Q "/DMyAppVersion=$PackageVersion" "/DPublishDir=$SourceDirectory" `
        "/DPackageOutputDir=$packageDirectory" "/DPackageMode=$Mode" /DVerificationBuild=1 `
        "/DVerificationAppId=$verificationId" (Join-Path $PSScriptRoot 'TokenConsumptionMonitoring-Setup.iss')
    if ($LASTEXITCODE -ne 0) { throw "Verification package compilation failed: $Mode $PackageVersion" }
    return Join-Path $packageDirectory "TokenConsumptionMonitoring-$Mode-$PackageVersion.exe"
}

# Tiny versioned PE files reproduce the old installer registration and overwrite
# behavior without running old application code or accessing user accounts.
function Compile-LegacyFixture([string]$LegacyVersion) {
    $legacyRoot = Join-Path $testRoot "baseline-$LegacyVersion"
    New-Item -ItemType Directory -Path $legacyRoot -Force | Out-Null
    # The non-.cs suffix keeps WPF's default compile glob from finding fixtures.
    $fixtureSource = Join-Path $legacyRoot 'Fixture.cs.txt'
    $fixturePayload = Join-Path $legacyRoot 'TokenConsumptionMonitoring.exe'
    @"
using System.Reflection;
[assembly: AssemblyFileVersion("$LegacyVersion.0")]
[assembly: AssemblyVersion("$LegacyVersion.0")]
internal static class Fixture { private static void Main() { } }
"@ | Set-Content -LiteralPath $fixtureSource -Encoding utf8
    & (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:winexe /platform:x64 "/out:$fixturePayload" $fixtureSource
    if ($LASTEXITCODE -ne 0) { throw "Baseline fixture compilation failed: $LegacyVersion" }
    $legacyScript = Join-Path $legacyRoot 'Legacy.iss'
    @"
[Setup]
AppId={{$verificationId}
AppName=TokenConsumptionMonitoring Packaging Verification
AppVersion=$LegacyVersion
DefaultDirName=$installDirectory
OutputDir=$packageDirectory
OutputBaseFilename=Legacy-$LegacyVersion
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=no
[Files]
Source: "$fixturePayload"; DestDir: "{app}"; Flags: ignoreversion
"@ | Set-Content -LiteralPath $legacyScript -Encoding utf8
    & $IsccPath /Q $legacyScript
    if ($LASTEXITCODE -ne 0) { throw "Legacy installer compilation failed: $LegacyVersion" }
    return Join-Path $packageDirectory "Legacy-$LegacyVersion.exe"
}

$setup = Compile-TestPackage 'Setup' $version $PublishDirectory
$upgrade = Compile-TestPackage 'Upgrade' $version $PublishDirectory
$baselines = @{}
$baselineVersions = @('1.2.2', '1.2.3', '1.3.0')
foreach ($baselineVersion in $baselineVersions) {
    $baselines[$baselineVersion] = Compile-LegacyFixture $baselineVersion
}
$downgrade = Compile-TestPackage 'Upgrade' '1.2.3' (Join-Path $testRoot 'baseline-1.2.3')
$payloadHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
$installedPayload = Join-Path $installDirectory 'TokenConsumptionMonitoring.exe'
$sentinelPath = Join-Path $installDirectory 'settings-preservation-sentinel.json'
$sentinelContent = '{"preserve":"packaging-verification-only"}'

try {
    Invoke-Installer $upgrade 'upgrade-without-installation' @('/CURRENTUSER', "/DIR=`"$installDirectory`"") $false
    Assert-Condition (-not (Test-Path -LiteralPath $testRegistryPath)) 'Rejected upgrade created an installation record.'
    Invoke-Installer $setup 'fresh-install' @('/CURRENTUSER', "/DIR=`"$installDirectory`"")
    $uninstaller = Get-TestUninstaller
    Assert-Condition ((Get-FileHash -LiteralPath $installedPayload).Hash -eq $payloadHash) 'Fresh installation payload differs.'
    Set-Content -LiteralPath $sentinelPath -Value $sentinelContent -NoNewline
    Invoke-Installer $upgrade 'upgrade-cannot-change-directory' @('/CURRENTUSER', "/DIR=`"$(Join-Path $testRoot 'wrong-directory')`"") $false
    $fileLock = [IO.File]::Open($installedPayload, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try { Invoke-Installer $upgrade 'upgrade-locked-file' @('/CURRENTUSER') $false }
    finally { $fileLock.Dispose() }
    $appMutex = [Threading.Mutex]::new($true, "TokenConsumptionMonitoring_Verification_$verificationId")
    try { Invoke-Installer $upgrade 'upgrade-running-application' @('/CURRENTUSER') $false }
    finally { $appMutex.ReleaseMutex(); $appMutex.Dispose() }
    Invoke-Installer $upgrade 'same-version-repair' @('/CURRENTUSER')
    Invoke-Installer $downgrade 'downgrade-rejected' @('/CURRENTUSER') $false
    Assert-Condition ((Get-Content -LiteralPath $sentinelPath -Raw) -eq $sentinelContent) 'Upgrade changed preserved data.'
    $appMutex = [Threading.Mutex]::new($true, "TokenConsumptionMonitoring_Verification_$verificationId")
    try { Invoke-Installer $uninstaller 'uninstall-running-application' @() $false }
    finally { $appMutex.ReleaseMutex(); $appMutex.Dispose() }
    $fileLock = [IO.File]::Open($installedPayload, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try { Invoke-Installer $uninstaller 'uninstall-locked-file' @() $false }
    finally { $fileLock.Dispose() }
    Invoke-Installer $uninstaller 'fresh-install-uninstall'
    Assert-Condition (-not (Test-Path -LiteralPath $testRegistryPath)) 'Uninstall left the verification registration.'
    Assert-Condition (-not (Test-Path -LiteralPath $installedPayload)) 'Uninstall left the installed application.'
    Assert-Condition ((Get-Content -LiteralPath $sentinelPath -Raw) -eq $sentinelContent) 'Uninstall changed preserved data.'

    foreach ($baselineVersion in $baselineVersions) {
        Invoke-Installer $baselines[$baselineVersion] "baseline-$baselineVersion-install" @('/CURRENTUSER', "/DIR=`"$installDirectory`"")
        # No explicit scope or directory: Inno Setup must detect and retain both.
        Invoke-Installer $upgrade "baseline-$baselineVersion-upgrade"
        Assert-Condition ((Get-FileHash -LiteralPath $installedPayload).Hash -eq $payloadHash) "Upgrade from $baselineVersion installed a different payload."
        $registration = Get-ItemProperty -LiteralPath $testRegistryPath
        Assert-Condition ($registration.DisplayVersion -eq $version) "Upgrade from $baselineVersion did not update the version."
        Assert-Condition ($registration.InstallLocation.TrimEnd('\') -eq $installDirectory) 'Upgrade changed the original installation directory.'
        Assert-Condition ((Get-Content -LiteralPath $sentinelPath -Raw) -eq $sentinelContent) 'Upgrade changed preserved data.'
        $uninstaller = Get-TestUninstaller
        Invoke-Installer $uninstaller "baseline-$baselineVersion-uninstall"
    }
    $productionRegistryAfter = Get-ProductionRegistrationSnapshot
    Assert-Condition ($productionRegistryBefore -eq $productionRegistryAfter) 'Production installation registration changed.'
    Assert-Condition (-not (Test-Path -LiteralPath $testRegistryPath)) 'Verification installation was not fully unregistered.'
    [pscustomobject]@{
        Version = $version
        Scope = 'CurrentUser'
        VerificationAppId = $verificationId
        PayloadSHA256 = $payloadHash
        ProductionRegistrationUnchanged = $true
        ApplicationPayloadLaunched = $false
        Cases = $results.ToArray()
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testRoot 'verification.json') -Encoding utf8
    Write-Host "Verification report: $(Join-Path $testRoot 'verification.json')"
} finally {
    # Cleanup targets only the uniquely identified verification installation.
    if (Test-Path -LiteralPath $testRegistryPath) {
        $registeredDirectory = (Get-ItemProperty -LiteralPath $testRegistryPath).InstallLocation.TrimEnd('\')
        if ([IO.Path]::GetFullPath($registeredDirectory) -ne $installDirectory -or
            -not $installDirectory.StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing cleanup because the verification installation path is outside its isolated directory.'
        }
        $uninstaller = Get-TestUninstaller
        if (Test-Path -LiteralPath $uninstaller) {
            Invoke-Installer $uninstaller 'cleanup-uninstall'
        }
    }
}
