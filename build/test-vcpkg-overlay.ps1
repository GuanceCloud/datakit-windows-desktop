[CmdletBinding()]
param(
    [string]$Vcpkg,

    [string]$ReleaseTag = "vcpkg_0.1.0-alpha.1",

    [string]$ValidationBaseDirectory = ".build\vcpkg-verify",

    [switch]$ElectronBridge
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "common-tools.ps1")
Repair-ProcessPath

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$cmake = Resolve-CMakeExecutable

if ([string]::IsNullOrWhiteSpace($Vcpkg)) {
    $Vcpkg = Resolve-VcpkgExecutable
}
if (-not (Test-Path -LiteralPath $Vcpkg -PathType Leaf)) {
    throw "vcpkg was not found."
}

$validationId = [Guid]::NewGuid().ToString("N").Substring(0, 12)
$validationBase = if ([IO.Path]::IsPathRooted($ValidationBaseDirectory)) {
    [IO.Path]::GetFullPath($ValidationBaseDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ValidationBaseDirectory))
}
$validationRoot = Join-Path $validationBase $validationId
$overlayRoot = Join-Path $validationRoot "ports"
$portDirectory = Join-Path $overlayRoot "guance-windows-native"
$installRoot = Join-Path $validationRoot "installed"
$buildtreesRoot = Join-Path $validationRoot "buildtrees"
$packagesRoot = Join-Path $validationRoot "packages"
$downloadsRoot = Join-Path $repositoryRoot ".build\vcpkg-downloads"
$consumerBuild = Join-Path $validationRoot "consumer"
$tripletDirectory = Join-Path $validationRoot "triplets"

$null = & (Join-Path $PSScriptRoot "prepare-vcpkg-registry-port.ps1") `
    -ReleaseTag $ReleaseTag `
    -SourceSha512 ("0" * 128) `
    -OutputDirectory $portDirectory

$vcpkgRoot = Split-Path -Parent $Vcpkg
$builtinBaseline = ""
if (Test-Path -LiteralPath (Join-Path $vcpkgRoot ".git")) {
    $builtinBaseline = (& git -C $vcpkgRoot rev-parse HEAD 2>&1 | Out-String).Trim()
} else {
    $remoteHead = (& git ls-remote https://github.com/microsoft/vcpkg HEAD 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -eq 0 -and $remoteHead -cmatch "^([0-9a-f]{40})\s+HEAD$") {
        $builtinBaseline = $Matches[1]
    }
}
if ($builtinBaseline -cnotmatch "^[0-9a-f]{40}$") {
    throw "A valid microsoft/vcpkg builtin registry baseline could not be resolved."
}

$verificationDependency = if ($ElectronBridge) {
@"
    {
      "name": "guance-windows-native",
      "features": [
        "electron-bridge"
      ]
    }
"@
} else {
    '    "guance-windows-native"'
}
$verificationManifest = @"
{
  "name": "guance-windows-native-verification",
  "version-string": "0",
  "builtin-baseline": "$builtinBaseline",
  "dependencies": [
$verificationDependency
  ]
}
"@
[IO.File]::WriteAllText(
    (Join-Path $validationRoot "vcpkg.json"),
    $verificationManifest,
    [Text.UTF8Encoding]::new($false))

New-Item -ItemType Directory -Force -Path $tripletDirectory | Out-Null
$verificationTriplet = @"
set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE dynamic)
set(VCPKG_LIBRARY_LINKAGE dynamic)
set(VCPKG_ENV_PASSTHROUGH_UNTRACKED GUANCE_WINDOWS_NATIVE_SOURCE_PATH)
"@
[IO.File]::WriteAllText(
    (Join-Path $tripletDirectory "x64-windows.cmake"),
    $verificationTriplet,
    [Text.UTF8Encoding]::new($false))

$savedSourcePath = $env:GUANCE_WINDOWS_NATIVE_SOURCE_PATH
$savedRegistryCache = $env:X_VCPKG_REGISTRIES_CACHE
$savedBinaryCache = $env:VCPKG_DEFAULT_BINARY_CACHE
try {
    $env:GUANCE_WINDOWS_NATIVE_SOURCE_PATH = $repositoryRoot
    $env:X_VCPKG_REGISTRIES_CACHE = Join-Path $validationRoot "registries"
    $env:VCPKG_DEFAULT_BINARY_CACHE = Join-Path $validationRoot "binary-cache"
    New-Item -ItemType Directory -Force -Path $env:X_VCPKG_REGISTRIES_CACHE | Out-Null
    New-Item -ItemType Directory -Force -Path $env:VCPKG_DEFAULT_BINARY_CACHE | Out-Null
    & $Vcpkg install `
        "--x-manifest-root=$validationRoot" `
        "--triplet=x64-windows" `
        "--overlay-ports=$overlayRoot" `
        "--overlay-triplets=$tripletDirectory" `
        "--x-install-root=$installRoot" `
        "--x-buildtrees-root=$buildtreesRoot" `
        "--x-packages-root=$packagesRoot" `
        "--downloads-root=$downloadsRoot"
    if ($LASTEXITCODE -ne 0) {
        throw "vcpkg overlay installation failed with exit code $LASTEXITCODE."
    }
} finally {
    $env:GUANCE_WINDOWS_NATIVE_SOURCE_PATH = $savedSourcePath
    $env:X_VCPKG_REGISTRIES_CACHE = $savedRegistryCache
    $env:VCPKG_DEFAULT_BINARY_CACHE = $savedBinaryCache
}

$prefix = Join-Path $installRoot "x64-windows"
$bridgeDirectory = Join-Path $prefix "tools\guance-windows-native"
$bridge = Join-Path $bridgeDirectory "guance_windows_electron_bridge.exe"
$bridgeRuntime = Join-Path $bridgeDirectory "guance_windows_native.dll"
if ($ElectronBridge) {
    if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) {
        throw "The electron-bridge feature did not install the bridge: $bridge"
    }
    if (-not (Test-Path -LiteralPath $bridgeRuntime -PathType Leaf)) {
        throw "The electron-bridge feature did not install its runtime dependency: $bridgeRuntime"
    }
} elseif (Test-Path -LiteralPath $bridge -PathType Leaf) {
    throw "The default feature set unexpectedly installed the Electron bridge: $bridge"
}

$consumerSource = Join-Path $repositoryRoot "src\Guance.Windows.Native\tests\package_consumer"
& $cmake `
    -S $consumerSource `
    -B $consumerBuild `
    -G "Visual Studio 17 2022" `
    -A x64 `
    "-DCMAKE_PREFIX_PATH=$prefix"
if ($LASTEXITCODE -ne 0) { throw "vcpkg consumer configure failed." }

& $cmake --build $consumerBuild --config Release --parallel
if ($LASTEXITCODE -ne 0) { throw "vcpkg consumer build failed." }

$consumer = Join-Path $consumerBuild "Release\guance_windows_native_package_consumer.exe"
$savedPath = $env:PATH
try {
    $env:PATH = "$(Join-Path $prefix 'bin');$savedPath"
    & $consumer
    if ($LASTEXITCODE -ne 0) { throw "vcpkg consumer returned exit code $LASTEXITCODE." }
} finally {
    $env:PATH = $savedPath
}

$featureDescription = if ($ElectronBridge) { " with electron-bridge" } else { "" }
Write-Output "vcpkg overlay$featureDescription and dynamic consumer validation passed."
