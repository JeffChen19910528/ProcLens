# 疑難排解

[English](../TROUBLESHOOTING.md) | **繁體中文**

請在下列表格中找出您看到的訊息（或症狀）。結束代碼列於 `CLI_USAGE.md`。這裡沒有任何做法要求您削弱保護機制：ProcLens 拒絕的地方，
解法是滿足它所指出的條件，而不是繞過。

> **關於訊息文字：** 「您看到的」欄位以英文原文列出訊息，因為這是程式碼中的原始用詞，也是搜尋時最穩定的關鍵字。使用 `--lang zh-TW`
> 時，您看到的是對應的中文訊息（對照 `src/ProcLens.Cli/Localization/Catalogs/zh-TW.json`）。結束代碼與狀態標籤在任何語言下都相同。

## 1. 先做的檢查

```
proclens system info
proclens system capabilities
proclens audit verify
```

`capabilities` 會告訴您這台機器到底能做什麼。如果某功能顯示 *Not Supported*，任何選項都無法啟用它。

## 2. 檢視處理程序

| 您看到 | 原因 | 該怎麼做 |
|---|---|---|
| `Process <id> was not found.` / `…was not found or has exited.`（結束代碼 1） | 沒有這個處理程序，或它已結束。系統會重複使用識別碼。 | 用 `proclens process list --name <文字>` 重新列出。 |
| 擁有者或執行檔顯示 `n/a (not readable)`；`Access: Partial` 或 `Denied` | 您的帳戶可能無法讀取該處理程序的詳細資料。 | 對系統與其他使用者的處理程序而言是預期行為。請以擁有者身分執行。 |
| 記憶體或執行緒的值出現 `Access denied or process exited.` | 同上，針對個別欄位。 | 同上；該欄位維持 `n/a`，絕不是 `0`。 |
| `CpuMonitoring is not available: …` | 平台不提供 CPU 取樣。 | 無需修正；見 `PLATFORM_SUPPORT.md`。 |
| `process inspect` 的 CPU 顯示 `n/a`，或 `monitor` 的第一筆樣本沒有 CPU | 使用率需要兩筆讀數。 | 使用 `process monitor` 或 `process list --sort cpu`。 |
| `Architecture mismatch: ProcLens is running as a 32-bit process and the target is 64-bit`（結束代碼 4） | 32 位元的 ProcLens 無法讀取 64 位元處理程序的模組。 | 使用 64 位元封裝。64 位元的 ProcLens 可以讀取 32 位元處理程序。 |
| `Access denied: Reading /proc/<pid>/maps requires ptrace-read permission…`（Linux） | 其他使用者的處理程序，或嚴格的 `ptrace_scope` / `hidepid`。 | 檢視您自己的處理程序；ProcLens 不會提升權限。 |
| 模組顯示 *Not supported*（macOS） | macOS 上沒有模組列舉。 | 預期行為；見 `PLATFORM_SUPPORT.md`。 |
| 私有記憶體 `n/a`（Linux） | `/proc` 沒有對應的值。 | 預期行為。 |
| 作業系統訊息以您的語言出現，例如 `存取被拒` | 作業系統的文字是原樣傳遞的。 | 狀態標籤與結束代碼是穩定的；作業系統的措辭則否。 |
| `monitor` 以 `Process <id> exited; monitoring stopped.` 結束 | 處理程序已結束，或其識別碼被另一個處理程序取得。 | 預期行為。 |

## 3. 建立傾印（Windows）

