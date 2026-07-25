[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.1.0',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$VPilotInstallDir = '',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

if ([string]::IsNullOrWhiteSpace($VPilotInstallDir)) {
    $registry = Get-ItemProperty -LiteralPath 'HKCU:\Software\vPilot' -ErrorAction SilentlyContinue
    $VPilotInstallDir = [string]$registry.Install_Dir
}
if ([string]::IsNullOrWhiteSpace($VPilotInstallDir)) {
    $VPilotInstallDir = Join-Path $env:LOCALAPPDATA 'vPilot'
}

$pluginApi = Join-Path $VPilotInstallDir 'RossCarlson.Vatsim.Vpilot.Plugins.dll'
if (-not (Test-Path -LiteralPath $pluginApi -PathType Leaf)) {
    throw "vPilot plugin API was not found at '$pluginApi'. Install vPilot or pass -VPilotInstallDir."
}

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$dotnet = $dotnetCommand.Source
$assemblyInfoPath = Join-Path $repoRoot 'EasyCPDLC\Properties\AssemblyInfo.cs'
$assemblyInfo = Get-Content -LiteralPath $assemblyInfoPath -Raw
$expectedFileVersion = "$Version.0"
$fileVersionMatch = [regex]::Match($assemblyInfo, 'AssemblyFileVersion\("(?<version>\d+(?:\.\d+){3})"\)')
if (-not $fileVersionMatch.Success -or $fileVersionMatch.Groups['version'].Value -ne $expectedFileVersion) {
    throw "Release version $Version does not match AssemblyFileVersion $($fileVersionMatch.Groups['version'].Value). Expected $expectedFileVersion."
}

$packageName = "EasyCPDLC-Printer-eLC-$Version-win-x64"
$publishDirectory = Join-Path $OutputDirectory '_publish'
$packageDirectory = Join-Path $OutputDirectory $packageName
$zipPath = Join-Path $OutputDirectory "$packageName.zip"
$checksumPath = "$zipPath.sha256"

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($path in @($publishDirectory, $packageDirectory, $zipPath, $checksumPath)) {
    $fullPath = [IO.Path]::GetFullPath($path)
    if (-not $fullPath.StartsWith($OutputDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release output path escaped the artifact directory: '$fullPath'."
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

& $dotnet publish (Join-Path $repoRoot 'EasyCPDLC\EasyCPDLC.csproj') `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'The EasyCPDLC publish failed.'
}

& $dotnet build (Join-Path $repoRoot 'EasyCPDLC.VPilotBridge\EasyCPDLC.VPilotBridge.csproj') `
    -c $Configuration `
    "-p:VPilotInstallDir=$VPilotInstallDir"
if ($LASTEXITCODE -ne 0) {
    throw 'The vPilot bridge build failed.'
}

$bridgeOutput = Join-Path $repoRoot "EasyCPDLC.VPilotBridge\bin\$Configuration\net48\EasyCPDLC.VPilotBridge.dll"
if (-not (Test-Path -LiteralPath $bridgeOutput -PathType Leaf)) {
    throw "The compiled bridge was not found at '$bridgeOutput'."
}

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'Bridge') -Force | Out-Null
Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $packageDirectory -Recurse -Force
Copy-Item -LiteralPath $bridgeOutput -Destination (Join-Path $packageDirectory 'Bridge\EasyCPDLC.VPilotBridge.dll') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-VPilotBridge.ps1') -Destination $packageDirectory -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-vPilot-Bridge.cmd') -Destination $packageDirectory -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\VPILOT-BRIDGE-INSTALL.txt') -Destination $packageDirectory -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $packageDirectory -Force

$vns430Root = Join-Path $repoRoot 'EasyCPDLC\VNS430'
$moduleRoot = Join-Path $vns430Root 'MSFS2024Module'
$bridgeRoot = Join-Path $moduleRoot 'Bridge'
$mobiFlightProfile = Join-Path $moduleRoot 'MobiFlight\EasyCPDLC-VNS430-Module.mfproj'
$dcduMobiFlightProfile = Join-Path $moduleRoot 'MobiFlight\EasyCPDLC-DCDU-Module.mfproj'
$cduMobiFlightProfile = Join-Path $moduleRoot 'MobiFlight\EasyCPDLC-WinWing-737-CDU.mfproj'
if (-not (Test-Path -LiteralPath $mobiFlightProfile -PathType Leaf)) {
    throw "The required VNS430 MobiFlight profile was not found at '$mobiFlightProfile'."
}
if (-not (Test-Path -LiteralPath $dcduMobiFlightProfile -PathType Leaf)) {
    throw "The required DCDU MobiFlight profile was not found at '$dcduMobiFlightProfile'."
}
if (-not (Test-Path -LiteralPath $cduMobiFlightProfile -PathType Leaf)) {
    throw "The required 737 CDU MobiFlight profile was not found at '$cduMobiFlightProfile'."
}

$vns430PackageDirectory = Join-Path $packageDirectory 'VNS430'
$modulePackageDirectory = Join-Path $vns430PackageDirectory 'MSFS2024Module'
$moduleMobiFlightDirectory = Join-Path $modulePackageDirectory 'MobiFlight'
$moduleSourceDirectory = Join-Path $modulePackageDirectory 'Bridge-Sources'
New-Item -ItemType Directory -Path $moduleMobiFlightDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $moduleSourceDirectory -Force | Out-Null
Copy-Item -LiteralPath $mobiFlightProfile -Destination $moduleMobiFlightDirectory -Force
Copy-Item -LiteralPath $dcduMobiFlightProfile -Destination $moduleMobiFlightDirectory -Force
Copy-Item -LiteralPath $cduMobiFlightProfile -Destination $moduleMobiFlightDirectory -Force
Copy-Item -LiteralPath (Join-Path $vns430Root 'README.md') -Destination $vns430PackageDirectory -Force
Copy-Item -LiteralPath (Join-Path $moduleRoot 'README.md') -Destination $modulePackageDirectory -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\HOPPIE-AIRCRAFT-ACARS-ROUTING.md') -Destination $vns430PackageDirectory -Force
Copy-Item -Path (Join-Path $bridgeRoot 'Sources\*') -Destination $moduleSourceDirectory -Recurse -Force
Copy-Item -LiteralPath (Join-Path $bridgeRoot 'Build-Wasm.ps1') -Destination $moduleSourceDirectory -Force

# Prefer the version-controlled, ready-to-drop Community package; fall back to a local
# SDK build under Bridge\BuiltPackage when one has just been produced.
$trackedCompanionRoot = Join-Path $moduleRoot 'CommunityPackage'
$builtCompanionRoot = if (Test-Path -LiteralPath $trackedCompanionRoot -PathType Container) {
    $trackedCompanionRoot
} else {
    Join-Path $bridgeRoot 'BuiltPackage'
}
$builtCompanionWasm = if (Test-Path -LiteralPath $builtCompanionRoot -PathType Container) {
    Get-ChildItem -LiteralPath $builtCompanionRoot -Filter '*.wasm' -File -Recurse | Select-Object -First 1
} else {
    $null
}
$companionWasmIncluded = $null -ne $builtCompanionWasm
if ($companionWasmIncluded) {
    $companionCommunityDirectory = Join-Path $modulePackageDirectory 'Bridge-Community'
    New-Item -ItemType Directory -Path $companionCommunityDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $builtCompanionRoot '*') -Destination $companionCommunityDirectory -Recurse -Force
}

$bridgeHash = (Get-FileHash -LiteralPath (Join-Path $packageDirectory 'Bridge\EasyCPDLC.VPilotBridge.dll') -Algorithm SHA256).Hash
$manifest = [ordered]@{
    product = 'EasyCPDLC-Loaded'
    version = $Version
    runtime = 'win-x64 self-contained'
    bridge = 'Bridge/EasyCPDLC.VPilotBridge.dll'
    bridgeSha256 = $bridgeHash
    bridgeInstaller = 'Install-vPilot-Bridge.cmd'
    mobiFlightProfile = 'VNS430/MSFS2024Module/MobiFlight/EasyCPDLC-VNS430-Module.mfproj'
    dcduMobiFlightProfile = 'VNS430/MSFS2024Module/MobiFlight/EasyCPDLC-DCDU-Module.mfproj'
    cduMobiFlightProfile = 'VNS430/MSFS2024Module/MobiFlight/EasyCPDLC-WinWing-737-CDU.mfproj'
    companionWasmIncluded = $companionWasmIncluded
    companionCommunityPackage = if ($companionWasmIncluded) { 'VNS430/MSFS2024Module/Bridge-Community' } else { $null }
    companionSdkSources = 'VNS430/MSFS2024Module/Bridge-Sources'
    aircraftAcarsRoutingPlan = 'VNS430/HOPPIE-AIRCRAFT-ACARS-ROUTING.md'
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDirectory 'release-manifest.json') -Encoding UTF8

Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
"$zipHash  $packageName.zip" | Set-Content -LiteralPath $checksumPath -Encoding ASCII

[pscustomobject]@{
    Version = $Version
    Package = $zipPath
    PackageSha256 = $zipHash
    Bridge = (Join-Path $packageDirectory 'Bridge\EasyCPDLC.VPilotBridge.dll')
    BridgeSha256 = $bridgeHash
    Installer = (Join-Path $packageDirectory 'Install-vPilot-Bridge.cmd')
}
