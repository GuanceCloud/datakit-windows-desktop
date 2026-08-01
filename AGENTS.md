# Repository Agent Guidance

## NuGet Release Tags

- Git release tags and NuGet package versions must be identical. Do not add a `v` prefix to release tags.
- Only these release version forms are supported:
  - Alpha: `X.Y.Z-alpha.N`, for example `0.1.0-alpha.1`.
  - Beta: `X.Y.Z-beta.N`, for example `0.1.0-beta.1`.
  - Stable: `X.Y.Z`, for example `0.1.0`.
- `X`, `Y`, and `Z` are non-negative integers without leading zeroes. `N` is a positive integer without leading zeroes.
- Do not use other prerelease labels, build metadata, four-part versions, or reuse an existing tag/package version.
- Release packaging must pass the tag name to `build\pack.ps1 -PackageVersion <tag>`.
- NuGet publishing is owned by `.github/workflows/ci.yml` and the `nuget-release` GitHub Environment.
- Do not create a release tag or publish a NuGet package unless the user explicitly requests the release.
