# Troubleshooting
**English** | [繁體中文](zh-TW/TROUBLESHOOTING.md)

Find your message (or the symptom) in the tables below. Exit codes are listed in `CLI_USAGE.md`. Nothing here asks you to
weaken a protection: where ProcLens refuses, the fix is to satisfy the condition it names, not to bypass it.

## 1. First checks

```
proclens system info
proclens system capabilities
proclens audit verify
```

`capabilities` tells you what this machine can do at all. If a feature shows *Not Supported*, no option will enable it.

## 2. Inspecting processes

| You see | Why | What to do |
|---|---|---|
| `Process <id> was not found.` / `…was not found or has exited.` (exit 1) | No such process, or it ended. Ids are reused by the system. | Re-list with `proclens process list --name <text>`. |
| `n/a (not readable)` for owner or executable; `Access: Partial` or `Denied` | Your account may not read that process's details. | Expected for system and other users' processes. Run as the owning user. |
| `Access denied or process exited.` against a memory or thread value | Same, per field. | As above; the field stays `n/a`, never `0`. |
| `CpuMonitoring is not available: …` | The platform does not provide CPU sampling. | Nothing to fix; see `PLATFORM_SUPPORT.md`. |
| CPU shows `n/a` on `process inspect`, or the first `monitor` sample has no CPU | A rate needs two readings. | Use `process monitor` or `process list --sort cpu`. |
| `Architecture mismatch: ProcLens is running as a 32-bit process and the target is 64-bit` (exit 4) | A 32-bit ProcLens cannot read a 64-bit process's modules. | Use the 64-bit package. A 64-bit ProcLens can read 32-bit processes. |
| `Access denied: Reading /proc/<pid>/maps requires ptrace-read permission…` (Linux) | Another user's process, or a restrictive `ptrace_scope` / `hidepid`. | Inspect your own processes; ProcLens does not raise privileges. |
| Modules say *Not supported* (macOS) | There is no module enumeration on macOS. | Expected; see `PLATFORM_SUPPORT.md`. |
| `n/a` private memory (Linux) | `/proc` has no equivalent value. | Expected. |
| Operating-system messages appear in your language, for example `存取被拒` | The OS text is passed through. | The status label and exit code are stable; the OS wording is not. |
| `monitor` ends with `Process <id> exited; monitoring stopped.` | The process ended or its id was taken by another process. | Expected. |

## 3. Creating dumps (Windows)

| You see | Why | What to do |
|---|---|---|
| `Process dump is not supported on this operating system. Supported platform: Windows.` (exit 2) | Linux and macOS never create dumps, by design. | Use Windows. |
| `The target process is owned by '…', not by the operator '…'. Only processes you own can be dumped.` (exit 3) | Ownership rule. | Run as the owner, or see "Administrative access" below. |
| `The owner of the target process could not be determined, so the request was denied.` (exit 3) | Protected or system process, or one your account may not open. ProcLens fails closed. | Same; many system processes cannot be dumped from an ordinary account. |
| `Windows denied access to the process. ProcLens does not elevate privileges or bypass process protection.` (exit 3) | Windows refused `PROCESS_VM_READ`: a protected process, or a higher-integrity process. | Run ProcLens at the same or higher integrity level. Protected Process Light targets cannot be dumped at all. |
| `The process start time cannot be read, so its identity cannot be verified and no dump was taken.` | System processes such as `System`. | Not dumpable. |
| `The process with this id is not the one that was inspected (its id was reused)…` | The process ended and the id now belongs to another. | Repeat the command. |
| `The process exited before the dump started.` | It ended in between. | Repeat. |
| `ProcLens cannot dump itself; dump it from another process.` | Dumping from inside the target is unreliable. | Run ProcLens from another process. |
| `Part of the target's memory could not be read (ERROR_PARTIAL_COPY)…` (exit 5) | It exited mid-dump, or part of it is protected. | Retry; try `--type mini`. |
| `ProcLens is running as a 32-bit process and the target is 64-bit; run the 64-bit build to dump it.` (exit 4) | Bitness. | Use the 64-bit package. |
| `There is not enough disk space for the dump.` | `full` dumps are as large as the process's memory. | Free space, pick another `--output`, or use `--type heap`/`mini`. |
| `'…' is a file, not a directory.` / `Device paths are not accepted…` / `…is a symbolic link or junction; choose a real directory…` / `The output directory is not usable: …` (exit 2) | `--output` takes a real directory. | Give a normal directory path. |
| `Warning: The output directory can be listed by BUILTIN\Users…` | The folder is broadly visible. The dump files themselves are private. | Optional: use a private folder. |
| `The audit log is not writable, so the operation was not performed: …` | Privileged operations are refused when they cannot be recorded. | Free the disk or fix permissions on the audit file, or point `PROCLENS_AUDIT_LOG` at a writable path. |
| `The password must be at least 12 characters long.` (exit 2) | Minimum length. | Use a longer password. |
| `The environment variable '<name>' is not set or is empty.` | `--password-env` names a variable that does not exist. | Set it in the same shell before running. |
| `A password prompt needs an interactive terminal; use the environment variable option instead.` | `--password-prompt` in a script. | Use `--password-env`. |
| `Encrypting the dump failed: …` (exit 5) | The machine key could not be used (see section 5). | Fix the key problem, or add `--password-env` and use a password. |
| `The temporary plaintext dump could not be removed.` | Antivirus or a backup tool holds the file. | Retry; delete the stray `*.dmp.plain.tmp` once released. |

