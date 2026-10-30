# ProcLens command-line reference
**English** | [繁體中文](zh-TW/CLI_USAGE.md)

```
proclens <group> <command> [options]
```

Every command accepts `-h`, `--help` and `-?`. `--version` prints the version. Commands that list or describe things
accept `--json`; with it, standard output contains only JSON, and warnings and progress go to standard error, so the
output is safe to pipe.

## Display language

ProcLens speaks English and Traditional Chinese (`zh-TW`). The language is chosen in this order:

1. `--lang en|zh-TW` on any command (also `--lang=zh-TW`; `zh`, `zh-Hant` and `zh_tw` are accepted for `zh-TW`)
2. the `PROCLENS_LANG` environment variable
3. the saved setting: `proclens config set-language zh-TW` (`auto` removes it)
4. English

The operating system's language is **not** used, so a script behaves the same on every machine. An unknown language
prints `Unknown language '<x>'. Available: en, zh-TW.` on stderr and continues in English.

What changes: help, table headings, labels, messages, and the TXT and HTML reports. What never changes: `--json`
output, the report's JSON, the audit log, dump metadata files, option names, and exit codes, so scripts can rely on
them in any language. A few phrases inside `System.CommandLine`'s own help screens (for example the `Description:`
heading) have no Chinese version and stay English.

With `zh-TW` ProcLens writes UTF-8 to the console for that run and puts the console back afterwards. If the text looks
garbled, see `TROUBLESHOOTING.md`.

```
proclens config show                 # which language is in effect, and why
proclens config set-language zh-TW   # remember it (stored in %LOCALAPPDATA%\ProcLens\config.json, or ~/.local/share/ProcLens/config.json)
proclens config set-language auto    # forget it
proclens --lang zh-TW process list --sort memory
```

---

Features that exist only on Windows (everything under `dump`) print this and exit with **2** elsewhere:

```
Process dump is not supported on this operating system.
Supported platform: Windows.
```

Run `proclens system capabilities` to see what the current platform supports. See `PLATFORM_SUPPORT.md` for the matrix.

---

## `process`: inspect running processes

Available on Windows, Linux and macOS. Inspection never elevates privileges; whatever the operating system will not
let your account read is reported as `n/a` with a reason, never as zero.

### `process list`

```
proclens process list [--sort pid|name|memory|cpu] [--name <text>] [--json]
```

| Option | Meaning |
|---|---|
| `--sort` | Order by process id (default), name, working set, or CPU usage. Processes whose value is unavailable sort last. |
| `--name` | Only processes whose name contains the text (case-insensitive). |

`--sort cpu` samples every process twice, one second apart, so it takes about a second. The CPU column is empty (`n/a`)
for other sorts because a single reading cannot give a rate.

### `process inspect`

```
proclens process inspect --pid <pid> [--json]
```

Shows id, name, executable path, parent id, owner, start time, working set, thread count, cumulative CPU time and an
`Access` value: `Full`, `Partial` (some fields unreadable) or `Denied`.

### `process monitor`

```
proclens process monitor --pid <pid> [--interval <seconds>] [--count <n>] [--json]
```

Samples one process until it exits, `--count` samples were taken, or you press Ctrl+C (exit code 130).

| Column | Meaning |
|---|---|
| `CPU%` | Percent of the **whole machine** (all logical processors). |
| `CORE%` | Percent of **one core**; above 100 for multi-threaded processes. |
| `AVG%` | Running average of `CPU%`. |
| `WORKSET` `PRIVATE` `VIRTUAL` `PEAK` | The four memory measures; any may be `n/a` on a platform. |

The first sample has no CPU values (a rate needs two readings). Warnings are printed under the sample that triggered
them: `HIGH_CPU` (80% of the machine or more) and `MEMORY_GROWTH` (working set rose on each of five consecutive samples
by at least 10% in total). `--json` prints one compact JSON object per line.

If the process id is reused by a different process while monitoring, monitoring stops rather than mixing the two.

### `process threads`, `process modules`

```
proclens process threads --pid <pid> [--json]
proclens process modules --pid <pid> [--json]
```

Thread state names are normalised (`Running`, `Ready`, `Waiting`, `Sleeping`, `Stopped`, `Terminated`, `Unknown`); the
platform's own detail (a Windows wait reason, a Linux state letter) is shown next to it. `modules` lists base address,
size, name and path. These commands return one of five outcomes rather than an empty list when something is missing:

