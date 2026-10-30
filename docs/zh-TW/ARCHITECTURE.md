# 架構

[English](../ARCHITECTURE.md) | **繁體中文**

ProcLens 是以 .NET 10 撰寫的命令列工具組，採用 Clean Architecture，並遵守一條形塑一切的規則：
**所有與作業系統相關的程式碼，都放在各自專案內的介面之後，核心對它們一無所知。**

## 1. 專案與相依關係

```
                         ProcLens.Cli          組合根：唯一看得到所有其他專案的專案
                  ┌─────────┬──────┴───────┬──────────────────┐
                  ▼         ▼              ▼                  ▼
      ProcLens.Infrastructure    ProcLens.Application     ProcLens.Platform.{Windows,Linux,MacOS}
        （檔案、稽核、報告）      （使用案例）              （所有作業系統專屬程式碼）
                  │                    │                          │
                  └────────────┬───────┴──────────────────────────┘
                               ▼
                         ProcLens.Core            模型、介面、純邏輯規則；不依賴任何專案
```

| 專案 | 職責 |
|---|---|
| `ProcLens.Core` | 模型（`ProcessInfo`、`Measurement<T>`、`InspectionResult<T>`、`MemoryStats` 等）、提供者介面、功能登錄表，以及所有純邏輯規則：CPU 使用率計算、監視分析、授權原則、保留規劃、稽核雜湊鏈、minidump 讀取器、傾印加密器、報告模型與分析器。除了可攜式的檔案輔助功能外，不呼叫任何作業系統 API。 |
| `ProcLens.Application` | 協調各提供者的使用案例：`ProcessQueryService`、`ProcessMonitor`、`DumpService`、`DumpVaultService`、`DumpRetentionService`、`ReportService`。每個使用案例都以介面公開（`Abstractions/UseCases.cs`），呼叫端依賴的是介面。只認識功能（capability），不認識平台。 |
| `ProcLens.Infrastructure` | 接觸本機環境、但不屬於特定作業系統 API 的部分：以檔案為基礎的稽核記錄、操作者內容、報告轉譯器（JSON、HTML、TXT）。 |
| `ProcLens.Platform.Windows` | `WindowsProcessProvider`（toolhelp 快照、處理程序／執行緒／模組 API、權杖擁有者）、`WindowsProcessDumpProvider`（DbgHelp）、`DpapiMachineKeySource`。所有 P/Invoke 都在這裡，並置於 `SafeHandle` 之後。 |
| `ProcLens.Platform.Linux` | `LinuxProcessProvider` 透過純解析器（`ProcParser`）讀取 `/proc`；傾印提供者只回報「不支援」。 |
| `ProcLens.Platform.MacOS` | 以受控 API 實作的 `MacOSProcessProvider`；模組宣告為不支援；以及一個回報不支援的傾印提供者。 |
| `ProcLens.Cli` | 每個葉節點指令一個 `ICliCommand` 類別（`Commands/<群組>/`）、主控台與結束代碼的接縫（`Hosting/`）、顯示語言機制（`Localization/`）、它所挑選的各平台 DI 模組，以及作為組合根的 `ServiceRegistration`。 |

相依方向永遠向內。`Core` 沒有任何專案或作業系統相依；平台專案只參考 `Core`，因此它們無法互相存取，也無法觸及使用案例。
每一層都有對應的測試（`tests/ProcLens.*.Tests`）。

## 2. 提供者模型

兩個介面把「讀取處理程序資訊」與「建立傾印」分開：

- `IPlatformProcessProvider`：`GetProcessesAsync`、`GetProcessInfoAsync`、`GetMemoryUsageAsync`、`GetThreadsAsync`、
  `GetModulesAsync` 與 `GetCapabilities()`。
- `IProcessDumpProvider`：`IsSupported` 與 `CreateDumpAsync`。在 Linux 與 macOS 上，它會回傳明確的
  `PlatformNotSupported` 結果，絕不碰 `gcore`、`ptrace` 或任何類似的機制。

