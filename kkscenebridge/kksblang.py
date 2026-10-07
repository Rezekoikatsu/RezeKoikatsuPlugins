# -*- coding: utf-8 -*-
"""kksblang —— kkscenebridge 的介面語言（繁體中文 / English / 日本語）。

跟 kkbridge 的 kklang.py、三個插件的 Lang.cs 同一套做法：

    **中文原文自己就是 key。**

查不到就回傳中文原文，所以漏翻不會壞，只是那一行顯示中文。

語言存在 kkscenebridge_settings.json 的 "lang"（0 中文、1 English、2 日本語），
這個模組一被 import 就先讀它 —— 各分頁有些字串在 import 時就算好了
（表頭、下拉選單），所以語言必須在那之前決定。換語言要重新啟動。

翻譯範圍：介面文字與重要訊息（開始、完成、錯誤、[提醒]、[注意]）。
合併核心（kkscenemerge.py 等）的技術紀錄維持中文。

格式參數：翻譯裡的 {0} {1}、%s %d 要跟中文那條一樣（{n} 可以換順序）。
check() 會把對不上的抓出來。
"""

import json
import os
import re
import sys

ZH, EN, JA = 0, 1, 2
NAMES = ("繁體中文", "English", "日本語")

Current = ZH

_TABLE = {}


def A(zh, en, ja):
    _TABLE[zh] = (zh, en, ja)


def set_lang(i):
    global Current
    try:
        i = int(i)
    except (TypeError, ValueError):
        i = ZH
    Current = i if i in (ZH, EN, JA) else ZH
    return Current


def name(i=None):
    i = Current if i is None else i
    return NAMES[i] if 0 <= i < len(NAMES) else NAMES[ZH]


def T(zh):
    """查表。沒有這條、或那個語言留空，就退回中文原文。"""
    row = _TABLE.get(zh)
    if not row:
        return zh
    return row[Current] or zh


def _settings_path():
    if getattr(sys, "frozen", False):
        base = os.path.dirname(sys.executable)
    else:
        base = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(base, "kkscenebridge_settings.json")


def load_from_settings():
    try:
        with open(_settings_path(), encoding="utf-8") as f:
            set_lang(json.load(f).get("lang", ZH))
    except Exception:                                   # noqa: BLE001
        set_lang(ZH)
    return Current


# ---------------------------------------------------------------- 翻譯表
#            繁體中文 / English / 日本語

A('合併場景',
  'Merge scenes',
  'シーン結合')
A('讀卡：{0}',
  'Reading card: {0}',
  'カード読み込み：{0}')
A('完成：{0}（{1}，總時長 {2}）',
  'Done: {0} ({1}, total length {2})',
  '完了：{0}（{1}、総時間 {2}）')
A('  [音頻] 沒有任何一段配到音檔，跳過',
  '  [Audio] No segment has an audio file, skipped',
  '  [音声] どのシーンにも音声ファイルがないためスキップ')
A('  [音頻] [{0}] {1} → 群組「{2}」 trigger={3}：',
  '  [Audio] [{0}] {1} → group "{2}" trigger={3}:',
  '  [音声] [{0}] {1} → グループ「{2}」 trigger={3}：')
A('  [音頻] {0} 沒有配音檔',
  '  [Audio] {0} has no voice file',
  '  [音声] {0} に音声ファイルがありません')
A('  [音頻][注意] ',
  '  [Audio][Warning] ',
  '  [音声][注意] ')
A('  [音頻] 共 {0} 個音頻、{1} 個群組（★＝100%，{2}）',
  '  [Audio] {0} sounds, {1} groups in total (★ = 100%, {2})',
  '  [音声] 音声 {0} 個、グループ {1} 個（★＝100%、{2}）')
A('  時長 {0:g} -> {1:g} 秒',
  '  Length {0:g} -> {1:g} s',
  '  長さ {0:g} -> {1:g} 秒')
A('瀏覽…',
  'Browse…',
  '参照…')
A('開啟',
  'Open',
  '開く')
A('換回預設位置',
  'Reset to default location',
  '既定の場所に戻す')
A('已經是預設值：\n',
  'Already the default:\n',
  'すでに既定値です：\n')
A('還沒有預設值（先填遊戲根目錄）',
  'No default yet (set the game folder first)',
  '既定値がありません（先にゲームフォルダを指定）')
A('換回預設：\n',
  'Reset to default:\n',
  '既定値に戻す：\n')
A('順序',
  'Order',
  '順番')
A('縮圖',
  'Thumbnail',
  'サムネ')
A('檔案',
  'File',
  'ファイル')
A('時長',
  'Length',
  '長さ')
A('節點',
  'Nodes',
  'ノード')
A('相機',
  'Camera',
  'カメラ')
A('包裝資料夾名稱',
  'Wrapper folder name',
  'ラッパーフォルダ名')
A('音頻',
  'Audio',
  '音声')
A('狀態',
  'Status',
  '状態')
A('等待讀卡',
  'Waiting to read',
  '読み込み待ち')
A('timeline 相機路徑',
  'Timeline camera path',
  'Timeline カメラパス')
A('已整理過',
  'Already prepared',
  '整理済み')
A('{0} 台相機，請選',
  '{0} cameras, pick one',
  'カメラ {0} 台、選んでください')
A('讀取失敗',
  'Failed to read',
  '読み込み失敗')
A('第 {0} 張縮短成 {1}（原本 {2}）—— 超出新結尾的關鍵影格會被拿掉，動畫等於剪短',
  'Card {0} shortened to {1} (was {2}) — keyframes past the new end are removed, the animation is cut',
  '{0} 枚目を {1} に短縮（元 {2}）— 新しい終端を超えるキーフレームは削除されます')
A('加長＝最後一幀多停一會；縮短＝超出的關鍵影格會被拿掉。\n原本 {0}，輸入 15 或 00:15.00 都可以。',
  'Longer = hold the last frame; shorter = keyframes past the end are removed.\nWas {0}; enter 15 or 00:15.00.',
  '延長＝最後のフレームで静止、短縮＝終端を超えるキーフレームを削除。\n元 {0}。15 や 00:15.00 の形式で入力。')
A('原本 {0}，',
  'Was {0}, ',
  '元 {0}、')
A('多停 {0:.2f} 秒（後面的場景一起延後）',
  'holds {0:.2f} s longer (later scenes shift later)',
  '{0:.2f} 秒延長（後のシーンも遅れます）')
A('剪短 {0:.2f} 秒（超出的關鍵影格會被拿掉，後面的場景一起提前）',
  'cut by {0:.2f} s (extra keyframes removed, later scenes shift earlier)',
  '{0:.2f} 秒短縮（超過分のキーフレームは削除、後のシーンも早まります）')
A('⚠ 時長不夠：動畫其實到 {0}，但時長只算到 {1}（短 {2:.2f} 秒）。\n超出的關鍵影格會被刪掉，後面每一段也會提早。\n解法：把這一格改成 {3}。',
  '⚠ Length too short: the animation actually runs to {0}, but the length is only {1} ({2:.2f} s short).\nKeyframes past it will be removed and every later segment starts early.\nFix: set this cell to {3}.',
  '⚠ 長さ不足：アニメは {0} まであるのに、長さは {1} しかありません（{2:.2f} 秒不足）。\n超えたキーフレームは削除され、後のシーンも早まります。\n対処：このセルを {3} にしてください。')
A('timeline 相機路徑（生一台相機接手）',
  'Timeline camera path (new camera takes over)',
  'Timeline カメラパス（新しいカメラが引き継ぐ）')
A('這張卡用 timeline 的相機軌道運鏡。選這個會生一台相機接手整段路徑；\n選別台相機的話，那兩條軌道會被砍掉。',
  'This card moves the camera with Timeline camera tracks. This option creates a camera that takes over the whole path;\npicking another camera deletes those two tracks.',
  'このカードは Timeline のカメラトラックでカメラワークしています。これを選ぶと新しいカメラがパスを引き継ぎます。\n別のカメラを選ぶとその 2 本のトラックは削除されます。')
A('  自動新增（鎖初始視角）',
  '  Auto-create (lock initial view)',
  '  自動作成（初期視点を固定）')
A('沒有相機，合併時會自動生一台鎖在這張卡存檔時的視角\n位置 {0:.3f}, {1:.3f}, {2:.3f}',
  'No camera. Merging will create one locked to the view saved in this card\nPosition {0:.3f}, {1:.3f}, {2:.3f}',
  'カメラなし。結合時にこのカード保存時の視点で固定したカメラを自動作成します\n位置 {0:.3f}, {1:.3f}, {2:.3f}')
A('{0} 個 · {1}',
  '{0} files · {1}',
  '{0} 個 · {1}')
A('選音頻…',
  'Pick audio…',
  '音声を選択…')
A('為這一段場景挑音檔。\n合併時會自動建群組（名稱＝包裝資料夾名稱）、綁該段的 (SFX) 觸發。',
  "Choose audio files for this scene.\nMerging creates a group (named after the wrapper folder) triggered by this segment's (SFX).",
  'このシーンの音声を選びます。\n結合時にグループ（名前＝ラッパーフォルダ名）を作り、このシーンの (SFX) で再生します。')
A('沒有相機',
  'No camera',
  'カメラなし')
A('沒有相機，自動生一台',
  'No camera, one will be created',
  'カメラなし、自動作成します')
A('請先在設定頁指定音頻根目錄',
  'Set the audio root folder in Settings first',
  '先に設定タブで音声ルートフォルダを指定してください')
A('音頻根目錄底下找不到對得上的資料夾，請用每列的按鈕自己選',
  "No matching folder under the audio root; pick one with each row's button",
  '音声ルートに一致するフォルダがありません。各行のボタンで選んでください')
A('自動配對：{0} → 共 {1} 個音檔',
  'Auto-match: {0} → {1} audio files',
  '自動マッチ：{0} → 音声 {1} 個')
A('，{0} 個配不出去',
  ', {0} unmatched',
  '、{0} 個は未割り当て')
A('至少要一張場景卡',
  'Add at least one scene card',
  'シーンカードが 1 枚以上必要です')
A('第 {0} 張還在讀卡',
  'Card {0} is still loading',
  '{0} 枚目はまだ読み込み中です')
A('第 {0} 張讀不起來',
  'Card {0} could not be read',
  '{0} 枚目を読み込めません')
A('第 {0} 張沒有相機（可在設定勾「沒有相機時自動新增」）',
  'Card {0} has no camera (enable "Auto-create when no camera" in Settings)',
  '{0} 枚目にカメラがありません（設定の「カメラがなければ自動作成」をオン）')
A('卡片的 studio 版本不一致：{0}',
  'Cards have different Studio versions: {0}',
  'カードの Studio バージョンが一致しません：{0}')
# ---- 合併引擎裡要使用者照著做的訊息（kkscenemerge.py 用 _T 查這張表）----
A('、',
  ', ',
  '、')
A('指定要存成 {0}，但這幾張卡沒有這個版本（{1}），改用最新的',
  'Asked to save as {0}, but none of these cards has that version ({1}); using the newest instead',
  '{0} で保存するよう指定されましたが、このバージョンのカードがありません（{1}）。最新のバージョンを使います')
A('{0} 個物件的動畫樣式',
  'the animation pattern of {0} item(s)',
  'アイテム {0} 個のアニメパターン')
A('天空設定',
  'sky settings',
  '空の設定')
A('場景的著色類型（shaderType）',
  'the scene shader type (shaderType)',
  'シーンのシェーダータイプ（shaderType）')
A('  studio 版本不一致（{0}）→ 合併卡存成 {1}',
  '  Studio versions differ ({0}) -> merged card saved as {1}',
  '  Studio バージョンが一致しません（{0}）→ 結合カードは {1} で保存')
A('這幾張卡的 studio 版本不一樣（{0}；1.0.x 是 Koikatsu、1.1.x 是 Koikatsu Sunshine 存的卡），合併卡存成 {1}。',
  'These cards have different Studio versions ({0}; 1.0.x is saved by Koikatsu, 1.1.x by Koikatsu Sunshine). The merged card is saved as {1}. ',
  'カードの Studio バージョンが違います（{0}。1.0.x はコイカツ、1.1.x はコイカツサンシャインで保存したカード）。結合カードは {1} で保存します。')
A('新版才有的欄位不會存進去（這次會掉：{0}）。',
  'Fields that only exist in the newer version are not saved (lost this time: {0}). ',
  '新しいバージョンにしかない項目は保存されません（今回失われるもの：{0}）。')
A('新版才有的欄位不會存進去（這次沒有用到那些欄位）。',
  'Fields that only exist in the newer version are not saved (none of them were in use this time). ',
  '新しいバージョンにしかない項目は保存されません（今回は使われていませんでした）。')
A('較新那幾張卡裡的角色是新版格式的人物資料，遊戲讀不讀得了要進遊戲確認',
  'The characters in the newer cards are stored in the newer character format — check in game whether they load',
  '新しい方のカードのキャラは新しい形式のキャラデータです。読み込めるかどうかはゲームで確認してください')
A('合併卡要用開得了 {0} 那張原卡的遊戲／外掛來開，裡面各段的角色、物件認不認得也跟單獨開原卡時一樣',
  'Open the merged card with the game / plugins that can open the {0} source card; whether each segment\'s characters and items are recognised is the same as when that source card is opened alone',
  '結合カードは {0} の元カードを開ける環境（ゲーム／プラグイン）で開いてください。各シーンのキャラやアイテムが認識されるかどうかは、元カードを単体で開いたときと同じです')
A('KKPE 碰撞器隔離失敗（合併照常完成）：{0}',
  'KKPE collider isolation failed (the merge itself completed): {0}',
  'KKPE コライダーの分離に失敗しました（結合自体は完了）：{0}')
A('(沒有名字)',
  '(no name)',
  '(名前なし)')
A('(角色)',
  '(character)',
  '(キャラ)')
A('KKPE 碰撞器：有不同的角色/物件在 KKPE 裡的編號（uniqueId）相同，碰撞器的設定可能套到別人身上；載入後請用 F6 的「一鍵修復碰撞器綁定」',
  'KKPE colliders: different characters / items share the same KKPE id (uniqueId), so collider settings may land on the wrong one; use F6 "Repair collider bindings" after loading',
  'KKPE コライダー：別のキャラ／アイテムが KKPE 内で同じ番号（uniqueId）を持っているため、コライダーの設定が別の対象に適用される可能性があります。読み込み後に F6 の「コライダー結合を一括修復」を使ってください')
A('  KKPE 碰撞器：{0} 顆碰撞器補上 {1} 筆「別段的角色/物件不吃這顆」（不補的話別段的人整個會被它拉住）',
  '  KKPE colliders: added {1} "disabled for other segments" entries to {0} collider(s) (without them, characters of other segments get pulled by the collider)',
  '  KKPE コライダー：{0} 個のコライダーに「他のシーンのキャラ／アイテムには効かせない」項目を {1} 件追加しました（追加しないと他のシーンのキャラが引っ張られます）')
A(' 等 {0} 個角色',
  ' and others ({0} characters in total)',
  ' ほか（合計 {0} キャラ）')
A('KKPE 碰撞器：{0} 的髮型/衣服/飾品跟碰撞器清單裡有資料的角色不同，只關得到共通的動骨（胸、臀、裙子…），它自己獨有的髮型/飾品動骨卡片裡沒有清單，補不到；載入後如果還有被拉住的地方，用 F6 的「一鍵修復碰撞器綁定」',
  'KKPE colliders: {0} use hair / clothes / accessories different from the characters the collider lists know about, so only the common dynamic bones (breasts, hips, skirt…) could be disabled; the card has no list of their own hair / accessory bones. If something is still pulled after loading, use F6 "Repair collider bindings"',
  'KKPE コライダー：{0} は、コライダーのリストにデータがあるキャラと髪型／衣装／アクセサリーが違うため、共通の揺れ物（胸・尻・スカート…）しか無効にできません。固有の髪型／アクセサリーの揺れ物はカードに一覧が無いため追加できません。読み込み後にまだ引っ張られる箇所があれば、F6 の「コライダー結合を一括修復」を使ってください')
A('KKPE 碰撞器：有不同的角色/物件在 KKPE 裡的編號（uniqueId）相同，這幾個沒有補；載入後如果碰撞器怪怪的，用 F6 的「一鍵修復碰撞器綁定」',
  'KKPE colliders: different characters / items share the same KKPE id (uniqueId); those were skipped. If colliders behave oddly after loading, use F6 "Repair collider bindings"',
  'KKPE コライダー：別のキャラ／アイテムが KKPE 内で同じ番号（uniqueId）を持っているため、それらには追加していません。読み込み後にコライダーの動きがおかしければ、F6 の「コライダー結合を一括修復」を使ってください')
A('KKPE 碰撞器：要補的筆數超過 {0}，後面的碰撞器沒有補（載入後用 F6 的「一鍵修復碰撞器綁定」）',
  'KKPE colliders: more than {0} entries would be needed, so the remaining colliders were left as they are (use F6 "Repair collider bindings" after loading)',
  'KKPE コライダー：追加が必要な件数が {0} を超えたため、残りのコライダーには追加していません（読み込み後に F6 の「コライダー結合を一括修復」を使ってください）')
A('合併後的場景名稱',
  'Merged scene name',
  '結合後のシーン名')
A('留空＝自動命名（共同名稱_merge_時間）',
  'Empty = automatic (common name_merge_time)',
  '空欄＝自動（共通名_merge_時刻）')
A('合併出來那張場景卡的檔名（不用打 .png）。\n留空就照以前的方式自動取名：<各卡的共同名稱>_merge_<時間>.png。\nF7 的設定檔會跟著用同一個名字。',
  'File name of the merged scene card (no need to type .png).\nLeave empty to name it automatically as before: <common name of the cards>_merge_<time>.png.\nThe F7 config file uses the same name.',
  '結合したシーンカードのファイル名（.png は不要）。\n空欄なら従来どおり自動で名前を付けます：<カードの共通名>_merge_<時刻>.png。\nF7 の設定ファイルも同じ名前になります。')
A('合併後的場景名稱跟清單裡的來源卡一樣，會把來源卡蓋掉。請換一個名稱。',
  'The merged scene name is the same as one of the source cards and would overwrite it. Choose another name.',
  '結合後のシーン名がリスト内の元カードと同じで、上書きしてしまいます。別の名前にしてください。')
