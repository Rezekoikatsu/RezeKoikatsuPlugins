# -*- coding: utf-8 -*-
"""kklang —— kkbridge / kkmerge 的介面語言。

設計跟三個 BepInEx 插件裡的 Lang.cs 一樣，就一個重點：

    **中文原文自己就是 key。**

不另外發明 MSG_OUTFIT_SLOT_EMPTY 這種鍵名。這樣做有三個好處，
都是實際被燙到之後才學會的：

  1. 漏翻不會爆炸。查不到就回傳原本那串中文 —— 介面照樣能用，
     只是那一行是中文。用鍵名的話漏一條就是畫面上出現 "MSG_XXX"。
  2. key 和值不會對錯。鍵名制度下，改了中文文案卻忘了改鍵名對應，
     是不會有任何錯誤訊息的 —— 直到有人截圖問「這句怎麼怪怪的」。
  3. 讀程式的人看得懂。T("至少勾一個服裝槽") 不用跳到別的檔案去查。

代價是 key 很長、中文改字就要同步改表。用漏翻會自動退回中文換掉
「改字就壞掉」，這筆交易在這種一個人維護的小工具上很划算。

加語言
======
在 TABLE 裡多一欄，改 NAMES 和 A() 就好。沒填的條目一律退回中文。

格式參數
========
翻譯字串裡的 {0} {1} 一定要跟中文那條一模一樣（數量、編號、
還有 {2:.0f} {0!r} 這種格式規格），但**順序可以換** ——
這正是用位置參數而不是 {} 的原因：日文和英文的語序跟中文常常不同，
翻的人要能把它搬到該去的地方。底下的 check() 會把對不上的抓出來。
"""

import re

ZH, EN, JA = 0, 1, 2
NAMES = ("繁體中文", "English", "日本語")

Current = ZH

_TABLE = {}


def A(zh, en, ja):
    _TABLE[zh] = (zh, en, ja)


def set_lang(i):
    """設語言。給了奇怪的值就回到中文，不要讓介面整個空掉。"""
    global Current
    Current = i if i in (ZH, EN, JA) else ZH
    return Current


def name(i=None):
    i = Current if i is None else i
    return NAMES[i] if 0 <= i < len(NAMES) else NAMES[ZH]


def next_lang():
    """給「按一下換下一個語言」的按鈕用。"""
    return set_lang((Current + 1) % len(NAMES))


def T(zh):
    """查表。沒有這條、或那個語言留空，就退回中文原文。"""
    row = _TABLE.get(zh)
    if not row:
        return zh
    return row[Current] or zh


# ---------------------------------------------------------------- 翻譯表
#            繁體中文                              English                                  日本語

# ---- 視窗、分頁、按鈕 ----
A("{0} {1} — 恋活合卡",
  "{0} {1} — Koikatsu card merger",
  "{0} {1} — コイカツ カード合成")
A("恋活角色卡飾品合併",
  "Koikatsu character-card accessory merger",
  "コイカツ キャラカード アクセサリ合成")
A("附加飾品", "Add accessories", "アクセサリ追加")
A("移植換裝", "Transplant outfit", "コーデ移植")
A("修卡", "Repair card", "カード修復")
A("監看工單", "Watch jobs", "ジョブ監視")
A("設定", "Settings", "設定")
A("執行", "Run", "実行")
A("執行清理", "Run cleanup", "クリーンアップ実行")
A("瀏覽…", "Browse...", "参照...")
A("開啟", "Open", "開く")
A("讀卡…", "Load card...", "カード読込...")
A("全選", "Select all", "すべて選択")
A("全不選", "Select none", "選択解除")
A("儲存設定", "Save settings", "設定を保存")
A("語言 / Language", "語言 / Language", "語言 / Language")
A("清掉②的服裝卡", "Clear the outfit card in ②", "②のコーデカードをクリア")
A("開始監看", "Start watching", "監視開始")
A("停止監看", "Stop watching", "監視停止")
A("紀錄", "Log", "ログ")