`ServiceRegistration` 依據 `PlatformDetector` 選擇實作。未知的平台在啟動時就會失敗。

### 功能（Capabilities）

每個提供者都宣告它真正支援的功能（`Capability.ProcessInspection`、`CpuMonitoring`、`MemoryMonitoring`、
`ThreadInspection`、`ModuleInspection`、`ProcessDump`）。`CapabilityRegistry` 彙整這些宣告；沒有人宣告的功能，預設就是
*不支援*。使用案例在行動前會先詢問登錄表，CLI 會把它印出來，報告也會包含它。缺少某功能的平台上，絕不會宣稱支援。

### 誠實的資料：`Measurement<T>` 與 `InspectionResult<T>`

平台無法提供的值，**不等於零**。

- `Measurement<T>` 帶有 `IsAvailable`、數值與原因。在取得兩筆樣本之前，CPU 讀數是無法取得的；Linux 沒有私有記憶體的數字；
  讀不到的擁有者就是讀不到。轉譯器會印出 `n/a (原因)`。
- `InspectionResult<T>` 帶有狀態（`Available`、`NotSupported`、`AccessDenied`、`ProcessNotFound`、
  `ArchitectureMismatch`），因此「清單不存在」永遠不會與「清單是空的」混為一談。

## 3. 測量 CPU

提供者只回報**累計 CPU 時間**。`CpuUsageCalculator`（Core）把兩次讀數轉成使用率：
`CPU 時間差 / 實際經過時間`，再除以邏輯處理器數得到整機數字。在下列情況它會回傳「無結果」，而不是錯誤的數字：經過時間為零、
缺少讀數、計數器倒退，或兩次讀數之間處理程序的啟動時間不同（識別碼被重複使用）。`MonitorAnalyzer` 維護移動平均並發出
`HIGH_CPU` 與 `MEMORY_GROWTH` 警告；`ProcessMonitor` 負責驅動取樣，並在處理程序結束或被取代時停止。

## 4. 建立傾印（Windows）

```
CLI  dump create
 └─ DumpService                       稽核記錄必須可寫入，否則拒絕
     ├─ 查出目標的 pid + 啟動時間
     └─ WindowsProcessDumpProvider
         1  驗證要求、輸出目錄、密碼強度
         2  目標是否存在？
         3  IDumpAuthorizer  （擁有者，或明確的系統管理員授權）
         4  OpenProcess，僅要求 QUERY_INFORMATION | VM_READ      （控制代碼會鎖定該處理程序）
         5  等待唯一的 DbgHelp 閘門
         6  從控制代碼重新驗證身分（啟動時間）
         7  以私有 + 新建方式建立檔案 → 經由 IMiniDumpWriter 呼叫 MiniDumpWriteDump
     └─ DumpFinalizer （Core，各平台相同）
         8  驗證：非空、minidump 結構、DbgHelp 可讀取
         9  加密（分塊 AES-GCM）、再解密並比對、抹除明文
        10  計算雜湊、寫入中繼資料
     └─ 稽核記錄（成功、拒絕與失敗一律記錄）
```

值得了解的設計決定：

- **DbgHelp 只有一個閘門。** DbgHelp 不是執行緒安全的；一個靜態 semaphore 會把所有呼叫（跨執行個體）序列化。
- **`IMiniDumpWriter` 是通往原生呼叫的唯一接縫。** 測試會以假物件取代它，來引發失敗、取消與並行情境；真正的實作還會安裝
  DbgHelp 的取消回呼。
- **失敗或取消的傾印不會留下任何東西**：不完整的傾印、暫存的明文與中繼資料都會被移除。
- **授權與作業系統權限是分開的。** 由 `AdminAwareDumpAuthorizer` 先決定；作業系統仍有最終決定權。兩者都不會提升任何權限。
- **身分是對照控制代碼檢查，而不是對照 pid。** 呼叫端所見到的啟動時間，必須與實際開啟的處理程序相符。

## 5. 安全建構元件

