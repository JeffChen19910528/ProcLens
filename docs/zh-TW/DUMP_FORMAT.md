# 傾印、中繼資料與稽核格式

[English](../DUMP_FORMAT.md) | **繁體中文**

本文說明 ProcLens 寫到磁碟上的內容，精確到足以手動檢查檔案，或撰寫相容的讀取器。除非另有說明，所有整數皆為小端序。
行為皆有測試涵蓋；加密容器帶有公開的版本標記（`PLENC001`），因此可以演進。

## 1. 傾印目錄中的檔案

每次傾印，ProcLens 會在您以 `--output` 指定的目錄中，精確地建立兩個檔案：

| 檔案 | 內容 |
|---|---|
| `<stem>.dmp.enc` | 加密的傾印（預設），**或** |
| `<stem>.dmp` | 原始的 Windows minidump（`--no-encrypt`） |
| `<stem>.dmp.enc.json` / `<stem>.dmp.json` | 中繼資料（第 4 節）；為傾印的檔名加上 `.json` |

`<stem>` 是 `<處理程序名稱>_<pid>_<類型>_<UTC yyyyMMddTHHmmssZ>_<8 位十六進位>`；例如
`PING_32528_heap_20261030T042903Z_059e038d.dmp.enc`。處理程序名稱會被縮減為字母、數字、`-`、`_` 與 `.`，且最多 40 個字元。
名稱是自動產生的，絕不由呼叫端提供。

產生加密傾印的期間，原始傾印會短暫地以 `<stem>.dmp.plain.tmp` 存在。它以私有方式建立，並在加密副本被再次解密並比對之後抹除、刪除。
當機時可能會殘留；`dump prune` 會在一小時後移除這類檔案。

所有檔案都是新建（絕不覆寫），且只有目前使用者可讀取。

## 2. Minidump（偵錯器開啟的內容）

ProcLens 呼叫 DbgHelp 的 `MiniDumpWriteDump`（只從 System32 載入）。三種類型對應到下列 `MINIDUMP_TYPE` 旗標：

| 類型 | 旗標 | 內容 |
|---|---|---|
| `mini` | `WithThreadInfo` (0x1000) `WithUnloadedModules` (0x20) `WithProcessThreadData` (0x100) | 執行緒、堆疊、模組清單、執行緒資料。 |
| `heap` | mini + `WithPrivateReadWriteMemory` (0x200) `WithDataSegs` (0x1) `WithFullMemoryInfo` (0x800) | 另外加上私有讀寫記憶體（堆積）與資料區段。 |
| `full` | mini + `WithFullMemory` (0x2) `WithFullMemoryInfo` (0x800) | 所有已提交的記憶體。 |

刻意不使用 `MiniDumpWithHandleData`（它需要更多處理程序權限，並會記錄已開啟核心物件的名稱）。開啟處理程序時只要求
`PROCESS_QUERY_INFORMATION | PROCESS_VM_READ`。

有效的傾印以下列標頭開頭；`dump info` 會回報它，並拒絕任何不符合的內容：

| 位移 | 大小 | 欄位 |
|---|---|---|
| 0 | 4 | 簽章 `MDMP`（`0x504D444D`） |
| 4 | 4 | 版本；低 16 位元必須是 `0xA793` |
| 8 | 4 | 資料流數量（接受 1 到 4096） |
| 12 | 4 | 資料流目錄的位移 |
| 20 | 4 | 時間戳記（Unix 秒） |
| 24 | 8 | 所使用的 `MINIDUMP_TYPE` 旗標 |

資料流目錄的項目為 12 位元組（類型、大小、位移）。ProcLens 會讀取資料流 7（系統資訊：架構、作業系統版本與組建）、3（執行緒清單）
與 4（模組清單）；每個位移都會對照檔案長度檢查，因此被截斷的檔案會如實回報。`dump info` 顯示的傾印類型是從旗標推斷的
（完整記憶體旗標 → `Full`，私有讀寫旗標 → `Heap`，否則為 `Mini`）。

寫入之後，ProcLens 還會請 DbgHelp 本身（`MiniDumpReadDumpStream`）去定位系統資訊與執行緒清單資料流，因此偵錯器所依賴的同一個函式庫也必須能讀取該檔案。

## 3. 加密容器（`PLENC001`）

分塊的 AES-256-GCM。傾印檔遠大於一次性 AES-GCM API 所能處理，因此內容被切成區塊，每個區塊各自驗證，**並且**綁定它在檔案中的位置。

```
位移    大小   欄位
0       8      魔術字串 "PLENC001"
8       4      區塊大小（位元組）（ProcLens 寫入 1,048,576；讀取器接受 4,096 到 67,108,864）
12      16     檔案識別碼（隨機）
28      4      nonce 前綴（隨機）
32      4      槽區塊長度（1 到 4,096）
36      n      槽區塊
36+n    ...    區塊，直到檔案結尾
```

前 32 個位元組是**固定標頭**。真實檔案的開頭如下：

```
00000000: 504c 454e 4330 3031 0000 1000 8607 61fc  PLENC001......a.
00000010: 9e26 27bc 2281 e957 b2d9 cea7 d4e8 0e0e  .&'."..W........
00000020: 4700 0000 0101 08f3 2e37 dcdc 1a74 3ce3  G........7...t<.
```

`0x00100000` 是區塊大小；`0x47` = 71 是槽長度；槽區塊以 `01`（一個槽）、`01`（電腦金鑰）、`08`（描述長度）開頭。

### 金鑰槽