# ---- 卡片面板 ----
A("角色卡", "Character card", "キャラカード")
A("服裝卡", "Outfit card", "コーデカード")
A("把卡片\n拖到這裡", "Drop a card\nhere", "カードを\nここにドロップ")
A("（尚未讀卡）", "(no card loaded)", "（カード未読込）")
A("選擇卡片", "Choose a card", "カードを選択")
A("卡片 (*.png)", "Cards (*.png)", "カード (*.png)")
A("角色卡 (*.png)", "Character cards (*.png)", "キャラカード (*.png)")
A("<span style='color:{0}'>讀不出來：{1}</span>",
  "<span style='color:{0}'>Could not read: {1}</span>",
  "<span style='color:{0}'>読み込めません：{1}</span>")
A("<span style='color:{0}'>這是{1}，這裡要的是{2}</span>",
  "<span style='color:{0}'>This is a {1}; a {2} is required here</span>",
  "<span style='color:{0}'>これは{1}です。ここに必要なのは{2}です</span>")
A("{0} 套換裝　{1} 個外掛　{2:.0f} MB",
  "{0} outfits　{1} plugins　{2:.0f} MB",
  "コーデ {0} 着　プラグイン {1} 個　{2:.0f} MB")
A("服裝卡「{0}」　{1} 個飾品　{2} 個欄位",
  "Outfit card \"{0}\"　{1} accessories　{2} slots",
  "コーデカード「{0}」　アクセサリ {1} 個　スロット {2} 個")

# ---- 表格欄位 ----
A("編號", "No.", "番号")
A("名稱", "Name", "名前")
A("飾品", "Accessories", "アクセサリ")
A("欄位", "Slots", "スロット")
A("輸出到第幾套", "Target outfit", "出力先コーデ")
A("第 {0} 套", "Outfit {0}", "コーデ {0}")

# ---- 換裝名稱（遊戲內建的分類） ----
A("制服1", "Uniform 1", "制服1")
A("制服2", "Uniform 2", "制服2")
A("私服", "Casual", "私服")
A("泳裝", "Swimsuit", "水着")
A("體操服", "Gym clothes", "体操服")
A("部活", "Club", "部活")
A("浴衣", "Yukata", "浴衣")

# ---- 分頁說明 ----
A("① 目標角色卡", "① Target character card", "① 対象キャラカード")
A("② 來源服裝卡", "② Source outfit card", "② 元コーデカード")
A("③ 勾選要加上飾品的服裝槽（可複選，會依序套用到同一張卡）",
  "③ Tick the outfit slots to add accessories to "
  "(multiple allowed; applied to the same card in order)",
  "③ アクセサリを追加するコーデスロットを選択"
  "（複数可・同じカードに順番に適用）")
A("① 來源角色卡（衣服從這裡拿）",
  "① Source character card (clothes come from here)",
  "① 元キャラカード（服はここから取ります）")
A("② 附加服裝卡（可留空）",
  "② Extra outfit card (optional)",
  "② 追加コーデカード（省略可）")
A("③ 目標角色卡（人是這個）",
  "③ Target character card (this is the person)",
  "③ 対象キャラカード（人物はこちら）")
A("④ 勾選要搬的服裝槽，右邊那欄改成要放到目標卡的第幾套（預設同號）",
  "④ Tick the outfit slots to move; the right-hand column sets which "
  "outfit on the target card they land in (same number by default)",
  "④ 移動するコーデスロットを選択。右の列で対象カードの何着目に入れるか"
  "を指定します（既定は同じ番号）")
A("②留空 = 直接把①移植到③；放了服裝卡 = ①先附加它的飾品，再移植到③",
  "② empty = transplant ① straight into ③; with an outfit card = "
  "① gets its accessories added first, then is transplanted into ③",
  "②が空の場合は①をそのまま③へ移植します。コーデカードを入れた場合は"
  "①にそのアクセサリを追加してから③へ移植します")