A('（請選擇）',
  '(choose one)',
  '（選択してください）')
A('⚠ 偵測到不同的 studio 版本（{0}），合併卡存成：',
  '⚠ Different Studio versions detected ({0}). Save the merged card as:',
  '⚠ 異なる Studio バージョンを検出しました（{0}）。結合カードの保存形式：')
A('混用不同版本的卡可能會出問題，合併後請進遊戲確認',
  'Mixing cards of different versions may cause problems — check the result in game',
  'バージョンの違うカードを混ぜると問題が出ることがあります。結合後にゲームで確認してください')
A('卡片的 studio 版本不一樣，請先在「選項」選要存成哪個版本',
  'The cards have different Studio versions — choose which version to save as under "Options" first',
  'カードの Studio バージョンが違います。先に「オプション」で保存するバージョンを選んでください')
A('卡片的 studio 版本不一樣（{0}）。\n請先在「選項」選合併卡要存成哪個版本。\n\n混用不同版本的卡可能會出問題，合併後請進遊戲確認。',
  'The cards have different Studio versions ({0}).\nChoose which version the merged card is saved as under "Options" first.\n\nMixing cards of different versions may cause problems — check the result in game.',
  'カードの Studio バージョンが違います（{0}）。\n先に「オプション」で結合カードを保存するバージョンを選んでください。\n\nバージョンの違うカードを混ぜると問題が出ることがあります。結合後にゲームで確認してください。')
A('合併卡只能用一個版本存。\n・存成較新的：舊卡的內容都留得住；要用開得了新版本原卡的遊戲／外掛來開。\n・存成較舊的：新版本才有的欄位不會存進去（物件的動畫樣式、天空設定、著色類型）。\n不管選哪個，另一個版本那幾段的角色、物件遊戲認不認得都要進遊戲確認。',
  'The merged card can only be saved as one version.\n- Newer: everything from the older cards is kept; open it with the game / plugins that can open the newer source card.\n- Older: fields that only exist in the newer version are not saved (item animation pattern, sky settings, shader type).\nEither way, check in game whether the characters and items from the other version are recognised.',
  '結合カードは 1 つのバージョンでしか保存できません。\n・新しい方：古いカードの内容はすべて残ります。新しい方の元カードを開ける環境（ゲーム／プラグイン）で開いてください。\n・古い方：新しいバージョンにしかない項目は保存されません（アイテムのアニメパターン、空の設定、シェーダータイプ）。\nどちらを選んでも、もう一方のバージョンのキャラやアイテムが認識されるかはゲームで確認してください。')
A('{0} {1} — 恋活場景卡合併',
  '{0} {1} — Koikatsu scene card merger',
  '{0} {1} — コイカツ シーンカード結合')
A('添加動畫音頻',
  'Cutscene audio',
  'ムービー音声')
A('VNGE音頻',
  'VNGE audio',
  'VNGE 音声')
A('整理',
  'Organize',
  '整理')
A('設定',
  'Settings',
  '設定')
A('紀錄',
  'Log',
  'ログ')
A('就緒',
  'Ready',
  '準備完了')
A('先在「資料夾名稱」輸入共同名稱',
  'Enter a common name in "Folder name" first',
  '先に「フォルダ名」に共通名を入力してください')
A('清單是空的，先加卡片',
  'The list is empty, add cards first',
  'リストが空です。先にカードを追加してください')
A('包裝資料夾名稱：{0}_(1) ~ {1}_({2})',
  'Wrapper folder names: {0}_(1) ~ {1}_({2})',
  'ラッパーフォルダ名：{0}_(1) ～ {1}_({2})')
A('（{0} 張已整理過、名稱鎖住，沒改）',
  '({0} already prepared with locked names, unchanged)',
  '（{0} 枚は整理済みで名前固定のため変更なし）')
A('[提醒] ',
  '[Note] ',
  '[お知らせ] ')
A('新增卡片…',
  'Add cards…',
  'カードを追加…')
A('移除',
  'Remove',
  '削除')
A('上移 ↑',
  'Move up ↑',
  '上へ ↑')
A('下移 ↓',
  'Move down ↓',
  '下へ ↓')
A('清空',
  'Clear',
  'クリア')
A('全部配音頻',
  'Match all audio',
  '音声を一括割り当て')
A('資料夾名稱',
  'Folder name',
  'フォルダ名')
A('例：Scenecard',
  'e.g. Scenecard',
  '例：Scenecard')
A('輸入共同名稱後按 Enter，依目前順序設成 名稱_(1)、名稱_(2)…\n已整理過（名稱鎖住）的卡不會改名。',
  'Enter a common name and press Enter to name them name_(1), name_(2)… in the current order.\nPrepared cards (locked names) are not renamed.',
  '共通名を入力して Enter で、今の順番に 名前_(1)、名前_(2)… と設定します。\n整理済み（名前固定）のカードは変更されません。')
A('套用 _(1)~',
  'Apply _(1)~',
  '_(1)~ を適用')
A('順序＝播放順序\n也可以直接把卡片\n拖到左邊清單',
  'Order = playback order\nYou can also drag cards\ninto the list on the left',
  '順番＝再生順\nカードを左のリストに\n直接ドラッグもできます')
A('選項',
  'Options',
  'オプション')
A('新相機名稱',
  'New camera name',
  '新カメラ名')
A('留空＝取各場景相機的共同開頭',
  'Empty = common prefix of the scene cameras',
  '空欄＝各シーンのカメラ名の共通部分')
A('輸出資料夾',
  'Output folder',
  '出力フォルダ')
A('換回設定頁的「輸出預設資料夾」',
  'Reset to "Default output folder" from Settings',
  '設定タブの「既定の出力フォルダ」に戻す')
A('執行合併',
  'Merge',
  '結合を実行')
A('兩張以上：照順序接成一張。\n只有一張：不接卡，只整理成合併後的形狀（檔名 _prep_）。',
  'Two or more: joined into one card in order.\nOnly one: no joining, just prepared into the merged layout (file name _prep_).',
  '2 枚以上：順番に 1 枚へ結合。\n1 枚だけ：結合せず、結合後と同じ構成に整理（ファイル名 _prep_）。')
A('資料夾',
  'Folder',
  'フォルダ')
A('改了這個，底下三個路徑與主頁面的輸出資料夾會一起換成對應的位置；自己填過的那一個就不動了。',
  'Changing this moves the three paths below and the main output folder to match; any path you set yourself is left alone.',
  '変更すると下の 3 つのパスとメイン画面の出力フォルダも連動します。自分で設定したものは変わりません。')
A('Koikatsu Sunshine 根目錄（選填）',
  'Koikatsu Sunshine root folder (optional)',
  'Koikatsu Sunshine のルートフォルダー（任意）')
A('Koikatsu Sunshine 根目錄',
  'Koikatsu Sunshine root folder',
  'Koikatsu Sunshine のルートフォルダー')
A('有填的話，「人物卡合卡」的工單監看會同時監看這款遊戲的工單資料夾（UserData\\chara\\female\\Temp），Sunshine 裡 F6 的合卡功能才有人接。其他設定不受影響。',
  'When set, the job watcher on the "Chara card merge" tab also watches this game\'s job folder (UserData\\chara\\female\\Temp), so F6\'s card merging works in Sunshine too. Nothing else is affected.',
  '入力すると、「キャラカード合成」タブのジョブ監視がこのゲームのジョブフォルダー（UserData\\chara\\female\\Temp）も監視し、Sunshine の F6 のカード合成が使えるようになります。ほかの設定には影響しません。')
A('遊戲根目錄',
  'Game folder',
  'ゲームフォルダ')
A('讀卡預設資料夾',
  'Default card folder',
  '既定のカードフォルダ')
A('場景卡資料夾',
  'Scene card folder',
  'シーンカードフォルダ')
A('換回「遊戲根目錄」算出來的位置',
  'Reset to the location under the game folder',
  'ゲームフォルダから算出した場所に戻す')
A('輸出預設資料夾',
  'Default output folder',
  '既定の出力フォルダ')
A('音頻根目錄（相對路徑從這裡往下算）',
  'Audio root (relative paths start here)',
  '音声ルート（相対パスの起点）')
A('音頻根目錄',
  'Audio root',
  '音声ルート')
A('存進卡片的相對路徑前綴',
  'Relative path prefix stored in the card',
  'カードに保存する相対パスの接頭辞')
A('卡片裡存的是「前綴 + 音檔在根目錄底下的位置」。\n音檔實際放在別的磁碟也沒關係，只要遊戲那邊這個相對路徑讀得到。',
  'The card stores "prefix + file location under the root".\nThe files can live on another drive as long as the game can read this relative path.',
  'カードには「接頭辞＋ルート以下の位置」が保存されます。\nゲーム側でこの相対パスが読めれば、音声が別ドライブにあっても大丈夫です。')
A('回預設',
  'Default',
  '既定値')
A('換回 UserData\\audio',
  'Reset to UserData\\audio',
  'UserData\\audio に戻す')
A('詳細設定（預設值就是常用值，沒事不用動）',
  'Advanced (the defaults are the usual choice)',
  '詳細設定（通常は既定値のままで OK）')
A('自動接管相機（生一台新相機，交界自動切換）',
  'Camera takeover (new camera, switches at each boundary)',
  'カメラ引き継ぎ（新カメラを作り境目で自動切替）')
A('timeline 每個場景各包一層群組',
  'Wrap each scene in its own Timeline group',
  'Timeline をシーンごとにグループ化')
A('NC 改名成「場景名 | 原名」',
  'Rename NC to "scene | original"',
  'NC 名を「シーン名 | 元の名前」に')
A('建 (MAP)(FX)(CHAR)(SFX)',
  'Create (MAP)(FX)(CHAR)(SFX)',
  '(MAP)(FX)(CHAR)(SFX) を作成')
A('相機資料夾在場景結束時歸零',
  'Reset camera folders at scene end',
  'シーン終了時にカメラフォルダを原点へ')
A('沒有相機時自動新增（鎖初始視角）',
  'Auto-create when no camera (lock initial view)',
  'カメラがなければ自動作成（初期視点を固定）')
A('純過場 / 靜態場景常常沒有相機。\n勾起來就自動生一台鎖在那張卡存檔時的視角；\n不勾的話沒有相機的卡會擋住不給合併。',
  'Cutscene-only or static scenes often have no camera.\nChecked: create one locked to the view saved in the card;\nunchecked: cards without a camera block the merge.',
  'ムービーだけ・静止シーンにはカメラがないことがよくあります。\nオン：カード保存時の視点で固定したカメラを作成。\nオフ：カメラのないカードがあると結合できません。')
A('沒輪到的場景取消勾選（物件啟用軌道）',
  "Uncheck scenes that aren't playing (object-enable tracks)",
  '出番以外のシーンをオフ（オブジェクト有効トラック）')
A('每個場景的主資料夾與 (SFX) 各寫一條物件啟用軌道，\n輪到自己才勾選，其餘時間取消 —— 跟外太空停放搭配用。',
  "Writes an enable track for each scene's main folder and (SFX):\nchecked only during its own turn. Used together with parking.",
  '各シーンのメインフォルダと (SFX) に有効トラックを書き込み、\n出番の間だけオンにします。宇宙への退避と併用します。')
A('按 id 整批拿掉那幾種軌道（大刀，預設關）',
  'Remove those track ids in bulk (heavy-handed, default off)',
  '特定 id のトラックを一括削除（強力、既定オフ）')
A('把 tears／blush／itemColor／charClothes 等幾種 id 的軌道整批拿掉，不管有沒有問題。\n會連好的一起丟（例如演到一半脫衣服），只在 timeline 仍整棵失效時才開。',
  'Removes every tears / blush / itemColor / charClothes … track, broken or not.\nGood ones go too (e.g. mid-scene undressing). Only use it if the Timeline still fails completely.',
  'tears／blush／itemColor／charClothes などの id のトラックを問題の有無に関係なく一括削除します。\n正常なもの（途中で脱ぐ演出など）も消えるので、Timeline が全く動かない時だけ使ってください。')
A('拿掉指錯節點的軌道（預設開）',
  'Remove tracks pointing at the wrong node (default on)',
  '違うノードを指すトラックを削除（既定オン）')
A('只拿掉 objectIndex 指到錯誤種類或不存在節點的軌道。\n這種軌道會讓 Timeline 從第 0 秒整棵中斷；好的軌道不動。',
  'Only removes tracks whose objectIndex points at a wrong node type or no node.\nThose stop the whole Timeline from second 0; good tracks are untouched.',
  'objectIndex が違う種類のノードや存在しないノードを指すトラックだけ削除します。\nこうしたトラックは Timeline 全体を 0 秒から止めます。正常なトラックはそのまま。')
A('場景 shaderType',
  'Scene shaderType',
  'シーン shaderType')
A('保留底卡的',
  "Keep the base card's",
  'ベースカードのまま')
A('整張用 0',
  'Use 0 everywhere',
  '全体を 0')
A('整張用 1',
  'Use 1 everywhere',
  '全体を 1')
A('shaderType 是整張卡一個值，合併只能留一個（預設用第 1 列的）。\n各段不一致時頭髮、眼睛可能變樣，紀錄裡會有 [注意]。',
  'shaderType is one value per card, so a merge keeps only one (row 1 by default).\nIf segments differ, hair or eyes may look different; the log shows a [Warning].',
  'shaderType はカード全体で 1 つの値なので、結合では 1 つしか残せません（既定は 1 行目）。\nシーンごとに違うと髪や目の見た目が変わることがあり、ログに [注意] が出ます。')
A('把沒輪到的段落搬到外太空',
  'Park inactive segments in outer space',
  '出番以外のシーンを宇宙へ退避')
A('沒輪到的段落搬到 (100,100,100)，輪到才搬回原位。\n瞬移可能甩亂頭髮等揺れ物；關掉就留在原地，只靠啟用軌道隱藏。',
  'Inactive segments move to (100,100,100) and jump back on their turn.\nThe jump can fling hair and other dynamic bones; off = stay in place, hidden only by enable tracks.',
  '出番以外のシーンを (100,100,100) へ移し、出番で元の位置に戻します。\n瞬間移動で髪などの揺れものが乱れることがあります。オフならその場で有効トラックだけで隠します。')
A('提早歸位(秒)',
  'Return early (s)',
  '早めに復帰(秒)')
A('提早幾秒搬回原位，讓揺れ物在入鏡前先靜下來（1~3 秒通常夠）。0 = 不提早。',
  'Return this many seconds early so dynamic bones settle before the cut (1–3 s is usually enough). 0 = off.',
  '指定秒数だけ早く元の位置へ戻し、映る前に揺れものを落ち着かせます（1〜3 秒で十分）。0＝しない。')
A('把作者的 NC 也寫進啟用軌道',
  "Add enable tracks for the author's NC too",
  '作者の NC にも有効トラックを追加')
A('每條 NC 加一條啟用軌道：只在自己那段開著，其餘時間關掉。\n已經有啟用軌道的 NC 會跳過。軌道會多很多。',
  'Each NC gets an enable track: on during its own segment, off otherwise.\nNCs that already have one are skipped. Adds a lot of tracks.',
  '各 NC に有効トラックを追加し、自分のシーンの間だけオンにします。\nすでにある NC はスキップ。トラックがかなり増えます。')
A('清掉外框',
  'Remove frame',
  'フレームを削除')
A('拿掉底卡的外框（StudioImageEmbed 的 FrameData），背景圖不動。',
  "Removes the base card's frame (StudioImageEmbed FrameData); the background image stays.",
  'ベースカードのフレーム（StudioImageEmbed の FrameData）を削除します。背景画像はそのまま。')
A('啟用相機軌道',
  'Enable camera tracks',
  'カメラトラックを有効化')
A('把沒打勾的時間流速、相機縮放/FOV、相機資料夾軌道打開，其他保留作者設定。\n⚠ 時間流速會改變播放速度，已量好的對應點可能要重量。',
  "Turns on unchecked time-scale, camera zoom/FOV and camera-folder tracks; everything else keeps the author's setting.\n⚠ Time scale changes playback speed; measured sync points may need redoing.",
  'オフになっている時間の速度、カメラズーム/FOV、カメラフォルダのトラックをオンにします。他は作者の設定のまま。\n⚠ 時間の速度は再生速度を変えるので、測った対応点をやり直す必要があるかもしれません。')
A('每段相機縮放/FOV',
  'Per-segment camera zoom/FOV',
  'シーンごとのカメラズーム/FOV')
A('每段開頭把相機縮放 / FOV 設成那張原卡的值，換段時瞬間切換。',
  "Sets camera zoom / FOV to each original card's value at the start of its segment, switching instantly.",
  '各シーンの開始時にカメラズーム / FOV を元カードの値にし、切り替えは瞬時に行います。')
A('交界間隙(秒)',
  'Boundary gap (s)',
  '境目の間隔(秒)')
A('流程',
  'Workflow',
  '流れ')
A('合併完成後',
  'After merging',
  '結合後')
A('問我要去哪一頁',
  'Ask me which tab',
  'どのタブへ行くか尋ねる')
A('跳到 VNGE音頻',
  'Go to VNGE audio',
  'VNGE 音声へ')
A('跳到 添加動畫音頻',
  'Go to Cutscene audio',
  'ムービー音声へ')
A('跳到 整理',
  'Go to Organize',
  '整理へ')
A('留在合併分頁',
  'Stay on the merge tab',
  '結合タブのまま')
A('不管選哪個，合併好的卡片都會自動填進其他分頁，差別只在要不要幫你切過去。',
  'Either way the merged card is filled into the other tabs; this only decides whether to switch.',
  'どれを選んでも結合したカードは他のタブに入ります。切り替えるかどうかだけの違いです。')
A('儲存設定',
  'Save settings',
  '設定を保存')
A('流程：加入場景卡 → 排順序 → 每張選一台相機 → 設資料夾名稱 →（要配音就選音頻）→ 執行合併。',
  'Workflow: add scene cards → order them → pick one camera each → set folder names → (pick audio for voices) → Merge.',
  '流れ：シーンカードを追加 → 並べ替え → 各カードのカメラを選択 → フォルダ名を設定 →（ボイスを付けるなら音声を選択）→ 結合を実行。')
A('儲存分頁狀態時出錯：\n',
  'Error while saving tab state:\n',
  'タブの状態を保存中にエラー：\n')
