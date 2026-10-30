# Dump, metadata and audit formats
**English** | [繁體中文](zh-TW/DUMP_FORMAT.md)

This describes what ProcLens writes to disk, precisely enough to check a file by hand or to write a compatible reader.
All integers are little-endian unless stated. Behaviour is covered by tests; the encrypted container has a published
version marker (`PLENC001`) so it can evolve.

## 1. Files in a dump directory

For one dump ProcLens creates exactly two files in the directory you gave to `--output`:

| File | Content |
|---|---|
| `<stem>.dmp.enc` | The encrypted dump (default), **or** |
| `<stem>.dmp` | The raw Windows minidump (`--no-encrypt`) |
| `<stem>.dmp.enc.json` / `<stem>.dmp.json` | Metadata (section 4); the dump's file name plus `.json` |

`<stem>` is `<process name>_<pid>_<type>_<UTC yyyyMMddTHHmmssZ>_<8 hex>`; for example
`PING_32528_heap_20261030T042903Z_059e038d.dmp.enc`. The process name is reduced to letters, digits, `-`, `_` and `.`
and at most 40 characters. The name is generated, never supplied by the caller.

While an encrypted dump is being produced, the raw dump exists briefly as `<stem>.dmp.plain.tmp`. It is created
private, wiped and deleted once the encrypted copy has been decrypted again and compared. A crash can leave it behind;
`dump prune` removes such files after an hour.

All files are created new (never overwritten) and readable only by the current user.

## 2. The minidump (what a debugger opens)

ProcLens calls `MiniDumpWriteDump` from DbgHelp (loaded from System32 only). The three types map to these
`MINIDUMP_TYPE` flags:

| Type | Flags | Contains |
|---|---|---|
| `mini` | `WithThreadInfo` (0x1000) `WithUnloadedModules` (0x20) `WithProcessThreadData` (0x100) | Threads, stacks, module list, thread data. |
| `heap` | mini + `WithPrivateReadWriteMemory` (0x200) `WithDataSegs` (0x1) `WithFullMemoryInfo` (0x800) | Plus private read/write memory (heaps) and data segments. |
| `full` | mini + `WithFullMemory` (0x2) `WithFullMemoryInfo` (0x800) | All committed memory. |

`MiniDumpWithHandleData` is deliberately not used (it needs more process rights and records the names of open kernel
objects). The process is opened with `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ` only.

A valid dump starts with this header; `dump info` reports on it and rejects anything that does not fit:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | Signature `MDMP` (`0x504D444D`) |
| 4 | 4 | Version; the low 16 bits must be `0xA793` |
| 8 | 4 | Number of streams (1 to 4096 accepted) |
| 12 | 4 | Offset of the stream directory |
| 20 | 4 | Time stamp (Unix seconds) |
| 24 | 8 | `MINIDUMP_TYPE` flags used |

The stream directory has 12-byte entries (type, size, offset). ProcLens reads stream 7 (system info: architecture, OS
version and build), 3 (thread list) and 4 (module list); every offset is checked against the file length, so a
truncated file is reported as such. The dump type shown by `dump info` is inferred from the flags (full-memory flag →
`Full`, private-read/write flag → `Heap`, otherwise `Mini`).

After writing, ProcLens also asks DbgHelp itself (`MiniDumpReadDumpStream`) to locate the system-info and thread-list
streams, so the same library debuggers build on must be able to read the file.

## 3. The encrypted container (`PLENC001`)

Chunked AES-256-GCM. Dumps are far too large for the one-shot AES-GCM API, so the content is cut into chunks, each
authenticated on its own **and** bound to its place in the file.

```
offset  size   field
0       8      magic "PLENC001"
8       4      chunk size in bytes (ProcLens writes 1,048,576; readers accept 4,096 to 67,108,864)
12      16     file id (random)
28      4      nonce prefix (random)
32      4      slots length (1 to 4,096)
36      n      slots block
36+n    ...    chunks until end of file
```

The first 32 bytes are the **fixed header**. A real file begins:

```
00000000: 504c 454e 4330 3031 0000 1000 8607 61fc  PLENC001......a.
00000010: 9e26 27bc 2281 e957 b2d9 cea7 d4e8 0e0e  .&'."..W........
00000020: 4700 0000 0101 08f3 2e37 dcdc 1a74 3ce3  G........7...t<.
```

`0x00100000` is the chunk size; `0x47` = 71 is the slots length; the slots block starts `01` (one slot) `01` (machine
key) `08` (descriptor length).

### Key slots

A fresh random 256-bit **data key** is generated for every file. It never leaves the process unwrapped; the file holds
it wrapped once per slot (up to 8):

```
slots block:  count u8, then count × slot
slot:         type u8 | descriptor length u8 | descriptor | nonce[12] | wrapped key[32] | tag[16]
```

The data key is wrapped with AES-256-GCM under the slot's key-encryption key, with associated data
`fixed header ‖ type ‖ descriptor length ‖ descriptor`.

| Type | Key-encryption key | Descriptor |
|---|---|---|
| 1, machine/user | A random 256-bit master key stored DPAPI-protected (`CurrentUser`, entropy `"ProcLens master key v1"`) in `master.key` | 8 bytes: first 8 bytes of SHA256 of the master key (identifies which key) |
| 2, password | PBKDF2-HMAC-SHA256 of the UTF-8 password, 32 bytes out | 20 bytes: iterations `u32` (600,000 written; 10,000 to 3,000,000 accepted) ‖ salt[16] (random) |

