# ProcLens 命令列參考

[English](../CLI_USAGE.md) | **繁體中文**

```
proclens <群組> <指令> [選項]
```

每個指令都接受 `-h`、`--help` 與 `-?`。`--version` 會印出版本。列出或描述事物的指令接受 `--json`；使用時，標準輸出只含 JSON，
警告與進度則輸出到標準錯誤，因此輸出可以安全地接管線。

## 顯示語言

ProcLens 支援英文與繁體中文（`zh-TW`）。語言依下列順序決定：

1. 任何指令上的 `--lang en|zh-TW`（也可用 `--lang=zh-TW`；`zh`、`zh-Hant` 與 `zh_tw` 都會被接受為 `zh-TW`）
2. 環境變數 `PROCLENS_LANG`
3. 已儲存的設定：`proclens config set-language zh-TW`（`auto` 會移除它）
4. 英文

作業系統的語言**不會**被使用，因此指令碼在每台機器上的行為都相同。未知的語言會在 stderr 印出 `Unknown language '<x>'. Available: en, zh-TW.`，並繼續使用英文。

會改變的：說明、表格標題、標籤、訊息，以及 TXT 與 HTML 報告。永遠不變的：`--json` 輸出、報告的 JSON、稽核記錄、傾印中繼資料檔、選項名稱與結束代碼，
因此指令碼在任何語言下都可以依賴它們。`System.CommandLine` 自己說明畫面中的少數詞句（例如 `Description:` 標題）沒有中文版本，會維持英文。

使用 `zh-TW` 時，ProcLens 在該次執行會把 UTF-8 寫到主控台，結束後再將主控台還原。若文字看起來是亂碼，請見 `TROUBLESHOOTING.md`。

```
proclens config show                 # 目前使用哪種語言，以及原因
proclens config set-language zh-TW   # 記住它（儲存在 %LOCALAPPDATA%\ProcLens\config.json，或 ~/.local/share/ProcLens/config.json）
proclens config set-language auto    # 忘記它
proclens --lang zh-TW process list --sort memory
```

---

僅存在於 Windows 的功能（`dump` 底下的一切），在其他平台上會印出以下內容並以結束代碼 **2** 結束：

```
Process dump is not supported on this operating system.
Supported platform: Windows.
```

執行 `proclens system capabilities` 可查看目前平台支援什麼。矩陣見 `PLATFORM_SUPPORT.md`。

---

## `process`：檢視執行中的處理程序

可在 Windows、Linux 與 macOS 上使用。檢視絕不提升權限；作業系統不讓您的帳戶讀取的內容，會以附原因的 `n/a` 回報，絕不當成零。

### `process list`

```
proclens process list [--sort pid|name|memory|cpu] [--name <文字>] [--json]
```

| 選項 | 意義 |
|---|---|
| `--sort` | 依處理程序識別碼（預設）、名稱、工作集或 CPU 使用率排序。值無法取得的處理程序排在最後。 |
| `--name` | 只列出名稱包含該文字的處理程序（不分大小寫）。 |

`--sort cpu` 會對每個處理程序間隔一秒取樣兩次，因此約需一秒。其他排序方式的 CPU 欄位為空（`n/a`），因為單一讀數無法算出使用率。

### `process inspect`

```
proclens process inspect --pid <pid> [--json]
```

顯示識別碼、名稱、執行檔路徑、父識別碼、擁有者、啟動時間、工作集、執行緒數、累計 CPU 時間，以及 `Access` 值：`Full`、`Partial`（部分欄位無法讀取）或 `Denied`。

### `process monitor`

```
proclens process monitor --pid <pid> [--interval <秒>] [--count <n>] [--json]
```

持續對一個處理程序取樣，直到它結束、取得 `--count` 筆樣本，或您按下 Ctrl+C（結束代碼 130）。

| 欄位 | 意義 |
|---|---|
| `CPU%` | 佔**整機**（所有邏輯處理器）的百分比。 |
| `CORE%` | 佔**單一核心**的百分比；多執行緒的處理程序會超過 100。 |
| `AVG%` | `CPU%` 的移動平均。 |
| `WORKSET` `PRIVATE` `VIRTUAL` `PEAK` | 四種記憶體量測；在某平台上任何一項都可能是 `n/a`。 |

