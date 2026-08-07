Set-StrictMode -Version Latest

function Repair-ProcessPath {
    $processPath = [Environment]::GetEnvironmentVariable("Path", "Process")
    [Environment]::SetEnvironmentVariable("PATH", $null, "Process")
    [Environment]::SetEnvironmentVariable("Path", $processPath, "Process")
}

function Resolve-CMakeExecutable {
    $command = Get-Command cmake -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $match = (& $vswhere `
            -latest `
            -products * `
            -find "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" |
            Select-Object -First 1)
        if (-not [string]::IsNullOrWhiteSpace($match) -and
            (Test-Path -LiteralPath $match -PathType Leaf)) {
            return $match
        }
    }

    throw "CMake was not found. Install CMake or the Visual Studio CMake component."
}

function Resolve-VcpkgExecutable {
    $command = Get-Command vcpkg -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $match = (& $vswhere `
            -latest `
            -products * `
            -find "VC\vcpkg\vcpkg.exe" |
            Select-Object -First 1)
        if (-not [string]::IsNullOrWhiteSpace($match) -and
            (Test-Path -LiteralPath $match -PathType Leaf)) {
            return $match
        }
    }

    throw "vcpkg was not found. Pass -VcpkgExe or install vcpkg."
}