A("要清理的角色卡", "Character card to clean up", "クリーンアップするキャラカード")
A("輸出到", "Save to", "出力先")
A("輸出檔", "Output file", "出力ファイル")

# ---- 選項 ----
A("皮膚／眼睛 overlay（KSOX）", "Skin / eye overlay (KSOX)", "肌・瞳オーバーレイ（KSOX）")
A("跟著服裝走", "Follow the outfit", "服に追従")
A("保留目標人物的", "Keep the target's own", "対象の人物のものを保持")
A("胸托參數（Pushup）", "Pushup settings", "ブラ補正（Pushup）")
A("胸托參數跟著服裝走還是保留目標人物的",
  "Whether pushup settings follow the outfit or stay with the target character",
  "ブラ補正を服に追従させるか、対象の人物のものを残すか")
A("存檔前自動清除指向空飾品欄位的殘渣（建議開著）",
  "Strip leftover data pointing at empty accessory slots before saving "
  "(recommended)",
  "保存前に空きアクセサリスロットを指す残存データを自動削除する（推奨）")
A("清掉指向空飾品欄位的殘留擴充資料。飾品拿掉後 MaterialEditor 的材質資料\n"
  "會留在卡上，載入時取到 null 物件會讓整個套用流程中止，那一套換裝的貼圖\n"
  "就全部不會套用——切到別套再切回來才正常的，就是這個毛病。",
  "Strips leftover plugin data that points at empty accessory slots. When an\n"
  "accessory is removed, its MaterialEditor material data stays on the card;\n"
  "on load the null object aborts the whole apply pass, so none of that\n"
  "outfit's textures get applied — that is the bug behind \"it only looks\n"
  "right after switching to another outfit and back\".",
  "空きアクセサリスロットを指す残存プラグインデータを削除します。アクセサリを\n"
  "外しても MaterialEditor のマテリアルデータはカードに残り、読み込み時に\n"
  "null オブジェクトを掴んで適用処理全体が中断されるため、そのコーデの\n"
  "テクスチャが一切適用されません。「別のコーデに切り替えて戻すと直る」\n"
  "症状の正体がこれです。")
A("遊戲根目錄", "Game root folder", "ゲームのルートフォルダー")
A("　讀角色卡預設開這裡", "　Character cards open here by default",
  "　キャラカードの既定の場所")
A("　讀服裝卡預設開這裡", "　Outfit cards open here by default",
  "　コーデカードの既定の場所")
A("預設輸出資料夾", "Default output folder", "既定の出力フォルダー")
A("監看資料夾", "Watch folder", "監視フォルダー")
A("開啟程式時自動開始監看", "Start watching when the program opens",
  "起動時に自動で監視を開始する")
A("在檔案總管開啟這個資料夾", "Open this folder in Explorer",
  "このフォルダーをエクスプローラーで開く")
A("填了根目錄就自動帶成：讀角色卡 {0}、讀服裝卡 {1}、輸出與工單 {2}。想改直接改。",
  "Filling in the game root fills these in: character cards {0}, outfit "
  "cards {1}, output and jobs {2}. Edit them directly if you want something else.",
  "ルートフォルダーを入力すると自動で設定されます：キャラカード {0}、"
  "コーデカード {1}、出力とジョブ {2}。変更したい場合は直接編集してください。")
A("插件把工單寫成 *{0} 丟進這個資料夾，處理完會在旁邊出現同名的 *{1}",
  "The plugin drops jobs named *{0} into this folder; when one is done a "
  "matching *{1} appears next to it",
  "プラグインは *{0} という名前のジョブをこのフォルダーに書き出します。"
  "処理が終わると同名の *{1} が隣に作られます")
A("選遊戲根目錄", "Choose the game root folder", "ゲームのルートフォルダーを選択")
A("選角色卡資料夾", "Choose the character-card folder", "キャラカードのフォルダーを選択")
A("選服裝卡資料夾", "Choose the outfit-card folder", "コーデカードのフォルダーを選択")
A("選輸出資料夾", "Choose the output folder", "出力フォルダーを選択")
A("選監看資料夾", "Choose the watch folder", "監視フォルダーを選択")
A("設定已儲存到 {0}", "Settings saved to {0}", "設定を {0} に保存しました")