| Outcome | Exit code |
|---|---|
| Listed | 0 |
| Process not found | 1 |
| Not supported on this platform (for example modules on macOS) | 2 |
| Access denied | 3 |
| Architecture mismatch (a 32-bit ProcLens cannot read a 64-bit process's modules) | 4 |

---

## `system`

```
proclens system info [--json]
proclens system capabilities [--json]
```

`info` shows platform, OS, architecture, logical processors, .NET runtime and the ProcLens version. `capabilities`
lists the six features (process inspection, CPU monitoring, memory monitoring, thread inspection, module inspection,
process dump) as `Supported` or `Not Supported`, with the reason for the latter.

---

## `dump`: process dumps (Windows only)

A dump is a copy of a process's memory and can contain passwords, tokens and keys. **Dumps are encrypted by default.**
Read `SECURITY.md` before using these commands; file layouts are in `DUMP_FORMAT.md`.

### `dump create`

```
proclens dump create --pid <pid> --output <directory> [--type mini|heap|full]
                     [--no-encrypt] [--password-env <VAR> | --password-prompt]
                     [--authorize-admin --reason "<text>"] [--progress] [--json]
```

| Option | Meaning |
|---|---|
| `--type` | `mini` (default): threads, modules, stacks, small. `heap`: adds private read/write memory (managed and native heaps). `full`: all committed memory, large. |
| `--output` | A **directory**. File names are generated. It is created (private to you) if missing; it is refused if it is a file, a device path, or a symbolic link/junction. |
| `--no-encrypt` | Store the raw `.dmp`. The result says "NOT protected". |
| `--password-env`, `--password-prompt` | Also allow the dump to be opened with a password (at least 12 characters). Passwords are never accepted as command-line arguments. |
| `--authorize-admin`, `--reason` | Needed only to dump a process **owned by another user**; see "Administrative access". |
| `--progress` | Prints bytes written to standard error about four times a second. |

Result: `<name>_<pid>_<type>_<UTC time>_<id>.dmp.enc` (or `.dmp` with `--no-encrypt`) plus `<same name>.json` metadata
next to it. You can only dump processes you own; ProcLens never elevates itself, never bypasses process protection and
refuses to dump itself.

| Exit code | Meaning |
|---|---|
| 0 | Dump created |
| 1 | Process not found, or it exited / its id was reused before the dump started |
| 2 | Not supported on this OS, or an invalid request (bad output directory, weak or missing password) |
| 3 | Not authorized, or Windows denied access |
| 4 | Architecture mismatch (32-bit ProcLens, 64-bit target) |
| 5 | Failed (message says why; no partial files are left) |
| 130 | Cancelled with Ctrl+C |

Command-line syntax errors (an unknown option, `--type everything`) also exit with 1 and print usage.

### Administrative access

Dumping another user's process needs **all** of the following; otherwise the request is refused and recorded:

1. `--authorize-admin --reason "..."` on the command (the reason is written to the audit log);
2. ProcLens already running elevated (start it from an elevated shell; it will not elevate itself);
3. a machine policy that enables it (see "Policy file");
4. your account on the policy's allow-list, if it has one.

### `dump info`

```
proclens dump info --file <path> [--json]
```

For an unencrypted dump: validity, dump type, creation time, target architecture and Windows build, thread and module
counts. For an encrypted one: the encryption scheme and what can unlock it (the structure is not shown while
encrypted). Both compare the file's SHA256 with the recorded value. Exit 0 when sound, 5 when invalid or modified,
1 when the file does not exist, 3 when it cannot be read.

### `dump verify`

```
proclens dump verify --file <path> [--password-env <VAR> | --password-prompt] [--json]
```

Checks an encrypted dump end to end, authenticating every chunk and comparing against both recorded hashes, without
writing any plaintext. Exit 0 verified, 3 cannot be unlocked with the available keys, 5 modified or corrupt.

### `dump decrypt`

```
proclens dump decrypt --file <x.dmp.enc> --output <new file> [--password-env <VAR> | --password-prompt]
```

Writes a plain `.dmp` that a debugger can open. The output must not exist (nothing is ever overwritten), is private to
you, and is deleted if anything fails. **It is an unprotected copy of the process's memory**; delete it when finished.
Exit 0 ok, 1 not found, 2 not an encrypted dump or the output exists, 3 cannot unlock, 5 modified or failed.

### `dump export`

```
proclens dump export --file <x.dmp.enc> --output <new file>
                     (--password-env <VAR> | --password-prompt)
                     [--current-password-env <VAR> | --current-password-prompt]
```

Creates a copy protected only by a new password (at least 12 characters), so it can leave this machine. The data is
not re-encrypted; the machine key is deliberately not carried over. Use `--current-password-*` if the source is itself
password-protected. The original is untouched. Exit codes as for `decrypt`.

### `dump prune`

```
proclens dump prune --directory <dir> [--older-than-days <n>] [--keep <n>] [--max-size-mb <n>] [--delete] [--json]
```

Applies the retention policy to a directory of ProcLens dumps. **It is a dry run unless `--delete` is given.** A dump
is selected if it is older than the limit, beyond the newest `--keep`, or outside the `--max-size-mb` budget (newest
kept first). Options override the policy file for one run; the default is 30 days. Only dumps with valid ProcLens
metadata are touched; other files, subdirectories and symbolic links are skipped and listed. Leftover temporary
plaintext (`*.dmp.plain.tmp`) from an interrupted dump is also removed once older than an hour. Every deletion is
audited; if the audit log cannot be written, nothing is deleted. Exit 0 ok, 1 directory problem, 2 invalid option,
5 at least one deletion failed. Nothing runs automatically: schedule it with Task Scheduler or cron if you want.

---

## `audit`

```
proclens audit show [--last <n>] [--json]
proclens audit verify [--anchor <hash>] [--json]
```

`show` prints recent records (default 20; `--json` prints one record per line). `verify` re-computes the hash chain and
exits 0 if intact, 5 and the first broken line if any record was edited, inserted, deleted or reordered. On success it
prints the **head hash**.

The hashes are not keyed: someone who can write the log can rebuild the whole chain, and cutting the newest records off
is invisible to a plain `verify`. To catch that, save the head hash somewhere the log's writer cannot change (a ticket,
another host) and later run `audit verify --anchor <hash>`; it fails (exit 5) if that record is gone or anything before
it was rewritten. New records after the anchor are fine.