| 您看到 | 原因 | 該怎麼做 |
|---|---|---|
| `Process dump is not supported on this operating system. Supported platform: Windows.`（結束代碼 2） | Linux 與 macOS 依設計不建立傾印。 | 請使用 Windows。 |
| `The target process is owned by '…', not by the operator '…'. Only processes you own can be dumped.`（結束代碼 3） | 擁有者規則。 | 以擁有者身分執行，或見下方「系統管理員存取」。 |
| `The owner of the target process could not be determined, so the request was denied.`（結束代碼 3） | 受保護或系統處理程序，或您的帳戶可能無法開啟的處理程序。ProcLens 採失敗即封閉。 | 同上；許多系統處理程序無法以一般帳戶傾印。 |
| `Windows denied access to the process. ProcLens does not elevate privileges or bypass process protection.`（結束代碼 3） | Windows 拒絕了 `PROCESS_VM_READ`：受保護的處理程序，或較高完整性等級的處理程序。 | 以相同或更高的完整性等級執行 ProcLens。Protected Process Light 的目標完全無法傾印。 |
| `The process start time cannot be read, so its identity cannot be verified and no dump was taken.` | `System` 之類的系統處理程序。 | 無法傾印。 |
| `The process with this id is not the one that was inspected (its id was reused)…` | 處理程序已結束，而該識別碼現在屬於另一個處理程序。 | 重新執行指令。 |
| `The process exited before the dump started.` | 它在這期間結束了。 | 重試。 |
| `ProcLens cannot dump itself; dump it from another process.` | 從目標內部傾印並不可靠。 | 從另一個處理程序執行 ProcLens。 |
| `Part of the target's memory could not be read (ERROR_PARTIAL_COPY)…`（結束代碼 5） | 傾印途中它結束了，或其一部分受到保護。 | 重試；試試 `--type mini`。 |
| `ProcLens is running as a 32-bit process and the target is 64-bit; run the 64-bit build to dump it.`（結束代碼 4） | 位元數不同。 | 使用 64 位元封裝。 |
| `There is not enough disk space for the dump.` | `full` 傾印與處理程序的記憶體一樣大。 | 釋放空間、改用其他 `--output`，或使用 `--type heap`/`mini`。 |
| `'…' is a file, not a directory.` / `Device paths are not accepted…` / `…is a symbolic link or junction; choose a real directory…` / `The output directory is not usable: …`（結束代碼 2） | `--output` 需要真正的目錄。 | 給一個一般的目錄路徑。 |
| `Warning: The output directory can be listed by BUILTIN\Users…` | 該資料夾廣泛可見。傾印檔本身是私有的。 | 可選：使用私有資料夾。 |
| `The audit log is not writable, so the operation was not performed: …` | 無法被記錄的特權操作會被拒絕。 | 釋放磁碟或修正稽核檔的權限，或將 `PROCLENS_AUDIT_LOG` 指向可寫入的路徑。 |
| `The password must be at least 12 characters long.`（結束代碼 2） | 最小長度。 | 使用較長的密碼。 |
| `The environment variable '<name>' is not set or is empty.` | `--password-env` 指名的變數不存在。 | 在執行前於同一個殼層中設定它。 |
| `A password prompt needs an interactive terminal; use the environment variable option instead.` | 在指令碼中使用 `--password-prompt`。 | 改用 `--password-env`。 |
| `Encrypting the dump failed: …`（結束代碼 5） | 無法使用電腦金鑰（見第 5 節）。 | 修正金鑰問題，或加上 `--password-env` 並使用密碼。 |
| `The temporary plaintext dump could not be removed.` | 防毒軟體或備份工具持有該檔案。 | 重試；待釋放後刪除殘留的 `*.dmp.plain.tmp`。 |

### 系統管理員存取

要傾印其他使用者的處理程序，每個條件都必須成立；錯誤訊息會指出第一個不成立的條件：

- `To dump another user's process use --authorize-admin with --reason, from an elevated session.`：兩個選項都要加。
- `Administrative authorization requires ProcLens to be started elevated (as Administrator). ProcLens never elevates itself.`：以系統管理員身分啟動殼層。
- `Administrative dumps are disabled by machine policy (…)`：系統管理員必須建立含 `"AdminDumpsEnabled": true` 的原則檔
  （見 `CLI_USAGE.md`）。檔案缺失或無法讀取，也代表停用。
- `The operator '…' is not on the allow-list of the administrative dump policy.`：將該帳戶加入 `AllowedOperators`。
- `A justification of at least 10 characters is required (--reason).`：提供有意義的理由；它會被記錄。

## 4. 開啟、驗證與匯出傾印

| 您看到 | 原因 | 該怎麼做 |
|---|---|---|
| `None of the available keys could unlock this file. The password may be wrong, or the file was protected with a key that belongs to another user or machine.`（結束代碼 3） | 密碼錯誤或缺少；或傾印是在不同的 Windows 帳戶或機器下建立的，或 `master.key` 已遺失。 | 提供正確的 `--password-env`。對於來自另一台機器的傾印，您需要該處以密碼保護的匯出檔。 |
| `Authentication failed at chunk <n>: the file is corrupt, truncated, extended or has been modified.`（結束代碼 5） | 檔案被變更、傳輸中損毀，或被截短。 | 從良好的副本還原。`dump verify` 會顯示哪個雜湊失敗。不完整的輸出絕不會保留。 |
| `The file is not an encrypted ProcLens dump.` / `The file header is invalid (…)` / `The file is truncated.` | 檔案錯誤，或已損壞。 | 檢查路徑與副檔名（`.dmp.enc`）。 |
| `'<path>' already exists; existing files are never overwritten.`（結束代碼 2） | ProcLens 絕不取代檔案。 | 選擇新的檔名。 |
| `The decrypted data does not match the hash recorded when the dump was taken.` | 中繼資料與資料不一致。 | 將該檔案視為不受信任。 |
| `Stored file hash does NOT match the recorded value` | 檔案在建立後被修改（或 `.json` 屬於另一個檔案）。 | 同上。 |
| `No metadata file: recorded hashes cannot be checked.` | 傾印旁邊的 `.json` 不見了。 | 請將傾印與其 `.json` 放在一起。 |
| 偵錯器無法開啟 `.dmp.enc` | 它是加密的。 | 使用 `proclens dump decrypt`，再開啟結果；用完後刪除。 |