# ---- 狀態列、紀錄 ----
A("就緒", "Ready", "準備完了")
A("未監看", "Not watching", "監視していません")
A("監看中：{0}", "Watching: {0}", "監視中：{0}")
A("開始監看 {0}", "Started watching {0}", "{0} の監視を開始しました")
A("處理中…", "Working...", "処理中...")
A("處理中：{0}", "Working: {0}", "処理中：{0}")
A("工單", "Job", "ジョブ")
# 這是工單表格的「做了哪個操作」，不是恋活的「動作」換裝分類
A("動作", "Action", "操作")
A("收到工單 {0}", "Job received: {0}", "ジョブを受け取りました：{0}")
A("時間", "Time", "時刻")
A("狀態", "Status", "状態")
A("完成：{0} — {1}", "Done: {0} — {1}", "完了：{0} — {1}")
A("失敗：{0} — {1}", "Failed: {0} — {1}", "失敗：{0} — {1}")
A("{0}（{1} 秒）", "{0} ({1} s)", "{0}（{1} 秒）")
A("[注意] {0}", "[note] {0}", "[注意] {0}")
A("── 前置：{0} 步", "── Preparation: {0} step(s)", "── 前処理：{0} ステップ")
A("── 第 {0}/{1} 步：{2}", "── Step {0}/{1}: {2}", "── ステップ {0}/{1}：{2}")
A("附加飾品到第 {0} 套", "Adding accessories to outfit {0}",
  "コーデ {0} にアクセサリを追加します")
A("先把服裝卡飾品附加到來源第 {0} 套，再移植",
  "Adding the outfit card's accessories to source outfit {0} first, "
  "then transplanting",
  "先にコーデカードのアクセサリを元コーデ {0} に追加してから移植します")
A("移植 {0}", "Transplanting {0}", "移植します {0}")
A("已清掉附加服裝卡，執行時會直接移植",
  "Extra outfit card cleared — the run will transplant directly",
  "追加コーデカードをクリアしました。実行時はそのまま移植します")

# ---- 錯誤與提醒 ----
A("先讀一張角色卡", "Load a character card first", "先にキャラカードを読み込んでください")
A("角色卡和服裝卡都要先讀進來",
  "Both the character card and the outfit card have to be loaded first",
  "キャラカードとコーデカードの両方を先に読み込んでください")
A("來源卡和目標卡都要先讀進來",
  "Both the source card and the target card have to be loaded first",
  "元カードと対象カードの両方を先に読み込んでください")
A("至少勾一個服裝槽", "Tick at least one outfit slot",
  "コーデスロットを少なくとも 1 つ選択してください")
A("輸出的服裝槽有重複，後面那筆會蓋掉前面的——先改掉再執行",
  "Two rows output to the same outfit slot; the later one would overwrite "
  "the earlier one — fix that before running",
  "出力先のコーデスロットが重複しています。後のものが前のものを上書き"
  "します。実行前に修正してください")
A("監看資料夾不存在，先選一個",
  "The watch folder does not exist — choose one first",
  "監視フォルダーが存在しません。先に選択してください")
A("預設監看資料夾不存在：{0}　→ 到「設定」填遊戲根目錄",
  "The default watch folder does not exist: {0}　→ set the game root "
  "folder under \"Settings\"",
  "既定の監視フォルダーが存在しません：{0}　→「設定」でゲームのルート"
  "フォルダーを指定してください")
A("寫不出結果檔 {0}", "Could not write the result file {0}",
  "結果ファイル {0} を書き出せませんでした")
A("結果檔寫不出來 {0}：{1}", "Could not write the result file {0}: {1}",
  "結果ファイル {0} を書き出せませんでした：{1}")
A("工單解析失敗 {0}：{1}", "Could not parse job {0}: {1}",
  "ジョブ {0} の解析に失敗しました：{1}")
