# Security review
**English** | [繁體中文](zh-TW/SECURITY_REVIEW.md)

**What this is:** a structured review of ProcLens 1.0.0 by its developer (with an AI assistant). It found real defects,
all fixed and covered by regression tests. **It is not an independent audit or a penetration test**, and its coverage
and limits are stated honestly in sections 5 and 6. Read `SECURITY.md` for the design decisions this review tested.

Severities are the reviewer's own judgement of impact × likelihood in the intended use (an operator inspecting and
dumping their own processes on a machine shared with other users).

## 1. Scope and threat model

ProcLens handles two sensitive things: **copies of other processes' memory** (dumps) and the **records and policy** that
govern them. It is a local, unprivileged tool; it never elevates itself.

| Attacker | Capability assumed | What they might try |
|---|---|---|
| A. Another ordinary user on the machine | Can create files in world-writable places (for example `C:\ProgramData`, `C:\Users\Public`, `/tmp`), cannot read the operator's profile | Plant a policy or data file the operator's ProcLens will believe; read or tamper with dumps |
| B. Hostile file | A crafted dump, metadata, policy, audit line or process name handed to ProcLens | Crash it, exhaust memory/CPU, forge output, trick the parsers |
| C. Hostile process | A process whose name, path or owner contains control characters, markup or look-alike text | Inject into reports, logs and file names |
| D. Malware running as the *same* user | Everything the operator can do | **Out of scope**: it can read the same processes and the same DPAPI key without ProcLens |
| E. A local administrator | Full control | **Out of scope** for confidentiality; relevant only to what the audit log and policy can prove |

## 2. Method

1. **Dependency check**: `dotnet list package --vulnerable --include-transitive` for all 13 projects.
2. **Static analysis**: the .NET analyzers with all rules enabled, looking at the security families (CA2xxx, CA3xxx,
   CA5xxx), plus a search for risky APIs (process creation, reflection loading, unsafe/legacy serializers, weak
   algorithms, raw memory access).
3. **Manual review** of every trust boundary: dump pipeline, key handling and cipher, authorization, audit log, policy
   and retention, file creation, native interop, parsers of untrusted input, report rendering, installers.
4. **Dynamic probing**, to turn suspicions into evidence: planting files as an ordinary user, a robustness sweep of 32
   hostile command lines, a 572 MB metadata file with memory measurement, symbolic-link tests on a real Linux kernel,
   and timing of the key-derivation limits.
5. **Regression tests** for every finding, written to fail before the fix where practical.

## 3. Results at a glance

| Check | Result |
|---|---|
| Known-vulnerable packages | None, in all 13 projects (direct and transitive) |
| Security analyzers (CA2xxx/3xxx/5xxx) | No findings |
| Risky APIs in product code | No process creation, no reflection loading, no unsafe/legacy serializers, no `unsafe` code; only AES-GCM, SHA-256, PBKDF2-SHA256; `Random` is used only for retry jitter |
| Robustness sweep (32 hostile command lines: garbage, truncated, oversized, device names, directories as files, negative ids; one 100,000-character argument could not even be launched because it exceeds the OS limit) | 0 crashes (but see F8: the library hid stack traces behind exit code 1) |
| Findings | 8, all fixed (F1 to F8); 3 were demonstrated against the running code before being fixed |

## 4. Findings

### F1 (Medium, fixed): the machine policy file could be planted by any user

*Evidence.* On a default Windows install `BUILTIN\Users` may create files and folders in `C:\ProgramData`. An ordinary,
non-elevated user created `C:\ProgramData\ProcLens\policy.json` containing `Retention.MaxAgeDays = 0` and
`AdminDumpsEnabled = true, RequireReason = false`. ProcLens accepted it: `dump prune` then selected a two-day-old dump
for deletion, and the administrative justification rule was weakened. Anyone running a scheduled `dump prune --delete`
would have lost their dumps; the administrative gate is also supposed to be an *administrator's* decision.

*Fix.* The policy file is believed only if an ordinary user could not have written it (`PolicyFileTrust`): on Windows
the file and its folder must be owned by Administrators, SYSTEM or TrustedInstaller, and nobody else may hold rights that
let them modify, replace, delete or take over either (creating sub-folders is allowed; inherit-only entries do not
count). On Unix neither may be writable by group or others. An untrusted policy is **ignored**: administrative dumps stay
disabled, retention uses its defaults, and `proclens policy show` says why. A path chosen with `PROCLENS_POLICY` is the
operator's own explicit decision and is trusted. Re-running the same attack after the fix: the planted file is ignored.

*Tests.* 16 data-driven and several more fact-based cases of the decision on synthetic ACLs; real-ACL controls (Windows system files trusted, user-created files
rejected, a policy planted in `C:\Users\Public` ignored end to end).

### F2 (Low to Medium, fixed): no size limit on metadata and policy files

*Evidence.* A 572 MB `.json` next to a dump made `dump prune` (a dry run) use **2,907 MB** of memory (baseline 30 MB). A
file planted in a shared dump folder could exhaust the memory of whoever ran `prune` or `report --dumps-dir`.

*Fix.* Metadata and policy files larger than 1 MiB are not read (real ones are about 1 KB).

### F3 (Low to Medium, fixed): secure delete wrote through symbolic links

*Evidence.* On Ubuntu 24.04 (real kernel), `SecureDelete` on a symbolic link to `precious.txt` deleted the link **and
overwrote the target with zeros** (29 bytes, all zero). Retention skips links at scan time, but a link swapped in between
scan and delete, or any other caller, could cause another file to be destroyed.

