[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$resolver = Join-Path $PSScriptRoot "resolve-release-tag.ps1"

function Assert-ValidTag {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Tag,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion
    )

    $actual = & $resolver -Tag $Tag
    if ($actual -cne $ExpectedVersion) {
        throw "Tag '$Tag' resolved to '$actual' instead of '$ExpectedVersion'."
    }
}

function Assert-InvalidTag {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Tag
    )

    try {
        $null = & $resolver -Tag $Tag
    } catch {
        return
    }

    throw "Tag '$Tag' was expected to be rejected."
}

Assert-ValidTag -Tag "nuget_0.1.0-alpha.1" -ExpectedVersion "0.1.0-alpha.1"
Assert-ValidTag -Tag "nuget_0.1.0-beta.1" -ExpectedVersion "0.1.0-beta.1"
Assert-ValidTag -Tag "nuget_0.1.0" -ExpectedVersion "0.1.0"
Assert-ValidTag -Tag "vcpkg_12.34.56-alpha.9" -ExpectedVersion "12.34.56-alpha.9"
Assert-ValidTag -Tag "vcpkg_12.34.56-beta.9" -ExpectedVersion "12.34.56-beta.9"
Assert-ValidTag -Tag "vcpkg_12.34.56" -ExpectedVersion "12.34.56"

$invalidTags = @(
    "0.1.0",
    "v0.1.0",
    "nuget-0.1.0",
    "NuGet_0.1.0",
    "vcpkg_01.1.0",
    "nuget_1.02.3",
    "nuget_1.2.03",
    "nuget_1.2.3-alpha.0",
    "nuget_1.2.3-beta.01",
    "nuget_1.2.3-rc.1",
    "nuget_1.2.3+build.1",
    "vcpkg_1.2.3.4"
)
foreach ($tag in $invalidTags) {
    Assert-InvalidTag -Tag $tag
}

try {
    $null = & $resolver -Tag "vcpkg_0.1.0-alpha.1" -ExpectedKind nuget
    throw "A vcpkg tag was accepted by the NuGet stream."
} catch {
    if ($_.Exception.Message -eq "A vcpkg tag was accepted by the NuGet stream.") {
        throw
    }
}

$null = & $resolver -Tag "nuget_0.1.0-alpha.1" -ExpectedKind nuget -CheckChangelog
$null = & $resolver -Tag "vcpkg_0.1.0-alpha.1" -ExpectedKind vcpkg -CheckChangelog

Write-Output "Release tag validation passed."