A('設定已儲存（含各分頁的狀態）：',
  'Settings saved (including tab state):',
  '設定を保存しました（各タブの状態を含む）：')
A('選擇場景卡',
  'Choose scene cards',
  'シーンカードを選択')
A('場景卡 (*.png)',
  'Scene cards (*.png)',
  'シーンカード (*.png)')
A('  {0}：版本 {1}、時長 {2}、節點 {3}、相機 {4} 台',
  '  {0}: version {1}, length {2}, {3} nodes, {4} cameras',
  '  {0}：バージョン {1}、長さ {2}、ノード {3}、カメラ {4} 台')
A('（用 timeline 相機路徑運鏡，會生一台相機接手）',
  '(camera moved by Timeline path; a new camera will take over)',
  '（Timeline カメラパスでカメラワーク、新カメラが引き継ぎます）')
A('（沒有相機，會自動生一台鎖初始視角）',
  '(no camera; one will be created with the initial view locked)',
  '（カメラなし、初期視点固定のカメラを自動作成）')
A('（已整理過）',
  '(already prepared)',
  '（整理済み）')
A('、內建地圖 %s',
  ', built-in map %s',
  '、内蔵マップ %s')
A('無',
  'none',
  'なし')
A('    [時長] 宣告 {0}，實際內容到 {1}（多 {2:.2f} 秒）—— 時長欄已標紅，滑鼠移上去有說明',
  '    [Length] declared {0}, content runs to {1} ({2:.2f} s longer) — Length cell marked red, hover for details',
  '    [長さ] 宣言 {0}、実際は {1} まで（{2:.2f} 秒長い）— 長さ欄を赤表示、マウスを乗せると説明')
A('第 %d 張 %s',
  'card %d %s',
  '%d 枚目 %s')
A('[提醒] 各張的內建地圖不一樣（%s）。合併卡只能存一張，會用 #%d；每段會放 [MAPINFO] 記號，F7 播放時自動切換/隱藏。沒裝 F7 或直接在 Timeline 播時，每段都會看到 #%d。',
  '[Note] The cards use different built-in maps (%s). A merged card can store only one, so #%d is used; each segment gets a [MAPINFO] marker and F7 switches/hides maps during playback. Without F7, or when playing directly in Timeline, every segment shows #%d.',
  '[お知らせ] カードごとに内蔵マップが違います（%s）。結合カードには 1 つしか保存できないので #%d を使います。各シーンに [MAPINFO] マーカーを置くので、F7 で再生すると自動で切替/非表示になります。F7 なしや Timeline 直接再生では全シーンで #%d が表示されます。')
A('[讀卡失敗] {0}：{1}',
  '[Read failed] {0}: {1}',
  '[読み込み失敗] {0}：{1}')
A('還不能執行',
  "Can't run yet",
  'まだ実行できません')
A('請先指定輸出資料夾',
  'Set the output folder first',
  '先に出力フォルダを指定してください')
A('找不到資料夾：\n{0}',
  'Folder not found:\n{0}',
  'フォルダが見つかりません：\n{0}')
A('有列選了音頻，但還沒在設定頁指定音頻根目錄（相對路徑是從那裡往下算的）',
  "Some rows have audio, but the audio root isn't set in Settings (relative paths start there)",
  '音声を選んだ行がありますが、設定タブで音声ルートが未指定です（相対パスの起点です）')
A('開始合併 ',
  'Start merging ',
  '結合開始 ')
A('（只有一張卡，不接卡）',
  '(only one card, no joining)',
  '（1 枚だけなので結合なし）')
A('開始整理 ',
  'Start preparing ',
  '整理開始 ')
A('整理中…',
  'Preparing…',
  '整理中…')
A('合併中…',
  'Merging…',
  '結合中…')
A('（大卡要幾分鐘，視窗沒有回應是正常的）',
  '(large cards take minutes; the window not responding is normal)',
  '（大きなカードは数分かかります。ウィンドウが応答しなくても正常です）')
A('失敗：',
  'Failed: ',
  '失敗：')
A('合併失敗',
  'Merge failed',
  '結合失敗')
A('合併完成',
  'Merge complete',
  '結合完了')
A('卡片已經填進其他分頁了，接下來要去哪裡？',
  'The card has been filled into the other tabs. Where next?',
  'カードは他のタブに入りました。次はどこへ？')
A('留在這裡',
  'Stay here',
  'ここに残る')
A('[提醒]',
  '[Note]',
  '[お知らせ]')
A('出錯了（程式沒有關掉）',
  'Something went wrong (the program is still running)',
  'エラーが発生しました（プログラムは動作中です）')
A('%s\n\n完整內容寫在：\n%s',
  '%s\n\nFull details written to:\n%s',
  '%s\n\n詳細の保存先：\n%s')
A('角色',
  'Character',
  'キャラ')
A('道具',
  'Item',
  'アイテム')
A('燈光',
  'Light',
  'ライト')
A('路徑',
  'Path',
  'パス')
A('路線',
  'Route',
  'ルート')
A('（未命名）#%s',
  '(unnamed) #%s',
  '（名前なし）#%s')
A('（未命名）dicKey %s',
  '(unnamed) dicKey %s',
  '（名前なし）dicKey %s')
A('讀入 %d 個節點，花了 %.1f 秒',
  'Read %d nodes in %.1f s',
  'ノード %d 個を読み込み、%.1f 秒')
A('已寫出 %s（%.1f 秒）',
  'Wrote %s (%.1f s)',
  '%s を書き出しました（%.1f 秒）')
A('名稱',
  'Name',
  '名前')
A('類型',
  'Type',
  '種類')
A('顯示',
  'Visible',
  '表示')
A("・拖曳搬家、新增資料夾、改名、勾顯示、改位置／旋轉／縮放：不影響 timeline 和 NC<br>・<span style='color:%s'>刪除</span>：先標記，存檔時才真的刪，並自動修正相關參照<br>・角色接點裡的東西只能在同一個接點裡拖，不能拖出角色",
  "・Drag, new folder, rename, visibility, position/rotation/scale: Timeline and NC are unaffected<br>・<span style='color:%s'>Delete</span>: only marked; really deleted on save, with references fixed automatically<br>・Items under a character's attach point can only move within that attach point",
  "・ドラッグ移動、新規フォルダ、名前変更、表示切替、位置／回転／拡縮：Timeline と NC に影響なし<br>・<span style='color:%s'>削除</span>：まず印を付け、保存時に実際に削除し関連参照も自動修正<br>・キャラの接続点の中のものは同じ接続点の中でしか移動できません")
A('場景卡（可以直接拖進來）',
  'Scene card (you can drag it in)',
  'シーンカード（ドラッグでも可）')
A('讀取',
  'Load',
  '読み込み')
A('新增資料夾',
  'New folder',
  '新規フォルダ')
A('在選取項目的同一層新增；選的是資料夾就放進去',
  'Adds at the same level as the selection; into it if a folder is selected',
  '選択項目と同じ階層に追加。フォルダを選んでいればその中へ')
A('重新命名',
  'Rename',
  '名前変更')
A('也可以直接雙擊名稱',
  'You can also double-click the name',
  '名前をダブルクリックでも可')
A('上移',
  'Up',
  '上へ')
A('下移',
  'Down',
  '下へ')
A('升一層',
  'Out one level',
  '一階層上へ')
A('搬到父資料夾的外面',
  'Move outside the parent folder',
  '親フォルダの外へ出す')
A('隱藏 / 顯示',
  'Hide / show',
  '非表示 / 表示')
A('等於樹狀圖那個勾',
  'Same as the tree checkbox',
  'ツリーのチェックと同じ')
A('刪除',
  'Delete',
  '削除')
A('標記為刪除（連同底下整棵）。存檔時才真的砍，而且會先告訴你會連帶砍掉幾條 timeline 軌道',
  'Mark for deletion (with everything below). Really deleted on save, after telling you how many Timeline tracks go with it',
  '削除の印を付けます（下の階層ごと）。保存時に実際に削除し、先に巻き添えで消える Timeline トラック数を表示します')
A('取消刪除',
  'Undelete',
  '削除を取り消し')
A('把標記為刪除的還原',
  'Restore items marked for deletion',
  '削除の印を外す')
A('全部展開',
  'Expand all',
  'すべて展開')
A('全部收合',
  'Collapse all',
  'すべて折りたたむ')
A('把展開/收合狀態一起存進卡片',
  'Save expand/collapse state into the card',
  '展開/折りたたみ状態もカードに保存')
A('勾了的話，下次在遊戲裡開這張卡，資料夾的展開狀態會跟你現在看到的一樣',
  'If checked, folders open in the game exactly as you see them now',
  'オンにすると、ゲームでこのカードを開いた時のフォルダの開閉が今と同じになります')
A('備份 .bak',
  'Backup .bak',
  '.bak を作成')
A('存回這張卡之前，先把原檔複製成 <卡名>.png.bak（已經有 .bak 就不再覆蓋）',
  'Before saving over this card, copy the original to <card>.png.bak (an existing .bak is kept)',
  '上書き保存の前に元ファイルを <カード名>.png.bak にコピー（既存の .bak は上書きしません）')
A('存回這張卡',
  'Save to this card',
  'このカードに保存')
A('另存新檔…',
  'Save as…',
  '名前を付けて保存…')
A('位置',
  'Position',
  '位置')
A('旋轉',
  'Rotation',
  '回転')
A('縮放',
  'Scale',
  '拡縮')
A('位置 / 旋轉 / 縮放（選好物件，改完按「套用」）',
  'Position / rotation / scale (select objects, edit, then press Apply)',
  '位置 / 回転 / 拡縮（オブジェクトを選び、変更して「適用」）')
A('沒打勾的那幾項按「套用」時不會動',
  'Unchecked rows are left alone on Apply',
  'チェックのない項目は「適用」しても変わりません')
A('讀取選取的值',
  'Read selected',
  '選択中の値を読む')
A('把目前選取那個物件的數值填進上面',
  'Fill in the values of the selected object',
  '選択中のオブジェクトの値を上に入れる')
A('套用到選取的全部',
  'Apply to all selected',
  '選択したすべてに適用')
A('打勾的那幾項會寫進所有選取的物件（可以多選）',
  'Checked rows are written to every selected object (multi-select OK)',
  'チェックした項目を選択中のすべてのオブジェクトに書き込みます（複数選択可）')
A('位置歸零',
  'Zero position',
  '位置をゼロに')
A('縮放設 1',
  'Scale to 1',
  '拡縮を 1 に')
A('已讀入 %s',
  'Read %s',
  '%s を読み込みました')
A('沒有選取物件，或三項都沒打勾',
  'Nothing selected, or none of the three rows is checked',
  'オブジェクト未選択、または 3 項目ともチェックなし')
A('已套用 %s 到 %d 個物件（%s）',
  'Applied %s to %d objects (%s)',
  '%s を %d 個のオブジェクトに適用（%s）')
A('套用 %s 到 %d 個物件：%s',
  'Apply %s to %d objects: %s',
  '%s を %d 個のオブジェクトに適用：%s')
A('場景卡',
  'Scene card',
  'シーンカード')
A('讀不了',
  "Can't read",
  '読み込めません')
A('先指定場景卡',
  'Choose a scene card first',
  '先にシーンカードを指定してください')
A('還沒存檔',
  'Unsaved',
  '未保存')
A('目前的修改還沒存，要放棄嗎？',
  'You have unsaved changes. Discard them?',
  '変更が保存されていません。破棄しますか？')
A('讀取中…（大卡要幾十秒）',
  'Loading… (large cards take tens of seconds)',
  '読み込み中…（大きなカードは数十秒かかります）')
A('（角色接點）',
  '(character attach point)',
  '（キャラの接続点）')
A('接點 %s',
  'Attach point %s',
  '接続点 %s')
A('（未命名）',
  '(unnamed)',
  '（名前なし）')
A('[提醒] 這張卡沒有 TreeNodeNaming 資料，%s「%s」的新名字存不進去',
  '[Note] This card has no TreeNodeNaming data; the new name of %s "%s" can\'t be saved',
  '[お知らせ] このカードには TreeNodeNaming のデータがないため、%s「%s」の新しい名前は保存できません')
A('新資料夾',
  'New folder',
  '新しいフォルダ')
A('新增資料夾，dicKey = %d（取 max+1，既有 RANK 不變）',
  'New folder, dicKey = %d (max+1, existing RANK unchanged)',
  '新規フォルダ、dicKey = %d（max+1、既存の RANK は変わりません）')
A('這一項掛在角色的接點上，不能再升一層',
  "This item hangs on a character attach point and can't move up",
  'この項目はキャラの接続点にあるため、これ以上上に出せません')
A('要刪：%s',
  'To delete: %s',
  '削除対象：%s')
A('連同底下總共 %d 個節點。',
  '%d nodes in total, including everything below.',
  '下の階層を含めて合計 %d 個のノード。')
A('（算不出會連帶砍掉多少軌道 —— 這張卡的 timeline 讀不動）',
  "(Can't tell how many tracks go with it — this card's Timeline can't be read)",
  '（巻き添えのトラック数を算出できません — このカードの Timeline を読めません）')
A('會連帶砍掉 %d 條 timeline 軌道、%d 個 NodesConstraints。',
  'This also removes %d Timeline tracks and %d NodesConstraints.',
  'Timeline トラック %d 本と NodesConstraints %d 個も一緒に削除されます。')
A('那些軌道是**指向這些節點**的，節點不在了它們也沒有意義，留著會讓 timeline 從那一條開始整棵中斷。',
  'Those tracks point at these nodes; without the nodes they are useless and would break the Timeline.',
  'それらのトラックはこのノードを指しているため、ノードがなくなると意味がなく、残すと Timeline が止まります。')
A('現在只是標記，按「存回這張卡」或「另存新檔」才真的動手。',
  'This only marks them; they\'re deleted when you press "Save to this card" or "Save as".',
  '今は印を付けるだけです。「このカードに保存」か「名前を付けて保存」で実際に削除されます。')
A('按錯的話重新讀取卡片就回來了。',
  'If it was a mistake, just reload the card.',
  '間違えたらカードを読み込み直せば元に戻ります。')
A('標記為刪除',
  'Mark for deletion',
  '削除の印を付ける')
A('標記刪除：%d 個節點（存檔時才真的砍）',
  'Marked for deletion: %d nodes (deleted on save)',
  '削除の印：ノード %d 個（保存時に削除）')
A('取消刪除標記：%d 個節點',
  'Unmarked: %d nodes',
  '削除の印を解除：ノード %d 個')
A('樹裡有對不到節點的項目，請重新讀取卡片',
  'The tree has items without a matching node; please reload the card',
  'ツリーに対応するノードのない項目があります。カードを読み込み直してください')
A('另存新檔',
  'Save as',
  '名前を付けて保存')
A('這次有刪除',
  'This save deletes nodes',
  '今回は削除があります')
A('這次會真的刪掉 %d 個節點，而且「覆蓋前先備份 .bak」沒有勾。\n\n刪除會重算整個 RANK 空間並重寫每一條軌道，沒辦法還原。\n原卡被蓋掉之後就沒有東西可以回去了。\n\n要幫你把備份打開再存嗎？',
  'This will really delete %d nodes, and "Backup .bak" is not checked.\n\nDeleting renumbers every RANK and rewrites every track; it can\'t be undone.\nOnce the original is overwritten there is nothing to go back to.\n\nTurn the backup on before saving?',
  '今回はノード %d 個を実際に削除しますが、「.bak を作成」がオフです。\n\n削除すると RANK 全体を振り直し全トラックを書き換えるので元に戻せません。\n元のカードを上書きすると戻す手段がなくなります。\n\nバックアップをオンにして保存しますか？')
A('刪掉 %d 個節點，參照清理：',
  'Deleted %d nodes, reference cleanup:',
  'ノード %d 個を削除、参照の整理：')
A('    [注意] %s',
  '    [Warning] %s',
  '    [注意] %s')
A('整理失敗',
  'Organize failed',
  '整理に失敗')
A('把樹寫回場景時出錯，看紀錄。\n這張卡在記憶體裡可能已經改了一半，請重新讀取再試一次。',
  'Error while writing the tree back to the scene; see the log.\nThe card in memory may be half-changed, please reload and try again.',
  'ツリーをシーンに書き戻す際にエラーが出ました。ログを見てください。\nメモリ上のカードが途中まで変更されている可能性があるので、読み込み直して再試行してください。')
A('寫出中…',
  'Writing…',
  '書き出し中…')
A('寫出失敗',
  'Write failed',
  '書き出し失敗')
A('%d 個節點，其中 %d 個隱藏',
  '%d nodes, %d hidden',
  'ノード %d 個、うち非表示 %d 個')
A("\u3000<span style='color:%s'><b>%d 個待刪除（存檔時才真的砍）</b></span>",
  "\u3000<span style='color:%s'><b>%d pending deletion (deleted on save)</b></span>",
  "\u3000<span style='color:%s'><b>削除待ち %d 個（保存時に削除）</b></span>")
A('\u3000（有未存檔的修改）',
  '\u3000(unsaved changes)',
  '\u3000（未保存の変更あり）')
A('場景 / 音檔',
  'Scene / audio file',
  'シーン / 音声ファイル')
A('群組名稱',
  'Group name',
  'グループ名')
A('觸發 (SFX)',
  'Trigger (SFX)',
  'トリガー (SFX)')
A('存進卡片的相對路徑',
  'Relative path stored in card',
  'カードに保存する相対パス')
A('\n  …等共 {0} 個',
  '\n  …{0} in total',
  '\n  …など計 {0} 個')
A('這些音檔不在音頻根目錄底下',
  "These audio files aren't under the audio root",
  'これらの音声は音声ルートの下にありません')
A('下面的音檔不在\n{0}\n底下：\n\n{1}',
  'The files below are not under\n{0}\n:\n\n{1}',
  '以下の音声は\n{0}\nの下にありません：\n\n{1}')
