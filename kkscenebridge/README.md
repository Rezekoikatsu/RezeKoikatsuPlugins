# kkscenebridge — 恋活場景卡合併工具

**繁體中文** ・ [English](README.en.md) ・ [日本語](README.ja.md)

把多張 CharaStudio 場景卡照順序接成一張，並處理接完之後的配音、過場影片與物件整理。
原本要在 Studio 裡手動做一整晚的事（刪相機、包資料夾、平移 Timeline、接相機、配 VNGE 音頻），
在這裡是加卡、排順序、按一次執行。

<!-- 示範影片之後補上 -->

## 分頁

| 分頁 | 做什麼 |
|---|---|
| **合併場景** | 把場景卡照順序接成一張（只有一張時就只整理，不接卡） |
| **添加動畫音頻** | 為合併好的卡產生 F7（Studio CutScene）用的 `.cutscene.json`：過場影片、配音、對應點 |
| **VNGE音頻** | 用 VNGE 的 VNSound 為每段場景配音（不需要 F7） |
| **整理** | 直接編輯卡片的物件樹：拖曳搬家、新增資料夾、改名、隱藏、刪除、改位置 |
| **設定** | 路徑、詳細選項、語言 |

## 合併場景

1. 把場景卡拖進清單（或按「新增卡片…」），清單順序＝播放順序
2. 每張選一台要留的相機
3. 在「資料夾名稱」輸入共同名稱按 Enter，包裝資料夾會依序變成 `名稱_(1)`、`名稱_(2)`…
4. 要配 VNGE 音頻就按「全部配音頻」或各列的「選音頻…」（不配就不用理）
5. 按「執行合併」

工具會自動做完這些事：

- **整理每張卡**：只留選定的相機（連同父資料夾鏈搬進 `(CAM)`），其餘物件包進 `<名稱>_(n)`，
  並建 `(MAP)` `(FX)` `(CHAR)` `(SFX)` 空資料夾方便之後分類
- **接卡**：後面每張的物件編號整批往後推、Timeline 整條平移到接續的時間、貼圖按內容去重
- **外掛資料一起接上並重新對應編號**：Timeline、NodesConstraints、KKPE、MaterialEditor、
  RendererEditor、OBJImport、TreeNodeNaming、LightSettings、ItemLayerEdit、SaveCameraObjectFOV、
  Sideloader（模組物件）、VNGE 音頻
- **輪流上場**：沒輪到的段落用物件啟用軌道取消勾選，並可搬到遠處（避免彼此干擾）
- **相機接管**：生一台新相機，在每段交界自動切到下一段的相機；沒有相機的卡可以自動生一台鎖住初始視角
- **地圖與畫面效果**：各段的內建地圖、畫面效果不一樣時，每段放一個 `[MAPINFO]` / `[ENV]` 記號資料夾。
  裝了 F7 的話播放時會自動切換；**取消勾選記號資料夾＝關掉那段的地圖**
- 已經整理過的卡（根節點有 `(CAM)`）會直接拿來用，不會再整理一次

時長欄可以改：加長＝最後一幀多停一會，縮短＝超出的關鍵影格會被拿掉。
卡片實際動畫比宣告的時長還長時，那一格會變紅並提示該填多少。

## 添加動畫音頻（產生 F7 的設定檔）

流程在分頁最上面：**① 合併場景 → ② 選影片／配音 → ③ 抓對應點 → ④ 產生設定**。

- **影片與配音**：把原影片、各版配音拖進清單；打勾的音訊成為配音版本，標 `★來源` 的影片是過場畫面的來源
- **抓對應點**：用內建播放器找到畫面，「擷取這一秒」把影片秒數填進表格，timeline 秒數從遊戲的 Timeline 面板抄過來。
  每一段至少兩個點
- **產生 cutscene.json**：寫到 `UserData\cutscene\<卡名>.cutscene.json`（F7 也會找場景卡旁邊的同名檔）

輔助功能：

- 從影片抽 wav、把各版配音的音量對齊到同一個響度（EBU R128）
- 把來源影片轉成無聲、關鍵影格密集的工作影片（拖時間軸更準）
- **從切好的音頻反推對應點** —— 以前用 VNGE 做的卡，音檔是一段段切好的，拿去跟原始音檔比對就能自動量出對應點
- **配音對照** —— 某一版配音的剪輯跟其他版不同（少了開場等）時，量一層換算表，F7 切換配音時即時換算

