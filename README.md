# Koikatsu CharaStudio Plugins & Tools

**繁體中文** ・ [English](README.en.md) ・ [日本語](README.ja.md)

給 Koikatsu（コイカツ！）**CharaStudio** 用的一組 BepInEx 外掛，加上幾個配套的 Windows 工具。
從換人、換裝、拍卡，到把好幾張場景卡接成一部有過場影片和配音的作品、再戴上頭顯去看 ——
整條流程都在這裡。作者：**Reze**

<!-- 示範影片之後補上 -->

> [!TIP]
> 每一支都可以單獨安裝。一起裝的話會自動互相接上（例如在頭顯裡看 F7 的過場、用手柄遙控 F7 播放）。

---

## 🚀 主要功能

### 👥 F6 — Studio Character Tools
場景角色的總管，場上每個角色一列。
- **換人**：四種身材模式（一般替換／保留舊卡身材／鎖身高／維持新卡身材），也能「保持服裝換人」
- **換裝、飾品**：換服裝卡時可以帶回頭髮等飾品；飾品欄逐一管理、一鍵套到所有角色
- **飾品整組縮放／移動**：勾選多個飾品，用 XYZ 滑桿即時調整大小和位置，可以套用上次的數值
- **同步服裝槽**：刪除、縮放、移動可以同步到其他套換裝的同一格同一個飾品
- **帶入場景的著色器**：換人時把場景原角色的著色器（MaterialEditor）套到新角色，只換著色器、不動參數（關／詢問／自動）
- **型態鍵鎖定**、**碰撞器綁定修復**（換人後頭髮被吸走的問題）、**一鍵重置姿勢**
- **拍縮圖存卡**：人物卡、服裝卡、姿勢、全部換裝一次存完，構圖一致
- **合卡**：把服裝卡的飾品合進人物卡、移植整套換裝（搭配 kkbridge）

### 🎬 F7 — Studio CutScene
讓過場影片、配音、地圖跟著 **Timeline** 走。
- 每張場景卡一份 `.cutscene.json`，載入卡片自動套用（卡片旁邊或 `UserData\cutscene`）
- 開場／過場／片尾影片，音軌用對應點精準對齊，連「時間流速」軌道都不會跑掉
- **多版配音**即時切換，剪輯不同的版本自動換算
- 合併卡的**地圖與畫面效果**依段落自動切換
- 桌面快捷鍵、VR 視角記憶、手柄遙控（需要 F9）

### 🥽 F9 — Studio VR Tools
CharaStudio 在 VR 裡的操作總成。
- 搖桿平移、轉身、升降、繞轉，左右手速度分開調
- **在頭顯裡直接按組合就能綁鍵**，衝突會警告
- 面板黏在手上、不透明底板，頭顯裡看得清楚
- 頭顯裡播 F7 的過場影片、握把＋扳機遙控播放
- 進 VR 自動關掉頭顯裡會變糊的特效物件；一鍵回到相機視角

