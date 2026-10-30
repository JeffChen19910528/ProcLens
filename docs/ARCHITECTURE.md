# Architecture

ProcLens is a .NET 10 command-line toolkit. It follows Clean Architecture with one rule that shapes everything:
**everything specific to an operating system lives behind an interface in its own project, and the core knows
nothing about any of them.**

## 1. Projects and dependencies

```
                         ProcLens.Cli          composition root: the only project that sees every other
                  ┌─────────┬──────┴───────┬──────────────────┐
                  ▼         ▼              ▼                  ▼
      ProcLens.Infrastructure    ProcLens.Application     ProcLens.Platform.{Windows,Linux,MacOS}
        (files, audit, reports)   (use cases)              (all OS-specific code)
                  │                    │                          │
                  └────────────┬───────┴──────────────────────────┘
                               ▼
                         ProcLens.Core            models, interfaces, pure rules; depends on nothing
```

| Project | Responsibility |
|---|---|
| `ProcLens.Core` | Models (`ProcessInfo`, `Measurement<T>`, `InspectionResult<T>`, `MemoryStats`, …), the provider interfaces, the capability registry, and every rule that is pure logic: CPU-rate maths, monitor analysis, authorization policy, retention planning, the audit hash chain, the minidump reader, the dump cipher, report model and analyzer. No OS calls except portable file helpers. |
| `ProcLens.Application` | Use cases that orchestrate providers: `ProcessQueryService`, `ProcessMonitor`, `DumpService`, `DumpVaultService`, `DumpRetentionService`, `ReportService`. Each is exposed as an interface (`Abstractions/UseCases.cs`) and callers depend on that. Knows capabilities, not platforms. |
| `ProcLens.Infrastructure` | Things that touch the local environment but not a particular OS API: the file-based audit log, the operator context, the report renderers (JSON, HTML, TXT). |
| `ProcLens.Platform.Windows` | `WindowsProcessProvider` (toolhelp snapshot, process/thread/module APIs, token owner), `WindowsProcessDumpProvider` (DbgHelp), `DpapiMachineKeySource`. All P/Invoke lives here, behind `SafeHandle`s. |
| `ProcLens.Platform.Linux` | `LinuxProcessProvider` reading `/proc` through pure parsers (`ProcParser`); a dump provider that only says "not supported". |
| `ProcLens.Platform.MacOS` | `MacOSProcessProvider` over managed APIs; modules declared unsupported; a not-supported dump provider. |
| `ProcLens.Cli` | One `ICliCommand` class per leaf command (`Commands/<group>/`), the console and exit-code seam (`Hosting/`), the display-language machinery (`Localization/`), the per-platform DI modules it picks from, and `ServiceRegistration`, the composition root. |

Dependencies point inward only. `Core` has no project or OS dependencies; platform projects reference `Core` and nothing
else, so they cannot reach each other or the use cases. Tests exist per layer (`tests/ProcLens.*.Tests`).

## 2. The provider model

Two interfaces separate "read information about processes" from "make dumps":

- `IPlatformProcessProvider`: `GetProcessesAsync`, `GetProcessInfoAsync`, `GetMemoryUsageAsync`, `GetThreadsAsync`,
  `GetModulesAsync` and `GetCapabilities()`.
- `IProcessDumpProvider`: `IsSupported` and `CreateDumpAsync`. On Linux and macOS it returns an explicit
  `PlatformNotSupported` result and never touches `gcore`, `ptrace` or anything similar.

`ServiceRegistration` chooses implementations from `PlatformDetector`. Unknown platforms fail at start-up.

### Capabilities

Every provider declares what it genuinely supports (`Capability.ProcessInspection`, `CpuMonitoring`,
`MemoryMonitoring`, `ThreadInspection`, `ModuleInspection`, `ProcessDump`). `CapabilityRegistry` combines those
declarations; anything nobody declares is *not supported* by default. Use cases ask the registry before acting, the CLI
prints it, and reports include it. Nothing is advertised on a platform that lacks it.

### Honest data: `Measurement<T>` and `InspectionResult<T>`

A value the platform cannot give is **not zero**.

- `Measurement<T>` carries `IsAvailable`, the value and a reason. A CPU reading is unavailable until two samples exist;
  Linux has no private-memory figure; an unreadable owner stays unreadable. Renderers print `n/a (reason)`.
- `InspectionResult<T>` carries a status (`Available`, `NotSupported`, `AccessDenied`, `ProcessNotFound`,
  `ArchitectureMismatch`) so a missing list is never confused with an empty one.

