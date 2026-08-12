$ErrorActionPreference = "Stop"

$sampleRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $sampleRoot "..\..\.."))
$nativeSource = Join-Path $sampleRoot "native-host"
$buildDirectory = Join-Path $nativeSource "build"
$installedDirectory = Join-Path $sampleRoot "vcpkg_installed"
$overlayRoot = Join-Path $repositoryRoot ".build\electron-preview-overlay\ports"
$tripletDirectory = Join-Path $repositoryRoot ".build\electron-preview-overlay\triplets"

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$visualStudioRoot = if (Test-Path -LiteralPath $vswhere) {
    & $vswhere `
        -latest `
        -products * `
        -requires Microsoft.VisualStudio.Component.VC.CMake.Project `
        -property installationPath
} else {
    $null
}

$cmakeCommand = Get-Command cmake.exe -ErrorAction SilentlyContinue
if (-not $cmakeCommand -and $visualStudioRoot) {
    $visualStudioCmake = Join-Path $visualStudioRoot `
        "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
    if (Test-Path -LiteralPath $visualStudioCmake) {
        $cmakeCommand = Get-Item -LiteralPath $visualStudioCmake
        }
}
if (-not $cmakeCommand) {
    throw "cmake.exe was not found. Install the Visual Studio C++ CMake tools."
}
$cmakeExecutable = if ($cmakeCommand.Source) {
    $cmakeCommand.Source
} else {
    $cmakeCommand.FullName
}

$vcpkgCommand = Get-Command vcpkg.exe -ErrorAction SilentlyContinue
if (-not $vcpkgCommand) {
    $visualStudioVcpkg = if ($visualStudioRoot) {
        Join-Path $visualStudioRoot "VC\vcpkg\vcpkg.exe"
    } else {
        $null
    }
    if ($visualStudioVcpkg -and (Test-Path -LiteralPath $visualStudioVcpkg)) {
        $vcpkgCommand = Get-Item -LiteralPath $visualStudioVcpkg
    }
}
if (-not $vcpkgCommand) {
    throw "vcpkg.exe was not found. Add vcpkg to PATH before building the native host."
}

$vcpkgExecutable = if ($vcpkgCommand.Source) {
    $vcpkgCommand.Source
} else {
    $vcpkgCommand.FullName
}
$vcpkgRoot = Split-Path -Parent $vcpkgExecutable
$toolchain = Join-Path $vcpkgRoot "scripts\buildsystems\vcpkg.cmake"
if (-not (Test-Path -LiteralPath $toolchain)) {
    throw "vcpkg toolchain was not found: $toolchain"
}

$configureArguments = @(
    "-S", $nativeSource,
    "-B", $buildDirectory,
    "-G", "Visual Studio 17 2022",
    "-A", "x64",
    "-DVCPKG_MANIFEST_DIR=$sampleRoot",
    "-DVCPKG_INSTALLED_DIR=$installedDirectory",
    "-DVCPKG_OVERLAY_PORTS=$overlayRoot",
    "-DVCPKG_OVERLAY_TRIPLETS=$tripletDirectory"
)
if (-not (Test-Path -LiteralPath (Join-Path $buildDirectory "CMakeCache.txt"))) {
    $configureArguments += "-DCMAKE_TOOLCHAIN_FILE=$toolchain"
}

$savedSourcePath = $env:GUANCE_WINDOWS_NATIVE_SOURCE_PATH
try {
    $env:GUANCE_WINDOWS_NATIVE_SOURCE_PATH = $repositoryRoot
    & $cmakeExecutable @configureArguments
    if ($LASTEXITCODE -ne 0) { throw "Native host CMake configuration failed." }

    & $cmakeExecutable --build $buildDirectory --config Release
    if ($LASTEXITCODE -ne 0) { throw "Native host build failed." }
} finally {
    $env:GUANCE_WINDOWS_NATIVE_SOURCE_PATH = $savedSourcePath
}

$runtime = Join-Path $installedDirectory "x64-windows\bin\guance_windows_native.dll"
$output = Join-Path $buildDirectory "Release"
if (-not (Test-Path -LiteralPath $runtime)) {
    throw "Native runtime was not installed: $runtime"
}
Copy-Item -LiteralPath $runtime -Destination $output -Force

Write-Host "Native host ready: $output\guance_windows_electron_mixed_host.exe"