A('算不出相對路徑。VNGE 的存檔模式是**整張卡共用**的，所以要嘛跳過這些檔案，要嘛把整張卡改成：\n\n· 絕對路徑 —— 卡片存完整路徑，換一台電腦或搬了資料夾就讀不到\n· 夾帶在卡片裡 —— 音檔本體塞進卡片，走到哪都能播，但卡片會膨脹約音檔大小的 1.5 倍',
  "Can't compute relative paths. VNGE's save mode applies to the whole card, so either skip these files or switch the whole card to:\n\n· Absolute paths — the card stores full paths; breaks on another PC or if folders move\n· Embed in card — the audio is packed into the card and plays anywhere, but the card grows by about 1.5× the audio size",
  '相対パスを算出できません。VNGE の保存モードはカード全体で共通なので、これらのファイルをスキップするか、カード全体を次に変更してください：\n\n· 絶対パス — フルパスを保存。別の PC やフォルダ移動で読めなくなります\n· カードに埋め込み — 音声本体をカードに入れるのでどこでも再生できますが、カードが音声の約 1.5 倍大きくなります')
A('跳過這些',
  'Skip these',
  'これらをスキップ')
A('改用絕對路徑',
  'Use absolute paths',
  '絶対パスにする')
A('夾帶在卡片裡',
  'Embed in card',
  'カードに埋め込む')
A('全部取消',
  'Cancel all',
  'すべてキャンセル')
A('音頻 — {0}',
  'Audio — {0}',
  '音声 — {0}')
A('場景「{0}」的音頻。左邊的圓點＝這一段預設播哪一個（100%），其餘存成 0%，\n在 Studio 裡可以即時切換。',
  'Audio for scene "{0}". The dot on the left = which one plays by default (100%); the rest are saved at 0%,\nand you can switch live in Studio.',
  'シーン「{0}」の音声。左の丸＝このシーンで既定で再生するもの（100%）、他は 0% で保存され、\nStudio で即座に切り替えられます。')
A('自動配對',
  'Auto-match',
  '自動マッチ')
A('加音檔…',
  'Add audio…',
  '音声を追加…')
A('移除選取',
  'Remove selected',
  '選択を削除')
A('{0} 個音檔',
  '{0} audio files',
  '音声 {0} 個')
A('\u3000群組名稱＝「{0}」',
  '\u3000group name = "{0}"',
  '\u3000グループ名＝「{0}」')
A('\u3000（沒有音檔＝這一段不配音）',
  '\u3000(no audio = no voice for this segment)',
  '\u3000（音声なし＝このシーンはボイスなし）')
A('選擇音檔',
  'Choose audio files',
  '音声ファイルを選択')
A('音檔 (*.wav *.mp3 *.ogg *.aif *.aiff)',
  'Audio (*.wav *.mp3 *.ogg *.aif *.aiff)',
  '音声 (*.wav *.mp3 *.ogg *.aif *.aiff)')
A('找不到音檔資料夾',
  'Audio folder not found',
  '音声フォルダが見つかりません')
A('在音頻根目錄底下找不到跟「{0}」對得上的資料夾。\n用「加音檔…」自己選，或到設定頁確認音頻根目錄。',
  'No folder matching "{0}" under the audio root.\nPick files with "Add audio…", or check the audio root in Settings.',
  '音声ルートの下に「{0}」に一致するフォルダがありません。\n「音声を追加…」で選ぶか、設定タブの音声ルートを確認してください。')
A('沒配到',
  'No match',
  '一致なし')
A('{0}\n裡面沒有對得上「{1}」的音檔。',
  '{0}\nhas no audio matching "{1}".',
  '{0}\nに「{1}」に一致する音声がありません。')
A('[讀卡失敗] {0}: {1}',
  '[Read failed] {0}: {1}',
  '[読み込み失敗] {0}: {1}')
A('  這張卡沒整理過（根節點沒有 (CAM)），當成一整段處理',
  "  This card isn't prepared (no (CAM) at the root); treated as one segment",
  '  このカードは整理されていません（ルートに (CAM) なし）。1 シーンとして扱います')
A('[注意] ',
  '[Warning] ',
  '[注意] ')
A('  {0} 段場景、卡片裡本來有 {1} 個音頻',
  '  {0} scenes, the card already has {1} sounds',
  '  シーン {0} 個、カードには既に音声 {1} 個')
A('重新讀卡：{0}',
  'Re-reading card: {0}',
  'カード再読み込み：{0}')
A('  [{0}] {1} 補上 (SFX) dicKey={2}，啟用軌道 {3}',
  '  [{0}] {1} added (SFX) dicKey={2}, enable track {3}',
  '  [{0}] {1} に (SFX) を追加 dicKey={2}、有効トラック {3}')
A('補了 {0} 個 (SFX)（還沒寫檔，按「寫入音頻」才會存）',
  'Added {0} (SFX) (not saved yet; press "Write audio")',
  '(SFX) を {0} 個追加（未保存。「音声を書き込む」で保存）')
A('寫檔中… {0}',
  'Writing… {0}',
  '書き込み中… {0}')
A('已清除 {0} 個音頻、{1} 個群組 → {2}',
  'Cleared {0} sounds, {1} groups → {2}',
  '音声 {0} 個、グループ {1} 個を削除 → {2}')
A('完成：{0}（{1}、音頻 {2} 個、群組 {3} 個、{4:,} bytes）',
  'Done: {0} ({1}, {2} sounds, {3} groups, {4:,} bytes)',
  '完了：{0}（{1}、音声 {2} 個、グループ {3} 個、{4:,} bytes）')
A('通常是合併好的那張（也可以拖進下面的清單）',
  'Usually the merged card (you can also drag it into the list below)',
  '普通は結合したカード（下のリストにドラッグも可）')
A('讀取場景',
  'Load scene',
  'シーンを読み込む')
A('依包裝資料夾名稱去音頻資料夾找對應的音檔',
  'Find matching audio in the audio folder by wrapper folder name',
  'ラッパーフォルダ名で音声フォルダから一致する音声を探す')
A('把選到的音檔加進目前選取的場景',
  'Add chosen audio files to the selected scene',
  '選んだ音声を選択中のシーンに追加')
A('清空音檔',
  'Clear audio',
  '音声をクリア')
A('只清畫面上的清單，還沒寫進卡片',
  'Only clears the list on screen; nothing is written to the card',
  '画面上のリストだけクリア。カードには書き込みません')
A('為選到的場景（沒選就是全部缺的）建一個 (SFX)，\n並照該段的起訖時間把物件啟用軌道一起寫好',
  "Create an (SFX) for the selected scenes (all missing ones if none selected),\nwith enable tracks matching each segment's start/end",
  '選択したシーン（未選択なら不足している全部）に (SFX) を作り、\n各シーンの開始/終了に合わせて有効トラックも書き込みます')
A('把卡片裡已經存在的音頻與群組全部刪掉並寫檔',
  'Delete all existing sounds and groups in the card and save',
  'カード内の既存の音声とグループをすべて削除して保存')
A('釋放記憶體',
  'Free memory',
  'メモリ解放')
A('放掉留在記憶體裡的那張卡',
  'Drop the card kept in memory',
  'メモリに残っているカードを解放')
A('每段場景一個群組\n群組名稱可直接改\n100% 那欄每段只能勾一個',
  'One group per scene\nGroup names are editable\nOnly one 100% per scene',
  'シーンごとに 1 グループ\nグループ名は直接編集可\n100% は各シーン 1 つだけ')
A('音頻路徑',
  'Audio paths',
  '音声パス')
A('例如 D:\\Koikatu\\UserData\\audio',
  'e.g. D:\\Koikatu\\UserData\\audio',
  '例：D:\\Koikatu\\UserData\\audio')
A('相對路徑就是從這裡往下算。\n音檔實際放在別的磁碟（例如 F:\\Koikatu_Audio）也沒關係，\n只要下面的前綴是遊戲讀得到的位置就好。',
  'Relative paths start here.\nThe files can be on another drive (e.g. F:\\Koikatu_Audio)\nas long as the prefix below is a place the game can read.',
  '相対パスはここが起点です。\n音声が別ドライブ（例：F:\\Koikatu_Audio）にあっても、\n下の接頭辞がゲームから読める場所なら大丈夫です。')
A('相對路徑前綴',
  'Relative path prefix',
  '相対パスの接頭辞')
A('存進卡片的路徑會是「前綴 + 檔案相對根目錄的位置」。',
  'The stored path is "prefix + location relative to the root".',
  '保存されるパスは「接頭辞＋ルートからの相対位置」です。')
A('換回 ',
  'Reset to ',
  '戻す：')
A('留空＝自動從根目錄底下找同名資料夾',
  'Empty = find a same-named folder under the root automatically',
  '空欄＝ルート以下の同名フォルダを自動で探す')
A('寫入前清掉卡片原有的音頻',
  "Clear the card's existing audio before writing",
  '書き込む前にカードの既存の音声を削除')
A('存檔模式',
  'Save mode',
  '保存モード')
A('VNGE 的 saveMode 是整張卡共用的，不能一部分相對一部分絕對。\n相對路徑：最省、最好搬，但音檔必須在音頻根目錄底下。\n絕對路徑：換電腦或搬資料夾就讀不到。\n夾帶：音檔塞進卡片，走到哪都能播，卡片會膨脹約音檔大小的 1.5 倍。',
  "VNGE's saveMode is shared by the whole card; it can't be partly relative and partly absolute.\nRelative: smallest and portable, but files must be under the audio root.\nAbsolute: breaks on another PC or if folders move.\nEmbed: audio packed into the card, plays anywhere; the card grows by about 1.5× the audio size.",
  'VNGE の saveMode はカード全体で共通で、一部だけ相対・一部だけ絶対にはできません。\n相対パス：最小で持ち運びやすいが、音声は音声ルートの下に必要。\n絶対パス：別の PC やフォルダ移動で読めなくなります。\n埋め込み：音声をカードに入れるのでどこでも再生できますが、カードが音声の約 1.5 倍大きくなります。')
A('輸出',
  'Output',
  '出力')
A('留空＝原地覆寫',
  'Empty = overwrite in place',
  '空欄＝上書き')
A('另存…',
  'Save as…',
  '別名保存…')
A('寫入音頻',
  'Write audio',
  '音声を書き込む')
A('音檔資料夾',
  'Audio folder',
  '音声フォルダ')
A('另存場景卡',
  'Save scene card as',
  'シーンカードを別名で保存')
A('讀不到',
  "Can't read",
  '読み込めません')
A('請先選一張場景卡',
  'Pick a scene card first',
  '先にシーンカードを選んでください')
A('讀卡中…（大卡要幾分鐘）',
  'Reading card… (large cards take minutes)',
  'カード読み込み中…（大きなカードは数分かかります）')
A('讀卡失敗',
  'Failed to read card',
  'カードの読み込みに失敗')
A('讀到 {0} 段場景',
  'Found {0} scenes',
  'シーン {0} 個を読み込みました')
A('  ← 新補的',
  '  ← newly added',
  '  ← 新規追加')
A('{0:.2f} ~ {1:.2f} 秒',
  '{0:.2f} ~ {1:.2f} s',
  '{0:.2f} ～ {1:.2f} 秒')
A('\n（檔案不存在）',
  '\n(file missing)',
  '\n（ファイルがありません）')
A('還沒讀卡',
  'No card loaded',
  'カード未読み込み')
A('請先讀取一張場景卡',
  'Load a scene card first',
  '先にシーンカードを読み込んでください')
A('根目錄底下找不到跟包裝資料夾同名的資料夾，\n請手動指定「音檔資料夾」。',
  'No folder named after the wrapper folder under the root;\nset "Audio folder" manually.',
  'ルートにラッパーフォルダと同名のフォルダがありません。\n「音声フォルダ」を手動で指定してください。')
A('自動配對：{0}（{1} 個音檔）',
  'Auto-match: {0} ({1} audio files)',
  '自動マッチ：{0}（音声 {1} 個）')
A('  [{0}] {1} ← {2} 個',
  '  [{0}] {1} ← {2}',
  '  [{0}] {1} ← {2} 個')
A('  [配不出去] {0}',
  '  [unmatched] {0}',
  '  [未割り当て] {0}')
A('有 {0} 個音檔配不出去，請看紀錄',
  "{0} audio files couldn't be matched, see the log",
  '音声 {0} 個を割り当てられませんでした。ログを見てください')
A('先選一段場景',
  'Select a scene first',
  '先にシーンを選んでください')
A('請先點一下要加音檔的那段場景',
  'Click the scene you want to add audio to first',
  '音声を追加するシーンを先にクリックしてください')
A('換回遊戲根目錄底下的位置：\n',
  'Reset to the location under the game folder:\n',
  'ゲームフォルダ以下の場所に戻す：\n')
A('設定頁還沒填遊戲根目錄',
  "The game folder isn't set in Settings yet",
  '設定タブでゲームフォルダが未入力です')
A('音檔必須在音頻根目錄底下',
  'Audio files must be under the audio root',
  '音声は音声ルートの下に置く必要があります')
A('換電腦或搬資料夾就會讀不到',
  'Breaks on another PC or if folders move',
  '別の PC やフォルダ移動で読めなくなります')
A('卡片會膨脹約音檔大小的 1.5 倍',
  'The card grows by about 1.5× the audio size',
  'カードが音声の約 1.5 倍大きくなります')
A('不用補',
  'Nothing to add',
  '追加不要')
A('每一段都已經有 (SFX) 了。\n要為特定一段再加一個的話，先點選那一段。',
  'Every segment already has an (SFX).\nTo add another one to a specific segment, select it first.',
  'すべてのシーンに (SFX) があります。\n特定のシーンにもう 1 つ追加するなら、先にそのシーンを選んでください。')
A('補 (SFX) 資料夾',
  'Add (SFX) folders',
  '(SFX) フォルダを追加')
A('要為下面這幾段建 (SFX) 並寫好物件啟用軌道嗎？\n\n{0}\n\n（只改記憶體裡的卡片，按「寫入音頻」才會存檔）',
  'Create (SFX) with enable tracks for these segments?\n\n{0}\n\n(Only the card in memory changes; press "Write audio" to save)',
  '以下のシーンに (SFX) と有効トラックを作成しますか？\n\n{0}\n\n（メモリ上のカードだけ変更。「音声を書き込む」で保存）')
A('已補 (SFX)，記得按「寫入音頻」存檔',
  '(SFX) added — remember to press "Write audio"',
  '(SFX) を追加しました。「音声を書き込む」で保存してください')
A('清除卡片音頻',
  'Clear card audio',
  'カードの音声を削除')
A('要把卡片裡所有的音頻與群組刪掉嗎？\n\n來源：{0}\n寫到：{1}',
  'Delete every sound and group in the card?\n\nSource: {0}\nWrite to: {1}',
  'カード内の音声とグループをすべて削除しますか？\n\n元：{0}\n書き込み先：{1}')
A('清除中…',
  'Clearing…',
  '削除中…')
A('已放掉記憶體裡的卡片（下次寫入會重新讀一次）',
  'Card released from memory (it will be re-read on the next write)',
  'メモリ上のカードを解放しました（次の書き込み時に再読み込みします）')
A('還不能寫',
  "Can't write yet",
  'まだ書き込めません')
A('每一段都沒有音檔',
  'No segment has audio',
  'どのシーンにも音声がありません')
A('輸出資料夾不存在：\n{0}',
  "Output folder doesn't exist:\n{0}",
  '出力フォルダがありません：\n{0}')
A('音頻根目錄不存在：\n{0}',
  "Audio root doesn't exist:\n{0}",
  '音声ルートがありません：\n{0}')
A('原地覆寫',
  'Overwrite',
  '上書き')
A('要直接覆寫這張卡嗎？\n{0}',
  'Overwrite this card directly?\n{0}',
  'このカードを直接上書きしますか？\n{0}')
A('寫入中…（大卡要幾分鐘）',
  'Writing… (large cards take minutes)',
  '書き込み中…（大きなカードは数分かかります）')
A('寫入音頻 → {0}',
  'Write audio → {0}',
  '音声を書き込む → {0}')
A('寫入失敗',
  'Write failed',
  '書き込み失敗')
A('-23 LUFS\u3000廣播標準 EBU R128（建議：這批素材本來就在這附近）',
  '-23 LUFS\u3000broadcast standard EBU R128 (recommended)',
  '-23 LUFS\u3000放送基準 EBU R128（推奨）')
A('-20 LUFS\u3000響一點',
  '-20 LUFS\u3000a bit louder',
  '-20 LUFS\u3000少し大きめ')
A('-16 LUFS\u3000串流常見（要推比較多，限幅會作用）',
  '-16 LUFS\u3000common for streaming (more limiting)',
  '-16 LUFS\u3000配信でよく使う値（リミッターが効く）')
A('-14 LUFS\u3000YouTube 播放響度（這種素材會被壓得很扁）',
  '-14 LUFS\u3000YouTube loudness (heavily compressed)',
  '-14 LUFS\u3000YouTube の音量（かなり潰れる）')
A('空白',
  'empty',
  '空欄')
A('看不懂的時間：%s',
  "Can't parse time: %s",
  '時間を解釈できません：%s')
A('\n[結束碼 %s]\n',
  '\n[exit code %s]\n',
  '\n[終了コード %s]\n')
A('  跳過（已存在）: %s',
  '  skipped (exists): %s',
  '  スキップ（既存）: %s')
A('  抽音訊: %s',
  '  extracting audio: %s',
  '  音声抽出: %s')
A('抽 wav %d/%d\u3000%s',
  'Extract wav %d/%d\u3000%s',
  'wav 抽出 %d/%d\u3000%s')
A('    失敗: ',
  '    failed: ',
  '    失敗: ')
A('完成 %d 個%s',
  'Done, %d files%s',
  '完了 %d 個%s')
A('量響度 %d/%d\u3000%s',
  'Measuring loudness %d/%d\u3000%s',
  '音量測定 %d/%d\u3000%s')
A('  %-46s 量不到響度，跳過',
  '  %-46s loudness not measurable, skipped',
  '  %-46s 音量を測定できずスキップ')
A('一個檔案都量不到響度 —— 確認 ffmpeg 讀得動這些檔。',
  'No file could be measured — check that ffmpeg can read them.',
  'どのファイルも測定できません — ffmpeg で読めるか確認してください。')
A('  量測結果（%d 個檔案，原本響度差距 %.1f dB）',
  '  Measured %d files, original loudness spread %.1f dB',
  '  測定結果（%d 個、元の音量差 %.1f dB）')
A('  共同目標 %.1f LUFS（限幅模式：峰值由 alimiter 壓回 %.1f dBTP）',
  '  Common target %.1f LUFS (limiter mode: peaks pulled back to %.1f dBTP)',
  '  共通目標 %.1f LUFS（リミッター：ピークは %.1f dBTP に抑制）')
