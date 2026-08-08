[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "common-tools.ps1")
Repair-ProcessPath

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceDirectory = Join-Path $repositoryRoot "src\Guance.Windows.Native"
$consumerSource = Join-Path $sourceDirectory "tests\package_consumer"
$validationRoot = Join-Path $repositoryRoot ".build\cmake-package\$([Guid]::NewGuid().ToString('N'))"
$buildDirectory = Join-Path $validationRoot "build"
$installDirectory = Join-Path $validationRoot "install"
$consumerBuild = Join-Path $validationRoot "consumer"

$cmake = Resolve-CMakeExecutable

& $cmake `
    -S $sourceDirectory `
    -B $buildDirectory `
    -G "Visual Studio 17 2022" `
    -A x64 `
    "-DBUILD_SHARED_LIBS=ON" `
    "-DBUILD_TESTING=OFF" `
    "-DGUANCE_WINDOWS_NATIVE_STAGE_RUNTIME=OFF"
if ($LASTEXITCODE -ne 0) { throw "CMake package configure failed." }

& $cmake --build $buildDirectory --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw "CMake package build failed." }

& $cmake --install $buildDirectory --config $Configuration --prefix $installDirectory
if ($LASTEXITCODE -ne 0) { throw "CMake package install failed." }

& $cmake `
    -S $consumerSource `
    -B $consumerBuild `
    -G "Visual Studio 17 2022" `
    -A x64 `
    "-DCMAKE_PREFIX_PATH=$installDirectory"
if ($LASTEXITCODE -ne 0) { throw "CMake consumer configure failed." }

& $cmake --build $consumerBuild --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw "CMake consumer build failed." }

$consumer = Join-Path $consumerBuild "$Configuration\guance_windows_native_package_consumer.exe"
if (-not (Test-Path -LiteralPath $consumer -PathType Leaf)) {
    throw "CMake consumer executable was not found: $consumer"
}

$savedPath = $env:PATH
try {
    $env:PATH = "$(Join-Path $installDirectory 'bin');$savedPath"
    & $consumer
    if ($LASTEXITCODE -ne 0) { throw "CMake consumer returned exit code $LASTEXITCODE." }
} finally {
    $env:PATH = $savedPath
}

Write-Output "CMake install/export and dynamic consumer validation passed."
