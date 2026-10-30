# ProcLens security decisions
**English** | [繁體中文](zh-TW/SECURITY.md)

A review of these decisions, with the defects it found and fixed, is in `SECURITY_REVIEW.md`.

This records decisions that exist for security reasons, so they are not "simplified" away later. It describes what
is implemented today; section 8 lists what is **not** done yet and section 9 the known limits.

## 1. Process dumps (Windows only)

A dump is a copy of a process's memory. It can contain passwords, tokens, session data, encryption keys and
personal information. Everything below follows from that.

### Who may be dumped
- Processes the operator **owns** are allowed. Processes owned by someone else are refused unless the
  administrative authorization in section 4 succeeds. If the owner cannot be determined the request is **denied**
  (fail closed); for example `audiodg` exposes a start time but not an owner to a non-elevated user.
- The check runs before the target is opened with memory-read rights.
- ProcLens never elevates, never enables `SeDebugPrivilege`, never bypasses Protected Process Light, never injects
  code. If Windows denies access the result is `AccessDenied` with a message saying so.
- ProcLens refuses to dump **itself** (`MiniDumpWriteDump` from inside the target is unreliable).

### Rights requested
- Exactly `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ` (`WindowsProcessDumpProvider.RequiredAccess`) for every dump
  type. Never `PROCESS_ALL_ACCESS`.
- `MiniDumpWithHandleData` is deliberately not used. It needs more rights and records the names of kernel objects
  the target has open.

### Process identity
- The caller supplies the start time it observed. The provider opens the process **first** (a held handle pins the
  process object, so the PID cannot be recycled underneath it), then compares `GetProcessTimes` from that handle
  with the expected value. It is re-checked after waiting for the DbgHelp gate, because the wait can be long.

### Output location and files
- Files are created with `FileMode.CreateNew` (never overwrite), `FileShare.None`, and an ACL with **one** entry
  for the current user and inheritance removed (mode 0600 on Unix). Administrators and SYSTEM are intentionally
  not listed; an administrator can still take ownership, which Windows audits.
- A directory that ProcLens **creates** is private to the current user. For an existing directory ProcLens does not
  change its permissions; it warns when Everyone, Users or Authenticated Users can list it (the files stay private,
  but names reveal process names and ids).