每個檔案都會產生全新的隨機 256 位元**資料金鑰**。它絕不會以未包裝的形式離開處理程序；檔案中針對每個槽（最多 8 個）各存放一份包裝後的金鑰：

```
槽區塊：  count u8，接著 count × slot
slot：    type u8 | 描述長度 u8 | 描述 | nonce[12] | 包裝後金鑰[32] | tag[16]
```

資料金鑰以該槽的金鑰加密金鑰（KEK）做 AES-256-GCM 包裝，附加驗證資料為
`固定標頭 ‖ type ‖ 描述長度 ‖ 描述`。

| 類型 | 金鑰加密金鑰 | 描述 |
|---|---|---|
| 1，電腦／使用者 | 一個隨機的 256 位元主金鑰，以 DPAPI 保護（`CurrentUser`，entropy 為 `"ProcLens master key v1"`）存放於 `master.key` | 8 位元組：主金鑰 SHA256 的前 8 個位元組（用來辨識是哪一把金鑰） |
| 2，密碼 | 對 UTF-8 密碼做 PBKDF2-HMAC-SHA256，輸出 32 位元組 | 20 位元組：迭代次數 `u32`（寫入 600,000；接受 10,000 到 3,000,000）‖ salt[16]（隨機） |

由於槽**不屬於**區塊的附加驗證資料，`dump export` 可以替換它們（換成僅密碼的槽），而不需要碰觸或重新加密區塊。

### 區塊

每個區塊是 `密文 ‖ tag[16]`。除了最後一個區塊外，所有區塊都帶有 `區塊大小` 位元組的明文；最後一個區塊帶有 1 到 `區塊大小` 位元組
（空的明文是一個 0 位元組的區塊）。對於區塊 `i`（從 0 開始）：

```
nonce = noncePrefix[4] ‖ i（u64 大端序）                              （12 位元組）
aad   = 固定標頭[32] ‖ i（u64 大端序）‖ isFinal u8（0 或 1）           （41 位元組）
```

只有最後一個區塊的 `isFinal` 為 1。結合固定標頭中的檔案識別碼，這代表區塊不可能被重新排序、遺漏、重複、截掉（即使剛好在區塊邊界）、
在其後追加，或搬到另一個檔案，而不導致驗證失敗。明文只會在其所屬區塊驗證通過後才釋出。

### 獨立讀取檔案

1. 讀取 32 + 4 個位元組，再讀取槽區塊；拒絕超出範圍的值。
2. 為您能提供的槽推導出金鑰加密金鑰；解密 32 位元組的包裝後金鑰。
3. 每次讀取 `區塊大小 + 16` 位元組；結束檔案的那個區塊是最後一個區塊。
4. 使用上述 nonce 與附加驗證資料解密每個區塊。

## 4. 中繼資料（`<dump>.json`）

ProcLens 會在每個傾印旁邊寫入此檔；絕不包含記憶體內容或金鑰。真實範例：

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

| 欄位 | 意義 |
|---|---|
| `ProcessId`、`ProcessName`、`ProcessStartTime` | 被傾印處理程序的身分；啟動時間用來和之後重複使用該識別碼的處理程序區分。 |
| `DumpType` | `Mini`、`Heap` 或 `Full`。 |
| `FileSize` | 所儲存檔案的大小（加密時為密文大小）。 |
| `CreationTime` | ProcLens 完成傾印的時間（UTC）。保留原則使用此欄位。 |
| `Sha256` | **依儲存形式**的檔案 SHA256。 |
| `PlaintextSha256` | 原始 minidump 的 SHA256；未加密時等於 `Sha256`。 |
| `EncryptionScheme` | 未保護的傾印為 `null`。 |
| `CollectionStatus` | 凡是存在的傾印都是 `Succeeded`（失敗的嘗試不會留下檔案）。 |

雜湊用來偵測損毀與竄改。它們不是加密，也不是存取控制。只有在此檔案可解析、且 `DumpFilePath` 指向同一個檔案時，傾印才算*受管理*
（會顯示在 `report` 中，可被 `prune` 移除）。

## 5. 稽核記錄（`audit.jsonl`）

每行一個 JSON 物件，只附加，絕不改寫。真實範例（路徑已縮短）：

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

| 欄位 | 意義 |
|---|---|
| `Operation` | `dump.create`、`dump.decrypt`、`dump.export`、`dump.verify` 或 `dump.delete`。 |
| `Authorization.Policy` | 由哪條規則決定：`owner`、`admin`、`key-holder`（解密／匯出／驗證）、`retention`，被拒絕時為 `denied` / `n/a`。 |
| `Outcome` | `Succeeded`、`Denied`、`Failed` 或 `Cancelled`。拒絕與失敗也會被記錄。 |
| `Justification` | 系統管理員要求時的 `--reason`（若有）。 |

沒有任何欄位用來存放密碼、金鑰或記憶體內容。自由文字限制在 500 個字元以內，並移除控制字元，因此一個值無法開啟第二筆記錄。

**雜湊鏈。** 第一筆記錄的 `PrevHash` 是 64 個零；之後是前一筆記錄的 `Hash`。`Hash` 是記錄的 UTF-8 JSON 的小寫十六進位 SHA256，
計算時 `Hash` 設為 `""`、`PrevHash` 已設定，並由 ProcLens 依所示的欄位順序序列化（列舉以字串表示、精簡、無縮排）。
`proclens audit verify` 會重新計算雜湊鏈。除了從結尾截斷之外，任何對記錄的編輯、插入、刪除或重新排序都會被偵測到。