第一筆樣本沒有 CPU 值（使用率需要兩筆讀數）。警告會印在觸發它的樣本下方：`HIGH_CPU`（佔整機 80% 以上）與 `MEMORY_GROWTH`
（工作集在連續五筆樣本中每次都上升，且總計至少上升 10%）。`--json` 每行印出一個精簡的 JSON 物件。

若監視期間處理程序識別碼被另一個處理程序重複使用，監視會停止，而不是把兩者混在一起。

### `process threads`、`process modules`

```
proclens process threads --pid <pid> [--json]
proclens process modules --pid <pid> [--json]
```

執行緒狀態名稱經過正規化（`Running`、`Ready`、`Waiting`、`Sleeping`、`Stopped`、`Terminated`、`Unknown`）；平台自己的細節
（Windows 的等待原因、Linux 的狀態字母）顯示在旁邊。`modules` 列出基底位址、大小、名稱與路徑。當有東西缺失時，這些指令會回傳下列五種結果之一，而不是空清單：

| 結果 | 結束代碼 |
|---|---|
| 已列出 | 0 |
| 找不到處理程序 | 1 |
| 此平台不支援（例如 macOS 上的模組） | 2 |
| 存取被拒 | 3 |
| 架構不相符（32 位元的 ProcLens 無法讀取 64 位元處理程序的模組） | 4 |

---

## `system`

```
proclens system info [--json]
proclens system capabilities [--json]
```

`info` 顯示平台、作業系統、架構、邏輯處理器數、.NET 執行階段與 ProcLens 版本。`capabilities` 將六項功能（處理程序檢視、CPU 監視、
記憶體監視、執行緒檢視、模組檢視、處理程序傾印）列為 `Supported` 或 `Not Supported`，後者附上原因。

---

## `dump`：處理程序傾印（僅限 Windows）

傾印是處理程序記憶體的副本，可能包含密碼、權杖與金鑰。**傾印預設加密。** 使用這些指令前請先閱讀 `SECURITY.md`；檔案格式見 `DUMP_FORMAT.md`。

### `dump create`

```
proclens dump create --pid <pid> --output <目錄> [--type mini|heap|full]
                     [--no-encrypt] [--password-env <變數> | --password-prompt]
                     [--authorize-admin --reason "<文字>"] [--progress] [--json]
```

| 選項 | 意義 |
|---|---|
| `--type` | `mini`（預設）：執行緒、模組、堆疊，檔案小。`heap`：加上私有讀寫記憶體（受控與原生堆積）。`full`：所有已提交的記憶體，檔案大。 |
| `--output` | 一個**目錄**。檔名自動產生。若不存在會建立（僅您可存取）；若它是檔案、裝置路徑或符號連結／連接點，則會被拒絕。 |
| `--no-encrypt` | 儲存原始 `.dmp`。結果會顯示「NOT protected」。 |
| `--password-env`、`--password-prompt` | 同時允許以密碼開啟傾印（至少 12 個字元）。絕不接受以命令列引數傳入密碼。 |
| `--authorize-admin`、`--reason` | 只有在傾印**屬於其他使用者**的處理程序時才需要；見「系統管理員存取」。 |
| `--progress` | 每秒約四次將已寫入的位元組數印到標準錯誤。 |

結果：`<名稱>_<pid>_<類型>_<UTC 時間>_<識別碼>.dmp.enc`（使用 `--no-encrypt` 時為 `.dmp`），旁邊另有 `<同名>.json` 中繼資料。
您只能傾印自己擁有的處理程序；ProcLens 絕不自行提升權限、絕不繞過處理程序保護，並拒絕傾印它自己。

| 結束代碼 | 意義 |
|---|---|
| 0 | 已建立傾印 |
| 1 | 找不到處理程序，或它在傾印開始前已結束／其識別碼被重複使用 |
| 2 | 此作業系統不支援，或要求無效（輸出目錄不正確、密碼太弱或缺少） |
| 3 | 未獲授權，或 Windows 拒絕存取 |
| 4 | 架構不相符（32 位元 ProcLens、64 位元目標） |
| 5 | 失敗（訊息會說明原因；不會留下不完整的檔案） |
| 130 | 以 Ctrl+C 取消 |

命令列語法錯誤（未知的選項、`--type everything`）也會以 1 結束並印出用法。

### 系統管理員存取

傾印其他使用者的處理程序，需要**全部**符合下列條件；否則要求會被拒絕並記錄：

