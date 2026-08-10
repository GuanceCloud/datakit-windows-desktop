[CmdletBinding()]
param(
    [ValidateSet("x64", "x86", "arm64")]
    [string]$TargetArch = "x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "common-tools.ps1")
Repair-ProcessPath

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceDirectory = Join-Path $repositoryRoot "src\Guance.Windows.Native"
$buildDirectory = Join-Path $repositoryRoot ".build\native-$TargetArch"

$cmake = Resolve-CMakeExecutable

$generatorArch = switch ($TargetArch) {
    "x64" { "x64" }
    "x86" { "Win32" }
    "arm64" { "ARM64" }
}
$buildTests = if ($SkipTests -or $TargetArch -cne "x64") { "OFF" } else { "ON" }

& $cmake `
    -S $sourceDirectory `
    -B $buildDirectory `
    -G "Visual Studio 17 2022" `
    -A $generatorArch `
    "-DBUILD_SHARED_LIBS=ON" `
    "-DBUILD_TESTING=$buildTests" `
    "-DGUANCE_WINDOWS_NATIVE_BUILD_ELECTRON_BRIDGE=ON" `
    "-DGUANCE_WINDOWS_NATIVE_STAGE_RUNTIME=ON" `
    "-DGUANCE_WINDOWS_NATIVE_RUNTIME_ARCH=$TargetArch"
if ($LASTEXITCODE -ne 0) {
    throw "CMake configure failed for $TargetArch."
}

& $cmake --build $buildDirectory --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) {
    throw "CMake build failed for $TargetArch."
}

if ($buildTests -ceq "ON") {
    & $cmake --build $buildDirectory --config $Configuration --target RUN_TESTS
    if ($LASTEXITCODE -ne 0) {
        throw "Native tests failed for $TargetArch."
    }
}

$runtime = Join-Path $sourceDirectory "bin\win-$TargetArch\guance_windows_native.dll"
if (-not (Test-Path -LiteralPath $runtime -PathType Leaf)) {
    throw "Expected native runtime was not staged: $runtime"
}
$bridge = Join-Path $sourceDirectory "bin\win-$TargetArch\guance_windows_electron_bridge.exe"
if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) {
    throw "Expected Electron bridge was not staged: $bridge"
}

Write-Output "Native $TargetArch build validation passed."