A('  共同目標 %.1f LUFS —— 被「%s」的峰值餘裕決定',
  '  Common target %.1f LUFS — limited by the peak headroom of "%s"',
  '  共通目標 %.1f LUFS —「%s」のピーク余裕で決定')
A('    （比選定的 %.1f LUFS 低 %.1f dB。這些素材本來就壓到頂，純增益推不上去 —— 把「不限幅」取消勾選再跑一次就到得了，代價是最頂端那幾個瞬間會被限幅動到。）',
  '    (%.1f LUFS chosen, %.1f dB lower. Gain alone can\'t go higher; uncheck "No limiter" to reach it, at the cost of limiting the loudest peaks.)',
  '    （選択した %.1f LUFS より %.1f dB 低い。ゲインだけでは上げられないので、「リミッターなし」を外せば届きますが、最大ピークにリミッターがかかります。）')
A('  %-46s %+6.2f dB   已經在目標上，不動',
  '  %-46s %+6.2f dB   already on target, unchanged',
  '  %-46s %+6.2f dB   目標どおりのため変更なし')
A('套增益 %d/%d\u3000%s（%+.2f dB）',
  'Applying gain %d/%d\u3000%s (%+.2f dB)',
  'ゲイン適用 %d/%d\u3000%s（%+.2f dB）')
A('  %-46s 失敗：%s',
  '  %-46s failed: %s',
  '  %-46s 失敗：%s')
A('  %-46s 換不掉原檔：%s',
  "  %-46s can't replace the original: %s",
  '  %-46s 元ファイルを置き換えられません：%s')
A('，有限幅',
  ', with limiting',
  '、リミッターあり')
A('音量對齊完成：全部對到 %.1f LUFS —— 改了 %d 個，本來就在目標上 %d 個%s',
  'Loudness matched: all at %.1f LUFS — %d changed, %d already on target%s',
  '音量合わせ完了：すべて %.1f LUFS — 変更 %d 個、元から目標どおり %d 個%s')
A('，失敗 %d 個',
  ', %d failed',
  '、失敗 %d 個')
A('\u3000（原本差距 %.1f dB → 現在 0.0 dB）',
  '\u3000(spread was %.1f dB → now 0.0 dB)',
  '\u3000（元の差 %.1f dB → 現在 0.0 dB）')
A('（沒有 ffprobe，跳過同剪輯檢查）',
  '(no ffprobe, same-edit check skipped)',
  '（ffprobe がないため同一編集チェックをスキップ）')
A('同剪輯檢查（比總長度）:',
  'Same-edit check (total length):',
  '同一編集チェック（全長）:')
A('  %-44s 讀不到長度',
  '  %-44s length unreadable',
  '  %-44s 長さを読めません')
A('  %-44s %9.3f s   同剪輯',
  '  %-44s %9.3f s   same edit',
  '  %-44s %9.3f s   同一編集')
A('  %-44s %9.3f s   差 %+.3f s   << 不同剪輯',
  '  %-44s %9.3f s   diff %+.3f s   << different edit',
  '  %-44s %9.3f s   差 %+.3f s   << 別の編集')
A('!! 長度對不上的那些不是同一個剪輯 —— 對應點不能共用，拿去當配音版本會整段歪掉。',
  "!! Files whose length doesn't match are a different edit — sync points can't be shared; using them as voice versions would drift.",
  '!! 長さが合わないものは別の編集です — 対応点は共有できず、ボイス版として使うとずれます。')
A('    失敗：',
  '    failed: ',
  '    失敗：')
A('，失敗 %d 支（%s）',
  ', %d failed (%s)',
  '、失敗 %d 本（%s）')
A('無聲工作影片：完成 %d 支%s',
  'Silent work videos: %d done%s',
  '無音作業動画：%d 本完了%s')
A('讀取來源長度…（%d/%d）',
  'Reading source length… (%d/%d)',
  '元の長さを読み込み中…（%d/%d）')
A('找不到 ffmpeg。把 ffmpeg 資料夾放在 kkscenebridge 旁邊（或裝好並加進 PATH）再試一次。',
  'ffmpeg not found. Put the ffmpeg folder next to kkscenebridge (or install it and add it to PATH), then try again.',
  'ffmpeg が見つかりません。ffmpeg フォルダを kkscenebridge の横に置く（またはインストールして PATH に追加する）か確認してから再試行してください。')
A('轉檔中…（%d/%d，ffmpeg 已啟動）',
  'Converting… (%d/%d, ffmpeg started)',
  '変換中…（%d/%d、ffmpeg 起動済み）')
A('轉檔中 %d/%d\u3000%.0f%%\u3000（%s / %s）',
  'Converting %d/%d\u3000%.0f%%\u3000(%s / %s)',
  '変換中 %d/%d\u3000%.0f%%\u3000（%s / %s）')
A('轉檔中 %d/%d\u3000已處理 %s',
  'Converting %d/%d\u3000processed %s',
  '変換中 %d/%d\u3000処理済み %s')
A('ffmpeg 結束碼 %s',
  'ffmpeg exit code %s',
  'ffmpeg 終了コード %s')
A('%s（%.1f MB，關鍵影格每 %d 幀，seek 誤差上限約 %.2f 秒）',
  '%s (%.1f MB, keyframe every %d frames, seek error up to about %.2f s)',
  '%s（%.1f MB、キーフレーム %d フレームごと、シーク誤差は最大約 %.2f 秒）')
A('這台電腦的 PyQt6 沒有多媒體模組，播放器停用。\n裝上之後重開就會出現：pip install PyQt6-Qt6 PyQt6\n（沒有播放器也能用，秒數自己從外部播放器抄進右邊表格）',
  'This PyQt6 has no multimedia module, so the player is disabled.\nInstall it and restart: pip install PyQt6-Qt6 PyQt6\n(Works without it too — copy times from an external player into the table on the right)',
  'この PC の PyQt6 にはマルチメディアモジュールがないため、プレーヤーは無効です。\nインストールして再起動すると表示されます：pip install PyQt6-Qt6 PyQt6\n（プレーヤーなしでも使えます。外部プレーヤーの秒数を右の表に入力してください）')
A('播放',
  'Play',
  '再生')
A('−1格',
  '−1F',
  '−1F')
A('+1格',
  '+1F',
  '+1F')
A('速度',
  'Speed',
  '速度')
A('音量',
  'Volume',
  '音量')
A('擷取這一秒 →',
  'Capture this time →',
  'この秒数を取り込む →')
A('把目前的影片秒數填進右邊那一列的「影片」欄',
  'Put the current video time into the "Video" column of the selected row on the right',
  '現在の動画の秒数を右の選択行の「動画」欄に入れる')
A('暫停',
  'Pause',
  '一時停止')
A('讀不到影片',
  "Can't read the video",
  '動画を読み込めません')
A('用',
  'Use',
  '使う')
A('影片 ★來源',
  'Video ★source',
  '動画 ★ソース')
A('影片預覽',
  'Video preview',
  '動画プレビュー')
A('影片',
  'Video',
  '動画')
A('說明',
  'Note',
  '説明')
A('第 %d 列只填了一半',
  'Row %d is only half filled',
  '%d 行目が半分しか入力されていません')
A('第 %d 列：%s',
  'Row %d: %s',
  '%d 行目：%s')
A('失敗：%s',
  'Failed: %s',
  '失敗：%s')
A('從切好的音頻反推對應點',
  'Derive sync points from pre-cut audio',
  'カット済み音声から対応点を逆算')
A('把切好的片段拿去跟原始音檔比對，量出每一段的頭尾落在原檔的第幾秒，\n配上卡片的段落時間軸，就是新插件要的對應點。\n檔案可以直接拖進這個視窗：.png 當場景卡，音訊進下面的清單。',
  "Compares the cut clips against the original audio to find where each clip starts and ends in it;\ncombined with the card's segment timeline, that gives the sync points.\nYou can drag files into this window: .png as the scene card, audio into the list below.",
  'カット済みのクリップを元の音声と照合し、各クリップの開始・終了が元の何秒かを測ります。\nカードのシーンのタイムラインと合わせたものが対応点になります。\nファイルはこのウィンドウにドラッグできます：.png はシーンカード、音声は下のリストへ。')
A('合併好的場景卡',
  'Merged scene card',
  '結合したシーンカード')
A('要有它才知道每一段的時間軸是幾秒到幾秒',
  "Needed to know each segment's time range",
  '各シーンのタイムラインの範囲を知るために必要')
A('原始音檔',
  'Original audio',
  '元の音声')
A('沒切過的那一支（bak 資料夾裡的原音頻）',
  'The uncut one (the original in the bak folder)',
  'カット前のもの（bak フォルダの元音声）')
A('切好的片段 —— 右邊選它對應卡片上的哪一段',
  'Cut clips — pick which card segment each belongs to on the right',
  'カット済みクリップ — 右でカードのどのシーンかを選ぶ')
A('切好的音頻',
  'Cut audio',
  'カット済み音声')
A('對應卡片的哪一段',
  'Card segment',
  '対応するシーン')
A('加入檔案…',
  'Add files…',
  'ファイルを追加…')
A('加入資料夾…',
  'Add folder…',
  'フォルダを追加…')
A('↑ 設為原始音檔',
  '↑ Set as original',
  '↑ 元の音声に設定')
A('依順序重新配段',
  'Reassign in order',
  '順番に割り当て直す')
A('開始比對',
  'Start matching',
  '照合開始')
A('套用到對應點表格',
  'Apply to sync-point table',
  '対応点の表に適用')
A('【錯誤】%s',
  '[Error] %s',
  '【エラー】%s')
A('出錯了（視窗沒關）',
  'Something went wrong (the window is still open)',
  'エラーが発生しました（ウィンドウは開いたまま）')
A('%s\n\n完整內容看下面的紀錄。',
  '%s\n\nSee the log below for details.',
  '%s\n\n詳細は下のログを見てください。')
A('選合併好的場景卡',
  'Choose the merged scene card',
  '結合したシーンカードを選択')
A('讀取卡片的段落中…',
  "Reading the card's segments…",
  'カードのシーンを読み込み中…')
A("<span style='color:%s'>卡片讀不出段落：%s</span>",
  "<span style='color:%s'>Can't read segments from the card: %s</span>",
  "<span style='color:%s'>カードからシーンを読み取れません：%s</span>")
A('卡片讀不出段落：%s',
  "Can't read segments from the card: %s",
  'カードからシーンを読み取れません：%s')
A('第%d段  %s  (%s ~ %s)',
  'Segment %d  %s  (%s ~ %s)',
  'シーン%d  %s  (%s ~ %s)')
A('卡片有 <b>%d</b> 段：%s',
  'The card has <b>%d</b> segments: %s',
  'カードのシーン数 <b>%d</b>：%s')
A('第%d段 %s',
  'Segment %d %s',
  'シーン%d %s')
A("<span style='color:%s'>還沒有段落資料 —— 指定合併好的場景卡才算得出對應點</span>",
  "<span style='color:%s'>No segment data yet — choose the merged scene card to compute sync points</span>",
  "<span style='color:%s'>シーンのデータがありません — 結合したシーンカードを指定すると対応点を計算できます</span>")
A('（不用這個片段）',
  "(don't use this clip)",
  '（このクリップは使わない）')
A('選切好的片段',
  'Choose cut clips',
  'カット済みクリップを選択')
A('音訊 (*.wav *.flac *.ogg *.m4a *.mp3);;全部 (*.*)',
  'Audio (*.wav *.flac *.ogg *.m4a *.mp3);;All (*.*)',
  '音声 (*.wav *.flac *.ogg *.m4a *.mp3);;すべて (*.*)')
A('選一個資料夾',
  'Choose a folder',
  'フォルダを選択')
A('選原始（沒切過的）音檔',
  'Choose the original (uncut) audio',
  '元の（カット前の）音声を選択')
A('音訊/影片 (*.wav *.flac *.ogg *.m4a *.mp3 *.mp4 *.mkv);;全部 (*.*)',
  'Audio/video (*.wav *.flac *.ogg *.m4a *.mp3 *.mp4 *.mkv);;All (*.*)',
  '音声/動画 (*.wav *.flac *.ogg *.m4a *.mp3 *.mp4 *.mkv);;すべて (*.*)')
A('還不能跑',
  "Can't run yet",
  'まだ実行できません')
A('先指定原始音檔',
  'Set the original audio first',
  '先に元の音声を指定してください')
A('先指定合併好的場景卡 —— 沒有段落的時間軸就算不出對應點',
  'Set the merged scene card first — without segment timing there are no sync points',
  '先に結合したシーンカードを指定してください — シーンのタイムラインがないと対応点を計算できません')
A('沒有任何片段指定了對應的段落',
  'No clip has a segment assigned',
  'シーンが割り当てられたクリップがありません')
A('重複了',
  'Duplicate',
  '重複')
A('原始音檔也出現在片段清單裡，移掉它',
  'The original audio is also in the clip list; remove it',
  '元の音声がクリップのリストにも入っています。削除してください')
A('%-40s → 第%d段 %s',
  '%-40s → segment %d %s',
  '%-40s → シーン%d %s')
A("<br><span style='color:#1a7f37'><b>★&nbsp;不需要影片：片段完整蓋滿原檔，沒有開場動畫、過場或片尾。<br>&nbsp;&nbsp;&nbsp;產生&nbsp;cutscene.json&nbsp;時不用指定&nbsp;★來源影片，把配音勾起來就好。</b></span>",
  "<br><span style='color:#1a7f37'><b>★&nbsp;No video needed: the clips cover the whole original, no intro, transitions or ending.<br>&nbsp;&nbsp;&nbsp;When generating&nbsp;cutscene.json&nbsp;you don't need a&nbsp;★source video, just check the voices.</b></span>",
  "<br><span style='color:#1a7f37'><b>★&nbsp;動画は不要：クリップが元の音声を完全にカバーしており、オープニング・つなぎ・エンディングがありません。<br>&nbsp;&nbsp;&nbsp;cutscene.json&nbsp;の生成時に&nbsp;★ソース動画は不要で、ボイスにチェックを入れるだけです。</b></span>")
A('\n量出 %d 個對應點，按「套用到對應點表格」帶進去。',
  '\nFound %d sync points; press "Apply to sync-point table" to use them.',
  '\n対応点を %d 個測定しました。「対応点の表に適用」で取り込みます。')
A('載入不了 kkvariantmap：%s',
  "Can't load kkvariantmap: %s",
  'kkvariantmap を読み込めません：%s')
A('掃描配音「%s」',
  'Scanning voice "%s"',
  'ボイス「%s」をスキャン中')
A('配音對照 —— 剪輯不一樣的版本',
  'Voice mapping — versions with a different edit',
  'ボイス対応 — 編集が違うバージョン')
A('各版配音如果是同一套剪輯重新上音（多數情況），這裡什麼都不用做。\n只有「某一版少了開場 / 中間剪掉一塊」時才要量對照點 —— 對照表是「主配音的第幾秒 → 這一版的第幾秒」。',
  'If every voice version is the same edit re-voiced (the usual case), nothing is needed here.\nOnly when a version lacks the intro or has a cut in the middle do you measure mapping points: "main voice time → this version\'s time".',
  '各ボイスが同じ編集の吹き替え違い（ほとんどの場合）なら、ここでは何もする必要はありません。\nあるバージョンだけオープニングがない・途中がカットされている場合に、「メインボイスの秒数 → このバージョンの秒数」を測ります。')
A('主配音',
  'Main voice',
  'メインボイス')
A('對應點表格（timeline → 影片）量的是哪一版的秒數。\n通常是跟來源影片一樣長的那一版。',
  'Which version the sync-point table (timeline → video) was measured against.\nUsually the one as long as the source video.',
  '対応点の表（timeline → 動画）をどのバージョンで測ったか。\n普通はソース動画と同じ長さのものです。')
A('配音版本',
  'Voice versions',
  'ボイスのバージョン')
A('配音',
  'Voice',
  'ボイス')
A('長度',
  'Length',
  '長さ')
A('自動掃所有需要的版本',
  'Auto-scan all needed versions',
  '必要なバージョンを全部スキャン')
A('長度跟主配音不一樣、而且還沒有對照表的版本，全部掃一次。\n要讀整支音檔，一個版本大概十幾秒。',
  'Scans every version whose length differs from the main voice and has no mapping yet.\nReads the whole file; about ten-odd seconds per version.',
  'メインボイスと長さが違い、まだ対応表がないバージョンを全部スキャンします。\n音声全体を読むので、1 バージョン十数秒かかります。')
A('只掃選取的這一個',
  'Scan only the selected one',
  '選択中のものだけスキャン')
A('對照點',
  'Mapping points',
  '対応点')
A('主配音秒數',
  'Main voice time',
  'メインボイスの秒数')
A('這一版的秒數',
  "This version's time",
  'このバージョンの秒数')
A('新增一列',
  'Add row',
  '行を追加')
A('刪除這列',
  'Delete row',
  '行を削除')
A('全部清掉',
  'Clear all',
  'すべて削除')
A('檢查與建議（存檔時會重算）',
  'Checks and suggestions (recomputed on save)',
  'チェックと提案（保存時に再計算）')
A('存檔',
  'Save',
  '保存')
A('寫回這張卡的 cutscene.json（還沒產生的話先暫存，產生時自動併入）。',
  "Write into this card's cutscene.json (kept aside if not generated yet, merged in when it is).",
  'このカードの cutscene.json に書き込みます（未生成なら一時保存し、生成時に自動で統合）。')
A('關閉',
  'Close',
  '閉じる')
A('（量不到長度）',
  '(length unknown)',
  '（長さ不明）')
A('長 %.2f 秒',
  '%.2f s long',
  '長さ %.2f 秒')
A('⇄ 有對照表',
  '⇄ has mapping',
  '⇄ 対応表あり')
A('≈ 長度不同，要量！',
  '≈ length differs, measure it!',
  '≈ 長さが違うので測定が必要！')
A('≈ 當成同步',
  '≈ treated as in sync',
  '≈ 同期として扱う')
A('「%s」是主配音，不需要對照表',
  '"%s" is the main voice and needs no mapping',
  '「%s」はメインボイスなので対応表は不要です')
