[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.1',
    # Appended to the package name only (e.g. 'beta' -> 1.0.1-beta). The numeric
    # $Version is what must match AssemblyFileVersion.
    [string]$VersionSuffix = '',
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

$displayVersion = if ([string]::IsNullOrWhiteSpace($VersionSuffix)) { $Version } else { "$Version-$VersionSuffix" }
$packageName = "EasyCPDLC-Loaded-$displayVersion-win-x64"
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

# Package layout: the two things a user has to install by hand (the MSFS WASM module
# and the vPilot bridge) sit at the top level in numbered folders, so nothing that
# matters is buried. Reference docs go under Docs\.
$communityDir = Join-Path $packageDirectory '1 - MSFS Community Folder'
$vpilotDir    = Join-Path $packageDirectory '2 - vPilot Bridge'
$profilesDir  = Join-Path $packageDirectory '3 - MobiFlight Profiles'
$docsDir      = Join-Path $packageDirectory 'Docs'

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
foreach ($d in @($communityDir, $vpilotDir, $profilesDir, $docsDir)) {
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}

# The app itself at the root.
Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $packageDirectory -Recurse -Force

# The publish output also carries the MobiFlight profiles (the csproj copies them so a
# dev build has them alongside the exe). In the release they live in the numbered
# folder instead, so drop the duplicate to avoid two copies of the same files.
$publishedProfiles = Join-Path $packageDirectory 'MobiFlight'
if (Test-Path -LiteralPath $publishedProfiles) {
    Remove-Item -LiteralPath $publishedProfiles -Recurse -Force
}

# vPilot bridge: DLL next to its installer. Install-VPilotBridge.ps1 already looks for
# the DLL beside itself, so no path change is needed on its side.
Copy-Item -LiteralPath $bridgeOutput -Destination (Join-Path $vpilotDir 'EasyCPDLC.VPilotBridge.dll') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-VPilotBridge.ps1') -Destination $vpilotDir -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-vPilot-Bridge.cmd') -Destination $vpilotDir -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\VPILOT-BRIDGE-INSTALL.txt') -Destination $vpilotDir -Force

# Manual at the root; the numbered guides and README under Docs. Screenshots come
# along so the guides render offline.
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\MANUAL.md') -Destination $packageDirectory -Force
# README stays at the package root: its links are repo-root relative, so moving it
# under Docs would break every image and guide link in it.
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $packageDirectory -Force
foreach ($guide in @('1-INSTALLATION.md', '2-CDU-GUIDE.md', '3-GNS430-GUIDE.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\$guide") -Destination $docsDir -Force
}
# Screenshots go beside Docs, not inside it, so the guides' "../assets/screenshots"
# links resolve exactly as they do in the repo.
$docsAssets = Join-Path $packageDirectory 'assets\screenshots'
New-Item -ItemType Directory -Path $docsAssets -Force | Out-Null
Copy-Item -Path (Join-Path $repoRoot 'assets\screenshots\*') -Destination $docsAssets -Force

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

# MobiFlight profiles at the top level.
Copy-Item -LiteralPath $mobiFlightProfile -Destination $profilesDir -Force
Copy-Item -LiteralPath $dcduMobiFlightProfile -Destination $profilesDir -Force
Copy-Item -LiteralPath $cduMobiFlightProfile -Destination $profilesDir -Force

# Reference documentation.
Copy-Item -LiteralPath (Join-Path $vns430Root 'README.md') -Destination (Join-Path $docsDir 'GNS430.md') -Force
# GNS430.md embeds images from the component's own Docs\images folder.
$gnsImages = Join-Path $docsDir 'Docs\images'
New-Item -ItemType Directory -Path $gnsImages -Force | Out-Null
Copy-Item -Path (Join-Path $vns430Root 'Docs\images\*') -Destination $gnsImages -Force
Copy-Item -LiteralPath (Join-Path $moduleRoot 'README.md') -Destination (Join-Path $docsDir 'Hardware-Guide.md') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\HOPPIE-AIRCRAFT-ACARS-ROUTING.md') -Destination $docsDir -Force

# WASM sources are for rebuilding only; keep them out of the way under Docs.
$moduleSourceDirectory = Join-Path $docsDir 'WASM-Sources'
New-Item -ItemType Directory -Path $moduleSourceDirectory -Force | Out-Null
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
    # Straight into the top-level Community folder so it is a single drag-and-drop.
    Copy-Item -Path (Join-Path $builtCompanionRoot '*') -Destination $communityDir -Recurse -Force
}

# Short signposts in each install folder.
@"
COPY THE FOLDER NEXT TO THIS FILE INTO YOUR MSFS COMMUNITY FOLDER
=================================================================

Copy the whole "easycpdlc-vns430-bridge" folder (not just the .wasm) into:

  Steam     %APPDATA%\Microsoft Flight Simulator 2024\Packages\Community
  MS Store  %LOCALAPPDATA%\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages\Community

