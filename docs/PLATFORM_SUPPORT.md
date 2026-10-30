# Platform support
**English** | [繁體中文](zh-TW/PLATFORM_SUPPORT.md)

ProcLens detects the operating system at start-up and only offers what that platform really implements. A feature is
never listed as available where it is not; `proclens system capabilities` always tells you the truth for the machine it
runs on. Where a platform cannot provide a value, ProcLens says "not supported" or "n/a (reason)" and never reports it
as zero.

## 1. Feature matrix

| Feature | Windows | Linux | macOS |
|---|---|---|---|
| Process enumeration, identity, executable path, start time | Yes | Yes | Yes |
| Parent process id | Yes | Yes | **n/a** (not exposed by the managed APIs) |
| Owner | Yes (where the token can be opened) | Yes (user name, or numeric uid) | **n/a** |
| CPU time and CPU % (sampled) | Yes | Yes | Yes |
| Working set / virtual / peak working set | Yes | Yes (`VmRSS` / `VmSize` / `VmHWM`) | Yes (where the OS reports them) |
| Private memory | Yes | **n/a** (no equivalent in `/proc`) | Where reported |
| Thread listing | Yes (with wait reason) | Yes (with name and state) | Yes, **state is always `Unknown`** |
| Module listing | Yes | Yes (from `/proc/<pid>/maps`) | **Not supported** |
| Process lifecycle monitoring | Yes | Yes | Yes |
| Diagnostic reports (txt / html / json) | Yes | Yes | Yes |
| Audit log viewing and verification | Yes | Yes | Yes |
| **Process dump** (`dump create`) | **Yes** | **No, by design** | **No, by design** |
| Dump encryption, decrypt, export, verify, info, prune | Yes | No | No |

Process dump is Windows-only on purpose. On Linux and macOS ProcLens will not run `gcore`, use `ptrace` or any other
native mechanism: the command prints

```
Process dump is not supported on this operating system.
Supported platform: Windows.
```

and exits with code 2 without creating anything. The `dump` family of commands is therefore Windows-only, including
those that only read files, because no other platform produces these files.

## 2. What has actually been verified

This table is about evidence, not intent.

| | Windows | Linux | macOS |
|---|---|---|---|
| Builds | Yes, Windows 11 (build 26300), .NET 10 SDK | Yes, Ubuntu 24.04 under WSL2 (kernel 6.6) | Compiles as part of the solution; **never run** |
| Automated tests | Pass, including live tests against real processes | Pass, including live `/proc` tests | **Its live tests exist but have never executed** |
| Checked against native tools | CPU against the performance counters; memory against `Get-Process` | CPU, memory, ids, threads and modules against `ps`, `ps -T` and `/proc` | No |
| Dumps | Created, decrypted, opened by an independent parser (ClrMD) and by DbgHelp | n/a | n/a |
| Run on a physical Windows ARM64, Linux ARM64 or Intel Mac | No | No | No |

`.github/workflows/ci.yml` builds and tests on `windows-latest`, `ubuntu-latest` and `macos-latest`. It has not been run
yet; until it has, everything about macOS (and ARM) is unconfirmed. **Treat macOS support as untested** until the
`MacOSLiveTests` have passed on a real machine.

## 3. Windows

- **Versions:** the Windows versions supported by .NET 10 (see Microsoft's support matrix). Tested on Windows 11 x64; an ARM64 package is built but untested.
- **Inspection** uses the toolhelp snapshot and the process, thread and module APIs. Owners come from the process token;
  protected and other users' processes usually show the owner as `n/a`.
- **A 64-bit ProcLens can read 32-bit processes** (their 32-bit and WOW64 modules both appear). A 32-bit ProcLens cannot
  read the modules of a 64-bit process and reports an *architecture mismatch* (exit code 4), not "access denied".
- **Dumps** need DbgHelp (always in System32) and DPAPI for the machine key. Dumps are encrypted with a key that only
  your Windows account on this machine can recover; `dump export` makes a password-protected copy that can be moved.
- **Protected processes** (Protected Process Light, the `System` process, anti-malware services) cannot be dumped or
  have their modules read from an ordinary account. ProcLens does not try to bypass this and reports access denied.
- **Administrative dumps** of other users' processes need an elevated shell and an enabling machine policy; see
  `CLI_USAGE.md`.

## 4. Linux

- **Versions:** distributions supported by .NET 10 on glibc (x64 and ARM64 packages are built; only x64, on Ubuntu 24.04, was run).
  musl-based distributions (Alpine) are not packaged; build from source with `-r linux-musl-x64`.
- **Inspection** reads `/proc`. The usual rules apply: what `hidepid` or your account hides, ProcLens cannot see.
- **CPU time** converts clock ticks to seconds assuming `USER_HZ` = 100, which is true of every mainstream kernel
  build (checked with `getconf CLK_TCK`); the start-time conversion is cross-checked against the runtime in tests.
- **Modules** are derived from `/proc/<pid>/maps`: every file-backed mapping that has an executable segment, with the
  lowest address as base and the span as size. Reading another user's `maps` needs ptrace-read permission, so those
  report *access denied* for an ordinary user. Data files that are merely memory-mapped are not listed.
- **Threads** come from `/proc/<pid>/task`; state letters are mapped (`R` running, `S`/`I` sleeping, `D` waiting, `T`/`t`
  stopped, `Z`/`X` terminated).
- **Private memory** is unavailable: `/proc` has no counterpart to Windows "private bytes", and ProcLens does not
  substitute a different quantity.

## 5. macOS

- **Versions:** the macOS versions supported by .NET 10 (see Microsoft's support matrix), on Apple silicon or Intel. Both packages are built; **neither has been run**.
- Inspection uses the managed `System.Diagnostics.Process` APIs.
- **Not available:** parent id, owner, loaded modules (a native dyld enumeration is not implemented), and thread state.
  These appear as `n/a` / *Not supported* / `Unknown` and are not collection errors.
- Other users' and system-protected processes are expected to refuse some queries to an ordinary account; ProcLens would report those as inaccessible, but this has not been observed on a Mac.
- The package is not code-signed or notarised, so Gatekeeper blocks it on first run; see `TROUBLESHOOTING.md`.

## 6. Reading the numbers consistently across platforms

- **CPU %** is always shown two ways: percent of the **whole machine** (divided by logical processors) and percent of
  **one core** (can exceed 100). Both need two samples; one reading never produces a rate.
- **Memory** keeps four separate measures (working set, private, virtual, peak working set) that are never merged.
  Their meaning differs slightly by OS (resident set on Linux, working set on Windows); the report states which are
  `n/a`.
- **Times** are stored and shown in UTC unless a field says otherwise.
- **Reports** have identical structure, section order and status vocabulary on every platform and use LF line endings
  everywhere.

## 7. Adding or changing a platform capability

A capability exists only if the platform's provider declares it (`IPlatformProcessProvider.GetCapabilities`). The
command layer asks the capability registry, so declaring support for something not implemented would be a bug, and the
tests assert the declared set. See `ARCHITECTURE.md`, section "Extending ProcLens".
