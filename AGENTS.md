# Repository Agent Guidance

## Release Tags

- NuGet and vcpkg use independent release streams and independent version histories.
- NuGet release tags use `nuget_<version>`, for example `nuget_0.1.0-alpha.1`.
- vcpkg release tags use `vcpkg_<version>`, for example `vcpkg_0.1.0-alpha.1`.
- The version is the tag suffix after `nuget_` or `vcpkg_`. Do not add a `v` prefix.
- Only these version forms are supported:
  - Alpha: `X.Y.Z-alpha.N`, for example `0.1.0-alpha.1`.
  - Beta: `X.Y.Z-beta.N`, for example `0.1.0-beta.1`.
  - Stable: `X.Y.Z`, for example `0.1.0`.
- `X`, `Y`, and `Z` are non-negative integers without leading zeroes. `N` is a positive integer without leading zeroes.
- Do not use other prerelease labels, build metadata, four-part versions, or reuse a tag in the same release stream.
- Every release version must have an exact heading in the corresponding package changelog.
- NuGet release packaging must pass the tag to `build\pack.ps1 -ReleaseTag <tag>`.
- vcpkg port preparation must pass the tag to `build\prepare-vcpkg-registry-port.ps1 -ReleaseTag <tag>`.
- NuGet Trusted Publishing is owned by `.github/workflows/ci.yml` and the `nuget-release` GitHub Environment. Only `nuget_*` tags may trigger it.
- `vcpkg_*` tags must not trigger NuGet packaging or publishing. Jenkins owns the custom registry update for `GuanceCloud/gc-vcpkg-registry`.
- Do not create a release tag or publish a NuGet package unless the user explicitly requests the release.
