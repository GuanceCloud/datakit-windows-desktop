[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseTag,

    [Parameter(Mandatory = $true)]
    [string]$SourceSha512,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($SourceSha512 -cnotmatch "^[0-9a-fA-F]{128}$") {
    throw "SourceSha512 must contain exactly 128 hexadecimal characters."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolver = Join-Path $PSScriptRoot "resolve-release-tag.ps1"
$version = & $resolver `
    -Tag $ReleaseTag `
    -ExpectedKind vcpkg `
    -CheckChangelog

$templateDirectory = Join-Path $repositoryRoot "packaging\vcpkg\ports\guance-windows-native"
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$replacements = [ordered]@{
    "@VERSION@" = $version
    "@SOURCE_REF@" = $ReleaseTag
    "@SOURCE_SHA512@" = $SourceSha512.ToLowerInvariant()
}

$templates = [ordered]@{
    "vcpkg.json.in" = "vcpkg.json"
    "portfile.cmake" = "portfile.cmake"
}
foreach ($templateName in $templates.Keys) {
    $content = Get-Content -LiteralPath (Join-Path $templateDirectory $templateName) -Raw
    foreach ($token in $replacements.Keys) {
        $content = $content.Replace($token, $replacements[$token])
    }
    if ($content -cmatch "@[A-Z0-9_]+@") {
        throw "Unresolved template token remains in '$templateName'."
    }

    $outputName = $templates[$templateName]
    [IO.File]::WriteAllText(
        (Join-Path $outputPath $outputName),
        $content,
        [Text.UTF8Encoding]::new($false))
}

$usage = Get-Content -LiteralPath (Join-Path $templateDirectory "usage") -Raw
[IO.File]::WriteAllText(
    (Join-Path $outputPath "usage"),
    $usage,
    [Text.UTF8Encoding]::new($false))

Write-Output $outputPath
