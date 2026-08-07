# guance-windows-native Port Template

This directory is the source-owned template for the `guance-windows-native`
port in `GuanceCloud/gc-vcpkg-registry`. Render the template files before
copying them into the registry.

Jenkins must:

1. Check out the exact `vcpkg_<version>` source tag.
2. Calculate the GitHub source archive SHA-512.
3. Run `build/prepare-vcpkg-registry-port.ps1` with the tag, SHA-512, and a
   temporary output directory.
4. Copy the generated `vcpkg.json`, `portfile.cmake`, and `usage` files into
   `ports/guance-windows-native` in the registry checkout.
5. Run the registry's normal version-database update and validation process.

The preparation script rejects unsupported tags and verifies the native
changelog before rendering. The port is dynamic x64-windows only. Session
Replay is experimental and runtime opt-in. SQLite is not a default dependency.