The log records `dump.create`, `dump.decrypt`, `dump.export`, `dump.verify` and `dump.delete`, including refusals.

---

## `policy`

```
proclens policy show [--json]
```

Shows the machine policy ProcLens is applying: the file it looked at, **whether it believes that file**, and the
effective administrative-dump and retention settings. Use it when a policy seems to be having no effect: a policy file
that an ordinary user could have written is ignored (see "Policy file" below), and `policy show` says exactly why.

---

## `report`

```
proclens report generate --pid <pid> [--format txt|html|json] [--output <new file>]
                         [--samples <n>] [--interval <seconds>] [--dumps-dir <dir>]
                         [--no-threads] [--no-modules]
```

| Option | Meaning |
|---|---|
| `--format` | `txt` (default), `html` (self-contained, no scripts or external resources) or `json`. |
| `--output` | Write to a new file instead of standard output. Never overwrites; the file is private to you. |
| `--samples`, `--interval` | CPU/memory samples (1–120, default 3) and seconds between them (0.1 or more, default 1). A CPU rate needs at least 2 samples. |
| `--dumps-dir` | Also list the dumps of this process recorded in that directory (Windows). |
| `--no-threads`, `--no-modules` | Leave those sections out (they are then "Not collected"). |

Every report has the same nine sections in the same order on every platform: System, Process, Cpu, Memory, Threads,
Modules, Dumps, Warnings, Errors. Each section has a status:

| Status | Meaning |
|---|---|
| Available | Collected. A value shown as `n/a (reason)` inside it was unavailable; a plain `0` is a real zero. |
| Not supported | This platform cannot provide it. Not an error. |
| Access denied | It could be provided, but the OS refused for this process. Listed under Errors. |
| Process not found / Architecture mismatch / Failed | Listed under Errors. |
| Not collected | Not requested, or not enough samples. |

Warnings that can appear (the `Warnings` section; each is also in the JSON as `Code` and `Message`):

| Code | Raised when |
|---|---|
| `LIMITED_ACCESS` | Only part of the process's details were readable (`Access: Partial` or `Denied`). |
| `HIGH_CPU` | Average CPU over the sampling period is 80% of the machine or more. |
| `MEMORY_GROWTH` | Working set rose on every sample of a five-sample window, by at least 10% in total. |
| `PROCESS_EXITED` | The process ended before all requested samples were taken. |
| `THREAD_STATE_UNKNOWN` | The platform reports no thread states (macOS), so per-state counts say nothing. |
| `UNENCRYPTED_DUMP` | A dump of this process on disk (found via `--dumps-dir`) is not encrypted. |

Exit 0 when a report was produced (even if sections are denied: the report says so), 1 when the process does not exist
(nothing is written), 2 when `--output` exists, 5 when the file cannot be written. Reports contain process metadata
only, no memory contents and no command line.

---

## `config`

```
proclens config show [--json]
proclens config set-language <en|zh-TW|auto>
```

`show` prints the language in effect, where it came from (`--lang`, `PROCLENS_LANG`, `config`, or `default`) and the
available languages; `--json` gives `Language`, `Source` and `Available` in English. `set-language` writes the
per-user setting file atomically and confirms in the language you chose. Exit 2 for an unknown language, 5 if the file
cannot be written. A damaged or oversized setting file is ignored (English is used); it never stops a command.