A('「%s」的對照點 —— 左欄是主配音「%s」的秒數',
  'Mapping points for "%s" — left column is main voice "%s" time',
  '「%s」の対応点 — 左の列はメインボイス「%s」の秒数')
A('第 %d 列看不懂（格式像 01:12.550 或 72.55）',
  "Row %d can't be parsed (format like 01:12.550 or 72.55)",
  '%d 行目を解釈できません（01:12.550 や 72.55 の形式）')
A('【格式】%s',
  '[Format] %s',
  '【形式】%s')
A('先選一個版本',
  'Pick a version first',
  '先にバージョンを選んでください')
A('左邊選一個「不是主配音」的版本再加點。',
  "Pick a version on the left that isn't the main voice, then add points.",
  '左で「メインボイス以外」のバージョンを選んでから点を追加してください。')
A('讀不到卡片的段落 —— 只能檢查對照點本身，沒辦法告訴你哪一段沒被蓋到',
  "Can't read the card's segments — only the points themselves can be checked, not which segment is uncovered",
  'カードのシーンを読めません — 対応点そのものしかチェックできず、どのシーンがカバーされていないかは分かりません')
A('建議：段 %d   主配音 %s  →  預估這一版 %s   （%s）',
  'Suggestion: segment %d   main voice %s  →  this version ≈ %s   (%s)',
  '提案：シーン %d   メインボイス %s  →  このバージョンの推定 %s   （%s）')
A('沒發現問題。',
  'No problems found.',
  '問題は見つかりませんでした。')
A('讀不到卡片段落：%s',
  "Can't read card segments: %s",
  'カードのシーンを読めません：%s')
A('主配音檔案不見了',
  'Main voice file is missing',
  'メインボイスのファイルがありません')
A('找不到「%s」的音檔，沒辦法拿它當基準。',
  'Can\'t find the audio for "%s", so it can\'t be the reference.',
  '「%s」の音声が見つからないため、基準にできません。')
A('沒有需要掃的',
  'Nothing to scan',
  'スキャン対象なし')
A('其他版本要嘛長度跟主配音一樣（同一套剪輯，不用對照表），要嘛已經有對照表了。\n\n想重掃某一個，選它再按「只掃選取的這一個」。',
  'Every other version is either as long as the main voice (same edit, no mapping needed) or already mapped.\n\nTo rescan one, select it and press "Scan only the selected one".',
  '他のバージョンはメインボイスと同じ長さ（同じ編集で対応表不要）か、すでに対応表があります。\n\n再スキャンしたい場合は選んで「選択中のものだけスキャン」を押してください。')
A('左邊選一個「不是主配音」的版本。',
  "Pick a version on the left that isn't the main voice.",
  '左で「メインボイス以外」のバージョンを選んでください。')
A('基準（主配音）：%s',
  'Reference (main voice): %s',
  '基準（メインボイス）：%s')
A('配音「%s」掃不出來：%s',
  'Voice "%s" couldn\'t be scanned: %s',
  'ボイス「%s」をスキャンできません：%s')
A('配音「%s」得到 %d 個對照點',
  'Voice "%s": %d mapping points',
  'ボイス「%s」の対応点 %d 個')
A('對照點格式不對',
  'Mapping points have a format error',
  '対応点の形式が正しくありません')
A('存不起來',
  "Can't save",
  '保存できません')
A('已存 %s（%s）',
  'Saved %s (%s)',
  '%s を保存（%s）')
A('已寫回：\n%s\n\n插件下次載入這張卡就讀得到了，不用重新產生。\n（重新產生也不會弄丟 —— 產生流程會從這份 json 把對照表讀回去）',
  "Written back:\n%s\n\nThe plugin reads it the next time this card loads; no need to regenerate.\n(Regenerating won't lose it either — generation reads the mapping back from this json)",
  '書き戻しました：\n%s\n\n次にこのカードを読み込むとプラグインに反映されます。再生成は不要です。\n（再生成しても消えません — 生成時にこの json から対応表を読み戻します）')
A('這張卡還沒有 cutscene.json，先暫存在：\n%s\n\n回主畫面按「產生 cutscene.json」就會併進去，然後這個暫存檔會被收掉。',
  'This card has no cutscene.json yet, so it\'s kept at:\n%s\n\nPress "Generate cutscene.json" on the main screen to merge it in; this temp file is then removed.',
  'このカードにはまだ cutscene.json がないので、一時的に次へ保存しました：\n%s\n\nメイン画面で「cutscene.json を生成」を押すと統合され、この一時ファイルは片付けられます。')
A('存好了',
  'Saved',
  '保存しました')
A('檔案（卡片 / 影片 / 音訊 / 對應點 / 資料夾 都可以直接拖進來）',
  'Files (cards / videos / audio / sync points / folders can be dragged in)',
  'ファイル（カード / 動画 / 音声 / 対応点 / フォルダをドラッグできます）')
A('挑好卡片之後，會自動帶進同名的 <卡名>.pairs.txt，\n以及輸出資料夾裡同名的 <卡名>.cutscene.json（有的話）。',
  'After picking a card, the same-named <card>.pairs.txt is loaded automatically,\nplus <card>.cutscene.json from the output folder if there is one.',
  'カードを選ぶと、同名の <カード名>.pairs.txt と、\n出力フォルダの同名の <カード名>.cutscene.json（あれば）を自動で読み込みます。')
A('設定檔輸出到',
  'Write config to',
  '設定ファイルの出力先')
A('產生的 <卡名>.cutscene.json 會寫到這裡。\n插件預設會搜尋 UserData/cutscene 和場景卡旁邊。\n挑卡片時也是到這裡找同名的設定檔。',
  "The generated <card>.cutscene.json goes here.\nThe plugin searches UserData/cutscene (and the card's own folder) by default.\nSame-named configs are also looked up here when picking a card.",
  '生成した <カード名>.cutscene.json をここに書き出します。\nプラグインは既定で UserData/cutscene（とカードと同じフォルダ）を探します。\nカードを選んだ時もここで同名の設定ファイルを探します。')
A('載入既有的 cutscene.json…',
  'Load existing cutscene.json…',
  '既存の cutscene.json を読み込む…')
A('把做好的設定整份讀回介面（卡片、影片、配音、對應點），\n改完按「產生」蓋回去。也可以直接把 json 拖進視窗。',
  'Load a finished config back into the UI (card, video, voices, sync points),\nthen press "Generate" to overwrite it. You can also drag the json into the window.',
  '作成済みの設定を丸ごと画面に読み戻します（カード、動画、ボイス、対応点）。\n変更後「生成」で上書きします。json をウィンドウにドラッグしても OK。')
A('影片與配音 —— 打勾的音訊成為配音版本；標 ★來源 的影片就是寫進 json 當 videoFile 的那一支',
  'Videos and voices — checked audio becomes voice versions; the video marked ★source is written into the json as videoFile',
  '動画とボイス — チェックした音声がボイスのバージョンに、★ソースの動画が json の videoFile になります')
A('全選',
  'Select all',
  'すべて選択')
A('全不選',
  'Select none',
  '選択解除')
A('把勾選的影片抽成 wav',
  'Extract wav from checked videos',
  'チェックした動画から wav を抽出')
A('抽 wav（另外挑檔案…）',
  'Extract wav (pick files…)',
  'wav を抽出（ファイルを選ぶ…）')
A('對齊音量…',
  'Match loudness…',
  '音量を合わせる…')
A('每支影片抽一個同名 wav（48kHz / pcm_s16le），\n抽完自動加進清單。需要 ffmpeg。',
  'Extracts a same-named wav from each video (48kHz / pcm_s16le)\nand adds it to the list. Needs ffmpeg.',
  '各動画から同名の wav（48kHz / pcm_s16le）を抽出し、\nリストに追加します。ffmpeg が必要です。')
A('開檔案總管多選影片，不必先加進上面的清單。\n抽出來的 wav 放在各自影片旁邊，抽完自動加進清單。',
  'Pick several videos in Explorer without adding them to the list first.\nThe wav goes next to each video and is added to the list.',
  'エクスプローラーで動画を複数選択します（先にリストへ追加しなくて OK）。\nwav は各動画の横に作られ、リストに追加されます。')
A('多選音檔，把響度對齊到同一個目標（EBU R128）。只套固定增益，長度不變。\n同一場景的各版配音一起選，切換時才不會忽大忽小。會直接換掉原檔。',
  "Pick several audio files and match their loudness to one target (EBU R128). Fixed gain only; length unchanged.\nPick all voice versions of a scene together so switching doesn't jump in volume. Replaces the originals.",
  '複数の音声を選び、音量を同じ目標（EBU R128）に合わせます。固定ゲインのみで長さは変わりません。\n同じシーンの各ボイスをまとめて選ぶと、切り替えても音量が揃います。元ファイルを置き換えます。')
A('已存在也重抽',
  'Re-extract existing',
  '既存も再抽出')
A('抽完順便對齊音量',
  'Match loudness after extracting',
  '抽出後に音量を合わせる')
A('抽出來的 wav 是原樣搬過來的，各版配音本來多大聲就多大聲。\n勾起來的話抽完直接對齊到同一個響度。',
  'Extracted wavs keep their original loudness, which differs between versions.\nChecked: match them to one loudness right after extracting.',
  '抽出した wav は元の音量のままなので、バージョンごとに音量が違います。\nオンにすると抽出後すぐに同じ音量へ揃えます。')
A('所有檔案都對到這個響度。越靠近 0 越大聲，峰值塞不下的部分會被限幅。',
  "Every file is matched to this loudness. Closer to 0 = louder; peaks that don't fit are limited.",
  'すべてのファイルをこの音量に合わせます。0 に近いほど大きく、収まらないピークはリミッターで抑えます。')
A('不限幅',
  'No limiter',
  'リミッターなし')
A('不勾：推到目標，峰值超出 %g dBTP 的部分用限幅壓回。\n勾起來：只套固定增益，不動波形，但可能比目標安靜。',
  'Unchecked: push to the target, limiting peaks above %g dBTP.\nChecked: fixed gain only, waveform untouched, but it may end up quieter than the target.',
  'オフ：目標まで上げ、%g dBTP を超えるピークはリミッターで抑えます。\nオン：固定ゲインのみで波形は変えませんが、目標より静かになることがあります。')
A('設為 ★來源影片',
  'Set as ★source video',
  '★ソース動画に設定')
A('★來源轉成無聲工作影片',
  'Make ★source a silent work video',
  '★ソースを無音作業動画に変換')
A('拿目前的 ★來源影片去轉：去掉音軌、加密關鍵影格。\n音訊另外走 wav，影片只負責畫面；關鍵影格夠密，拖時間軸才定得準。\n轉好的檔會自動接手成新的 ★來源。',
  'Converts the current ★source video: strips audio, adds dense keyframes.\nAudio goes via wav; dense keyframes make timeline seeking accurate.\nThe result becomes the new ★source.',
  '現在の ★ソース動画を変換します：音声を除去し、キーフレームを密にします。\n音声は wav で別に扱い、キーフレームが密なのでシークが正確になります。\n変換後のファイルが新しい ★ソースになります。')
A('批次轉無聲（選檔案…）',
  'Batch silent convert (pick files…)',
  '無音に一括変換（ファイルを選ぶ…）')
A('多選影片排隊轉成 <原名>_src.mp4（放在原片旁邊），\n不會自動設成 ★來源。',
  'Queue several videos into <name>_src.mp4 (next to each original);\nnot set as ★source automatically.',
  '複数の動画を順番に <元の名前>_src.mp4 へ変換します（元動画の横）。\n★ソースには自動設定されません。')
A('壓成 1080p',
  'Scale to 1080p',
  '1080p に縮小')
A('壓成 1440p（VR 留餘裕）',
  'Scale to 1440p (headroom for VR)',
  '1440p に縮小（VR 向けに余裕）')
A('壓成 720p',
  'Scale to 720p',
  '720p に縮小')
A('不壓，保持原本（4K 就是 4K）',
  'Keep original size (4K stays 4K)',
  '縮小しない（4K は 4K のまま）')
A('長寬比保持原樣。VR 通常 1080p 就夠，4K 容易讓 Studio 掉幀。',
  'Aspect ratio is kept. 1080p is usually enough for VR; 4K easily drops frames in Studio.',
  '縦横比は維持します。VR でも通常 1080p で十分で、4K は Studio でフレーム落ちしやすいです。')
A('主要配音',
  'Main voice',
  'メインボイス')
A('改輸出 VNGE 格式（預設是新的插件格式）',
  'Output VNGE format instead (default is the new plugin format)',
  'VNGE 形式で出力（既定は新しいプラグイン形式）')
A('影片預覽 —— 找到畫面後按「擷取這一秒」',
  'Video preview — find the frame, then press "Capture this time"',
  '動画プレビュー — 画面を見つけたら「この秒数を取り込む」')
A('放大成獨立視窗 ⬈',
  'Pop out into a window ⬈',
  '別ウィンドウに拡大 ⬈')
A('整塊搬到一個可自由縮放的視窗，按鍵一起帶過去。\n關掉那個視窗就搬回來，播放位置不會重來。',
  'Moves this whole panel, buttons included, into a resizable window.\nClosing that window moves it back; playback position is kept.',
  'このパネルをボタンごと自由に拡大縮小できるウィンドウへ移します。\nそのウィンドウを閉じると戻り、再生位置はそのままです。')
A('對應點（timeline 秒數從遊戲面板抄過來）',
  'Sync points (copy timeline times from the in-game panel)',
  '対応点（timeline の秒数はゲーム内パネルから写す）')
A('跳到這列的影片秒數',
  "Jump to this row's video time",
  'この行の動画秒数へ移動')
A('讀 pairs.txt',
  'Load pairs.txt',
  'pairs.txt を読む')
A('存 pairs.txt',
  'Save pairs.txt',
  'pairs.txt を保存')
A('從切好的音頻反推…',
  'Derive from pre-cut audio…',
  'カット済み音声から逆算…')
A('配音對照…',
  'Voice mapping…',
  'ボイス対応…')
A('以前用 VNGE 做的卡片，音檔是一段一段切好的。\n這個功能把切好的片段拿去跟原始音檔比對，\n自動量出每一段落在原檔的第幾秒，直接變成對應點。',
  'Old VNGE cards have their audio cut into pieces.\nThis compares the pieces with the original audio,\nmeasures where each one sits in it and turns that into sync points.',
  '以前 VNGE で作ったカードは音声がシーンごとにカットされています。\nこの機能はカット済みのクリップを元の音声と照合し、\n各クリップが元の何秒にあるかを測って対応点にします。')
A('某一版配音的剪輯跟其他版不同（少了開場、中間剪掉）時用：\n量「主配音的第幾秒 → 這一版的第幾秒」。各版長度一樣就不用開。',
  'Use when one voice version\'s edit differs (missing intro, a cut in the middle):\nmeasure "main voice time → this version\'s time". Not needed if all versions are the same length.',
  'あるボイスの編集が他と違う（オープニングがない、途中カット）時に使います：\n「メインボイスの秒数 → このバージョンの秒数」を測ります。全部同じ長さなら不要です。')
A('跳到 timeline',
  'Jump to timeline',
  'timeline へ移動')
A('→ 預測的影片位置',
  '→ Predicted video position',
  '→ 予測した動画の位置')
A('用目前這份設定推算該 timeline 秒數對應到影片的哪裡，\n播放器跳過去，你再確認實際是第幾秒',
  'Uses the current setup to predict where this timeline time lands in the video;\nthe player jumps there so you can confirm the real time',
  '今の設定でこの timeline 秒数が動画のどこに当たるかを予測し、\nプレーヤーをそこへ移動します。実際の秒数を確認してください')
A('建議下一個去量的位置（跑完才會有，雙擊直接跳過去）',
  'Suggested next points to measure (after a run; double-click to jump)',
  '次に測るとよい位置（実行後に表示、ダブルクリックで移動）')
A('產生 cutscene.json',
  'Generate cutscene.json',
  'cutscene.json を生成')
A('開啟輸出資料夾',
  'Open output folder',
  '出力フォルダを開く')
A('報告',
  'Report',
  'レポート')
A('對應點',
  'Sync points',
  '対応点')
A('既有設定（整份帶入）',
  'Existing config (load everything)',
  '既存の設定（丸ごと読み込み）')
A('%d 個影片/音訊',
  '%d videos/audio',
  '動画/音声 %d 個')
A('（順便設成 ★來源）',
  '(also set as ★source)',
  '（★ソースにも設定）')
A('已收下：',
  'Received: ',
  '受け取り：')
A('① 合併場景',
  '① Merge scenes',
  '① シーンを結合')
A('② 選影片／配音',
  '② Pick video / voices',
  '② 動画／ボイスを選ぶ')
A('③ 抓對應點',
  '③ Measure sync points',
  '③ 対応点を測る')
A('④ 產生設定',
  '④ Generate config',
  '④ 設定を生成')
A("<span style='color:%s'><b>有檔案的長度對不上，不是同一個剪輯 —— 詳見下方紀錄</b></span>",
  "<span style='color:%s'><b>Some file lengths don't match — not the same edit; see the log below</b></span>",
  "<span style='color:%s'><b>長さが合わないファイルがあり、同じ編集ではありません — 下のログを見てください</b></span>")
A('加入影片或音訊',
  'Add videos or audio',
  '動画または音声を追加')
A('影片與音訊 (*.mp4 *.mkv *.mov *.webm *.avi *.m4v *.wmv *.wav *.ogg *.mp3 *.m4a *.flac);;全部 (*.*)',
  'Video and audio (*.mp4 *.mkv *.mov *.webm *.avi *.m4v *.wmv *.wav *.ogg *.mp3 *.m4a *.flac);;All (*.*)',
  '動画と音声 (*.mp4 *.mkv *.mov *.webm *.avi *.m4v *.wmv *.wav *.ogg *.mp3 *.m4a *.flac);;すべて (*.*)')
A('加入整個資料夾裡的影片與音訊',
  'Add all videos and audio in a folder',
  'フォルダ内の動画と音声をすべて追加')
A('加入 %d 個檔案',
  'Added %d files',
  '%d 個のファイルを追加')
A('★來源改成 ',
  '★source set to ',
  '★ソースを変更：')
A('選一列影片再按這個',
  'Select a video row, then press this',
  '動画の行を選んでから押してください')
A('沒有來源影片',
  'No source video',
  'ソース動画がありません')