Then RESTART MSFS - it only scans the Community folder at startup.

This is optional. It is only needed for physical buttons, encoders and LEDs.
You do NOT need the MSFS SDK; the module is already built.

See Docs\Hardware-Guide.md for the full walkthrough.
"@ | Set-Content -LiteralPath (Join-Path $communityDir 'READ ME FIRST.txt') -Encoding UTF8

@"
VPILOT BRIDGE INSTALLER
=======================

1. CLOSE vPilot completely.
2. Double-click  Install-vPilot-Bridge.cmd
3. Restart vPilot and type  .debug  to confirm "EasyCPDLC vPilot Bridge" is loaded.

This is optional. It imports vTDLS PDC clearances and controller "Contact Me"
alerts from vPilot. It does not use Hoppie and does not create a CPDLC session.

See VPILOT-BRIDGE-INSTALL.txt for details.
"@ | Set-Content -LiteralPath (Join-Path $vpilotDir 'READ ME FIRST.txt') -Encoding UTF8

@"
MOBIFLIGHT PROFILES
===================

Import into MobiFlight Connector with File > Open.

  EasyCPDLC-WinWing-737-CDU.mfproj  <- START HERE for a WinWing CDU.
                                       71 key inputs + 5 annunciator lamp outputs
                                       (MSG, CALL, FAIL, OFST, EXEC).
  EasyCPDLC-DCDU-Module.mfproj      <- just the 12 line-select keys + a few actions.
  EasyCPDLC-VNS430-Module.mfproj    <- for the GNS430 instrument instead of the CDU.

Every row ships bound to a PLACEHOLDER controller, so all rows show as unassigned
until you reassign them to your own board. That is expected. Change the device on
each row; leave the command / source side alone.

Requires: CDU  SETUP > HW KEYS = ON

See Docs\Hardware-Guide.md for the full walkthrough.
"@ | Set-Content -LiteralPath (Join-Path $profilesDir 'READ ME FIRST.txt') -Encoding UTF8

@"
EasyCPDLC-Loaded $displayVersion
================================

1. Run  EasyCPDLC.exe
2. Read MANUAL.md

Everything in the numbered folders is OPTIONAL - the app works on its own with
mouse and keyboard.

  1 - MSFS Community Folder   physical buttons/encoders/LEDs via MobiFlight
  2 - vPilot Bridge           vTDLS PDCs and Contact Me alerts
  3 - MobiFlight Profiles     key and lamp bindings
  Docs                        full documentation

IMPORTANT: if your aircraft has its own Hoppie/ACARS setup, set it to NONE before
connecting. Hoppie delivers each message once, so two clients on one callsign will
split your messages between them.
"@ | Set-Content -LiteralPath (Join-Path $packageDirectory 'START HERE.txt') -Encoding UTF8

$bridgeHash = (Get-FileHash -LiteralPath (Join-Path $vpilotDir 'EasyCPDLC.VPilotBridge.dll') -Algorithm SHA256).Hash
$manifest = [ordered]@{
    product = 'EasyCPDLC-Loaded'
    version = $displayVersion
    runtime = 'win-x64 self-contained'
    manual = 'MANUAL.md'
    bridge = '2 - vPilot Bridge/EasyCPDLC.VPilotBridge.dll'
    bridgeSha256 = $bridgeHash
    bridgeInstaller = '2 - vPilot Bridge/Install-vPilot-Bridge.cmd'
    mobiFlightProfile = '3 - MobiFlight Profiles/EasyCPDLC-VNS430-Module.mfproj'
    dcduMobiFlightProfile = '3 - MobiFlight Profiles/EasyCPDLC-DCDU-Module.mfproj'
    cduMobiFlightProfile = '3 - MobiFlight Profiles/EasyCPDLC-WinWing-737-CDU.mfproj'
    companionWasmIncluded = $companionWasmIncluded
    companionCommunityPackage = if ($companionWasmIncluded) { '1 - MSFS Community Folder/easycpdlc-vns430-bridge' } else { $null }
    companionWasmSha256 = if ($companionWasmIncluded) { (Get-FileHash -LiteralPath $builtCompanionWasm.FullName -Algorithm SHA256).Hash } else { $null }
    companionSdkSources = 'Docs/WASM-Sources'
    aircraftAcarsRoutingPlan = 'Docs/HOPPIE-AIRCRAFT-ACARS-ROUTING.md'
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDirectory 'release-manifest.json') -Encoding UTF8

Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
"$zipHash  $packageName.zip" | Set-Content -LiteralPath $checksumPath -Encoding ASCII

[pscustomobject]@{
    Version = $displayVersion
    Package = $zipPath
    PackageSha256 = $zipHash
    Bridge = (Join-Path $vpilotDir 'EasyCPDLC.VPilotBridge.dll')
    BridgeSha256 = $bridgeHash
    Installer = (Join-Path $vpilotDir 'Install-vPilot-Bridge.cmd')
    WasmIncluded = $companionWasmIncluded
}
