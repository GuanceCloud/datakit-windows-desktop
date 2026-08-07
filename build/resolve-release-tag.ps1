[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [ValidateSet("nuget", "vcpkg")]
    [string]$ExpectedKind,

    [switch]$CheckChangelog
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$semverPattern = "(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?:alpha|beta)\.(?:[1-9][0-9]*))?"
$tagPattern = "^(nuget|vcpkg)_($semverPattern)$"

if ($Tag -cnotmatch $tagPattern) {
    throw "Invalid release tag '$Tag'. Expected nuget_<version> or vcpkg_<version> with an alpha, beta, or stable version."
}

$kind = $Matches[1]
$version = $Matches[2]

if ($PSBoundParameters.ContainsKey("ExpectedKind") -and $kind -cne $ExpectedKind) {
    throw "Release tag '$Tag' belongs to '$kind', not the expected '$ExpectedKind' stream."
}

if ($CheckChangelog) {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $changelog = if ($kind -ceq "nuget") {
        Join-Path $repositoryRoot "src\Guance.Windows\CHANGELOG.md"
    } else {
        Join-Path $repositoryRoot "src\Guance.Windows.Native\CHANGELOG.md"
    }

    if (-not (Test-Path -LiteralPath $changelog -PathType Leaf)) {
        throw "Release changelog was not found: $changelog"
    }

    $content = Get-Content -LiteralPath $changelog -Raw
    $headingPattern = "(?m)^## \[$([Regex]::Escape($version))\](?: - [^\r\n]+)?\r?$"
    if ($content -cnotmatch $headingPattern) {
        throw "Changelog '$changelog' does not contain an exact heading for version '$version'."
    }
}

Write-Output $version