A('先在清單裡選一列影片按「設為 ★來源影片」。',
  'Select a video row in the list and press "Set as ★source video" first.',
  '先にリストで動画の行を選び「★ソース動画に設定」を押してください。')
A('已經有這個檔',
  'File already exists',
  'ファイルが既にあります')
A('%s 已存在，要覆蓋嗎？',
  '%s already exists. Overwrite?',
  '%s は既にあります。上書きしますか？')
A('選要轉成無聲工作影片的影片（可多選）',
  'Choose videos to convert into silent work videos (multi-select)',
  '無音作業動画に変換する動画を選択（複数可）')
A('影片 (*.mp4 *.mkv *.mov *.avi *.wmv *.m4v *.webm);;所有檔案 (*.*)',
  'Video (*.mp4 *.mkv *.mov *.avi *.wmv *.m4v *.webm);;All files (*.*)',
  '動画 (*.mp4 *.mkv *.mov *.avi *.wmv *.m4v *.webm);;すべてのファイル (*.*)')
A('跳過已經存在的 %d 支：%s',
  'Skipping %d that already exist: %s',
  '既にある %d 本をスキップ：%s')
A('沒有要轉的',
  'Nothing to convert',
  '変換対象なし')
A('選到的影片都已經有對應的 _src.mp4 了。\n要重轉的話先把那些檔刪掉或改名。',
  'Every chosen video already has its _src.mp4.\nDelete or rename those files to convert again.',
  '選んだ動画はすべて _src.mp4 があります。\n再変換するならそのファイルを削除するか名前を変えてください。')
A('轉無聲工作影片：%d 支（%s，關鍵影格每 %d 幀，seek 誤差上限約 %.2f 秒）',
  'Silent work videos: %d (%s, keyframe every %d frames, seek error up to about %.2f s)',
  '無音作業動画に変換：%d 本（%s、キーフレーム %d フレームごと、シーク誤差は最大約 %.2f 秒）')
A('原解析度',
  'original resolution',
  '元の解像度')
A('準備轉檔…',
  'Preparing to convert…',
  '変換の準備中…')
A('轉檔中…（%d 支，跑在背景）',
  'Converting… (%d, in the background)',
  '変換中…（%d 本、バックグラウンド）')
A('完成',
  'Done',
  '完了')
A('轉檔沒全部成功',
  'Not every conversion succeeded',
  '一部の変換に失敗しました')
A('已加進清單並設成 ★來源 —— 產生 json 時 videoFile 就會是這一支。音訊記得用同一支**原片**抽的 wav，剪輯才會對得上。',
  "Added to the list and set as ★source — it will be the json's videoFile. Extract the audio wav from the same **original** video so the edit matches.",
  'リストに追加し ★ソースに設定しました — json の videoFile はこれになります。音声は同じ**元動画**から抽出した wav を使ってください（編集を合わせるため）。')
A('已加進清單（批次轉的不會自動變成 ★來源）—— 要用哪一支就選它按「設為 ★來源影片」。',
  'Added to the list (batch results aren\'t set as ★source) — select the one you want and press "Set as ★source video".',
  'リストに追加しました（一括変換は ★ソースに自動設定されません）— 使うものを選んで「★ソース動画に設定」を押してください。')
A('已彈出（關掉視窗收回）',
  'Popped out (close the window to bring it back)',
  '別ウィンドウ表示中（閉じると戻ります）')
A('選要抽 wav 的影片（可多選）',
  'Choose videos to extract wav from (multi-select)',
  'wav を抽出する動画を選択（複数可）')
A('沒有勾選影片',
  'No video checked',
  '動画にチェックがありません')
A('在上面的清單裡勾選要抽 wav 的影片，或用「抽 wav（另外挑檔案…）」自己挑。',
  'Check videos in the list above, or use "Extract wav (pick files…)".',
  '上のリストで wav を抽出する動画にチェックを入れるか、「wav を抽出（ファイルを選ぶ…）」を使ってください。')
A('抽 wav（%d 支影片）',
  'Extract wav (%d videos)',
  'wav 抽出（動画 %d 本）')
A('準備抽 wav…',
  'Preparing to extract wav…',
  'wav 抽出の準備中…')
A('抽音訊中…（2GB 的檔案要一兩分鐘）',
  'Extracting audio… (a 2 GB file takes a minute or two)',
  '音声抽出中…（2GB のファイルは 1〜2 分かかります）')
A('已把 %d 個新的 wav 加進清單',
  'Added %d new wav files to the list',
  '新しい wav を %d 個リストに追加しました')
A('抽 wav 沒全部成功',
  'Not every wav extraction succeeded',
  '一部の wav 抽出に失敗しました')
A('音訊',
  'Audio',
  '音声')
A('選要對齊音量的音檔（同一個場景的各版配音一起選）',
  'Choose audio files to match (pick all voice versions of a scene together)',
  '音量を合わせる音声を選択（同じシーンの各ボイスをまとめて）')
A('音訊 (*.wav *.mp3 *.ogg *.flac *.m4a);;所有檔案 (*.*)',
  'Audio (*.wav *.mp3 *.ogg *.flac *.m4a);;All files (*.*)',
  '音声 (*.wav *.mp3 *.ogg *.flac *.m4a);;すべてのファイル (*.*)')
A('音量對齊：目標 %g LUFS / 真峰值上限 %g dBTP（%s）',
  'Loudness match: target %g LUFS / true peak max %g dBTP (%s)',
  '音量合わせ：目標 %g LUFS / トゥルーピーク上限 %g dBTP（%s）')
A('峰值超出的部分限幅',
  'peaks above are limited',
  '超えたピークはリミッター')
A('純增益，不動波形形狀',
  'gain only, waveform untouched',
  'ゲインのみ、波形はそのまま')
A('  %d 個檔案，會直接換掉原檔。',
  '  %d files; the originals will be replaced.',
  '  %d 個、元ファイルを直接置き換えます。')
A('量響度…',
  'Measuring loudness…',
  '音量測定中…')
A('音量對齊中…（要整支讀過一遍，大檔要一點時間）',
  'Matching loudness… (reads every file fully; big files take a while)',
  '音量合わせ中…（ファイル全体を読むので大きいと時間がかかります）')
A('音量對齊沒全部成功',
  'Not every loudness match succeeded',
  '一部の音量合わせに失敗しました')
A('先選一列',
  'Select a row first',
  '先に行を選んでください')
A('這一列還沒有影片秒數',
  'This row has no video time yet',
  'この行にはまだ動画の秒数がありません')
A('跳到影片 ',
  'Jump to video ',
  '動画へ移動 ')
A('對應點 —— <b>%s</b>%s',
  'Sync points — <b>%s</b>%s',
  '対応点 — <b>%s</b>%s')
A("<span style='color:%s'>（共用檔，存檔後會改成卡片專屬名）</span>",
  "<span style='color:%s'>(shared file; saving switches it to a card-specific name)</span>",
  "<span style='color:%s'>（共有ファイル。保存するとカード専用の名前になります）</span>")
A('這張卡還沒有 cutscene.json —— 已解除跟 %s 的綁定，並清掉上一張卡的影片與配音',
  "This card has no cutscene.json yet — unlinked from %s and cleared the previous card's videos and voices",
  'このカードにはまだ cutscene.json がありません — %s との関連を解除し、前のカードの動画とボイスをクリアしました')
A('上一張卡的設定檔',
  "previous card's config",
  '前のカードの設定ファイル')
A('換了卡片 —— 已清掉上一張卡的影片與配音',
  "Card changed — cleared the previous card's videos and voices",
  'カードを変更 — 前のカードの動画とボイスをクリアしました')
A('[自動載入] %s 讀不了，跳過：%s',
  "[Auto-load] can't read %s, skipped: %s",
  '[自動読み込み] %s を読めないためスキップ：%s')
A('（自動帶入這張卡的設定檔：%s）',
  "(loaded this card's config automatically: %s)",
  '（このカードの設定ファイルを自動で読み込みました：%s）')
A('（這張卡已經有設定檔：%s —— 要整份帶回介面的話按「載入既有的 cutscene.json」，或重新挑一次卡片）',
  '(this card already has a config: %s — press "Load existing cutscene.json" to bring it all back, or pick the card again)',
  '（このカードには設定ファイルがあります：%s — 丸ごと戻すなら「既存の cutscene.json を読み込む」か、カードを選び直してください）')
A('這張卡還沒有 pairs.txt —— 已清掉上一張卡留下的 %d 個對應點',
  'This card has no pairs.txt yet — cleared %d sync points left from the previous card',
  'このカードにはまだ pairs.txt がありません — 前のカードの対応点 %d 個をクリアしました')
A('換了卡片，對應點已清空（這張卡還沒有 pairs.txt）',
  'Card changed; sync points cleared (this card has no pairs.txt yet)',
  'カードを変更したので対応点をクリアしました（このカードには pairs.txt がありません）')
A('這個 Python 沒有 numpy',
  'This Python has no numpy',
  'この Python には numpy がありません')
A('「從切好的音頻反推」要用 FFT 互相關，需要 numpy。\n\n目前這個工具跑在：\n    %s\n    (%s)\n\n用這一行裝到**這個** Python：\n    %s\n\n如果你剛才已經 pip install numpy 卻還是看到這個視窗，就是裝到別的 Python 去了。\n\n錯誤訊息：%s',
  '"Derive from pre-cut audio" uses FFT cross-correlation and needs numpy.\n\nThis tool is running on:\n    %s\n    (%s)\n\nInstall it into **this** Python with:\n    %s\n\nIf you already ran pip install numpy and still see this, it went into a different Python.\n\nError: %s',
  '「カット済み音声から逆算」は FFT 相互相関を使うため numpy が必要です。\n\nこのツールが動いている Python：\n    %s\n    (%s)\n\n**この** Python に入れるコマンド：\n    %s\n\npip install numpy 済みなのにこの画面が出る場合は、別の Python に入っています。\n\nエラー：%s')
A('複製指令',
  'Copy command',
  'コマンドをコピー')
A('安裝指令已複製到剪貼簿',
  'Install command copied to the clipboard',
  'インストールコマンドをクリップボードにコピーしました')
A('[從切好的音頻反推] 載入失敗：%s',
  '[Derive from pre-cut audio] failed to load: %s',
  '[カット済み音声から逆算] 読み込み失敗：%s')
A('  目前的 Python：%s',
  '  Current Python: %s',
  '  現在の Python：%s')
A('  安裝指令：%s',
  '  Install command: %s',
  '  インストールコマンド：%s')
A('開不起來',
  "Can't open",
  '開けません')
A('配音對照要對照卡片的段落，才知道哪一段缺點。',
  "Voice mapping needs the card's segments to know which segment lacks points.",
  'ボイス対応はカードのシーンと照らし合わせて、どのシーンに点が足りないかを判断します。')
A('至少要兩個配音',
  'At least two voices needed',
  'ボイスが 2 つ以上必要です')
A('配音對照是「這一版相對主配音差多少」，只有一個版本沒有對照的對象。\n\n上面的清單把要用的音訊都勾起來再開。',
  'Voice mapping is "how this version differs from the main voice"; with only one version there\'s nothing to compare.\n\nCheck all the audio you want to use in the list above, then open it.',
  'ボイス対応は「このバージョンがメインボイスとどれだけ違うか」なので、1 つだけでは比較対象がありません。\n\n上のリストで使う音声にすべてチェックを入れてから開いてください。')
A('配音對照：主配音 %s，%d 個版本有對照表（%s）← %s',
  'Voice mapping: main voice %s, %d versions mapped (%s) ← %s',
  'ボイス対応：メインボイス %s、対応表ありのバージョン %d 個（%s）← %s')
A('配音對照：目前沒有任何對照表，各版當成同步',
  'Voice mapping: no mappings, all versions treated as in sync',
  'ボイス対応：対応表なし、全バージョンを同期として扱います')
A('從切好的音頻反推：帶入 %d 個對應點（尚未存檔）',
  'Derive from pre-cut audio: %d sync points brought in (not saved)',
  'カット済み音声から逆算：対応点 %d 個を取り込み（未保存）')
A('已帶入 %d 個對應點，確認後按「存 pairs.txt」',
  'Brought in %d sync points; check them and press "Save pairs.txt"',
  '対応点 %d 個を取り込みました。確認して「pairs.txt を保存」を押してください')
A("<span style='color:#1a7f37'><b>不需要影片 —— 配音完整蓋滿原始音檔，這張卡沒有開場動畫、過場或片尾。把配音勾起來直接按「產生 cutscene.json」就好，★來源影片可以留空。</b></span>",
  '<span style=\'color:#1a7f37\'><b>No video needed — the voices fully cover the original audio; this card has no intro, transitions or ending. Check the voices and press "Generate cutscene.json"; the ★source video can stay empty.</b></span>',
  "<span style='color:#1a7f37'><b>動画は不要 — ボイスが元の音声を完全にカバーしており、このカードにはオープニング・つなぎ・エンディングがありません。ボイスにチェックを入れて「cutscene.json を生成」を押すだけで、★ソース動画は空欄で OK。</b></span>")
A('不需要影片：配音完整蓋滿原檔（沒有開場 / 過場 / 片尾）',
  'No video needed: voices fully cover the original (no intro / transitions / ending)',
  '動画不要：ボイスが元の音声を完全にカバー（オープニング / つなぎ / エンディングなし）')
A('載入既有的 cutscene.json',
  'Load existing cutscene.json',
  '既存の cutscene.json を読み込む')
A('cutscene 設定檔 (*.cutscene.json);;所有 json (*.json)',
  'cutscene config (*.cutscene.json);;All json (*.json)',
  'cutscene 設定ファイル (*.cutscene.json);;すべての json (*.json)')
A('讀不了這份設定',
  "Can't read this config",
  'この設定ファイルを読めません')
A('這不是 cutscene 設定檔',
  "This isn't a cutscene config",
  'cutscene 設定ファイルではありません')
A('%s\n\n這是 F7 插件存 VR 視角用的 <卡名>.view.json，裡面只有視角座標，沒有音軌也沒有對應點。\n同資料夾裡也找不到對應的 <卡名>.cutscene.json。\n\n介面沒有動，重選一次檔案就好。',
  '%s\n\nThis is <card>.view.json, where the F7 plugin stores VR views; it only has view coordinates, no tracks or sync points.\nNo matching <card>.cutscene.json was found in the same folder either.\n\nNothing changed; just pick the file again.',
  '%s\n\nこれは F7 プラグインが VR 視点を保存する <カード名>.view.json で、視点の座標だけで音声トラックや対応点はありません。\n同じフォルダに対応する <カード名>.cutscene.json も見つかりません。\n\n画面は変わっていないので、ファイルを選び直してください。')
A('選到的是視角檔，沒有載入',
  'That was a view file; nothing loaded',
  '視点ファイルだったので読み込みませんでした')
A('[注意] %s 是 F7 的視角檔，已自動改讀 %s',
  '[Warning] %s is an F7 view file; loaded %s instead',
  '[注意] %s は F7 の視点ファイルなので、代わりに %s を読み込みました')
A('載入：',
  'Loaded: ',
  '読み込み：')
A('場景卡：',
  'Scene card: ',
  'シーンカード：')
A('找不到對應的場景卡，要自己指定（舊的設定檔沒記這個）',
  "No matching scene card found; set it yourself (older configs didn't record it)",
  '対応するシーンカードが見つかりません。手動で指定してください（古い設定ファイルには記録がありません）')
A('json 裡的配音名稱有 %d 個、檔案有 %d 個，數量對不上；對不到名字的用檔名重新猜，產生出來的按鈕名稱會跟原本不同',
  "The json has %d voice names but %d files; names that don't match are re-guessed from file names, so button names may change",
  'json のボイス名は %d 個、ファイルは %d 個で数が合いません。名前が合わないものはファイル名から推測し直すので、ボタン名が元と変わることがあります')
A('來源影片：',
  'Source video: ',
  'ソース動画：')
A('配音 %d 個：%s',
  '%d voices: %s',
  'ボイス %d 個：%s')
A('這幾個檔案不在原來的位置了，沒有帶進清單（產生前要補回來，不然新的 json 會少掉它們）：\n    ',
  "These files are no longer where they were and weren't added (restore them before generating, or the new json will lack them):\n    ",
  '以下のファイルは元の場所にないため、リストに入れていません（生成前に戻さないと新しい json から抜けます）：\n    ')
A('主要配音「%s」不在帶回來的配音裡，已改用 %s',
  'Main voice "%s" isn\'t among the loaded voices; using %s',
  'メインボイス「%s」が読み込んだボイスにないため、%s を使います')
A('（沒有可用的配音）',
  '(no usable voice)',
  '（使えるボイスなし）')
A('主要配音：',
  'Main voice: ',
  'メインボイス：')
A('設定檔內建的備份',
  'backup inside the config',
  '設定ファイル内のバックアップ')
A('對應點 %d 個（來源：%s）',
  '%d sync points (from: %s)',
  '対応点 %d 個（出所：%s）')
A('這份設定裡沒有對應點，pairs.txt 也找不到 —— 要重新量',
  'This config has no sync points and no pairs.txt was found — measure again',
  'この設定には対応点がなく、pairs.txt も見つかりません — 測り直してください')
A('已載入 <b>%s</b>%s',
  'Loaded <b>%s</b>%s',
  '<b>%s</b> を読み込みました%s')
A("\u3000<span style='color:%s'>有 %d 項要處理</span>",
  "\u3000<span style='color:%s'>%d items need attention</span>",
  "\u3000<span style='color:%s'>要対応 %d 件</span>")
A('已載入 ',
  'Loaded ',
  '読み込み完了 ')
A('找不到 pairs.txt，直接在表格裡新增就好',
  'No pairs.txt found; just add rows in the table',
  'pairs.txt が見つかりません。表に直接追加してください')
A('讀入 %d 個對應點：%s',
  'Read %d sync points: %s',
  '対応点 %d 個を読み込み：%s')
A('[注意] 這是共用的 pairs.txt，同資料夾的其他卡片也會讀到它。按一下「存 pairs.txt」就會改存成這張卡專屬的名字。',
  '[Warning] This is a shared pairs.txt that other cards in the folder also read. Press "Save pairs.txt" to save it under this card\'s own name.',
  '[注意] これは共有の pairs.txt で、同じフォルダの他のカードも読みます。「pairs.txt を保存」を押すとこのカード専用の名前で保存します。')
