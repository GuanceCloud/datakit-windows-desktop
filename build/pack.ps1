[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$OutputDirectory = "dist",

    [string]$PackageVersion,

    [string]$ReleaseTag,

    [switch]$SkipTests,

    [switch]$SkipConsumer
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "common-tools.ps1")
Repair-ProcessPath

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot "Guance.Windows.sln"
$project = Join-Path $repositoryRoot "src\Guance.Windows\Guance.Windows.csproj"
$tests = Join-Path $repositoryRoot "tests\Guance.Windows.Tests\Guance.Windows.Tests.csproj"
$resolver = Join-Path $PSScriptRoot "resolve-release-tag.ps1"

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

if ($PSBoundParameters.ContainsKey("ReleaseTag")) {
    if ($PSBoundParameters.ContainsKey("PackageVersion")) {
        throw "Use either -ReleaseTag or -PackageVersion, not both."
    }
    $PackageVersion = & $resolver `
        -Tag $ReleaseTag `
        -ExpectedKind nuget `
        -CheckChangelog
}
$isReleaseBuild = $PSBoundParameters.ContainsKey("ReleaseTag")

if (-not $PSBoundParameters.ContainsKey("PackageVersion") -and
    -not $PSBoundParameters.ContainsKey("ReleaseTag")) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $versionNode = $projectXml.SelectSingleNode("/Project/PropertyGroup/Version")
    if ($null -eq $versionNode) {
        throw "Package version was not found in $project."
    }
    $PackageVersion = [string]$versionNode.InnerText
}

$semverPattern = "^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?:alpha|beta)\.(?:[1-9][0-9]*))?$"
if ([string]::IsNullOrWhiteSpace($PackageVersion) -or $PackageVersion -cnotmatch $semverPattern) {
    throw "Invalid package version '$PackageVersion'. Only alpha, beta, and stable versions are supported."
}

$outputPath = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

Invoke-DotNet -Arguments @("restore", $solution)
if (-not $SkipTests) {
    Invoke-DotNet -Arguments @("test", $tests, "-c", $Configuration, "--no-restore")
}

Invoke-DotNet -Arguments @(
    "pack",
    $project,
    "-c", $Configuration,
    "--no-restore",
    "--output", $outputPath,
    "-p:PackageVersion=$PackageVersion"
)

$package = Join-Path $outputPath "Guance.Windows.$PackageVersion.nupkg"
$symbols = Join-Path $outputPath "Guance.Windows.$PackageVersion.snupkg"
foreach ($artifact in @($package, $symbols)) {
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "Expected package artifact was not created: $artifact"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace("\", "/") })
    $requiredEntries = @(
        "Guance.Windows.nuspec",
        "lib/net6.0/Guance.Windows.dll",
        "lib/net8.0/Guance.Windows.dll",
        "runtimes/win-x64/native/guance_windows_native.dll"
    )
    if ($isReleaseBuild) {
        $requiredEntries += @(
            "runtimes/win-x86/native/guance_windows_native.dll",
            "runtimes/win-arm64/native/guance_windows_native.dll"
        )
    }
    foreach ($entry in $requiredEntries) {
        if ($entries -cnotcontains $entry) {
            throw "NuGet package is missing required entry '$entry'."
        }
    }
    if ($entries | Where-Object { $_ -cmatch "Guance\.Rum|guance_rum_native" }) {
        throw "NuGet package contains a legacy package or native runtime name."
    }
} finally {
    $archive.Dispose()
}

if (-not $SkipConsumer) {
    $consumerRoot = Join-Path $repositoryRoot ".build\nuget-consumer\$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $consumerRoot | Out-Null

    $consumerProject = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Guance.Windows" Version="$PackageVersion" />
  </ItemGroup>
</Project>
"@
    $consumerProgram = @"
using Guance.Windows;

var config = new GuanceConfig();
Console.WriteLine(config.ServiceName);
"@
    [IO.File]::WriteAllText(
        (Join-Path $consumerRoot "Consumer.csproj"),
        $consumerProject,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $consumerRoot "Program.cs"),
        $consumerProgram,
        [Text.UTF8Encoding]::new($false))

    Invoke-DotNet -Arguments @(
        "restore",
        (Join-Path $consumerRoot "Consumer.csproj"),
        "--source", $outputPath,
        "--ignore-failed-sources"
    )
    Invoke-DotNet -Arguments @(
        "run",
        "--project", (Join-Path $consumerRoot "Consumer.csproj"),
        "-c", $Configuration,
        "--no-restore"
    )

    $consumerRuntime = Join-Path $consumerRoot "bin\$Configuration\net8.0\runtimes\win-x64\native\guance_windows_native.dll"
    if (-not (Test-Path -LiteralPath $consumerRuntime -PathType Leaf)) {
        throw "NuGet consumer output is missing the win-x64 native runtime."
    }
}

$manifest = [ordered]@{
    packageId = "Guance.Windows"
    version = $PackageVersion
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    artifacts = @(
        [ordered]@{
            file = [IO.Path]::GetFileName($package)
            sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
        },
        [ordered]@{
            file = [IO.Path]::GetFileName($symbols)
            sha256 = (Get-FileHash -LiteralPath $symbols -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    )
}
[IO.File]::WriteAllText(
    (Join-Path $outputPath "release-manifest.json"),
    ($manifest | ConvertTo-Json -Depth 4),
    [Text.UTF8Encoding]::new($false))

Write-Output "NuGet package validation passed for Guance.Windows $PackageVersion."