### Administrative access

To dump another user's process every condition must hold; the error names the first one that does not:

- `To dump another user's process use --authorize-admin with --reason, from an elevated session.`: add both options.
- `Administrative authorization requires ProcLens to be started elevated (as Administrator). ProcLens never elevates itself.`: start the shell as Administrator.
- `Administrative dumps are disabled by machine policy (…)`: an administrator must create the policy file with
  `"AdminDumpsEnabled": true` (see `CLI_USAGE.md`). A missing or unreadable file also means disabled.
- `The operator '…' is not on the allow-list of the administrative dump policy.`: add the account to `AllowedOperators`.
- `A justification of at least 10 characters is required (--reason).`: give a meaningful reason; it is recorded.

## 4. Opening, verifying and exporting dumps

| You see | Why | What to do |
|---|---|---|
| `None of the available keys could unlock this file. The password may be wrong, or the file was protected with a key that belongs to another user or machine.` (exit 3) | Wrong or missing password; or the dump was made under a different Windows account or machine, or `master.key` is gone. | Supply the right `--password-env`. For a dump from another machine you need a password-protected export from there. |
| `Authentication failed at chunk <n>: the file is corrupt, truncated, extended or has been modified.` (exit 5) | The file was changed, damaged in transfer, or cut short. | Restore it from a good copy. `dump verify` shows which hash fails. Partial output is never kept. |
| `The file is not an encrypted ProcLens dump.` / `The file header is invalid (…)` / `The file is truncated.` | Wrong file, or damage. | Check the path and the extension (`.dmp.enc`). |
| `'<path>' already exists; existing files are never overwritten.` (exit 2) | ProcLens never replaces files. | Choose a new file name. |
| `The decrypted data does not match the hash recorded when the dump was taken.` | The metadata and the data disagree. | Treat the file as untrusted. |
| `Stored file hash does NOT match the recorded value` | The file was modified after creation (or the `.json` belongs to another file). | Same. |
| `No metadata file: recorded hashes cannot be checked.` | The `.json` next to the dump is missing. | Keep the dump and its `.json` together. |
| A debugger will not open `.dmp.enc` | It is encrypted. | `proclens dump decrypt`, then open the result; delete it afterwards. |

## 5. Keys

- **"The machine key is gone."** Dumps protected only by the machine/user key cannot be opened if `master.key`
  (`%LOCALAPPDATA%\ProcLens\keys`) is deleted, or your Windows profile or password state is reset so DPAPI cannot
  unprotect it. There is no recovery by design. Prevent it: back up that folder with your profile, and keep important
  dumps as password-protected exports (`dump export`).
- **Moving dumps to another computer.** Create `dump export --password-env …` first. The export carries only the
  password slot.
- **Rotating.** Re-export with a new password. There is no master-key rotation tool yet.

## 6. Audit log

| You see | Why | What to do |
|---|---|---|
| `Audit log TAMPERED or damaged at line <n>: The record's content does not match its hash (it was modified).` | A record was edited. | Preserve the file and investigate; lines before `n` are intact. |
| `…does not follow the previous one (a record was removed, inserted or reordered).` | A record was deleted, inserted or moved. | As above. |
| `…is not a valid audit record.` | Garbage in the file. | As above. |
| `Audit log intact` but you suspect missing recent entries | Removal from the **end** cannot be detected from the file alone. | Ship the log elsewhere regularly, or note the last hash. |
| Appends are slow or fail with a sharing error | Several ProcLens processes write the same file; each waits up to about ten seconds. | Retry; avoid putting the log on a slow network share. |

### Policy and output paths

| You see | Why | What to do |
|---|---|---|
| `Status: IGNORED` / `Ignored because it is owned by …` / `…can modify it` / `…can add, replace or delete files in its folder` / `…writable by group or others` (`proclens policy show`) | The policy file could have been written by an ordinary user, so ProcLens does not believe it. Defaults apply. | Create it protected, from an elevated shell: see `SECURITY.md`, "Protecting the policy file". Check with `proclens policy show`. |
| `Administrative dumps are disabled by machine policy (… ignored: …)` | Same cause, seen from a dump request. | Same. |
| `Audit log FAILED the anchor check: The anchor hash is not in the log…` (exit 5) | The hash you saved is not in the log: it was rewritten before that point, or cut short. | Treat the log as untrustworthy from the last good anchor; compare with copies. |
| `A colon is only allowed in the drive letter; 'name:stream' would write into an NTFS alternate data stream.` / `'CON' is a reserved Windows device name.` / `Device paths are not accepted.` (exit 2) | The `--output` path would not create a normal file. | Use an ordinary file path. |
| `The source does not match the hash recorded when the dump was taken, so it was not exported.` / `Authentication failed at chunk …` during `dump export` | The dump was damaged or modified; export checks it first. | Do not use the file; restore a good copy. |
| `Unexpected error (<Type>): <message>` (exit 70) | A bug in ProcLens. | Rerun with `PROCLENS_DEBUG=1` and report it (without any dump, password or key). |
| A command-line mistake prints only on stderr | By design, so stdout stays clean for `--json`. | Read the message and run `proclens <command> --help`. |