A('起點',
  'start',
  '開始')
A('終點',
  'end',
  '終了')
A('標著「自動對齊」的點應該落在段落的起點或終點上，但 %s 不是（最近：第 %d 段的%s %s，差 %.3f 秒）。\n手動改過的話把說明清掉即可；如果是別張卡算出來的舊點，請刪掉或重跑反推。',
  "A point marked auto-aligned (自動對齊) should sit on a segment start or end, but %s doesn't (nearest: segment %d %s %s, off by %.3f s).\nIf you edited it by hand, clear the note; if it's an old point from another card, delete it or rerun the derive step.",
  '「自動對齊」の点はシーンの開始か終了にあるはずですが、%s は違います（最寄り：シーン %d の%s %s、差 %.3f 秒）。\n手動で変えたなら説明を消せば OK。別のカードで計算した古い点なら削除するか逆算をやり直してください。')
A('[確認一下] 第 %d 列 timeline %s 標著「自動對齊」，卻不在任何段落的起訖上（最近：第 %d 段%s %s）—— 手動改過的話沒事，說明清掉即可',
  "[Check] row %d timeline %s is marked auto-aligned but isn't on any segment start/end (nearest: segment %d %s %s) — fine if edited by hand, just clear the note",
  '[確認] %d 行目 timeline %s は「自動對齊」ですが、どのシーンの開始/終了にもありません（最寄り：シーン %d %s %s）— 手動で変えたなら説明を消せば OK')
A('有 %d 列標著「自動對齊」但對不上段落起訖 —— 標紅了，滑鼠移上去看說明',
  "%d rows marked auto-aligned don't match a segment start/end — marked red, hover for details",
  '「自動對齊」なのにシーンの開始/終了と合わない行が %d 行 — 赤表示、マウスを乗せると説明')
A('還不能存',
  "Can't save yet",
  'まだ保存できません')
A('先指定場景卡，才知道要存在哪裡',
  'Set the scene card first so we know where to save',
  '保存先を決めるため、先にシーンカードを指定してください')
A('表格有問題',
  'Table has problems',
  '表に問題があります')
A('存不了',
  "Can't save",
  '保存できません')
A('寫出 %d 個對應點：%s',
  'Wrote %d sync points: %s',
  '対応点 %d 個を書き出し：%s')
A('（原本讀的是共用的 pairs.txt，已改存成這張卡專屬的名字）',
  "(it was read from a shared pairs.txt; saved under this card's own name)",
  '（共有の pairs.txt から読んだので、このカード専用の名前で保存しました）')
A('對應點已存檔',
  'Sync points saved',
  '対応点を保存しました')
A('還沒有對應曲線，先按「產生 cutscene.json」',
  'No mapping curve yet; press "Generate cutscene.json" first',
  '対応カーブがまだありません。先に「cutscene.json を生成」を押してください')
A('這個 timeline 秒數不在任何段落裡',
  "This timeline time isn't inside any segment",
  'この timeline 秒数はどのシーンにも入っていません')
A('timeline %s → 預測影片 %s',
  'timeline %s → predicted video %s',
  'timeline %s → 予測した動画 %s')
A('先指定合併好的場景卡',
  'Set the merged scene card first',
  '先に結合したシーンカードを指定してください')
A('對應點太少',
  'Too few sync points',
  '対応点が少なすぎます')
A('每一段至少要兩個點。只有一個點的話尺度無從驗證，實測出過 8 秒等級的偏差。',
  "Each segment needs at least two points. With one the scale can't be verified; errors of around 8 s have happened.",
  '各シーンに最低 2 点必要です。1 点では速度を検証できず、8 秒程度ずれた例があります。')
A('[注意] 播放器裡的不是 ★來源影片，長度改由 ffprobe 量',
  "[Warning] The player isn't showing the ★source video; length measured with ffprobe instead",
  '[注意] プレーヤーの動画が ★ソース動画ではないため、長さは ffprobe で測ります')
A('輸出資料夾建立不了',
  "Can't create the output folder",
  '出力フォルダを作成できません')
A('執行中…',
  'Running…',
  '実行中…')
A('產生中…',
  'Generating…',
  '生成中…')
A('<b>已寫出</b> ',
  '<b>Written</b> ',
  '<b>書き出し完了</b> ')
A("\u3000<span style='color:%s'>報告裡有提醒，往下看</span>",
  "\u3000<span style='color:%s'>the report has notes, see below</span>",
  "\u3000<span style='color:%s'>レポートに注意事項があります。下を見てください</span>")
A('完成：',
  'Done: ',
  '完了：')
A("<span style='color:%s'>失敗，看報告</span>",
  "<span style='color:%s'>Failed, see the report</span>",
  "<span style='color:%s'>失敗、レポートを見てください</span>")
A('失敗',
  'Failed',
  '失敗')
A('相對路徑（UserData\\audio\\…）',
  'Relative path (UserData\\audio\\…)',
  '相対パス（UserData\\audio\\…）')
A('絕對路徑（D:\\…）',
  'Absolute path (D:\\…)',
  '絶対パス（D:\\…）')
A('夾帶在卡片裡（卡片會變很大）',
  'Embed in card (card gets much bigger)',
  'カードに埋め込む（カードが大きくなる）')
A('換語言後要重新啟動才會生效',
  'Restart the tool after changing the language',
  '言語の変更は再起動後に反映されます')
A('找不到（影片／音訊功能不能用，把 ffmpeg 資料夾放在程式旁邊）',
  'not found (video/audio features won\'t work; put the ffmpeg folder next to the program)',
  '見つかりません（動画・音声機能は使えません。ffmpeg フォルダをプログラムの横に置いてください）')
A('語言',
  'Language',
  '言語')
A('已切換成 {0}。要現在重新啟動嗎？',
  'Switched to {0}. Restart now?',
  '{0} に切り替えました。今すぐ再起動しますか？')


# ---- 依長度自動設定對應點、合併場景時接 F7 設定
A('接 F7 設定（cutscene.json）…',
  'Joining F7 configs (cutscene.json)…',
  'F7 設定（cutscene.json）を結合中…')
A('；F7 設定已接好：{0}',
  '; F7 config joined: {0}',
  '；F7 設定を結合しました：{0}')
A('[提醒] 額外分頁 {0} 載入失敗：',
  '[Note] Extra tab {0} failed to load:',
  '[お知らせ] 追加タブ {0} を読み込めませんでした：')
A('[提醒] F7 設定（cutscene.json）沒有接成：{0}',
  '[Note] The F7 config (cutscene.json) could not be joined: {0}',
  '[注意] F7 設定（cutscene.json）を結合できませんでした：{0}')
A('；F7 設定沒有接成（看紀錄）',
  '; F7 config was not joined (see the log)',
  '；F7 設定は結合できませんでした（ログを確認）')
A('｜有 F7 設定',
  ' | has F7 config',
  '｜F7 設定あり')
A('這張卡已經有 F7 的設定檔，合併時可以一起接：\n{0}',
  'This card already has an F7 config; it can be joined when merging:\n{0}',
  'このカードには F7 の設定ファイルがあり、結合時に一緒につなげられます：\n{0}')
A('接 F7 設定：配音怎麼配',
  'Join F7 configs: how to pair the voices',
  'F7 設定の結合：ボイスの組み合わせ')
A('合併後的名稱',
  'Name after merging',
  '結合後の名前')
A('第 {0} 張　{1}',
  'Card {0}  {1}',
  '{0} 枚目　{1}')
A('加一列',
  'Add row',
  '行を追加')
A('第 {0} 張沒有 cutscene.json，那一段不會有配音和過場。',
  'Card {0} has no cutscene.json; that part will have no voice or cutscenes.',
  '{0} 枚目には cutscene.json がないため、その区間はボイスもムービーもありません。')
A('開始合併',
  'Start merging',
  '結合開始')
A('取消',
  'Cancel',
  'キャンセル')
A('F7 設定（cutscene.json）一起接',
  'Join F7 configs (cutscene.json) too',
  'F7 設定（cutscene.json）も一緒につなげる')
A('[提醒] 讀 cutscene.json 時出錯，這次不接：{0}',
  '[Note] Error while reading cutscene.json; not joining this time: {0}',
  '[注意] cutscene.json の読み込み中にエラー。今回は結合しません：{0}')
A('F7 設定：{0} 張卡裡有 {1} 張有 cutscene.json，合併完會一起接',
  'F7 config: {1} of {0} cards have a cutscene.json; they will be joined after merging',
  'F7 設定：{0} 枚中 {1} 枚に cutscene.json があり、結合後に一緒につなげます')
A('依長度自動設定',
  'Auto-set by length',
  '長さで自動設定')
A('清單裡有音檔（或影片）跟場景卡大致一樣長的時候用：\n這種音檔是照著場景從頭播到尾的，每一段的頭尾直接對上，不用自己量。\n把音檔拖進來時，對應點表格是空的就會自動做一次。',
  'Use this when an audio (or video) file in the list is about as long as the scene card:\nsuch a file plays alongside the scene from start to end, so the start and end of every segment line up directly and nothing has to be measured.\nIt runs once automatically when you drop files in while the sync point table is empty.',
  'リスト内の音声（または動画）がシーンカードとほぼ同じ長さのときに使います：\nこの種の音声はシーンに合わせて最初から最後まで流れるので、各区間の頭と末尾をそのまま合わせられ、自分で測る必要がありません。\n対応点の表が空のときに音声をドロップすると、自動で 1 回実行されます。')
A('先把音檔（或影片）加進清單',
  'Add audio (or video) files to the list first',
  '先に音声（または動画）をリストに追加してください')
A('對應點表格裡已經有東西了，長度對得上的話會整個換掉。要繼續嗎？',
  'The sync point table already has entries; if the lengths match it will be replaced entirely. Continue?',
  '対応点の表にはすでに内容があります。長さが合えば全部置き換えます。続けますか？')
A('比對音檔長度和場景卡…（大卡要等一下）',
  'Comparing audio length with the scene card… (large cards take a moment)',
  '音声の長さとシーンカードを照合中…（大きいカードは少し待ちます）')
A('依長度自動設定：讀不了這張卡（%s）',
  'Auto-set by length: cannot read this card (%s)',
  '長さで自動設定：このカードを読めません（%s）')
A('讀不了這張卡：\n%s',
  'Cannot read this card:\n%s',
  'このカードを読めません：\n%s')
A('（timeline %s，有時間流速軌道）',
  ' (timeline %s, has a time-scale track)',
  '（timeline %s、時間の速度トラックあり）')
A('沒有一個檔案的長度跟場景卡對得上（場景 %s，容許差 %.1f 秒）：%s',
  'No file matches the scene card in length (scene %s, tolerance %.1f s): %s',
  'シーンカードと長さが合うファイルがありません（シーン %s、許容差 %.1f 秒）：%s')
A('依長度自動設定：',
  'Auto-set by length: ',
  '長さで自動設定：')
A('最接近的是 %s（差 %+.2f 秒）。要不管長度，直接頭對頭、尾對尾套上去嗎？\n音檔裡有開場動畫或過場的話，這樣會整段對不上。',
  'The closest is %s (off by %+.2f s). Ignore the length and fit it start-to-start, end-to-end anyway?\nIf the audio contains an intro or cutscenes, the whole thing will be out of sync.',
  '一番近いのは %s（差 %+.2f 秒）です。長さを無視して、頭と頭・末尾と末尾で合わせますか？\n音声にオープニングやムービーが含まれていると、全体がずれます。')
A('音檔長度跟場景卡不一樣，對應點要自己量',
  'Audio length differs from the scene card; sync points must be measured manually',
  '音声の長さがシーンカードと違うため、対応点は自分で測ってください')
A('依長度自動設定：%s 長 %s，場景 %s（差 %+.2f 秒）→ 頭對頭、尾對尾，帶入 %d 個對應點（尚未存檔）',
  'Auto-set by length: %s is %s long, scene %s (off by %+.2f s) → start-to-start, end-to-end; %d sync points filled in (not saved yet)',
  '長さで自動設定：%s は %s、シーン %s（差 %+.2f 秒）→ 頭と頭・末尾と末尾で合わせ、対応点 %d 個を入力（未保存）')
A("<span style='color:#1a7f37'><b>音檔長度跟場景卡一致（%s）—— 對應點已經自動設好，確認配音有打勾就可以直接按「產生 cutscene.json」。</b></span>",
  "<span style='color:#1a7f37'><b>Audio length matches the scene card (%s) — sync points were set automatically. Make sure the voices are checked, then press \"Generate cutscene.json\".</b></span>",
  "<span style='color:#1a7f37'><b>音声の長さがシーンカードと一致（%s）— 対応点は自動で設定済みです。ボイスにチェックが入っているのを確認して「cutscene.json を生成」を押してください。</b></span>")
A('已自動帶入 %d 個對應點（音檔長度跟場景卡一致）',
  '%d sync points filled in automatically (audio length matches the scene card)',
  '対応点 %d 個を自動入力しました（音声の長さがシーンカードと一致）')
A('這份設定是接出來的',
  'This config was joined from several cards',
  'この設定は結合で作られたものです')
A('這張卡現有的 cutscene.json 是「合併場景」時從各張卡的設定接起來的。\n在這裡重新產生會蓋掉它，而且後面幾張卡的過場和音訊位置會算錯。\n\n要改的話，建議改原本各張卡的設定，再回「合併場景」重新接一次。\n\n還是要在這裡重新產生嗎？',
  'This card\'s current cutscene.json was joined from each card\'s config in "Merge scenes".\nRegenerating it here overwrites it, and the cutscenes and audio positions of the later cards will come out wrong.\n\nTo change something, edit the original cards\' configs and join again in "Merge scenes".\n\nRegenerate here anyway?',
  'このカードの cutscene.json は「シーン結合」で各カードの設定をつなげて作ったものです。\nここで再生成すると上書きされ、後ろのカードのムービーや音声位置がずれます。\n\n変更したい場合は、元の各カードの設定を直してから「シーン結合」でもう一度つなげてください。\n\nそれでもここで再生成しますか？')


A('每一列是合併後的一個配音版本（F7 面板上的一顆按鈕），每張卡挑一個版本。\n音檔和影片不會動：播到哪張卡，F7 就換成那張卡挑的音檔。版本比較少的卡可以重複用同一個。',
  'Each row is one voice version of the merged scene (one button on the F7 panel); pick one version per card.\nAudio and video files are left untouched: F7 switches to the file picked for whichever card is playing. A card with fewer versions can reuse the same one.',
  '各行が結合後のボイス 1 バージョン（F7 パネルのボタン 1 つ）で、カードごとに 1 つ選びます。\n音声と動画はそのまま：再生中のカードで選んだ音声に F7 が切り替えます。バージョンが少ないカードは同じものを繰り返し使えます。')

A('卡片各自已經做好 F7 的設定檔（狀態欄有「有 F7 設定」）時，合併完順便把設定也接成一份，\n不用重新量對應點。音檔和影片不會動：設定檔記下每張卡各用哪個檔，F7 播到哪張卡就換哪個檔\n（需要 F7 1.14.0 以上）。',
  'When the cards already have their own F7 configs (the Status column says "has F7 config"), the configs are joined into one after merging,\nso sync points don\'t have to be measured again. Audio and video files are left untouched: the config records which file each card uses, and F7 switches files as playback moves from card to card\n(needs F7 1.14.0 or later).',
  'カードごとに F7 の設定ファイルが出来ている場合（状態欄に「F7 設定あり」）、結合後に設定も 1 つにつなげます。\n対応点を測り直す必要はありません。音声と動画はそのまま：カードごとにどのファイルを使うかを設定に記録し、再生がカードをまたぐと F7 がファイルを切り替えます\n（F7 1.14.0 以降が必要）。')

# ---- 人物卡合卡分頁（原本的 kkbridge）
A('人物卡合卡',
  'Chara card merge',
  'キャラカード合成')
A('原本的 kkbridge：人物卡附加飾品、移植整套換裝、修卡。\nF6（StudioCharTools）的「添加飾品」「保持服裝換人」要這個程式開著、而且這一頁的「監看工單」在監看中。',
  'Formerly kkbridge: add accessories to a character card, transplant whole outfits, repair cards.\nF6 (StudioCharTools) "Add accessories" and "Swap keeping outfit" need this program open with "Watch jobs" on this tab running.',
  '旧 kkbridge：キャラカードへのアクセサリ追加、衣装まるごと移植、カード修復。\nF6（StudioCharTools）の「アクセサリ追加」「衣装を保ったまま入れ替え」は、このプログラムを開いてこのタブの「ジョブ監視」を動かしておく必要があります。')

# ---------------------------------------------------------------- 檢查
_PH = re.compile(r"\{(\d+)(![rsa])?(:[^}]*)?\}")
_PCT = re.compile(r"%[-+ #0]*\d*(?:\.\d+)?[sdfgrx%]")


def check():
    """每一條翻譯的格式參數要跟中文那條對得上。回傳 [(key, 語言, 說明)]。"""
    bad = []
    for zh, row in _TABLE.items():
        want = sorted(m.group(0) for m in _PH.finditer(zh))
        wpct = [m.group(0) for m in _PCT.finditer(zh)]
        for li in (EN, JA):
            s = row[li]
            if not s:
                continue
            got = sorted(m.group(0) for m in _PH.finditer(s))
            gpct = [m.group(0) for m in _PCT.finditer(s)]
            if got != want:
                bad.append((zh, NAMES[li], "{} 參數：中文 %s、譯文 %s" % (want, got)))
            if gpct != wpct:
                bad.append((zh, NAMES[li], "%% 參數：中文 %s、譯文 %s" % (wpct, gpct)))
    return bad


def coverage():
    n = len(_TABLE)
    return (n, sum(1 for r in _TABLE.values() if not r[EN]),
            sum(1 for r in _TABLE.values() if not r[JA]))


load_from_settings()

if __name__ == "__main__":
    total, no_en, no_ja = coverage()
    print("翻譯表 %d 條；英文缺 %d、日文缺 %d" % (total, no_en, no_ja))
    problems = check()
    for k, lang, why in problems:
        print("  [%s] %s\n      %s" % (lang, k.replace("\n", "\\n")[:70], why))
    print("格式參數檢查：%s" % ("全部通過" if not problems else "%d 個問題" % len(problems)))