1. 指令上帶有 `--authorize-admin --reason "..."`（理由會寫入稽核記錄）；
2. ProcLens 已經以提升權限執行（從提升權限的殼層啟動；它不會自行提升）；
3. 有啟用此功能的本機原則（見「原則檔」）；
4. 若原則有允許清單，您的帳戶必須在其中。

### `dump info`

```
proclens dump info --file <路徑> [--json]
```

對未加密的傾印：有效性、傾印類型、建立時間、目標架構與 Windows 組建、執行緒與模組數量。對加密的傾印：加密方案，以及可由什麼解鎖
（加密時不顯示結構）。兩者都會將檔案的 SHA256 與記錄值比對。正常時結束代碼 0，無效或被修改時為 5，檔案不存在為 1，無法讀取為 3。

### `dump verify`

```
proclens dump verify --file <路徑> [--password-env <變數> | --password-prompt] [--json]
```

端到端檢查加密的傾印，驗證每個區塊並與兩個記錄的雜湊比對，且不寫出任何明文。結束代碼 0 表示已驗證，3 表示無法用現有金鑰解鎖，5 表示被修改或損毀。

### `dump decrypt`

```
proclens dump decrypt --file <x.dmp.enc> --output <新檔案> [--password-env <變數> | --password-prompt]
```

寫出可供偵錯器開啟的一般 `.dmp`。輸出檔必須不存在（絕不覆寫任何東西）、僅您可存取，且任何失敗都會將其刪除。**它是處理程序記憶體的未保護副本**；用完請刪除。
結束代碼 0 成功，1 找不到，2 不是加密傾印或輸出檔已存在，3 無法解鎖，5 被修改或失敗。

### `dump export`

```
proclens dump export --file <x.dmp.enc> --output <新檔案>
                     (--password-env <變數> | --password-prompt)
                     [--current-password-env <變數> | --current-password-prompt]
```

建立僅以新密碼（至少 12 個字元）保護的副本，使其可以離開這台機器。資料不會重新加密；電腦金鑰刻意不帶入。若來源本身受密碼保護，請使用
`--current-password-*`。原檔不受影響。結束代碼與 `decrypt` 相同。

### `dump prune`

```
proclens dump prune --directory <目錄> [--older-than-days <n>] [--keep <n>] [--max-size-mb <n>] [--delete] [--json]
```

對一個存放 ProcLens 傾印的目錄套用保留原則。**除非指定 `--delete`，否則只是試執行。** 傾印若比限制舊、超出最新的 `--keep` 份，
或超出 `--max-size-mb` 的額度（優先保留最新的），就會被選取。選項可針對單次執行覆寫原則檔；預設為 30 天。只有具備有效 ProcLens 中繼資料的傾印會被處理；
其他檔案、子目錄與符號連結會被略過並列出。被中斷的傾印所殘留、超過一小時的暫存明文（`*.dmp.plain.tmp`）也會被移除。每次刪除都會被稽核；
若稽核記錄無法寫入，就不會刪除任何東西。結束代碼 0 成功，1 目錄有問題，2 選項無效，5 至少有一次刪除失敗。不會自動執行任何東西：如有需要，請用工作排程器或 cron 排程。

---

## `audit`

```
proclens audit show [--last <n>] [--json]
proclens audit verify [--anchor <hash>] [--json]
```

`show` 印出最近的記錄（預設 20 筆；`--json` 每行印出一筆記錄）。`verify` 重新計算雜湊鏈，完整時結束代碼為 0；若有任何記錄被編輯、插入、刪除或重新排序，
則為 5 並指出第一個損壞的行。成功時會印出**檔頭雜湊**。

雜湊沒有使用金鑰：能寫入記錄的人可以重建整條鏈，而從最新端截掉記錄，單純的 `verify` 是看不出來的。要抓出這種情形，請把檔頭雜湊存放在記錄寫入者無法更改之處
（工單、另一台主機），之後執行 `audit verify --anchor <hash>`；若該記錄已消失或它之前的任何內容被改寫，驗證就會失敗（結束代碼 5）。錨點之後的新記錄沒有問題。

記錄會涵蓋 `dump.create`、`dump.decrypt`、`dump.export`、`dump.verify` 與 `dump.delete`，包括拒絕的情形。

---

## `policy`

```
proclens policy show [--json]
```