## 5. 金鑰

- **「電腦金鑰不見了。」** 僅受電腦／使用者金鑰保護的傾印，在 `master.key`（`%LOCALAPPDATA%\ProcLens\keys`）被刪除，或您的 Windows
  使用者設定檔或密碼狀態被重設而使 DPAPI 無法解除保護時，就無法開啟。依設計沒有復原的方法。預防方式：隨著您的設定檔一起備份該資料夾，
  並將重要傾印保存為以密碼保護的匯出檔（`dump export`）。
- **將傾印搬到另一台電腦。** 先建立 `dump export --password-env …`。匯出檔只帶有密碼槽。
- **輪替。** 以新密碼重新匯出。目前還沒有主金鑰輪替工具。

## 6. 稽核記錄

| 您看到 | 原因 | 該怎麼做 |
|---|---|---|
| `Audit log TAMPERED or damaged at line <n>: The record's content does not match its hash (it was modified).` | 有記錄被編輯。 | 保存該檔案並調查；第 `n` 行之前的內容完整。 |
| `…does not follow the previous one (a record was removed, inserted or reordered).` | 有記錄被刪除、插入或移動。 | 同上。 |
| `…is not a valid audit record.` | 檔案中有亂碼。 | 同上。 |
| `Audit log intact`，但您懷疑最近的項目遺失 | 從**結尾**移除是無法單憑檔案偵測的。 | 定期將記錄送往他處，或記下最後的雜湊值。 |
| 附加很慢或因共用錯誤而失敗 | 多個 ProcLens 處理程序寫入同一個檔案；每個最多等待約十秒。 | 重試；避免把記錄放在緩慢的網路共用上。 |

### 原則與輸出路徑

| 您看到 | 原因 | 該怎麼做 |
|---|---|---|
| `Status: IGNORED` / `Ignored because it is owned by …` / `…can modify it` / `…can add, replace or delete files in its folder` / `…writable by group or others`（`proclens policy show`） | 原則檔可能是由一般使用者寫入的，因此 ProcLens 不採信。套用預設值。 | 以保護方式從提升權限的殼層建立它：見 `SECURITY.md` 的「保護原則檔」。並用 `proclens policy show` 檢查。 |
| `Administrative dumps are disabled by machine policy (… ignored: …)` | 相同原因，從傾印要求端看到。 | 同上。 |
| `Audit log FAILED the anchor check: The anchor hash is not in the log…`（結束代碼 5） | 您儲存的雜湊不在記錄中：在那之前的內容被改寫，或記錄被截短。 | 從最後一個良好的錨點起，將該記錄視為不可信；與副本比對。 |
| `A colon is only allowed in the drive letter; 'name:stream' would write into an NTFS alternate data stream.` / `'CON' is a reserved Windows device name.` / `Device paths are not accepted.`（結束代碼 2） | `--output` 路徑不會建立一般檔案。 | 使用一般的檔案路徑。 |
| `The source does not match the hash recorded when the dump was taken, so it was not exported.` / `dump export` 期間出現 `Authentication failed at chunk …` | 傾印已損毀或被修改；匯出會先檢查它。 | 不要使用該檔案；還原良好的副本。 |
| `Unexpected error (<Type>): <message>`（結束代碼 70） | ProcLens 的錯誤。 | 以 `PROCLENS_DEBUG=1` 重新執行並回報（不要附上任何傾印、密碼或金鑰）。 |
| 命令列錯誤只印在 stderr | 依設計，讓 stdout 對 `--json` 保持乾淨。 | 閱讀訊息並執行 `proclens <command> --help`。 |

## 7. 保留（`dump prune`）

| 您看到 | 原因 | 該怎麼做 |
|---|---|---|
| "Dry run: nothing was deleted" | 預設行為。 | 清單看起來正確後再加上 `--delete`。 |
| `skipped … not valid ProcLens metadata, so the dump is not managed.` / `…describes a different file…` / `…the dump it describes is missing.` / `symbolic links are never followed or removed.` | 只有具備相符 ProcLens 中繼資料的檔案才會被移除。 | 請自行移除其他檔案。 |
| `FAILED … The file could not be removed (it may be in use).` | 有東西開啟著該檔案。 | 關閉它後再執行；其中繼資料會保留，使其仍受管理。 |
| 傾印已經很舊卻沒有選到任何項目 | 時間取自**記錄的**建立時間，預設 30 天，或您的原則。 | 檢查輸出中的 `Policy:`；傳入 `--older-than-days`。 |
| `Retention limits cannot be negative.`（結束代碼 2） | 選項值。 | 使用 0 或更大。 |