### 🧩 kkscenebridge（工具）
把多張場景卡照順序**接成一張**。
- 自動整理相機、包資料夾、平移 Timeline、接相機、貼圖去重，外掛資料一起重新對應編號
- 產生 F7 用的 `.cutscene.json`：內建播放器抓對應點、抽 wav、音量對齊、從切好的音頻反推
- VNGE 音頻、物件樹整理（拖曳、改名、刪除並自動修正參照）
- 影片／音訊處理要用 ffmpeg：下載 `kkscenebridge_ffmpeg.zip` 就已經附好；`kkscenebridge.zip` 不含，要自己安裝（沒有的話只少這部分，[哪些功能、怎麼裝](kkscenebridge/README.md#ffmpeg)）

### 🔗 kkbridge（工具）
角色卡離線合卡：附加飾品、移植整套換裝、修卡。F6 的合卡功能靠它，所以 `kkbridge.exe` 直接包在 `StudioCharTools.zip` 裡。

---

## 📋 需求

| | 需要 | 誰用到 |
|---|---|---|
| **必裝** | [BepInEx 5](https://github.com/BepInEx/BepInEx)（建議直接裝 [HF Patch](https://github.com/ManlyMarco/KK-HF_Patch)，常用外掛都內建） | 全部外掛 |
| | [KKAPI / ModdingAPI](https://github.com/IllusionMods/IllusionModdingAPI) | 工具列圖示（沒有也能用熱鍵） |
| F7 | **Timeline**（Joan6694，HF Patch 內建） | F7 整支圍著它做 |
| F9 | **[Ermin610/KK_VR](https://github.com/Ermin610/KK_VR)** | CharaStudio 的 VR 本體 |
| F9 | **[YukyoMoe/KK_VR_CameraSync](https://github.com/YukyoMoe/KK_VR_CameraSync)** | VR 視角跟著工作室相機走、回到相機視角 |
| F9 | SteamVR / OpenVR | 用 `--vr` 或 `--studiovr` 啟動 CharaStudio |
| 選配 | [MaterialEditor（KK_Plugins）](https://github.com/IllusionMods/KK_Plugins) | F6 換人時帶入場景的著色器 |
| 選配 | [KKABMX](https://github.com/ManlyMarco/KKABMX)、KKPE | F6 的「維持新卡身材」、碰撞器修復 |
| 選配 | VNGE | kkscenebridge 的 VNGE 音頻 |
| 選配 | [ffmpeg](https://ffmpeg.org/)（`kkscenebridge_ffmpeg.zip` 已附；用不含的版本就自己安裝，[步驟](kkscenebridge/README.md#ffmpeg)） | kkscenebridge「添加動畫音頻」分頁的抽 wav、音量對齊、轉無聲影片、反推對應點 |

> [!IMPORTANT]
> 桌面模式下 F9 完全安靜，沒裝 VR 外掛也不會有任何錯誤。缺選配外掛只是少那個功能，不會壞。

---

## 🛠️ 安裝

1. 到 **[Releases](../../releases)** 下載需要的 zip（每一個都可以單獨安裝）
2. **外掛的 zip 在遊戲根目錄解壓縮**（有 `BepInEx` 資料夾的那一層），dll 會自己進到 `BepInEx\plugins\`
3. `kkscenebridge` 的 zip 解壓到任何地方，執行裡面的 `kkscenebridge.exe`
4. 進 CharaStudio，按 **F6 / F7 / F9** 開面板

| 下載 | 內容 | 解壓到 |
|---|---|---|
| `StudioCharTools.zip` | F6 外掛 ＋ `kkbridge.exe`（合卡工具，會放在遊戲根目錄） | 遊戲根目錄 |
| `StudioCutScene.zip` | F7 外掛 | 遊戲根目錄 |
| `StudioVrTools.zip` | F9 外掛 | 遊戲根目錄 |
| `kkscenebridge_ffmpeg.zip` | kkscenebridge ＋ ffmpeg，解壓就能用 | 任何地方 |
| `kkscenebridge.zip` | 只有 kkscenebridge（檔案小；已經有 ffmpeg，或之後只更新 exe 時用） | 任何地方 |

兩個 kkscenebridge 的 zip 二選一即可。

設定檔在 `BepInEx\config\reze.studio.*.cfg`。要全部回到預設，執行根目錄的 `重置插件設定.bat`（只會搬移設定檔，不刪任何東西）。

## 🌐 語言

全部支援 **繁體中文 / English / 日本語**：
- 外掛：面板最下方的 **Language** 按鈕，三支會一起換
- kkscenebridge：「設定」分頁最下方（換完重新啟動）
- kkbridge：「設定」分頁最下方（立即生效）

## 📖 詳細說明

| | 中文 | English | 日本語 |
|---|---|---|---|
| F6 Studio Character Tools | [說明](StudioCharTools/README.md) | [Docs](StudioCharTools/README.en.md) | [説明](StudioCharTools/README.ja.md) |
| F7 Studio CutScene | [說明](StudioCutScene/README.md) | [Docs](StudioCutScene/README.en.md) | [説明](StudioCutScene/README.ja.md) |
| F9 Studio VR Tools | [說明](StudioVrTools/README.md) | [Docs](StudioVrTools/README.en.md) | [説明](StudioVrTools/README.ja.md) |
| kkscenebridge | [說明](kkscenebridge/README.md) | [Docs](kkscenebridge/README.en.md) | [説明](kkscenebridge/README.ja.md) |
| kkbridge | [說明](kkbridge/README.md) | [Docs](kkbridge/README.en.md) | [説明](kkbridge/README.ja.md) |

## 👨‍💻 自己編譯

- **外掛**：把 `Directory.Build.props` 的 `KoikatuDir` 改成你的遊戲路徑，用 Visual Studio 開 `StudioPlugins.sln` 建置
  （.NET Framework 3.5，建置完自動複製到 `BepInEx\plugins\`）。或用命令列：
  ```
  msbuild StudioPlugins.sln /p:Configuration=Release
  ```
- **工具**：需要 Python 3.11 以上，雙擊各資料夾的 `build.bat`。kkscenebridge 也可以用 `run_source.bat` 直接從原始碼執行。

## 🙏 致謝

- [Ermin610/KK_VR](https://github.com/Ermin610/KK_VR)、[YukyoMoe/KK_VR_CameraSync](https://github.com/YukyoMoe/KK_VR_CameraSync) —— F9 建立在這兩支之上
- [BepInEx](https://github.com/BepInEx/BepInEx)、[IllusionMods](https://github.com/IllusionMods) 的 ModdingAPI 與 KK_Plugins、Joan6694 的 Timeline
- [kkloader](https://pypi.org/project/kkloader/) —— kkscenebridge 讀寫場景卡
- [FFmpeg](https://ffmpeg.org/) —— kkscenebridge 的影片／音訊處理。`kkscenebridge_ffmpeg.zip` 附的是未修改的第三方編譯版（GPL，不適用本專案的 MIT），授權與原始碼連結在壓縮檔的 `ffmpeg\README_ffmpeg.txt`

## 📄 授權

[MIT](LICENSE)
