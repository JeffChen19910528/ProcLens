# ProcLens

[English](README.md) | **繁體中文**

ProcLens 是以 .NET 10 / C# 撰寫的跨平台（Windows、Linux、macOS）命令列處理程序診斷工具組。
它可以列出與檢視處理程序、取樣 CPU 與記憶體、顯示執行緒與模組、產生診斷報告（TXT / HTML / JSON），
並且僅在 Windows 上建立附有稽核記錄的加密處理程序傾印檔。

介面支援 **英文** 與 **繁體中文（zh-TW）**。

## 設計原則

- **誠實的資料。** 讀不到的值顯示為 `n/a（原因）`，絕不顯示成 `0`。
- **各平台據實呈現。** `proclens system capabilities` 會列出目前作業系統真正支援的功能，未實作的功能不會被宣稱支援。
- **傾印僅限 Windows（刻意設計）。** 在 Linux 與 macOS 上，`dump` 指令會顯示「此作業系統不支援處理程序傾印」並以代碼 2 結束；ProcLens 絕不使用 `gcore` 或 `ptrace`。
- **預設安全。** 傾印檔預設以 AES-256-GCM 加密、絕不覆寫既有檔案、預設只能傾印自己擁有的處理程序（除非系統管理員原則允許）、絕不自行提升權限，且每次傾印都會記錄在雜湊鏈稽核記錄中。傾印檔可能含有處理程序記憶體中的機密，ProcLens 絕不會上傳它們。

## 平台支援

| 功能 | Windows | Linux | macOS |
|---|---|---|---|
| 列出／檢視／監視處理程序、CPU 與記憶體 | 是 | 是 | 是 |
| 執行緒 | 是 | 是 | 是（狀態為 `Unknown`） |
| 模組 | 是 | 是 | 不支援 |
| 報告（txt / html / json）、稽核、原則、設定 | 是 | 是 | 是 |
| 處理程序傾印與所有 `dump` 指令 | **是** | 否 | 否 |


## 安裝

`deploy/package.cs` 會在 `artifacts/<版本>/` 產生免安裝 .NET 執行階段的單一檔案封裝：
`proclens-<版本>-win-x64.zip`、`linux-x64.tar.gz`、`osx-arm64.tar.gz` 等，並附 `SHA256SUMS.txt`。
執行前請先驗證雜湊值。每個封裝包含執行檔、安裝指令碼與 `docs/`。

從原始碼建置（需要 .NET 10 SDK）：

```
dotnet build
dotnet test
dotnet run --project src/ProcLens.Cli -- process list --sort memory
dotnet run deploy/package.cs -- --rids win-x64 --skip-tests     # 建置發行封裝
```

## 快速上手

```
proclens system capabilities                       # 這台機器支援哪些功能
proclens process list --sort memory                # 依記憶體排序列出處理程序
proclens process inspect --pid 1234                # 檢視單一處理程序
proclens process monitor --pid 1234 --interval 2 --count 10
proclens process threads --pid 1234
proclens process modules --pid 1234
proclens report generate --pid 1234 --format html --output report.html
```

多數指令可加上 `--json` 取得機器可讀的輸出（stdout 只有 JSON；記錄與進度輸出到 stderr）。

### 傾印（僅限 Windows）

```
$env:DUMP_PASSWORD = "一段很長的密語"              # PowerShell；密碼絕不以引數傳入
proclens dump create --pid 1234 --type heap --output .\dumps --password-env DUMP_PASSWORD
proclens dump info   --file .\dumps\<檔名>.dmp.enc
proclens dump verify --file .\dumps\<檔名>.dmp.enc --password-env DUMP_PASSWORD
proclens dump decrypt --file .\dumps\<檔名>.dmp.enc --output .\plain.dmp --password-env DUMP_PASSWORD
proclens dump export --file .\dumps\<檔名>.dmp.enc --output .\portable.dmp.enc --password-env DUMP_PASSWORD
proclens dump prune  --directory .\dumps                 # 試執行；加上 --delete 才會實際刪除
proclens audit show --last 20
proclens audit verify
```

傾印類型：`mini`、`heap`、`full`。可用 `--password-prompt` 以隱藏輸入方式輸入密碼；僅在您會自行保護檔案時才使用 `--no-encrypt`。

## 語言切換

```
proclens --lang zh-TW process list                 # 只對這次指令生效
proclens config set-language zh-TW                 # 記住設定
proclens config set-language auto                  # 清除設定
proclens config show                               # 顯示目前語言及其來源
```

優先順序：`--lang` → `PROCLENS_LANG` → 已儲存的設定 → 英文。作業系統的語言刻意不列入考量。
`--json` 輸出、報告 JSON、稽核記錄、傾印中繼資料與結束代碼永遠不會被翻譯。

若終端機顯示亂碼，請參閱 `docs/TROUBLESHOOTING.md` 的「Display language」一節。

## 結束代碼

| 代碼 | 意義 |
|---|---|
| 0 | 成功 |
| 1 | 找不到目標，或命令列語法錯誤 |
| 2 | 此平台不支援，或要求無效 |
| 3 | 存取被拒、未獲授權，或無法解鎖 |
| 4 | 架構不相符（32 位元 ProcLens 對 64 位元目標） |
| 5 | 失敗、被修改、損毀或遭竄改 |
| 70 | 未預期的內部錯誤 |
| 130 | 以 Ctrl+C 取消 |

## 專案結構

```
src/ProcLens.Core            模型、介面、純邏輯規則（加密、稽核鏈、原則、保留）
src/ProcLens.Application     使用案例
src/ProcLens.Infrastructure  稽核記錄、報告轉譯器
src/ProcLens.Platform.*      Windows / Linux / macOS 程式碼（所有作業系統專屬程式碼都在此）
src/ProcLens.Cli             指令（每個指令一個類別）、主控台、在地化、組合根
tests/  docs/  deploy/  tools/perf/
```

## 文件

`docs/ARCHITECTURE.md`（架構）· `docs/CLI_USAGE.md`（完整指令參考）· `docs/PLATFORM_SUPPORT.md` · `docs/SECURITY.md` ·
`docs/SECURITY_REVIEW.md` · `docs/DUMP_FORMAT.md` · `docs/TROUBLESHOOTING.md` · `deploy/README.md`

上述文件目前為英文版。