Because the slots are **not** part of the chunk associated data, `dump export` can replace them (to a password-only
slot) without touching or re-encrypting the chunks.

### Chunks

Each chunk is `ciphertext ‖ tag[16]`. All chunks carry `chunk size` bytes of plaintext except the last, which carries
1 to `chunk size` bytes (an empty plaintext is one chunk of 0 bytes). For chunk `i` (starting at 0):

```
nonce = noncePrefix[4] ‖ i as u64 big-endian                       (12 bytes)
aad   = fixedHeader[32] ‖ i as u64 big-endian ‖ isFinal u8 (0 or 1)   (41 bytes)
```

`isFinal` is 1 only for the last chunk. Together with the file id in the fixed header this means a chunk cannot be
reordered, dropped, duplicated, truncated away (even exactly at a chunk boundary), appended after, or moved to another
file without authentication failing. Plaintext is released only after its own chunk verifies.

### Reading a file independently

1. Read the 32 + 4 bytes, then the slots block; reject out-of-range values.
2. Derive the key-encryption key of a slot you can serve; decrypt the 32-byte wrapped key.
3. Read `chunk size + 16` bytes at a time; the chunk that ends the file is final.
4. Decrypt each chunk with the nonce and associated data above.

## 4. Metadata (`<dump>.json`)

Written by ProcLens next to every dump; never contains memory contents or keys. Real example:

```json
{
  "DumpId": "a54c5426-af37-4b37-93b9-22549dd92397",
  "ProcessId": 32528,
  "ProcessName": "PING",
  "ProcessStartTime": "2026-10-30T12:29:02.1388349+08:00",
  "DumpType": "Heap",
  "DumpFilePath": "C:\\...\\dumps\\PING_32528_heap_20261030T042903Z_059e038d.dmp.enc",
  "FileSize": 668110,
  "CreationTime": "2026-10-30T04:29:04.0311765+00:00",
  "OperatingSystem": "Microsoft Windows 10.0.26300",
  "ApplicationVersion": "1.0.0",
  "CollectionStatus": "Succeeded",
  "ErrorDetails": null,
  "Sha256": "d8fd7189...5502eae",
  "PlaintextSha256": "95967783...fba148",
  "EncryptionScheme": "PLENC001/AES-256-GCM-chunked"
}
```

| Field | Meaning |
|---|---|
| `ProcessId`, `ProcessName`, `ProcessStartTime` | The identity of the dumped process; the start time distinguishes it from a later process that reuses the id. |
| `DumpType` | `Mini`, `Heap` or `Full`. |
| `FileSize` | Size of the stored file (the ciphertext when encrypted). |
| `CreationTime` | When ProcLens finished the dump (UTC). Retention uses this. |
| `Sha256` | SHA256 of the file **as stored**. |
| `PlaintextSha256` | SHA256 of the original minidump; equals `Sha256` when not encrypted. |
| `EncryptionScheme` | `null` for an unprotected dump. |
| `CollectionStatus` | `Succeeded` for any dump that exists (failed attempts leave no files). |

Hashes detect corruption and tampering. They are not encryption and not access control. A dump is *managed* (shown by
`report`, removable by `prune`) only if this file parses and `DumpFilePath` names the same file.

## 5. Audit log (`audit.jsonl`)

One JSON object per line, appended only, never rewritten. Real example (paths shortened):

```json
{
  "Id": "ebb7977c-ecd8-498a-9757-ec37a5e8c3e7",
  "Timestamp": "2026-10-30T04:29:04.0649291+00:00",
  "Operator": "DESKTOP-A2S5VV0\\owner",
  "OperatorElevated": false,
  "Machine": "DESKTOP-A2S5VV0",
  "Operation": "dump.create",
  "TargetPid": 32528,
  "TargetName": "PING",
  "TargetStartTime": "2026-10-30T12:29:02.1388349+08:00",
  "TargetOwner": "DESKTOP-A2S5VV0\\owner",
  "DumpType": "Heap",
  "Location": "C:\\...\\PING_32528_heap_20261030T042903Z_059e038d.dmp.enc",
  "Authorization": { "Allowed": true, "Policy": "owner", "Detail": "The operator owns the target process." },
  "Outcome": "Succeeded",
  "ErrorDetails": null,
  "FileSha256": "d8fd7189...5502eae",
  "Encrypted": true,
  "Justification": null,
  "Version": 1,
  "PrevHash": "0000000000000000000000000000000000000000000000000000000000000000",
  "Hash": "98900340...3d34582"
}
```

| Field | Meaning |
|---|---|
| `Operation` | `dump.create`, `dump.decrypt`, `dump.export`, `dump.verify` or `dump.delete`. |
| `Authorization.Policy` | Which rule decided: `owner`, `admin`, `key-holder` (decrypt/export/verify), `retention`, or `denied` / `n/a` when refused. |
| `Outcome` | `Succeeded`, `Denied`, `Failed` or `Cancelled`. Refusals and failures are recorded too. |
| `Justification` | The `--reason` for an administrative request, if any. |

There are no fields for passwords, keys or memory contents. Free text is limited to 500 characters and stripped of
control characters so a value cannot start a second record.

**Hash chain.** `PrevHash` of the first record is 64 zeros; afterwards it is the previous record's `Hash`. `Hash` is the
lower-case hex SHA256 of the UTF-8 JSON of the record with `Hash` set to `""` and `PrevHash` set, serialised by ProcLens
in the field order shown (enums as strings, compact, no indentation). `proclens audit verify` recomputes the chain.
Editing, inserting, deleting or reordering any record other than by truncating the end is detected.