A("不認得的 op：{0}", "Unknown op: {0}", "不明な op：{0}")

# ---- kkmerge：卡片結構 ----
A("不是合法的 PNG 開頭", "Not a valid PNG header", "正しい PNG ヘッダーではありません")
A("PNG 資料不完整", "PNG data is incomplete", "PNG データが不完全です")
A("PNG chunk 長度越界", "PNG chunk length out of bounds",
  "PNG チャンク長が範囲外です")
A("這不是角色卡（標識為 {0!r}）",
  "This is not a character card (marker is {0!r})",
  "これはキャラカードではありません（識別子は {0!r}）")
A("這不是服裝卡（標識為 {0!r}）",
  "This is not an outfit card (marker is {0!r})",
  "これはコーデカードではありません（識別子は {0!r}）")
A("服裝卡尾端不是 KKEx 而是 {0!r}",
  "The outfit card does not end with KKEx but with {0!r}",
  "コーデカードの末尾が KKEx ではなく {0!r} です")
A("角色卡沒有 KKEx 區塊，沒有任何外掛資料可以合併",
  "The character card has no KKEx block — there is no plugin data to merge",
  "キャラカードに KKEx ブロックがありません。合成できるプラグインデータが"
  "ありません")
A("服裝卡裡沒有任何飾品", "The outfit card has no accessories at all",
  "コーデカードにアクセサリが 1 つもありません")
A("coordinate 資料段長度對不上：{0} vs {1}",
  "Coordinate segment length mismatch: {0} vs {1}",
  "coordinate データ長が一致しません：{0} と {1}")
A("換裝編號超出範圍：{0}，這張卡有 {1} 套",
  "Outfit index out of range: {0}; this card has {1}",
  "コーデ番号が範囲外です：{0}。このカードには {1} 着あります")
A("來源換裝編號超出範圍：{0}（來源有 {1} 套）",
  "Source outfit index out of range: {0} (the source has {1})",
  "元コーデ番号が範囲外です：{0}（元カードには {1} 着）")
A("目標換裝編號超出範圍：{0}（目標有 {1} 套）",
  "Target outfit index out of range: {0} (the target has {1})",
  "対象コーデ番号が範囲外です：{0}（対象カードには {1} 着）")

# ---- kkmerge：msgpack ----
A("頂層不是 msgpack map", "The top level is not a msgpack map",
  "最上位が msgpack map ではありません")
A("不是 msgpack map", "Not a msgpack map", "msgpack map ではありません")
A("不是 msgpack 陣列", "Not a msgpack array", "msgpack 配列ではありません")
A("未知的 msgpack 型別位元組 0x{0:02x}",
  "Unknown msgpack type byte 0x{0:02x}",
  "不明な msgpack 型バイト 0x{0:02x}")
A("7-bit 變長整數過長", "7-bit varint is too long", "7 ビット可変長整数が長すぎます")
A("讀取越界：位置 {0} 需要 {1} 位元組",
  "Read out of bounds: position {0} needs {1} byte(s)",
  "読み取りが範囲外です：位置 {0} に {1} バイト必要です")

# ---- kkmerge：搬移結果 ----
A("{0}：搬入第 {1} 套的資料 -> 第 {2} 套",
  "{0}: moved outfit {1}'s data -> outfit {2}",
  "{0}：コーデ {1} のデータを コーデ {2} へ移動しました")
A("{0}/{1} 看起來是換裝層級資料，不在支援清單中，未搬移",
  "{0}/{1} looks like outfit-level data but is not on the supported list — "
  "not moved",
  "{0}/{1} はコーデ単位のデータに見えますが対応リストにないため移動して"
  "いません")
A("{0} 的 key 名稱帶換裝索引（{1}…），不在支援清單中，未搬移",
  "{0} has outfit indices in its key names ({1}...) but is not on the "
  "supported list — not moved",
  "{0} はキー名にコーデ番号を含みますが（{1}…）対応リストにないため移動して"
  "いません")
