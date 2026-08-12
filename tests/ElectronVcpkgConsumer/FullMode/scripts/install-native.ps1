$ErrorActionPreference = "Stop"

$sampleRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $sampleRoot "..\..\.."))
$overlayRoot = Join-Path $repositoryRoot ".build\electron-vcpkg-consumer-overlay\ports"
$portDirectory = Join-Path $overlayRoot "guance-windows-native"
$tripletDirectory = Join-Path $repositoryRoot ".build\electron-vcpkg-consumer-overlay\triplets"
$vcpkgCommand = Get-Command vcpkg.exe -ErrorAction SilentlyContinue
if (-not $vcpkgCommand) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
        $visualStudioRoot = & $vswhere `
            -latest `
            -products * `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -property installationPath
        if ($visualStudioRoot) {
            $visualStudioVcpkg = Join-Path $visualStudioRoot "VC\vcpkg\vcpkg.exe"
            if (Test-Path -LiteralPath $visualStudioVcpkg) {
                $vcpkgCommand = Get-Item -LiteralPath $visualStudioVcpkg
            }
        }
    }
}
if (-not $vcpkgCommand) {
    throw "vcpkg.exe was not found. Install vcpkg or the Visual Studio C++ tools."
}

$vcpkgExecutable = if ($vcpkgCommand.Source) {
    $vcpkgCommand.Source
} else {
    $vcpkgCommand.FullName
}

$null = & (Join-Path $repositoryRoot "build\prepare-vcpkg-local-overlay-port.ps1") `
    -OutputDirectory $portDirectory
New-Item -ItemType Directory -Force -Path $tripletDirectory | Out-Null
$triplet = @"
set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE dynamic)
set(VCPKG_LIBRARY_LINKAGE dynamic)
set(VCPKG_ENV_PASSTHROUGH_UNTRACKED GUANCE_WINDOWS_NATIVE_SOURCE_PATH)
"@
[IO.File]::WriteAllText(
    (Join-Path $tripletDirectory "x64-windows.cmake"),
    $triplet,
    [Text.UTF8Encoding]::new($false))

$savedSourcePath = $env:GUANCE_WINDOWS_NATIVE_SOURCE_PATH
Push-Location $sampleRoot
try {
    $env:GUANCE_WINDOWS_NATIVE_SOURCE_PATH = $repositoryRoot
    & $vcpkgExecutable install `
        --binarysource=clear `
        --triplet x64-windows `
        "--overlay-ports=$overlayRoot" `
        "--overlay-triplets=$tripletDirectory"
    if ($LASTEXITCODE -ne 0) { throw "Full-mode Native Bridge installation failed." }
} finally {
    $env:GUANCE_WINDOWS_NATIVE_SOURCE_PATH = $savedSourcePath
    Pop-Location
}
