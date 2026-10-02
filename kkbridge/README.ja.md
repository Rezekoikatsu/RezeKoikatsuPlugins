# kkbridge / kkmerge

[繁體中文](README.md) ・ [English](README.en.md) ・ **日本語**

コイカツのキャラカードをオフラインで合成するツールです。`kkbridge.exe` が GUI、`kkmerge.exe` は同じエンジンの
コマンドライン版です。F6（Studio Character Tools）のカード合成機能はこれを使います。

## できること

| タブ | |
|---|---|
| **アクセサリ追加** | キャラカード＋コーデカード → 選んだコーデ枠にコーデカードのアクセを追加（複数枠を一度に） |
| **コーデ一式を移植** | 元カード＋先カード → コーデを丸ごと、指定した枠番号へ移す |
| **カード修復** | 空のアクセ枠を指す残りデータを削除（「別のコーデに切り替えて戻すまでマテリアルがおかしい」カードに有効） |
| **ジョブ監視** | F6 が置く `*.job.json` をフォルダで監視して処理 |

カード欄には PNG をドラッグするか「カード読込...」。コーデ表には全枠の名前・アクセ数・枠数が並び、
チェックした行は赤の太字になります。

## 言語

繁体字中国語 / 英語 / 日本語 — **設定** タブ最下部で切り替え、即座に反映されます。選択は `kkbridge_settings.json`
に保存されます。`kkmerge.exe` も同じファイルに従い、`--lang 0|1|2`（0＝中国語、1＝英語、2＝日本語）でも指定できます。

## F6 との連携

F6 の設定で `kkmerge.exe` を指定する（または `kkbridge.exe` を起動して監視を開始する）と、ジョブフォルダを設定します。
F6 はキャラを保存 → ジョブを置く → 結果を待つ → 読み戻す、という流れで動きます。kkbridge が起動していなければ
ジョブはフォルダに残り、次回起動時に処理されます。

## ジョブ形式（概略）

監視フォルダに UTF-8 の `*.job.json` を置きます：

```json
{"op": "append", "chara": "C:\\tmp\\chara.png", "coord": "C:\\...\\outfit.png", "outfit": 3, "out": "C:\\tmp\\merged.png"}
{"op": "transplant", "src": "C:\\tmp\\a.png", "src_outfit": 3, "dst": "C:\\tmp\\b.png", "dst_outfit": 0, "out": "C:\\tmp\\result.png"}
{"op": "clean", "chara": "C:\\tmp\\card.png", "out": "C:\\tmp\\cleaned.png"}
```

処理後、横に `*.done.json`（`{"ok": true, "out": ..., "warnings": [...]}` または `{"ok": false, "error": ...}`）ができます。
`warnings` には、ツールが認識できず移せなかったコーデ単位のプラグインデータが並ぶので確認してください。
全項目は中国語の README を参照してください。

## ビルド

`kkmerge.py`・`kkbridge.py`・`kklang.py`・`kkbridge.ico`・`build.bat` を同じフォルダに置き、`build.bat` をダブルクリック
（Python 3.11 以上）。出力：`dist\kkmerge.exe`（約 9 MB、コマンドライン、プラグインが呼ぶもの）と
`dist\kkbridge.exe`（約 40 MB、同じエンジン＋PyQt6 GUI）。