A("outfit{0} 飾品欄位 {1} -> {2}（搬入 {3} 個）；其他套不動",
  "outfit{0} accessory slots {1} -> {2} ({3} moved in); other outfits untouched",
  "outfit{0} のアクセサリスロット {1} -> {2}（{3} 個を移動）。他のコーデは"
  "変更しません")
A("換裝 {0} -> {1}；該套飾品欄位 {2} -> {3}（其他套不動）",
  "Outfit {0} -> {1}; that outfit's accessory slots {2} -> {3} "
  "(other outfits untouched)",
  "コーデ {0} -> {1}。そのコーデのアクセサリスロット {2} -> {3}"
  "（他のコーデは変更しません）")
A("Sideloader 條目 +{0}", "Sideloader entries +{0}", "Sideloader エントリ +{0}")
A("Sideloader 條目：換上 {0} 筆", "Sideloader entries: {0} replaced",
  "Sideloader エントリ：{0} 件を差し替え")
A("MaterialEditor 貼圖 +{0}、條目 +{1}",
  "MaterialEditor textures +{0}, entries +{1}",
  "MaterialEditor テクスチャ +{0}、エントリ +{1}")
A("MaterialEditor：條目 {0} 筆、貼圖 +{1} / 清掉 {2} 張孤兒",
  "MaterialEditor: {0} entries, textures +{1} / {2} orphan(s) removed",
  "MaterialEditor：エントリ {0} 件、テクスチャ +{1} / 孤立 {2} 枚を削除")
A("KSOX 皮膚 overlay：跟著服裝搬了 {0} 張",
  "KSOX skin overlay: {0} moved with the outfit",
  "KSOX 肌オーバーレイ：服と一緒に {0} 枚を移動")
A("KSOX 皮膚 / 眼睛 overlay：保留目標人物的（不隨服裝移植）",
  "KSOX skin / eye overlay: kept the target's own (not transplanted "
  "with the outfit)",
  "KSOX 肌・瞳オーバーレイ：対象の人物のものを保持（服と一緒に移植しません）")
A("KSOX 皮膚/臉/眼睛 overlay：預設保留目標人物的",
  "KSOX skin/face/eye overlay: the target's own are kept by default",
  "KSOX 肌・顔・瞳オーバーレイ：既定では対象の人物のものを保持します")
A("KSOX：目標第 {0} 套原本沒有 overlay，已從其他套補上",
  "KSOX: target outfit {0} had no overlay — filled in from another outfit",
  "KSOX：対象のコーデ {0} にオーバーレイが無かったため、他のコーデから"
  "補いました")
A("清除指向空欄位的殘渣：{0} -{1} 筆",
  "Stripped leftovers pointing at empty slots: {0} -{1}",
  "空きスロットを指す残存データを削除：{0} -{1} 件")

# ---- kkmerge：命令列 ----
A("介面語言：0=繁體中文 1=English 2=日本語",
  "Interface language: 0=Traditional Chinese 1=English 2=Japanese",
  "表示言語：0=繁体字中国語 1=英語 2=日本語")
A("印出卡片資訊後結束", "Print the card's information and exit",
  "カード情報を表示して終了します")
A("要合併到第幾套換裝（0 起算）",
  "Which outfit to merge into (0-based)",
  "何着目のコーデに合成するか（0 から数えます）")
A("只搬指定欄位，逗號分隔，例：0,3,7",
  "Move only these slots, comma separated, e.g. 0,3,7",
  "指定したスロットのみ移動します。カンマ区切り、例：0,3,7")
A("append=服裝卡飾品附加（預設）；transplant=整套換裝移植",
  "append = add the outfit card's accessories (default); "
  "transplant = move a whole outfit",
  "append＝コーデカードのアクセサリを追加（既定）、"
  "transplant＝コーデ一式を移植")
A("transplant：來源角色卡", "transplant: source character card",
  "transplant：元キャラカード")
A("transplant：目標角色卡", "transplant: target character card",
  "transplant：対象キャラカード")