The setting is not security-relevant, so unlike the machine policy it lives in the user's own profile.

---

## Files, environment variables and policy

| What | Default location | Override |
|---|---|---|
| Audit log | `%LOCALAPPDATA%\ProcLens\audit\audit.jsonl` on Windows; `~/.local/share/ProcLens/audit/audit.jsonl` elsewhere | `PROCLENS_AUDIT_LOG` |
| Master key (DPAPI-protected) | `%LOCALAPPDATA%\ProcLens\keys\master.key` | `PROCLENS_KEY_DIR` (the directory) |
| Machine policy | `%ProgramData%\ProcLens\policy.json` on Windows; `/etc/proclens/policy.json` elsewhere | `PROCLENS_POLICY` |
| Display language setting | `%LOCALAPPDATA%\ProcLens\config.json` on Windows; `~/.local/share/ProcLens/config.json` elsewhere | `PROCLENS_CONFIG` (the file); `PROCLENS_LANG` and `--lang` override the saved value |

Uninstalling ProcLens does not delete any of these. Keep `master.key`: dumps protected only by it cannot be opened
without it. Use `dump export` (password) for dumps you must keep long-term.

### Policy file

One JSON file holds both the administrative-dump policy and the retention policy. Comments and trailing commas are
allowed. A missing, unreadable, invalid or oversized (over 1 MiB) file means: administrative dumps **disabled**,
retention at its defaults.

**ProcLens only believes the file at the default location if an ordinary user could not have written it.** On a default
Windows install any user can create files under `C:\ProgramData`, so an unprotected policy there could be planted by
someone else. On Windows the file and its folder must be owned by Administrators, SYSTEM or TrustedInstaller and
not be modifiable by anyone else; on Unix neither may be writable by group or others. A policy that fails this is
**ignored**, and `proclens policy show` tells you why. A file chosen with `PROCLENS_POLICY` is always trusted (it is your
own explicit choice). How to create a protected policy is in `SECURITY.md`, "Protecting the policy file".

```json
{
  "AdminDumpsEnabled": true,
  "AllowedOperators": ["CORP\\alice", "CORP\\bob"],
  "RequireReason": true,
  "MinReasonLength": 10,

  "Retention": {
    "MaxAgeDays": 30,
    "MaxCount": 20,
    "MaxTotalMegabytes": 4096
  }
}
```

| Key | Default | Meaning |
|---|---|---|
| `AdminDumpsEnabled` | `false` | Allow `--authorize-admin` dumps at all. |
| `AllowedOperators` | none (anyone elevated) | If present, only these accounts (case-insensitive). |
| `RequireReason`, `MinReasonLength` | `true`, `10` | Require a justification of at least this length. |
| `Retention.MaxAgeDays` | `30` | `dump prune` selects dumps older than this. `0` selects all. |
| `Retention.MaxCount` | none | Keep at most this many dumps. |
| `Retention.MaxTotalMegabytes` | none | Keep the newest dumps that fit. |

### Passwords

Never put a password on the command line. Either export it into an environment variable and pass its **name**, or let
ProcLens prompt for it (input hidden, asked twice when creating one):

```powershell
$env:DUMP_PASSWORD = Read-Host -AsSecureString | ConvertFrom-SecureString -AsPlainText   # PowerShell 7
proclens dump create --pid 1234 --type full --output .\dumps --password-env DUMP_PASSWORD
```

---

## Exit code summary

| Code | Usual meaning |
|---|---|
| 0 | Success |
| 1 | Target (process, file, directory) not found, or a command-line syntax error |
| 2 | Not supported on this platform, or an invalid request |
| 3 | Access denied, not authorized, or cannot be unlocked |
| 4 | Architecture mismatch (32-bit ProcLens, 64-bit target) |
| 5 | Failed, modified, corrupt, or tampered |
| 70 | Unexpected internal error (a bug): one line on stderr; set `PROCLENS_DEBUG=1` for full details |
| 130 | Cancelled with Ctrl+C |

The codes are stable and are covered by tests; the exact message wording is not guaranteed.

**Streams.** Results go to standard output; warnings, progress, errors and usage mistakes go to standard error. A
command-line mistake prints the problem and a hint on stderr and leaves stdout empty, so scripts that read `--json`
output are not corrupted.

**Output paths.** Every path ProcLens is asked to write (`--output` of `dump create`, `dump decrypt`, `dump export`
and `report generate`) is refused if it contains control characters, names a Windows device (`CON`, `NUL`, `COM1` …,
also with an extension, or `\\.\` paths), or contains a colon anywhere but the drive letter (which would write into a
hidden NTFS alternate data stream). Existing files are never overwritten.