- File names are generated; the caller only supplies a directory, so a name cannot escape it. The directory is
  refused if it is a file, a device path (`\\.\`), or a symbolic link / junction.
- Any failure or cancellation removes the partial dump, the temporary plaintext and the metadata.

### Native code
- DbgHelp is imported with `DllImportSearchPath.System32`, so a planted `dbghelp.dll` is never loaded.
- All DbgHelp calls go through one static `SemaphoreSlim` (DbgHelp is not thread-safe), across provider instances.
- Process, token and snapshot handles are `SafeHandle`s.

## 2. Encryption at rest

Dumps are **encrypted by default** (`--no-encrypt` opts out and is reported as "NOT protected").

- **Cipher:** chunked AES-256-GCM (`PLENC001`), because dumps can be many gigabytes and the one-shot AES-GCM API
  cannot stream. Each 1 MiB chunk is authenticated with associated data = fixed header + chunk index + "final" flag,
  and a nonce from the file's random prefix and the index. Reordering, dropping, duplicating, truncating (even at a
  chunk boundary), extending, or moving a chunk to another file fails authentication.
- **Keys:** every file gets a random 256-bit data key. It is wrapped in one or more **key slots**; the data key is
  never stored unwrapped.
  - *Machine/user slot:* wrapped by a per-user master key protected with **DPAPI (CurrentUser)**. Only the same
    Windows user on the same machine can recover it. The master key never encrypts dump contents directly.
  - *Password slot:* PBKDF2-HMAC-SHA256 (600,000 iterations, random salt). Minimum 12 characters. Files that ask for
    fewer than 10,000 or more than 3,000,000 iterations are refused (trivially weak / CPU denial of service).
- **Passwords are never taken from command-line arguments** (they would appear in process listings and shell
  history): use `--password-env <VARIABLE>` or the hidden `--password-prompt`. Internally they live in `SecretText`,
  which has no accessor that can leak through `ToString`, logging or JSON, and is wiped on dispose.
- **Temporary plaintext:** MiniDumpWriteDump needs a real file, so the raw dump exists briefly as
  `*.dmp.plain.tmp`. It is created private, verified, encrypted, the encrypted copy is **decrypted again and
  hash-compared**, and only then is the plaintext overwritten with zeros and deleted. If it cannot be removed the
  operation fails. A crash can leave this temporary file behind (private to you); delete it.
- **Export:** `dump export` re-wraps the data key under a password only. The data is not re-encrypted, the original
  is untouched, and the machine key is deliberately not carried over, so the copy can leave this machine.
- **Decrypt:** `dump decrypt` writes a new plaintext file (never overwriting) and says so. Output is only produced
  chunk by chunk after each chunk verifies, and is deleted on any failure.
- Losing the DPAPI master key (new profile, reinstalled Windows) makes dumps protected only by it unreadable. Export
  important dumps with a password, or add `--password-env` when creating them.

## 3. Audit trail

- JSON Lines, append only, default `%LOCALAPPDATA%\ProcLens\audit\audit.jsonl` (override: `PROCLENS_AUDIT_LOG`),
  created private to the current user.
- Every `dump create`, `dump decrypt`, `dump export` and `dump verify` is recorded, including refusals and
  failures: operator, elevation, machine, operation, target pid/name/start time/owner, dump type, location,
  authorization (allowed, which policy, why), outcome, error, stored-file SHA256, whether encrypted, and the
  administrative justification.
- **Never recorded:** passwords, key material, memory contents. There are no fields for them; free text is
  length-limited and stripped of control characters so a value cannot start a second record.
- **Tamper evidence (and its limit):** each record stores the hash of the previous one and its own hash.
  `proclens audit verify` detects a single edited, inserted, deleted or reordered record and reports the first broken
  line. **The hashes are not keyed**, so someone who can write the file can rebuild the whole chain consistently, and
  cutting records off the end is invisible. The defence is an **anchor**: `audit verify` prints the head hash; keep it
  somewhere the log's writer cannot change (a ticket, another host) and later run `audit verify --anchor <hash>`, which
  fails if that record is gone or anything before it was rewritten.
- **Fail closed:** if the audit log cannot be written, privileged operations are **refused before they start**. If the
  record cannot be written after the fact, the result carries a visible warning.
- Appends take an exclusive lock with jittered retries, so several ProcLens processes cannot fork the chain.

## 4. Administrative authorization

Dumping a process owned by another user needs **all** of:
1. `--authorize-admin` with `--reason "<justification>"` on that command (the reason is audited);
2. ProcLens already running **elevated**: it never elevates itself;
3. a machine policy that enables it: `%ProgramData%\ProcLens\policy.json` (override: `PROCLENS_POLICY`),
   `{ "AdminDumpsEnabled": true, "AllowedOperators": ["DOMAIN\\user"], "RequireReason": true, "MinReasonLength": 10 }`;
4. the operator being on `AllowedOperators`, if that list is present.

A missing, unreadable or invalid policy means **disabled**. The OS still decides what an elevated token may open, and
Protected Process Light is not bypassed. The policy is an accountability control, not a boundary against
administrators, who can edit it.

### Protecting the policy file

On a default Windows install **any ordinary user can create files under `C:\ProgramData`**, so a policy file at the
default location could be planted by someone else (the security review demonstrated this; see `SECURITY_REVIEW.md`, F1).
ProcLens therefore believes the file at the default location only if an ordinary user could not have written it:

- **Windows:** the file *and* its folder are owned by Administrators, SYSTEM or TrustedInstaller, and no other account
  holds rights to modify, replace, delete or take over either of them (creating sub-folders is fine).
- **Unix:** neither the file nor its folder is writable by group or others (the default location is under `/etc`).

Otherwise the file is **ignored** (administrative dumps stay disabled, retention uses its defaults) and
`proclens policy show` says why. A path chosen with `PROCLENS_POLICY` is trusted: it is the operator's own decision.

To create a protected policy on Windows, from an **elevated** PowerShell (these commands encode the rule above but the
author has not been able to run them elevated; check the result with `proclens policy show` and report a mismatch):

```powershell
$dir = Join-Path $env:ProgramData 'ProcLens'
New-Item -ItemType Directory -Force $dir | Out-Null
icacls $dir /setowner "Administrators" | Out-Null
icacls $dir /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "Users:(OI)(CI)RX" | Out-Null
Set-Content -Path (Join-Path $dir 'policy.json') -Value '{ "AdminDumpsEnabled": true }'
icacls (Join-Path $dir 'policy.json') /setowner "Administrators" | Out-Null
proclens policy show
```

## 5. Retention and deletion

Nothing is ever deleted automatically or in the background. `proclens dump prune --directory <dir>` applies the
retention policy and is a **dry run** unless `--delete` is given.

- **Policy:** `{ "Retention": { "MaxAgeDays": 30, "MaxCount": 10, "MaxTotalMegabytes": 2048 } }` in the machine policy
  file (the same file as section 4). Rules combine: a dump is selected if any rule selects it. Defaults are 30 days
  and no other limit; a missing, unreadable or invalid section gives the defaults, never "keep" or "delete" everything.
  `--older-than-days`, `--keep` and `--max-size-mb` override it for one run.
- **Only ProcLens's own dumps:** a dump is managed only if its metadata file parses and names that same file.
  Unrelated files, bare `.dmp` files, mismatched or corrupt metadata, subdirectories and symbolic links are never
  removed (they are listed as skipped). A directory that is a symbolic link is refused.
- **Age** is taken from the recorded creation time. A dump dated in the future (clock skew) is not removed by age.
- **Interrupted dumps:** leftover `*.dmp.plain.tmp` files (raw memory from a crash) older than one hour are removed;
  younger ones may belong to a dump in progress and are kept. Only ProcLens-generated names match.
- **How files are removed:** unencrypted dumps are overwritten with zeros, then deleted; encrypted dumps are just
  deleted, because their content is protected by keys. If the file cannot be removed (for instance it is in use) it is
  reported as failed and its metadata is kept so it stays managed.
- **Accountability:** every removal writes a `dump.delete` audit record (reason, target, hash, outcome). If the audit
  trail cannot be written, `--delete` refuses to run.
- **Limits of deletion:** overwriting does not guarantee erasure on SSDs, copy-on-write or journaling file systems,
  snapshots or backups. For encrypted dumps the practical guarantee is key destruction: copies elsewhere remain
  readable to anyone holding a key or password. Run `dump prune` from the OS task scheduler if you want it regularly.

## 6. Diagnostic reports

`proclens report generate` (JSON, HTML or TXT) collects process metadata only: identity, owner, paths, CPU and memory
figures, thread and module listings, capabilities and, on Windows, metadata of this process's dumps.

- **No memory contents and no command line** are ever included; the report model has no field for them.
- **Hostile process data cannot attack the reader.** Names, paths, owners and thread names come from the inspected
  process and may be crafted. Every such string has control characters, line breaks and Unicode direction overrides
  removed, so a TXT report cannot be made to contain forged lines or sections. HTML additionally HTML-encodes every
  value, contains no scripts, event handlers or external resources, and declares `Content-Security-Policy:
  default-src 'none'`.
- **`--output` writes a new file readable only by the current user** (0600 / single-ACE ACL) and never overwrites.
- Reports reveal what the OS lets you inspect (process names, paths, owners). Treat them as internal documents.
- A report lists dumps on disk and warns (`UNENCRYPTED_DUMP`) when one of this process is not encrypted.
- Generating a report is inspection, not a privileged action, so it is not written to the audit trail.

## 7. Process inspection
- No stealth inspection, no privilege changes. Information that cannot be read is reported as unavailable with a
  reason, never as zero, and per-process inspections return an explicit status (not found / not supported /
  access denied / architecture mismatch).
- Inspecting (not dumping) other users' processes is limited only by what the OS lets an ordinary user read.

## 8. Not done yet
- There is no scheduler: retention runs only when `dump prune --delete` is invoked.
- Audit-log **tail truncation** and total deletion cannot be detected from the file alone; use an external anchor
  (`audit verify --anchor`) or ship the records to a separate system.
- **The raw dump touches the disk** (briefly, private, wiped) before it is encrypted. Streaming it through DbgHelp's
  I/O callbacks into the encryptor would avoid that entirely; it has not been attempted.
- The master key has no rotation or backup tooling.
- No code signing, no package lock files; see `SECURITY_REVIEW.md`, section 5, for the full list of accepted and open risks.
- Linux/macOS have no dump functionality by design; their file-permission handling (0600) is covered by tests on
  Linux only, and **nothing here has been run on macOS**.

## 9. Known limits
- Code running as the same user can read the same processes and the same key; ownership rules and DPAPI limit
  accidents and other users, they are not a sandbox against malware in your session.
- Plain overwrite does not securely erase data on SSDs, journaling / copy-on-write file systems, or snapshotted
  volumes. Treat deletion as best effort.
- SHA256 values detect corruption and tampering; they are not access control. AES-GCM authentication protects
  encrypted dumps; unencrypted dumps (`--no-encrypt`) rely on file permissions alone.