| 建構元件 | 位置 | 說明 |
|---|---|---|
| `DumpCipher`、`IKekSource` | Core | 具金鑰槽的串流式 AES-256-GCM。電腦金鑰槽（`DpapiMachineKeySource`，Windows）與密碼金鑰槽（`PasswordKekSource`）。格式見 `DUMP_FORMAT.md`。 |
| `SecretText` | Core | 保存密碼，且沒有任何成員會經由格式化或序列化而洩漏。 |
| `RestrictedFile` | Core | 僅限擁有者的檔案與目錄（Windows 用 ACL，其他平台用 0600/0700）、新建語意、盡力而為的安全刪除。 |
| `AuditChain`、`FileAuditLog` | Core / Infrastructure | 附雜湊鏈、僅能附加的 JSONL；並行寫入者使用獨占鎖。 |
| `AdminDumpPolicy`、`RetentionPolicy` | Core | 從同一份本機原則檔解析；失敗時退回安全的預設值。 |
| `RetentionPlanner` | Core | 純函式，決定哪些項目超出保留原則。 |

各項決定與限制記錄於 `SECURITY.md`。

## 6. 報告

`ReportService` 填寫一份由九個固定章節組成的 `DiagnosticReport`，每個章節都有 `SectionStatus` 與原因。
`ReportAnalyzer`（純邏輯）推導出警告與收集錯誤。`ReportViewBuilder` 產生一份中立的檢視，文字與 HTML 轉譯器都印出同一份，
因此兩者不可能不一致；它同時會清理所有來自被檢視處理程序的字串。JSON 則直接序列化模型。所有輸出在各平台都使用 LF 換行。

### 顯示語言

英文與繁體中文（`zh-TW`）為內建。語言只會選擇一次，且在建立指令樹之前（因為指令樹建立時就會翻譯描述）：先看 `--lang`，
再看 `PROCLENS_LANG`，然後是已儲存的設定（`config set-language`），最後是英文。作業系統的語言被刻意忽略，讓同一個指令在每台機器上
印出相同的內容。

- 程式碼中的英文原文就是目錄的索引鍵（`Localization/Catalogs/zh-TW.json`）。缺少項目時顯示英文（絕不會是空白或索引鍵名稱），
  並會被記錄下來，讓測試能抓出來。來自較低層的句子（錯誤原因、警告）會拿去比對目錄中的範本來翻譯，範本中的 `{0}` 部分當作萬用字元；
  固定文字太少的範本不會用於比對，以免吞掉不相關的訊息。
- **永不翻譯：** `--json` 輸出、稽核記錄、傾印中繼資料檔、結束代碼、選項名稱，以及報告 JSON。只有給人看的文字會變。
  測試會檢查 JSON 在不同語言下逐位元組相同，且稽核記錄中不會出現中文。
- 報告：`ReportViewBuilder` 接收一個 `ITextLocalizer`（定義在 Core，因此 Infrastructure 不必依賴 CLI）。章節識別碼（HTML 錨點）
  維持英文。文字表格以終端機寬度補白（`TextWidth`），因為中文字元佔兩欄。
- `System.CommandLine` 自己的說明與解析錯誤會透過它的 `zh-Hant` 附屬組件跟隨 `CurrentUICulture`；少數詞句沒有中文版本，會維持英文。
- 使用 `zh-TW` 時，`Program` 會在該次執行期間把主控台切換為 UTF-8，結束後還原。英文輸出完全不受影響。

## 7. 橫切規則

- 在 I/O 邊界使用 **非同步**；會阻塞的原生呼叫以 `Task.Run` 包裝。
- **不使用 unsafe 程式碼。** 原生互通使用 `SafeHandle` 與 `DllImport` 封送處理；DbgHelp 只從 System32 載入。
- **相依性注入** 使用 `Microsoft.Extensions.DependencyInjection`。`ServiceRegistration` 是唯一認識所有專案的地方，而且它自己不會建構
  任何平台型別：每個平台專案各自公開一個模組（`AddWindowsPlatform`、`AddLinuxPlatform`、`AddMacOSPlatform`）來登錄自己的提供者。
  指令透過建構式接收介面；沒有任何地方依具體型別去查找服務。
