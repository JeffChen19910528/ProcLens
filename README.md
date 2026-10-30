# ProcLens

**English** | [繁體中文](README.zh-TW.md)

ProcLens is a cross-platform (Windows, Linux, macOS) command-line toolkit for diagnosing processes, written in .NET 10 / C#.
It lists and inspects processes, samples CPU and memory, shows threads and modules, produces diagnostic reports
(TXT / HTML / JSON), and, on Windows only, creates encrypted process dumps with an audit trail.

The interface is available in **English** and **Traditional Chinese (zh-TW)**.

## Principles

- **Honest data.** A value that cannot be read is shown as `n/a (reason)`, never as `0`.
- **Per-platform truth.** `proclens system capabilities` lists what the current OS really supports. Nothing is advertised that is not implemented.
- **Dumps are Windows-only, by design.** On Linux and macOS, `dump` commands print `Process dump is not supported on this operating system.` and exit with code 2. ProcLens never uses `gcore` or `ptrace`.
- **Safe by default.** Dumps are encrypted (AES-256-GCM), never overwrite files, only target processes you own unless an administrator policy says otherwise, never elevate themselves, and every dump is recorded in a hash-chained audit log. Dumps may contain secrets from process memory; they are never uploaded anywhere.

## Platform support

| Feature | Windows | Linux | macOS |
|---|---|---|---|
| List / inspect / monitor processes, CPU and memory | Yes | Yes | Yes |
| Threads | Yes | Yes | Yes (state `Unknown`) |
| Modules | Yes | Yes | Not supported |
| Reports (txt / html / json), audit, policy, config | Yes | Yes | Yes |
| Process dump and everything under `dump` | **Yes** | No | No |


## Install

Pre-built, self-contained single-file packages (no .NET runtime needed) are produced by `deploy/package.cs` into
`artifacts/<version>/`: `proclens-<version>-win-x64.zip`, `linux-x64.tar.gz`, `osx-arm64.tar.gz`, and so on, with
`SHA256SUMS.txt`. Verify the checksum before running. Each package contains the
executable, an install script and `docs/`.

Build from source (.NET 10 SDK):

```
dotnet build
dotnet test
dotnet run --project src/ProcLens.Cli -- process list --sort memory
dotnet run deploy/package.cs -- --rids win-x64 --skip-tests     # build a release package
```

## Quick start

```
proclens system capabilities                       # what this machine supports
proclens process list --sort memory                # processes by memory
proclens process inspect --pid 1234                # details of one process
proclens process monitor --pid 1234 --interval 2 --count 10
proclens process threads --pid 1234
proclens process modules --pid 1234
proclens report generate --pid 1234 --format html --output report.html
```

Add `--json` to most commands for machine-readable output (stdout carries only JSON; logs and progress go to stderr).

### Dumps (Windows only)

```
$env:DUMP_PASSWORD = "a long passphrase"          # PowerShell; passwords are never passed as arguments
proclens dump create --pid 1234 --type heap --output .\dumps --password-env DUMP_PASSWORD
proclens dump info   --file .\dumps\<file>.dmp.enc
proclens dump verify --file .\dumps\<file>.dmp.enc --password-env DUMP_PASSWORD
proclens dump decrypt --file .\dumps\<file>.dmp.enc --output .\plain.dmp --password-env DUMP_PASSWORD
proclens dump export --file .\dumps\<file>.dmp.enc --output .\portable.dmp.enc --password-env DUMP_PASSWORD
proclens dump prune  --directory .\dumps                 # dry run; add --delete to remove
proclens audit show --last 20
proclens audit verify
```

Dump types: `mini`, `heap`, `full`. Use `--password-prompt` for a hidden prompt, `--no-encrypt` only if you protect the file yourself.

## Language

```
proclens --lang zh-TW process list                 # one command
proclens config set-language zh-TW                 # remember it
proclens config set-language auto                  # forget it
proclens config show                               # which language, and why
```

Order of precedence: `--lang` → `PROCLENS_LANG` → saved setting → English. The OS language is deliberately ignored.
`--json` output, report JSON, audit records, dump metadata and exit codes are never translated.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Target not found, or a command-line syntax error |
| 2 | Not supported on this platform, or an invalid request |
| 3 | Access denied, not authorized, or cannot be unlocked |
| 4 | Architecture mismatch (32-bit ProcLens, 64-bit target) |
| 5 | Failed, modified, corrupt or tampered |
| 70 | Unexpected internal error |
| 130 | Cancelled with Ctrl+C |

## Project layout

```
src/ProcLens.Core            models, interfaces, pure rules (cipher, audit chain, policy, retention)
src/ProcLens.Application     use cases
src/ProcLens.Infrastructure  audit log, report renderers
src/ProcLens.Platform.*      Windows / Linux / macOS code (all OS-specific code lives here)
src/ProcLens.Cli             commands (one class each), console, localization, composition root
tests/  docs/  deploy/  tools/perf/
```

## Documentation

`docs/ARCHITECTURE.md` · `docs/CLI_USAGE.md` (full command reference) · `docs/PLATFORM_SUPPORT.md` · `docs/SECURITY.md` ·
`docs/SECURITY_REVIEW.md` · `docs/DUMP_FORMAT.md` · `docs/TROUBLESHOOTING.md` · `deploy/README.md`