上面這些影片／音訊處理要用 **ffmpeg** —— 下載 `kkscenebridge_ffmpeg.zip` 就已經附好，其餘見下方 [ffmpeg](#ffmpeg)。

## VNGE音頻

讀一張合併好的卡，按「自動配對」會依包裝資料夾名稱到音頻資料夾找對應的音檔
（例如 `CharcardPT1` 對到 `_(1)`）。每段場景一個群組、綁該段的 `(SFX)` 觸發；
同一段有多個音檔時一個 100%、其餘 0%，在 Studio 裡可以即時切換。

存檔模式是整張卡共用的：相對路徑（預設，音檔要在音頻根目錄底下）、絕對路徑、或把音檔夾帶進卡片。

## 整理

- 拖曳搬家、新增資料夾、改名、勾選顯示、改位置／旋轉／縮放 —— 都不影響 Timeline 和 NC
- **刪除**：先標記，存檔時才真的刪，並自動修正 Timeline、NC 等所有參照
- 角色接點裡的物件只能在同一個接點裡拖
- 存回這張卡前預設會先備份成 `<卡名>.png.bak`

## 設定

- **遊戲根目錄**：其他路徑（讀卡、輸出、音頻根目錄）預設都從這裡算，改了會一起換
- **詳細設定**：預設值就是常用值，每一項滑鼠移上去有說明
- **Language / 語言 / 言語**：在最下方，換完重新啟動

設定存在 exe 旁邊的 `kkscenebridge_settings.json`。

## ffmpeg

Releases 有兩個版本，差別只在有沒有附 ffmpeg：

| 下載 | 內容 | 適合 |
|---|---|---|
| **`kkscenebridge_ffmpeg.zip`** | `kkscenebridge.exe` ＋ `ffmpeg\`（`ffmpeg.exe`、`ffprobe.exe`） | 解壓就能用，不想自己裝 ffmpeg |
| **`kkscenebridge.zip`** | 只有 `kkscenebridge.exe` | 電腦已經有 ffmpeg，或用不到影片／音訊處理（檔案小很多） |

兩個解壓出來都是一個 `kkscenebridge` 資料夾，放哪裡都可以。之後出新版時只要換掉 `kkscenebridge.exe`，
`ffmpeg` 資料夾留著繼續用（所以更新時下載小的 `kkscenebridge.zip` 就好）。

附的 ffmpeg 是未修改的第三方編譯版，不屬於 kkscenebridge、也不適用它的 MIT 授權；
授權（GPL）與原始碼連結寫在 `ffmpeg\README_ffmpeg.txt`。想換版本直接換掉 `ffmpeg` 資料夾即可。

沒有 ffmpeg 的話，只有「添加動畫音頻」分頁的影片／音訊處理不能用，
**合併場景、VNGE音頻、整理、設定完全不受影響**。

### 沒有 ffmpeg 不能用的功能（都在「添加動畫音頻」分頁）

| 功能 | 按鈕 |
|---|---|
| 從影片抽出 wav | 「把勾選的影片抽成 wav」「抽 wav（另外挑檔案…）」 |
| 各版配音音量對齊 | 「對齊音量…」、勾選「抽完順便對齊音量」 |
| 轉成無聲工作影片 | 「★來源轉成無聲工作影片」「批次轉無聲（選檔案…）」 |
| 從切好的音頻反推對應點 | 「從切好的音頻反推…」視窗的「開始比對」 |
| 配音對照的自動掃描 | 「配音對照…」視窗的「自動掃所有需要的版本」「只掃選取的這一個」 |

### 會少一些檢查，但功能照常

- 加入檔案時的「同剪輯檢查」（比對各檔總長度）會跳過
- 產生 `cutscene.json` 時量影片、配音長度：wav 會直接讀檔頭；影片要在內建播放器裡開著才量得到，
  沒開的話報告裡少了「片尾是否蓋到」這一項檢查

### 沒有 ffmpeg 也能用的

內建播放器、手動抓對應點、讀寫 `pairs.txt`、**產生 `cutscene.json`**。
只要配音自己準備成 wav（用其他軟體從影片轉出來），整個流程照樣走得完。

### 自己安裝 ffmpeg（用 `kkscenebridge.zip` 的人，任選一種）

**方法 A：winget（Windows 10 / 11 內建，最簡單）**

1. 開始選單搜尋「終端機」或「命令提示字元」，打開
2. 輸入下面這行，按 Enter：
   ```
   winget install --id Gyan.FFmpeg -e
   ```
3. 裝完**把 kkscenebridge 關掉重開**（新加進 PATH 的程式要重開才讀得到）

**方法 B：手動下載，放在 kkscenebridge 旁邊（不用改 PATH）**

1. 到 <https://www.gyan.dev/ffmpeg/builds/> 下載 **`ffmpeg-release-essentials.zip`**
2. 解壓縮，把解出來的資料夾（例如 `ffmpeg-8.0-essentials_build`）**改名成 `ffmpeg`**
3. 整個資料夾放到 `kkscenebridge.exe` 旁邊，變成 `kkscenebridge\ffmpeg\bin\ffmpeg.exe`
4. 重開 kkscenebridge

**確認**：「設定」分頁最下方顯示 `ffmpeg：<路徑>` 就是抓到了；紅字「找不到」表示沒抓到。

> 請用 essentials / full 這類 **GPL 版**。只標 LGPL 的版本沒有 libx264，「轉成無聲工作影片」會失敗。

## 需求

- Windows。用 Releases 的 zip 不需要安裝 Python
- 「添加動畫音頻」分頁的影片／音訊處理需要 [ffmpeg](https://ffmpeg.org/)：`kkscenebridge_ffmpeg.zip` 已附，或自己安裝，見上方 [ffmpeg](#ffmpeg)
- 合併卡的過場影片、地圖切換要在遊戲裡裝 **F7（Studio CutScene）**；VNGE 音頻要裝 VNGE

## 注意事項

- **原卡請留著。** 工具不會改原卡，但合併卡出問題時要用原卡重做
- 大卡（上 GB）讀寫要幾分鐘，視窗沒有回應是正常的
- `shaderType` 是整張卡一個值，合併只能留一個；各段不一樣時紀錄裡會有 `[注意]`
- 只有一張卡才有的外掛資料會整包搬過去，但裡面的編號沒有重新對應（紀錄裡會列出）

## 從原始碼執行 / 打包

```
run_source.bat          直接從原始碼執行（缺套件會自動安裝）
build.bat               用 PyInstaller 打包成 dist\kkscenebridge.exe
```

相依套件：`pip install kkloader==0.1.23 msgpack PyQt6 numpy`（打包另外需要 `pyinstaller`）

命令列（不需要介面）：

```
python kkscenemerge.py info  <卡.png>
python kkscenemerge.py prep  <卡.png> --name "場景A" [--camera <dicKey>] --out <出.png>
python kkscenemerge.py merge <第1張> <第2張> [第3張 ...] --out <合併.png>
```

## 檔案

| 檔案 | 內容 |
|---|---|
| `kkscenebridge.py` | 主視窗、合併分頁、設定分頁 |
| `kksblang.py` | 介面翻譯表（繁中 / English / 日本語） |
| `kkscenemerge.py` | 整理與合併的核心 |
| `kkscene2.py` / `kkmsgpack.py` | 場景卡讀寫、msgpack 位元組層級處理 |
| `kkref.py` | 哪些外掛欄位指向物件、用哪一種編號 |
| `kkcheck.py` | 讀寫驗收（round-trip） |
| `kktl_scan.py` | Timeline 軌道檢查 |
| `kkcuttab.py` / `kkcutscene*.py` / `kkaudioalign.py` / `kkvariantmap.py` | 添加動畫音頻分頁與設定檔產生 |
| `kkaudiotab.py` / `kkvnsound.py` | VNGE音頻分頁 |
| `kktreetab.py` | 整理分頁 |
| `kkffmpeg.py` | 找 ffmpeg（exe 旁的 `ffmpeg\` 資料夾 → PATH），並讓它不閃主控台視窗 |