A("transplant：來源第幾套", "transplant: which source outfit",
  "transplant：元の何着目")
A("transplant：目標第幾套", "transplant: which target outfit",
  "transplant：対象の何着目")
A("只輸出最後那行 JSON", "Print only the final JSON line",
  "最後の JSON 行のみ出力します")
A("把結果 JSON 另外寫到這個檔案（UTF-8）。呼叫端不必處理 stdout 編碼，讀檔就好",
  "Also write the result JSON to this file (UTF-8), so the caller can read "
  "a file instead of dealing with stdout encoding",
  "結果の JSON をこのファイルにも書き出します（UTF-8）。呼び出し側は "
  "stdout のエンコーディングを気にせずファイルを読むだけで済みます")
A("不要清除指向空飾品欄位的殘留擴充資料",
  "Do not strip leftover plugin data pointing at empty accessory slots",
  "空きアクセサリスロットを指す残存プラグインデータを削除しません")
A("不清除沒人引用的孤兒貼圖",
  "Do not remove orphan textures that nothing references",
  "参照されていない孤立テクスチャを削除しません")
A("需要 --chara / --coord / --out（或改用 --info）",
  "--chara / --coord / --out are required (or use --info instead)",
  "--chara / --coord / --out が必要です（または --info を使用）")
A("transplant 需要 --src / --dst / --out",
  "transplant requires --src / --dst / --out",
  "transplant には --src / --dst / --out が必要です")
A("clean 需要 --chara / --out", "clean requires --chara / --out",
  "clean には --chara / --out が必要です")
A("來源服裝卡 png", "Source outfit card png", "元コーデカードの png")
A("目標角色卡 png", "Target character card png", "対象キャラカードの png")
A("輸出角色卡 png", "Output character card png", "出力キャラカードの png")
A("移植整套換裝", "Transplant a whole outfit", "コーデ一式を移植")


# ---------------------------------------------------------------- 自我檢查

_PH = re.compile(r"\{(\d+)(![rsa])?(:[^}]*)?\}")


def check():
    """每一條翻譯的格式參數要跟中文那條對得上。

    順序可以不一樣（各語言語序不同），但**用到的編號集合**和
    每個編號的轉換／格式規格必須一致 —— 不然 .format() 會在執行時
    炸在使用者面前，而且多半是在最少人走到的那條錯誤訊息上。

    回傳 [(key, 語言, 說明)]，沒問題就是空的。
    """
    bad = []
    for zh, row in _TABLE.items():
        want = {}
        for m in _PH.finditer(zh):
            want[m.group(1)] = (m.group(2) or "", m.group(3) or "")
        for li in (EN, JA):
            s = row[li]
            if not s:
                continue
            got = {}
            for m in _PH.finditer(s):
                got[m.group(1)] = (m.group(2) or "", m.group(3) or "")
            if set(got) != set(want):
                bad.append((zh, NAMES[li],
                            "參數編號對不上：中文 %s、譯文 %s"
                            % (sorted(want) or "無", sorted(got) or "無")))
                continue
            for k in want:
                if got[k] != want[k]:
                    bad.append((zh, NAMES[li],
                                "{%s} 的格式不一樣：中文 %r、譯文 %r"
                                % (k, want[k], got[k])))
    return bad


def coverage():
    """回傳 (總條數, 英文沒填, 日文沒填)。"""
    n = len(_TABLE)
    return (n,
            sum(1 for r in _TABLE.values() if not r[EN]),
            sum(1 for r in _TABLE.values() if not r[JA]))


if __name__ == "__main__":
    total, no_en, no_ja = coverage()
    print("翻譯表 %d 條；英文缺 %d、日文缺 %d" % (total, no_en, no_ja))
    problems = check()
    for k, lang, why in problems:
        print("  [%s] %s\n      %s" % (lang, k.replace("\n", "\\n")[:60], why))
    print("格式參數檢查：%s" % ("全部通過" if not problems else "%d 個問題" % len(problems)))