- **輸出一律經由 `ICliConsole`**，絕不直接用 `Console`：結果到 stdout、問題到 stderr，測試時只有一個地方可擷取，翻譯也只有一個地方。
  **結束代碼** 只在 `ExitCodes` 定義一次，包括從每種結果狀態對應到代碼的映射。
- **傾印的最後處理與平台無關。** `DumpFinalizer`（Core）把平台提供者寫出的原始檔案轉成最終成品：計算雜湊、加密、證明可解回、抹除明文、
  寫入中繼資料，並且只清理它自己建立的檔案。Windows 提供者只負責用 DbgHelp 寫出原始傾印。
- **記錄** 輸出到標準錯誤，且只有警告以上等級，讓 `--json` 輸出保持乾淨。ProcLens 不記錄任何處理程序資料；稽核記錄才是特權動作的紀錄。
- 行為相依於**時間與時鐘**之處，一律注入（`TimeProvider`）。
- **結束代碼** 屬於合約的一部分，並有測試涵蓋（見 `CLI_USAGE.md`）。

## 8. 擴充 ProcLens

**新的處理程序資訊功能**

1. 在 `Capability`（Core）加入一個成員，必要時在 `IPlatformProcessProvider` 加入方法。
2. 在每個平台專案中實作。某平台做不到時，回傳附原因的 `NotSupported`／`Unavailable`；不要回傳空清單或零。
3. 只有在確實可運作時，才在該提供者的 `GetCapabilities()` 中宣告；在 `Application` 加入使用案例，並以登錄表把關。
4. 在 `ProcLens.Cli` 加入指令，在報告模型與 `ReportViewBuilder` 加入章節／欄位，並在每一層加入測試，包括每個平台的即時測試。

**新的指令**：在 `Commands/<群組>/` 下新增一個實作 `ICliCommand` 的類別，在建構式接收所需的使用案例介面與 `ICliConsole`。
`AddCliCommands` 會以反射找到它；`CliCommands.BuildRoot` 依其 `Group` 放進指令樹。新增*群組*時，還需要在 `CliCommands` 的群組表中加一行。
每個給使用者看的字串都寫成 `console.T("English text {0}", arg)`，並把中文加入 `Localization/Catalogs/zh-TW.json`；
如果某個情境顯示了目錄中沒有的句子，在地化測試就會失敗。

**新的平台** 需要一個 `IPlatformProcessProvider`、一個 `IProcessDumpProvider`（回報不支援的也可以）、一個 `PlatformKind`、
該平台專案的 DI 模組，以及 `ServiceRegistration` 中的一個 case。其他地方都不用改。

**新的報告格式**：以 `ReportViewBuilder.Build(...)` 為基礎實作 `IReportRenderer`，並加入 `ReportRenderers.All`。

## 9. 測試策略

| 層 | 做法 |
|---|---|
| 純邏輯（Core） | 不含 I/O 的單元測試：加密竄改、稽核鏈、保留規劃、授權矩陣、報告分析、CPU 計算。 |
| 解析器 | `/proc` 與 `maps` 解析器以字面文字及假的 `/proc` 目錄樹測試。 |
| 使用案例（Application） | 腳本化的假提供者、記憶體內的稽核記錄、真正的暫存目錄。 |
| 平台提供者 | 針對真實處理程序的即時測試，在其他作業系統上略過。 |
| 原生邊界 | 以假的 `IMiniDumpWriter` 測試失敗路徑；真實傾印由 DbgHelp 與獨立的解析器驗證。 |
| CLI | 行程內執行（`CliApplication.RunAsync`），稽核記錄、金鑰與原則都導向暫存目錄。 |

需要真實作業系統的測試會依平台標記，並在其他平台上略過。設定 `PROCLENS_HEAVY_TESTS=1` 會啟用一個傾印約 700 MB 處理程序的測試，
用來驗證原生取消回呼。
