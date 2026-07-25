[CmdletBinding()]
param(
    [string]$SdkRoot = 'C:\MSFS 2024 SDK',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
$sourceRoot = Join-Path $scriptRoot 'Sources'
$buildRoot = Join-Path $scriptRoot 'BuiltPackage\easycpdlc-vns430-bridge'
$moduleRoot = Join-Path $buildRoot 'modules'
$objectPath = Join-Path $moduleRoot 'easycpdlc-vns430-bridge.obj'
$wasmPath = Join-Path $moduleRoot 'easycpdlc-vns430-bridge.wasm'
$sourcePath = Join-Path $sourceRoot 'EasyCpdlcVnsModule.cpp'
$clang = Join-Path $SdkRoot 'WASM\llvm\bin\clang-cl.exe'
$linker = Join-Path $SdkRoot 'WASM\llvm\bin\wasm-ld.exe'
$wasiRoot = Join-Path $SdkRoot 'WASM\wasi-sysroot'
$wasiLib = Join-Path $wasiRoot 'lib\wasm32-wasi'
$versionLibrary = Join-Path $SdkRoot 'WASM\WasmVersions\MSFS_WasmVersions.a'
$builtinsLibrary = Join-Path $wasiLib 'libclang_rt.builtins-wasm32.a'
$utf8NoBom = [Text.UTF8Encoding]::new($false)

foreach ($required in @($sourcePath, $clang, $linker, $versionLibrary, $builtinsLibrary)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required SDK/source file not found: $required"
    }
}

if (Test-Path -LiteralPath $buildRoot) {
    $resolvedBuildRoot = [IO.Path]::GetFullPath($buildRoot)
    $resolvedCompanionRoot = [IO.Path]::GetFullPath($scriptRoot)
    if (-not $resolvedBuildRoot.StartsWith($resolvedCompanionRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear build output outside $resolvedCompanionRoot"
    }
    Remove-Item -LiteralPath $resolvedBuildRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $moduleRoot -Force | Out-Null

$optimization = if ($Configuration -eq 'Release') { '/clang:-O3' } else { '/clang:-O0' }
$compileArguments = @(
    '/clang:--target=wasm32-wasi'
    "/clang:--sysroot=$wasiRoot"
    '/clang:-std=c++17'
    $optimization
    '/clang:-fno-stack-protector'
    '/clang:-fno-exceptions'
    '/clang:-fno-rtti'
    '/EHsc-'
    '/GR-'
    '/GS-'
    "/I$(Join-Path $SdkRoot 'WASM\include')"
    "/I$(Join-Path $SdkRoot 'SimConnect SDK\include')"
    '/c'
    $sourcePath
    "/Fo$objectPath"
)
& $clang @compileArguments
if ($LASTEXITCODE -ne 0) {
    throw "clang-cl failed with exit code $LASTEXITCODE"
}

$linkArguments = @(
    $objectPath
    '--whole-archive'
    $versionLibrary
    '--no-whole-archive'
    "-L$wasiLib"
    '-lc++'
    '-lc++abi'
    '-lc'
    $builtinsLibrary
    '--no-entry'
    '--allow-undefined'
    '--export=__wasm_call_ctors'
    '--export=module_init'
    '--export=module_deinit'
    '--export=malloc'
    '--export=free'
    '--export-table'
)
if ($Configuration -eq 'Release') {
    $linkArguments += '--strip-all'
}
$linkArguments += @('-o', $wasmPath)
& $linker @linkArguments
if ($LASTEXITCODE -ne 0) {
    throw "wasm-ld failed with exit code $LASTEXITCODE"
}

Remove-Item -LiteralPath $objectPath -Force

$moduleBytes = [IO.File]::ReadAllBytes($wasmPath)
if ($moduleBytes.Length -lt 8 -or
    $moduleBytes[0] -ne 0 -or
    $moduleBytes[1] -ne 0x61 -or
    $moduleBytes[2] -ne 0x73 -or
    $moduleBytes[3] -ne 0x6d) {
    throw 'The linker output is not a valid WebAssembly binary.'
}

$manifest = [ordered]@{
    dependencies = @()
    content_type = 'MISC'
    title = 'EasyCPDLC VNS430 Bridge'
    manufacturer = ''
    creator = 'EasyCPDLC Community'
        package_version = '0.4.0'
    minimum_game_version = '1.8.8'
    minimum_compatibility_version = '8.8.0.230'
    export_type = 'Community'
    builder = 'EasyCPDLC MSFS 2024 SDK build'
    package_order_hint = 'MISC'
    release_notes = [ordered]@{
        neutral = [ordered]@{
            LastUpdate = 'Re-bind L-var ids after flight loads and poll hardware inputs every frame.'
            OlderHistory = ''
        }
    }
    total_package_size = $moduleBytes.Length.ToString([Globalization.CultureInfo]::InvariantCulture)
}
[IO.File]::WriteAllText(
    (Join-Path $buildRoot 'manifest.json'),
    ($manifest | ConvertTo-Json -Depth 10),
    $utf8NoBom)

$moduleFile = Get-Item -LiteralPath $wasmPath
$layout = [ordered]@{
    content = @(
        [ordered]@{
            path = 'modules/easycpdlc-vns430-bridge.wasm'
            size = $moduleFile.Length
            date = $moduleFile.LastWriteTimeUtc.ToFileTimeUtc()
        }
    )
}
[IO.File]::WriteAllText(
    (Join-Path $buildRoot 'layout.json'),
    ($layout | ConvertTo-Json -Depth 5),
    $utf8NoBom)

Write-Host "Built MSFS 2024 standalone module package:"
Write-Host $buildRoot
Write-Host "WASM bytes: $($moduleFile.Length)"
Write-Host "SHA256: $((Get-FileHash -LiteralPath $wasmPath -Algorithm SHA256).Hash)"