*Fix.* A reparse point is only ever removed, never opened for writing. Test runs on Linux.

### F4 (Low, fixed): key-derivation ceiling too generous

A crafted encrypted file may name the PBKDF2 iteration count of each of up to 8 password slots. At the old ceiling
(10,000,000) one slot costs about 1.3 s of CPU, so opening a hostile file could burn about 10 s. The ceiling is now
3,000,000 (5× the default of 600,000), refused before any work is done; files written with the default stay readable.

### F5 (Low, fixed): output paths could write to alternate data streams or devices

*Evidence.* `report generate --output <dir>\a:b.txt` reported success and silently wrote the report into an NTFS alternate
data stream of a file named `a`. The operator chose the path, so this is a surprise rather than an attack, but hidden
streams are a classic way to hide data.

*Fix.* `OutputPathPolicy` refuses, for every command that writes a file, a colon outside the drive letter, DOS device
names (`CON`, `NUL`, `COM1` …) with or without an extension, device paths (`\\.\`, `\\?\GLOBALROOT`) and control
characters. Applied to `dump create --output`, `dump decrypt/export --output` and `report generate --output`.

### F6 (Medium for honesty, fixed): the audit hash chain was described as stronger than it is

The chain uses unkeyed SHA-256. It catches damage and careless edits (any single edit, insertion, deletion or reorder
breaks it, tested), but **anyone who can write the file can recompute the whole chain consistently** and nothing
detects that, and cutting records off the end is invisible. The earlier documentation said edits "are detected" without
that qualification.

*Fix.* The documentation now states the limit plainly. `audit verify` prints the head hash and accepts `--anchor <hash>`:
a head hash saved beforehand in a place the log's writer cannot reach (a ticket, another host) proves nothing earlier was
rewritten and nothing was cut off. Tests include a fully rebuilt, internally valid forgery that passes the plain check and
fails the anchor check.

### F7 (Low, fixed): `dump export` copied encrypted chunks without checking them

Exporting re-wraps the key and copies the chunks as they are, so a damaged or modified dump would have been exported
without complaint and only failed when finally opened. The source is now authenticated end to end (and compared with its
recorded original hash) before anything is written; nothing is created on failure.

### F8 (Low, fixed): unexpected failures printed a stack trace; native callback exceptions would crash the process

*Evidence.* System.CommandLine's default handler catches exceptions from commands and prints the **full stack trace** to
standard error, and for usage mistakes it also printed the whole help page to **standard output**, which would corrupt
scripts reading `--json`.

*Fix.* Unexpected errors now give one line (`Unexpected error (Type): message`) and exit code 70; the full detail is
available with `PROCLENS_DEBUG=1`. Usage errors go to standard error only. Exceptions inside the DbgHelp cancel callback
are caught and abort that one dump instead of terminating the process.

## 5. Residual risks (accepted or open)

| Risk | Status | Why |
|---|---|---|
| **Raw dump exists briefly on disk** as `*.dmp.plain.tmp` before it is encrypted | Open, by design | `MiniDumpWriteDump` needs a file handle. The file is private to the user, wiped and deleted after the encrypted copy is verified, and orphans are removed by `prune`. But it can be seen by backup/indexing agents, and overwriting does not guarantee erasure on SSDs. **The real fix is to stream the dump through DbgHelp's I/O callbacks into the encryptor so plaintext never touches disk; this was not attempted.** |
| Malware running as the same user | Out of scope | Can read the same processes and unprotect the DPAPI master key without ProcLens |
| Passwords from `--password-env` | Accepted | Visible to same-user processes in the environment block; the managed string copy cannot be wiped (the `SecretText` copy is) |
| Owner comparison uses the account *name*, not the SID | Accepted | Names are fully qualified (`DOMAIN\user`) and creating a look-alike needs administrator rights |
| Pre-existing audit-log or key files are not checked for permissive ACLs | Accepted | ProcLens creates them private; it does not repair files someone else made |
| Time-of-check/time-of-use on the output directory (checked for links, then used) | Accepted | Needs write access to the parent folder; created directories are private |
| Audit hashes are unkeyed | Mitigated | Use `--anchor` with an externally stored head hash |
| No code signing; unsigned installers | Open | Needs certificates; verify `SHA256SUMS.txt` |
| Supply chain: no package lock files, CI actions pinned by tag, not commit | Open | Packages are pinned to exact versions; consider lock files and pinned action SHAs |
| `dump export` source must be unlockable on this machine | By design | The password-only copy is the portable format |

## 6. What this review did not cover

- **Administrative dumps with a real elevated token.** The authorization logic is unit-tested and the refusals were
  observed, but the success path was never run elevated.
- **macOS.** Nothing was run there. Linux file-permission handling was tested on Linux only (WSL2).
- **Windows ARM64 and Linux ARM64.**
- **Fuzzing beyond the sweep and the unit tests** (no coverage-guided fuzzing of the minidump, container or policy parsers).
- **Side channels** (timing, memory remanence). AES-GCM and key derivation use the platform cryptography; ProcLens does not
  implement primitives.
- **Protected Process Light and anti-malware interactions**: ProcLens does not try to defeat them and reports access denied.
- **An independent review.** A second pair of eyes, ideally with a different method, is still advisable before ProcLens is
  trusted with sensitive environments.

## 7. Verification

After the fixes: 450 automated tests pass on Windows (15 more are skipped because they need another operating system)
and the same suites pass on Linux (Windows-only tests are skipped there). Each finding has at least one regression test; F1, F2 and F3 were reproduced against the running
product before they were fixed.