## 7. Retention (`dump prune`)

| You see | Why | What to do |
|---|---|---|
| "Dry run: nothing was deleted" | Default behaviour. | Add `--delete` once the list looks right. |
| `skipped … not valid ProcLens metadata, so the dump is not managed.` / `…describes a different file…` / `…the dump it describes is missing.` / `symbolic links are never followed or removed.` | Only files with matching ProcLens metadata are ever removed. | Remove other files yourself. |
| `FAILED … The file could not be removed (it may be in use).` | Something holds the file open. | Close it and run again; its metadata is kept so it stays managed. |
| Nothing selected although dumps are old | The age comes from the **recorded** creation time, 30 days by default, or your policy. | Check `Policy:` in the output; pass `--older-than-days`. |
| `Retention limits cannot be negative.` (exit 2) | Option value. | Use 0 or more. |

## 8. Reports

| You see | Why | What to do |
|---|---|---|
| `Process <id> was not found or has exited.` (exit 1) and no file | Nothing to report on. | Check the id. |
| Section status **Access denied** and an entry under *Errors* | The OS refused for that process. | Expected for other users' processes. |
| Section status **Not supported** | The platform cannot provide it. It is not an error. | None. |
| Section status **Not collected**: *A CPU rate needs at least two samples* | `--samples 1`, or the section was switched off. | Use `--samples 2` or more. |
| `…already exists; existing files are never overwritten.` (exit 2) | `--output` points at an existing file. | Pick a new name. |
| Warning `LIMITED_ACCESS`, `HIGH_CPU`, `MEMORY_GROWTH`, `UNENCRYPTED_DUMP`, `PROCESS_EXITED`, `THREAD_STATE_UNKNOWN` | Rules documented in `ARCHITECTURE.md`/`CLI_USAGE.md`. | They describe the process, not a ProcLens fault. |

## 9. Installing and running a release package

| You see | Why | What to do |
|---|---|---|
| macOS: "proclens cannot be opened because the developer cannot be verified" / killed on start | The package is not code-signed or notarised, and downloads carry a quarantine flag. | After verifying the checksum, run `xattr -d com.apple.quarantine proclens` (the installer script does this when asked). |
| Linux/macOS: `Permission denied` | Missing execute bit after extraction. | `chmod +x proclens`; the installer sets it. |
| `proclens: command not found` | The install folder is not on `PATH`. | Open a new shell after installing; the Windows installer edits the user `PATH`, the Unix one prints the line to add. |
| Windows SmartScreen warning | Unsigned binary. | Verify `SHA256SUMS.txt` against the file before running. |
| The very first run takes a fraction of a second longer (about 150 ms against 90 ms measured on Windows) | The operating system scans and caches a newly downloaded executable. ProcLens itself extracts nothing at run time. | Normal. |
| Package for another CPU architecture fails to start | Wrong archive (`x64` vs `arm64`). | Use the one matching `uname -m` / `systeminfo`. |

## 9b. Display language

| You see | Why | What to do |
|---|---|---|
| Chinese appears as `?` or boxes | The terminal font has no Chinese glyphs, or the terminal ignores UTF-8. | Use Windows Terminal or a UTF-8 terminal with a CJK font. ProcLens already writes UTF-8 for `zh-TW`. |
| Chinese appears as nonsense like `�䤣��` in a script or CI log | The consumer decodes the output with a legacy code page (for example cp950) instead of UTF-8. | Decode as UTF-8, or use `--json` / English for machine reading. |
| `Unknown language 'x'. Available: en, zh-TW.` | A bad value in `--lang` or `PROCLENS_LANG`. | Use `en` or `zh-TW`. English is used meanwhile. |
| Still English although you saved zh-TW | `PROCLENS_LANG` or `--lang` overrides the saved value, or the setting file was unreadable. | `proclens config show` says which source won. |
| Some words stay English | A few phrases of `System.CommandLine`'s help have no Chinese version. | Expected; everything ProcLens writes itself is translated. |

## 10. Reporting a problem

Include: `proclens system info`, the command and its exit code, the message, and (if relevant) the first lines of
`proclens audit show --last 5`. **Do not attach dumps, decrypted dumps, passwords, `master.key` or the policy file.**
Reports (`report generate`) contain process names, paths and owners; read them before sharing.