顯示 ProcLens 目前套用的本機原則：它查看的檔案、**是否採信該檔案**，以及生效的系統管理員傾印與保留設定。當原則似乎沒有作用時請使用它：
一般使用者可能寫入過的原則檔會被忽略（見下方「原則檔」），而 `policy show` 會確切說明原因。

---

## `report`

```
proclens report generate --pid <pid> [--format txt|html|json] [--output <新檔案>]
                         [--samples <n>] [--interval <秒>] [--dumps-dir <目錄>]
                         [--no-threads] [--no-modules]
```

| 選項 | 意義 |
|---|---|
| `--format` | `txt`（預設）、`html`（自成一體，無腳本或外部資源）或 `json`。 |
| `--output` | 寫入新檔案而非標準輸出。絕不覆寫；檔案僅您可存取。 |
| `--samples`、`--interval` | CPU／記憶體樣本數（1–120，預設 3）與樣本間隔秒數（0.1 以上，預設 1）。計算 CPU 使用率至少需要 2 筆樣本。 |
| `--dumps-dir` | 同時列出該目錄中記錄的此處理程序傾印檔（Windows）。 |
| `--no-threads`、`--no-modules` | 略過這些章節（它們就會是「Not collected」）。 |

每份報告在每個平台上都有相同的九個章節、相同順序：System、Process、Cpu、Memory、Threads、Modules、Dumps、Warnings、Errors。每個章節都有狀態：

| 狀態 | 意義 |
|---|---|
| Available | 已收集。其中顯示為 `n/a (原因)` 的值是無法取得的；單純的 `0` 則是真正的零。 |
| Not supported | 此平台無法提供。不算錯誤。 |
| Access denied | 可以提供，但作業系統拒絕了這個處理程序。列於 Errors 之下。 |
| Process not found / Architecture mismatch / Failed | 列於 Errors 之下。 |
| Not collected | 未被要求，或樣本不足。 |

可能出現的警告（在 `Warnings` 章節；每個在 JSON 中也以 `Code` 與 `Message` 呈現）：

| 代碼 | 觸發時機 |
|---|---|
| `LIMITED_ACCESS` | 只能讀取處理程序的部分詳細資料（`Access: Partial` 或 `Denied`）。 |
| `HIGH_CPU` | 取樣期間的平均 CPU 佔整機 80% 以上。 |
| `MEMORY_GROWTH` | 在五筆樣本的視窗內，工作集每筆都上升，且總計至少上升 10%。 |
| `PROCESS_EXITED` | 處理程序在取得所有要求的樣本前就結束了。 |
| `THREAD_STATE_UNKNOWN` | 平台不回報執行緒狀態（macOS），因此各狀態的數量沒有參考價值。 |
| `UNENCRYPTED_DUMP` | 磁碟上此處理程序的傾印（透過 `--dumps-dir` 找到）沒有加密。 |

產生報告時結束代碼為 0（即使有些章節被拒絕：報告會註明），處理程序不存在為 1（不寫出任何東西），`--output` 已存在為 2，檔案無法寫入為 5。
報告只含處理程序中繼資料，不含記憶體內容，也不含命令列。

---

## `config`

```
proclens config show [--json]
proclens config set-language <en|zh-TW|auto>
```

`show` 印出生效的語言、它的來源（`--lang`、`PROCLENS_LANG`、`config` 或 `default`）以及可用的語言；`--json` 以英文提供 `Language`、`Source` 與 `Available`。
`set-language` 會以不可分割的方式寫入每使用者的設定檔，並以您所選的語言確認。未知的語言為結束代碼 2，檔案無法寫入為 5。損毀或過大的設定檔會被忽略（使用英文）；
它絕不會讓指令中止。

此設定與安全無關，因此不同於本機原則，它存放在使用者自己的設定檔中。

---

## 檔案、環境變數與原則

| 項目 | 預設位置 | 覆寫方式 |
|---|---|---|
| 稽核記錄 | Windows：`%LOCALAPPDATA%\ProcLens\audit\audit.jsonl`；其他：`~/.local/share/ProcLens/audit/audit.jsonl` | `PROCLENS_AUDIT_LOG` |
| 主金鑰（受 DPAPI 保護） | `%LOCALAPPDATA%\ProcLens\keys\master.key` | `PROCLENS_KEY_DIR`（目錄） |
| 本機原則 | Windows：`%ProgramData%\ProcLens\policy.json`；其他：`/etc/proclens/policy.json` | `PROCLENS_POLICY` |
| 顯示語言設定 | Windows：`%LOCALAPPDATA%\ProcLens\config.json`；其他：`~/.local/share/ProcLens/config.json` | `PROCLENS_CONFIG`（檔案）；`PROCLENS_LANG` 與 `--lang` 優先於已儲存的值 |