## 3. Measuring CPU

Providers report only **cumulative CPU time**. `CpuUsageCalculator` (Core) turns two readings into a rate:
`delta CPU time / elapsed wall time`, then divides by logical processors for the whole-machine figure. It returns
nothing, instead of a wrong number, if the elapsed time is zero, a reading is missing, the counter went backwards, or the
process's start time differs between readings (the id was reused). `MonitorAnalyzer` keeps the running average and
raises `HIGH_CPU` and `MEMORY_GROWTH` warnings; `ProcessMonitor` drives sampling and stops if the process exits or is
replaced.

## 4. Dump creation (Windows)

```
CLI  dump create
 └─ DumpService                       audit must be writable, or refuse
     ├─ look up the target's pid + start time
     └─ WindowsProcessDumpProvider
         1  validate request, output directory, password strength
         2  target exists?
         3  IDumpAuthorizer  (owner, or explicit administrative authorization)
         4  OpenProcess with QUERY_INFORMATION | VM_READ only      (handle pins the process)
         5  wait on the single DbgHelp gate
         6  re-verify identity from the handle (start time)
         7  create the file private + new → MiniDumpWriteDump via IMiniDumpWriter
         8  verify: non-empty, minidump structure, DbgHelp can read it
         9  encrypt (chunked AES-GCM), decrypt again and compare, wipe the plaintext
        10  hash, write metadata
     └─ audit record (success, refusal and failure alike)
```

Design decisions worth knowing:

- **One gate for DbgHelp.** DbgHelp is not thread-safe; a static semaphore serialises every call, across instances.
- **`IMiniDumpWriter` is the only seam to the native call.** Tests substitute fakes to provoke failures, cancellation
  and concurrency; the real implementation also installs the DbgHelp cancel callback.
- **A failed or cancelled dump leaves nothing behind**: partial dump, temporary plaintext and metadata are removed.
- **Authorization is separate from OS permission.** `AdminAwareDumpAuthorizer` decides first; the OS still has the last
  word. Neither elevates anything.
- **Identity is checked against a handle, not a pid.** The start time the caller saw must match the opened process.

## 5. Security building blocks

| Building block | Where | Notes |
|---|---|---|
| `DumpCipher`, `IKekSource` | Core | Streaming AES-256-GCM with key slots. Machine slot (`DpapiMachineKeySource`, Windows) and password slot (`PasswordKekSource`). Format in `DUMP_FORMAT.md`. |
| `SecretText` | Core | Holds passwords without any member that can leak through formatting or serialisation. |
| `RestrictedFile` | Core | Owner-only files and directories (ACL on Windows, 0600/0700 elsewhere), create-new semantics, best-effort secure delete. |
| `AuditChain`, `FileAuditLog` | Core / Infrastructure | Append-only JSONL with a hash chain; exclusive lock for concurrent writers. |
| `AdminDumpPolicy`, `RetentionPolicy` | Core | Parsed from one machine policy file; failures fall back to the safe default. |
| `RetentionPlanner` | Core | Pure function that decides what falls outside a retention policy. |

Decisions and limits are recorded in `SECURITY.md`.

## 6. Reports

`ReportService` fills a `DiagnosticReport` of nine fixed sections, each with a `SectionStatus` and a reason.
`ReportAnalyzer` (pure) derives warnings and collection errors. `ReportViewBuilder` produces one neutral view that
both the text and HTML renderers print, so they cannot disagree; it also sanitises every string that came from the
inspected process. JSON serialises the model directly. All output uses LF line endings on every platform.

### Display language

English and Traditional Chinese (`zh-TW`) are built in. The language is chosen once, before the command tree is built
(descriptions are translated as the tree is made): `--lang`, then `PROCLENS_LANG`, then the saved setting
(`config set-language`), then English. The operating system's language is deliberately ignored so the same command
prints the same thing on every machine.

- The English wording in the code is the catalog key (`Localization/Catalogs/zh-TW.json`). A missing entry shows
  English, never a blank, and is recorded so tests can catch it. Lower-layer sentences (error reasons, warnings) are
  translated by matching them against catalog templates whose `{0}` parts are wildcards; templates with little fixed
  text are not used for matching so they cannot swallow unrelated messages.
- **Never translated:** `--json` output, the audit log, dump metadata files, exit codes, option names, and the report
  JSON. Only text meant for a person changes. Tests check that JSON is byte-identical across languages and that no
  Chinese reaches the audit trail.
