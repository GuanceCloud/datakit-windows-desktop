[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$templateDirectory = Join-Path $repositoryRoot "packaging\vcpkg\ports\guance-windows-native"
$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

# Local consumer tests build the current worktree. Use a development-only
# version so the resulting package cannot be mistaken for a published release.
$replacements = [ordered]@{
    "@VERSION@" = "0.0.0"
    "@SOURCE_REF@" = "local-source-path-required"
    "@SOURCE_SHA512@" = ("0" * 128)
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