解除安裝 ProcLens 不會刪除上述任何一項。請保留 `master.key`：僅受它保護的傾印，沒有它就無法開啟。需要長期保存的傾印，請使用 `dump export`（密碼）。

### 原則檔

一個 JSON 檔同時包含系統管理員傾印原則與保留原則。允許註解與結尾逗號。檔案缺失、無法讀取、無效或過大（超過 1 MiB）時：系統管理員傾印**停用**，保留使用預設值。

**ProcLens 只有在一般使用者不可能寫入時，才會採信預設位置的檔案。** 在預設的 Windows 安裝上，任何使用者都可以在 `C:\ProgramData` 底下建立檔案，
因此那裡未受保護的原則可能被他人植入。在 Windows 上，檔案及其資料夾的擁有者必須是 Administrators、SYSTEM 或 TrustedInstaller，且不可被其他任何人修改；
在 Unix 上，兩者都不可被群組或其他人寫入。未通過此檢查的原則會被**忽略**，而 `proclens policy show` 會告訴您原因。以 `PROCLENS_POLICY` 指定的檔案則一律被信任
（那是您自己的明確選擇）。如何建立受保護的原則，見 `SECURITY.md` 的「保護原則檔」。

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

| 索引鍵 | 預設值 | 意義 |
|---|---|---|
| `AdminDumpsEnabled` | `false` | 是否允許 `--authorize-admin` 傾印。 |
| `AllowedOperators` | 無（任何提升權限者） | 若存在，只有這些帳戶（不分大小寫）。 |
| `RequireReason`、`MinReasonLength` | `true`、`10` | 要求至少這個長度的理由。 |
| `Retention.MaxAgeDays` | `30` | `dump prune` 會選取比此更舊的傾印。`0` 表示全選。 |
| `Retention.MaxCount` | 無 | 最多保留這麼多份傾印。 |
| `Retention.MaxTotalMegabytes` | 無 | 保留容納得下的最新傾印。 |

### 密碼

絕不要把密碼放在命令列上。請將它匯出到環境變數並傳入其**名稱**，或讓 ProcLens 提示輸入（輸入會隱藏，建立時會問兩次）：

```powershell
$env:DUMP_PASSWORD = Read-Host -AsSecureString | ConvertFrom-SecureString -AsPlainText   # PowerShell 7
proclens dump create --pid 1234 --type full --output .\dumps --password-env DUMP_PASSWORD
```

---

## 結束代碼摘要

| 代碼 | 一般意義 |
|---|---|
| 0 | 成功 |
| 1 | 找不到目標（處理程序、檔案、目錄），或命令列語法錯誤 |
| 2 | 此平台不支援，或要求無效 |
| 3 | 存取被拒、未獲授權，或無法解鎖 |
| 4 | 架構不相符（32 位元 ProcLens、64 位元目標） |
| 5 | 失敗、被修改、損毀或遭竄改 |
| 70 | 未預期的內部錯誤（程式錯誤）：stderr 一行訊息；設定 `PROCLENS_DEBUG=1` 可看完整細節 |
| 130 | 以 Ctrl+C 取消 |

這些代碼是穩定的，並有測試涵蓋；訊息的確切措辭則不保證。

**資料流。** 結果輸出到標準輸出；警告、進度、錯誤與用法錯誤輸出到標準錯誤。命令列錯誤只在 stderr 印出問題與提示，stdout 保持空白，
因此讀取 `--json` 輸出的指令碼不會被破壞。

**輸出路徑。** ProcLens 被要求寫入的每個路徑（`dump create`、`dump decrypt`、`dump export` 與 `report generate` 的 `--output`）若含有控制字元、
指名 Windows 裝置（`CON`、`NUL`、`COM1` 等，包含帶副檔名的，或 `\\.\` 路徑），或在磁碟機代號以外的位置含有冒號（會寫入隱藏的 NTFS 替代資料流），都會被拒絕。
既有的檔案絕不會被覆寫。