## 8. 報告

| 您看到 | 原因 | 該怎麼做 |
|---|---|---|
| `Process <id> was not found or has exited.`（結束代碼 1）且沒有檔案 | 沒有可報告的對象。 | 檢查識別碼。 |
| 章節狀態 **Access denied** 與 *Errors* 下的一筆項目 | 作業系統拒絕了該處理程序。 | 對其他使用者的處理程序而言是預期行為。 |
| 章節狀態 **Not supported** | 平台無法提供。這不是錯誤。 | 無需處理。 |
| 章節狀態 **Not collected**：*A CPU rate needs at least two samples* | `--samples 1`，或該章節被關閉。 | 使用 `--samples 2` 或更多。 |
| `…already exists; existing files are never overwritten.`（結束代碼 2） | `--output` 指向既有檔案。 | 選擇新的檔名。 |
| 警告 `LIMITED_ACCESS`、`HIGH_CPU`、`MEMORY_GROWTH`、`UNENCRYPTED_DUMP`、`PROCESS_EXITED`、`THREAD_STATE_UNKNOWN` | 規則記錄於 `ARCHITECTURE.md`／`CLI_USAGE.md`。 | 它們描述的是處理程序，而不是 ProcLens 的錯誤。 |

## 9. 安裝與執行發行封裝

| 您看到 | 原因 | 該怎麼做 |
|---|---|---|
| macOS：「無法打開 proclens，因為無法驗證開發者」／啟動時被終止 | 封裝沒有程式碼簽章或公證，而下載的檔案帶有隔離旗標。 | 驗證雜湊值後，執行 `xattr -d com.apple.quarantine proclens`（安裝指令碼在被要求時會這麼做）。 |
| Linux/macOS：`Permission denied` | 解壓縮後缺少執行權限位元。 | `chmod +x proclens`；安裝指令碼會設定它。 |
| `proclens: command not found` | 安裝資料夾不在 `PATH` 上。 | 安裝後開新的殼層；Windows 安裝程式會編輯使用者 `PATH`，Unix 的則會印出要加入的那一行。 |
| Windows SmartScreen 警告 | 執行檔未簽章。 | 執行前先用 `SHA256SUMS.txt` 驗證檔案。 |
| 第一次執行多花不到一秒（在 Windows 上實測約 150 ms 對 90 ms） | 作業系統會掃描並快取剛下載的執行檔。ProcLens 本身在執行時不會解壓縮任何東西。 | 正常。 |
| 針對其他 CPU 架構的封裝無法啟動 | 封裝錯誤（`x64` 對 `arm64`）。 | 使用與 `uname -m`／`systeminfo` 相符的那一個。 |

## 9b. 顯示語言

| 您看到 | 原因 | 該怎麼做 |
|---|---|---|
| 中文顯示為 `?` 或方塊 | 終端機字型沒有中文字形，或終端機忽略 UTF-8。 | 使用 Windows Terminal 或搭配 CJK 字型的 UTF-8 終端機。對 `zh-TW`，ProcLens 已經輸出 UTF-8。 |
| 在指令碼或 CI 記錄中，中文顯示為 `�䤣��` 之類的亂碼 | 接收端以舊式字碼頁（例如 cp950）而非 UTF-8 解碼輸出。 | 以 UTF-8 解碼，或機器讀取時使用 `--json`／英文。 |
| `Unknown language 'x'. Available: en, zh-TW.` | `--lang` 或 `PROCLENS_LANG` 的值不正確。 | 使用 `en` 或 `zh-TW`。在此期間使用英文。 |
| 已儲存 zh-TW 卻仍是英文 | `PROCLENS_LANG` 或 `--lang` 覆寫了儲存的值，或設定檔無法讀取。 | `proclens config show` 會說明哪個來源勝出。 |
| 某些詞句仍是英文 | `System.CommandLine` 說明中的少數詞句沒有中文版本。 | 預期行為；ProcLens 自己寫出的所有內容都已翻譯。 |

## 10. 回報問題

請附上：`proclens system info`、指令及其結束代碼、訊息，以及（若相關）`proclens audit show --last 5` 的前幾行。
**請勿附上傾印檔、已解密的傾印檔、密碼、`master.key` 或原則檔。** 報告（`report generate`）包含處理程序名稱、路徑與擁有者；分享前請先閱讀。
