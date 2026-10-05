# kkbridge / kkmerge

**繁體中文** ・ [English](README.en.md) ・ [日本語](README.ja.md)

> 🤖 **Created by Claude AI** —— 這個專案的程式碼與說明文件都是由 Claude（Anthropic 的 AI）寫的；Reze 負責發想、提需求、在遊戲裡實測。

> [!NOTE]
> **v1.0.1 起 kkbridge 已經併進 [kkscenebridge](../kkscenebridge/README.md#人物卡合卡原本的-kkbridge) 的「人物卡合卡」分頁**，功能和工單格式都沒變。
> 新版的 Releases 不再附 `kkbridge.exe`，F6 的合卡功能開著 `kkscenebridge.exe` 就能用。
> **不想用 kkscenebridge 的話，可以到 [v1.0.0](https://github.com/Rezekoikatsu/RezeKoikatsuPlugins/releases/tag/v1.0.0) 下載獨立版的 `kkbridge.exe`**（在那一版的 `StudioCharTools.zip` 裡），新版 F6 照樣可以搭配；兩個不要同時開，不然工單會被做兩次。

恋活角色卡的離線合卡工具。`kkbridge.exe` 是介面,`kkmerge.exe` 是同一套引擎的命令列版。

**下載**:[v1.0.0](https://github.com/Rezekoikatsu/RezeKoikatsuPlugins/releases/tag/v1.0.0) 的 `StudioCharTools.zip` 裡有 `kkbridge.exe`(解壓後在遊戲根目錄);之後的版本沒有附,要用就從那一版拿,或照下面「打包」自己做。

## 語言 / Language / 言語

介面有 **繁體中文 / English / 日本語** 三種。在「設定」分頁最下面的
**語言 / Language** 選單切換,選下去就立刻換,不用重開程式;選擇會記在
`kkbridge_settings.json`,下次開還是同一種。

The interface is available in **Traditional Chinese, English and Japanese**.
Switch it with the **語言 / Language** selector at the bottom of the
**Settings** tab — it applies immediately, no restart needed, and the choice
is remembered in `kkbridge_settings.json`.

インターフェースは **繁体字中国語・英語・日本語** に対応しています。
「設定」タブ下部の **語言 / Language** で切り替えられます。即座に反映され、
再起動は不要です。選択は `kkbridge_settings.json` に保存されます。

命令列的 `kkmerge.exe` 跟著同一份設定檔,也可以用 `--lang 0|1|2` 當場指定
(0=繁體中文 1=English 2=日本語)。

## 打包

把 `kkmerge.py`、`kkbridge.py`、`kklang.py`、`kkbridge.ico`、`build.bat`
放同一個資料夾,點兩下 `build.bat`。`kklang.py` 是翻譯表,少了它兩個 exe 都建不起來。

產物:

| | 大小 | 用途 |
|---|---|---|
| `dist\kkmerge.exe` | 約 9 MB | 引擎本體,命令列介面。給自己寫腳本、直接下命令的人用(Releases 沒有附) |
| `dist\kkbridge.exe` | 約 40 MB | 同一套引擎 + PyQt6 介面。手動合卡、監看工單用。**F6 用的是這個** |

兩個 exe 的圖示都是 `kkbridge.ico`,跟 F6(StudioCharTools)工具列上那顆是同一個
圖案。想重新產生的話:`python make_icon.py`(需要 Pillow)。它另外會產生
`kkbridge_flat.ico`——白色小人配透明背景,跟工具列上那顆一模一樣,但放在
檔案總管的白底資料夾裡幾乎看不見,所以預設用的是有深色底板的那顆。
要換的話把 `build.bat` 和兩個 `.spec` 裡的檔名改掉就好。

兩個都含完整引擎,功能一樣,差別只在有沒有介面。F6 是把工單丟進資料夾、等 `kkbridge.exe`
處理,所以只要 `kkbridge.exe` 開著並在監看就夠了;不開遊戲時也可以用它手動合卡、看卡片資訊、修老卡。

## 介面怎麼用

### 讀卡

每個卡片欄位都可以**把 png 拖進去**,或按「讀卡…」開檔案總管。讀完會顯示縮圖、
換裝套數、外掛數量、檔案大小。丟錯類型(把服裝卡丟進角色卡欄位)會直接擋下來並說明。

### 服裝槽表格

讀完角色卡就會列出所有服裝槽:編號、名稱、飾品數、欄位數。

名稱來源:前七套用 KK 的固定名稱(制服1、制服2、私服、泳裝、體操服、部活、浴衣),
之後的套數如果裝了 MoreOutfits 且改過名,會顯示你自己取的名字。

勾選就變**紅色粗體**。可以複選,「全選 / 全不選」在下方。

### 附加飾品

角色卡 + 服裝卡 → 勾選要加飾品的服裝槽 → 執行。勾多個就依序套用到同一張卡上,
最後輸出一張。

### 移植整套換裝

來源角色卡 + 目標角色卡 → 勾選要搬的服裝槽 → 右邊那欄改成要放到目標卡的第幾套。

預設同號(來源第 3 套 → 目標第 3 套),改掉就會照你指定的搬。例如來源第 1 套搬到
第 3 套、第 3 套搬到第 6 套,兩筆一起勾、各自設好輸出編號,按一次執行就好。
輸出編號重複會擋下來,不然後面那筆會把前面的蓋掉。

### 修卡

清掉指向空飾品欄位的殘留擴充資料。任何一張「某套換裝的材質會亂、切到別套再切回來
才正常」的卡都用得上。

### 監看工單

選一個資料夾,按「開始監看」,放著不管。設定頁可以勾「開啟程式時自動開始監看」。

## 工單格式

UTF-8 的 `*.job.json`,丟進監看資料夾:

```json
{"op": "append", "chara": "C:\\tmp\\scene_chara.png",
 "coord": "C:\\Koikatu\\UserData\\coordinate\\某套.png",
 "outfit": 3, "out": "C:\\tmp\\merged.png"}
```

```json
{"op": "transplant", "src": "C:\\tmp\\merged.png", "src_outfit": 3,
 "dst": "C:\\tmp\\新人物.png", "dst_outfit": 0, "out": "C:\\tmp\\result.png"}
```

```json
{"op": "clean", "chara": "C:\\tmp\\某張卡.png", "out": "C:\\tmp\\cleaned.png"}
```

命令列版另外支援 `--result <檔案>`,把結果 JSON 額外寫成一份 UTF-8 檔案。
呼叫端就不必處理 stdout 的編碼(.NET 3.5 沒有 `StandardOutputEncoding`,
中文會亂碼),讀檔就好。

可選欄位:`pushup`(`follow_outfit` / `keep_target`)、`skin_overlay`
(`keep_target` / `follow_outfit`)、`clean`(true / false)。沒給就用設定頁的值。
`out` 留空就丟到設定的輸出資料夾。

處理完會在旁邊產出同名的 `*.done.json`:

```json
{"ok": true, "out": "C:\\tmp\\result.png", "slot_total": 59,
 "materialeditor_entries": 913, "textures_added": 42,
 "orphans_removed": {}, "warnings": [], "seconds": 6.2}
```

失敗時 `{"ok": false, "error": "..."}`。

**`warnings` 要看。** 裡面會列出來源卡上「看起來是換裝層級、但工具不認得」的外掛資料
——那些東西不會被搬移。目前支援四種存法:以換裝索引當 dict 的鍵、條目自帶
`Coordinate` 欄位、索引編在 key 名稱裡(如 `slots3`)、貼圖側表加查表。

## 插件那邊要改什麼

插件的角色從「自己算合併」變成「存卡 → 丟工單 → 等結果 → 讀回來」。

```csharp
static readonly string JobDir = @"C:\Koikatu\UserData\kkbridge";

static IEnumerator MergeCoroutine(ChaControl chara, string coordPath, int outfit)
{
    Directory.CreateDirectory(JobDir);
    string id      = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
    string tmpCard = Path.Combine(JobDir, id + "_in.png");
    string outCard = Path.Combine(JobDir, id + "_out.png");
    string jobFile = Path.Combine(JobDir, id + ".job.json");
    string doneFile= Path.Combine(JobDir, id + ".done.json");

    chara.chaFile.SaveCharaFile(tmpCard, byte.MaxValue, false);

    // 手寫 JSON，不必為了這幾行拉一個序列化函式庫進來
    string job = "{\\"op\\":\\"append\\",\\"chara\\":\\"" + Esc(tmpCard) +
                 "\\",\\"coord\\":\\"" + Esc(coordPath) +
                 "\\",\\"outfit\\":" + outfit +
                 ",\\"out\\":\\"" + Esc(outCard) + "\\"}";
    File.WriteAllText(jobFile, job, new UTF8Encoding(false));

    float deadline = Time.realtimeSinceStartup + 180f;
    while (!File.Exists(doneFile))
    {
        if (Time.realtimeSinceStartup > deadline)
        {
            Log.LogError("kkbridge 沒有回應，工具開著嗎？");
            yield break;
        }
        yield return new WaitForSeconds(0.5f);
    }

    string result = File.ReadAllText(doneFile, Encoding.UTF8);
    if (result.Contains("\\"ok\\": true") && File.Exists(outCard))
        chara.chaFile.LoadFileLimited(outCard);        // 或整張重載
    else
        Log.LogError("合卡失敗：" + result);

    foreach (var f in new[] { tmpCard, jobFile, doneFile })
        try { File.Delete(f); } catch { }
}

static string Esc(string p) => p.Replace("\\\\", "\\\\\\\\");
```

要點:

- 路徑在 JSON 裡要跳脫反斜線,`Esc()` 就是做這件事
- **一定要用 coroutine 等**,不要 `while` 卡住主執行緒,不然遊戲會整個凍住
- 60 MB 的卡處理大約 3 到 8 秒,`deadline` 別設太短
- 工具沒開時工單會留在資料夾裡,下次開起來會自動補做——所以插件端要處理逾時,
  不要假設一定會有結果

不想用工單也可以直接 `Process.Start` 叫 `kkmerge.exe`,參數看 `kkmerge.exe --help`。
差別只在工具沒開時的行為:工單會補做,直接呼叫則是當場失敗。

## 想多加一個語言

`kklang.py` 一個檔案就是全部。翻譯表的 key **就是中文原文**:

```python
A("至少勾一個服裝槽", "Tick at least one outfit slot",
  "コーデスロットを少なくとも 1 つ選択してください")
```

不另外取 `MSG_XXX` 這種鍵名,是因為查不到時會自動退回中文——漏翻一條的後果
是那一行顯示中文,而不是畫面上冒出 `MSG_XXX`。

字串裡的 `{0}` `{1}` 要跟中文那條一樣(數量、編號、還有 `{2:.0f}` 這種格式),
但**順序可以換**,因為各語言語序不同。跑 `python kklang.py` 會檢查這件事,
並印出每個語言缺幾條。

## 已知範圍

- 只處理 KK / KKParty 的角色卡與服裝卡,不碰場景卡
- 移植換裝時,MaterialEditor 的角色層級條目(臉、身體)、ABMX、UncensorSelector、
  SkinEffects、KSOX 皮膚與眼睛 overlay 都保留目標人物的
- Pushup 的胸托參數是**每套換裝一份**,預設跟著服裝走,可在設定頁改
- ABMX 的飾品骨骼調整未處理
