[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageVersion,

    [string]$OutputDirectory = ".build\vcpkg-verify",

    [string]$VcpkgExe,

    [switch]$ElectronBridge
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$arguments = @{
    ReleaseTag = "vcpkg_$PackageVersion"
    ValidationBaseDirectory = $OutputDirectory
}
if (-not [string]::IsNullOrWhiteSpace($VcpkgExe)) {
    $arguments.Vcpkg = $VcpkgExe
}
if ($ElectronBridge) {
    $arguments.ElectronBridge = $true
}

& (Join-Path $PSScriptRoot "test-vcpkg-overlay.ps1") @arguments