- Reports: `ReportViewBuilder` takes an `ITextLocalizer` (defined in Core, so Infrastructure does not depend on the CLI).
  Section ids (HTML anchors) stay English. Text tables are padded by terminal width (`TextWidth`), because Chinese
  characters take two columns.
- `System.CommandLine`'s own help and parse errors follow `CurrentUICulture` through its `zh-Hant` satellite; a few of
  its phrases have no Chinese version, and those stay English.
- With `zh-TW`, `Program` switches the console to UTF-8 for the run and restores it afterwards. English output is
  untouched.

## 7. Cross-cutting rules

- **Async** at I/O boundaries; blocking native calls are wrapped in `Task.Run`.
- **No unsafe code.** Native interop uses `SafeHandle` and `DllImport` marshalling; DbgHelp is loaded from System32 only.
- **Dependency injection** via `Microsoft.Extensions.DependencyInjection`. `ServiceRegistration` is the only place that
  knows every project, and it does not construct a platform type itself: each platform project exposes one module
  (`AddWindowsPlatform`, `AddLinuxPlatform`, `AddMacOSPlatform`) that registers its own providers. Commands receive
  interfaces through their constructors; nothing looks services up by concrete type.
- **Output goes through `ICliConsole`**, never `Console`: results to stdout, problems to stderr, one place to capture
  in tests and one place to translate. **Exit codes** are defined once in `ExitCodes`, including the mapping from each
  result status to a code.
- **Dump finalisation is platform-neutral.** `DumpFinalizer` (Core) turns the raw file a platform provider wrote into
  the final artefact: hash, encrypt, prove it decrypts, wipe the plaintext, write metadata, and clean up only the files
  it created. The Windows provider only writes the raw dump with DbgHelp.
- **Logging** goes to standard error and only warnings and above, so `--json` output stays clean. ProcLens logs no
  process data; the audit trail is the record of privileged actions.
- **Time and clocks** are injected (`TimeProvider`) where behaviour depends on them.
- **Exit codes** are part of the contract and covered by tests (see `CLI_USAGE.md`).

## 8. Extending ProcLens

**A new process-information capability**

1. Add a member to `Capability` (Core) and, if needed, a method on `IPlatformProcessProvider`.
2. Implement it in each platform project. Where a platform cannot, return `NotSupported`/`Unavailable` with a reason;
   do not return an empty list or zero.
3. Declare it in that provider's `GetCapabilities()` only when it works; add the use case to `Application` and guard it
   with the registry.
4. Add the command in `ProcLens.Cli`, the section/field in the report model and `ReportViewBuilder`, and tests at each
   layer, including a live test per platform.

**A new command**: add one class implementing `ICliCommand` under `Commands/<group>/`, taking the use-case interfaces and
`ICliConsole` it needs in its constructor. `AddCliCommands` finds it by reflection; `CliCommands.BuildRoot` places it
under its `Group`. A new *group* also needs a line in the group table in `CliCommands`. Write every user-facing string
as `console.T("English text {0}", arg)` and add the Chinese to `Localization/Catalogs/zh-TW.json`; the localization
tests fail if a scenario shows a sentence the catalog lacks.

**A new platform** needs an `IPlatformProcessProvider`, an `IProcessDumpProvider` (a not-supported one is fine), a
`PlatformKind`, a DI module for the platform project, and a case in `ServiceRegistration`. Nothing else changes.

**A new report format**: implement `IReportRenderer` over `ReportViewBuilder.Build(...)` and add it to
`ReportRenderers.All`.

## 9. Testing strategy

| Layer | Approach |
|---|---|
| Pure logic (Core) | Unit tests with no I/O: cipher tampering, audit chain, retention planning, authorization matrix, report analysis, CPU maths. |
| Parsers | `/proc` and `maps` parsers are tested with literal text and with a fake `/proc` tree. |
| Use cases (Application) | Scripted fake providers, in-memory audit log, real temporary directories. |
| Platform providers | Live tests against real processes, skipped on other operating systems. |
| Native boundary | Fake `IMiniDumpWriter` for failure paths; real dumps validated by DbgHelp and by an independent parser. |
| CLI | In-process runs (`CliApplication.RunAsync`) with the audit log, keys and policy redirected to a temp directory. |

Tests that need a real OS are marked per platform and skip elsewhere. `PROCLENS_HEAVY_TESTS=1` enables one test that
dumps a ~700 MB process to exercise the native cancel callback.
