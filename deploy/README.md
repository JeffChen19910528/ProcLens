# Packaging and releasing ProcLens

`deploy/package.cs` builds the release packages with nothing but the .NET 10 SDK. It runs the same way on Windows,
Linux and macOS and writes the Unix permission bits into the tar archives even when run on Windows.

```
dotnet run deploy/package.cs                                   # every platform, tests first
dotnet run deploy/package.cs -- --rids win-x64,linux-x64       # only some
dotnet run deploy/package.cs -- --skip-tests --version 1.0.1
dotnet run deploy/package.cs -- --output D:\release --no-smoke-test
```

Output, under `artifacts/<version>/`:

| File | Platform |
|---|---|
| `proclens-<v>-win-x64.zip`, `…-win-arm64.zip` | Windows |
| `proclens-<v>-linux-x64.tar.gz`, `…-linux-arm64.tar.gz` | Linux (glibc) |
| `proclens-<v>-osx-x64.tar.gz`, `…-osx-arm64.tar.gz` | macOS |
| `SHA256SUMS.txt` | `sha256sum -c`-compatible checksums of every archive |

Each package is **self-contained, single-file** (no .NET runtime required, one executable, not trimmed) and holds the
executable, `README.txt`, the install script for that platform and `docs/`. The directories `windows/`, `linux/` and
`macos/` here hold those install scripts; everything else in the package is generated.

## What the script does

1. Runs the whole test suite (skip with `--skip-tests`). **No package is built if a test fails.**
2. Publishes each runtime id: Release, self-contained, single file, no debug symbols, version stamped.
3. Adds the docs, README and installer and creates the archive.
4. Writes `SHA256SUMS.txt`.
5. **Smoke-tests the package for the machine it is running on**: extracts it like a user would, then runs
   `--version`, `system capabilities`, `process inspect` and `report generate`, with audit log, keys and policy
   redirected to a temporary folder.

Packages for any other platform are built but **cannot be run by the script**. Test them on that platform.

## Release checklist

- [ ] All tests pass on Windows, Linux **and macOS** (`.github/workflows/ci.yml`; macOS has never been run).
- [ ] `docs/` reviewed for the version being released; `docs/SECURITY.md` sections "Not done yet" and "Known limits" are current.
- [ ] Run the packager, then smoke-test each package on its real platform (at least: `--version`, `system capabilities`,
      and on Windows a `dump create` / `dump verify` / `dump decrypt` round trip).
- [ ] Publish the archives **and** `SHA256SUMS.txt`; tell users to verify it.
- [ ] Say plainly in the release notes which platforms were tested (see `README.txt` in each package).

## Not done yet

- **Code signing.** The Windows executable is not Authenticode-signed and the macOS binary is not signed or notarised,
  so SmartScreen and Gatekeeper warn. Signing needs certificates that are not part of this repository.
- **A license file.** There is no `LICENSE` in the repository; the packager includes one automatically if you add it at
  the repository root. Choose and add one before distributing publicly.
- **Linux musl (Alpine) packages** and distribution formats (`.deb`, `.rpm`, MSI, Homebrew, winget) are not produced.
- **A release workflow in CI.** `ci.yml` builds and tests; publishing is manual.
- **Startup time** has not been tuned (no ReadyToRun or trimming): both add risk and were deliberately left out.
