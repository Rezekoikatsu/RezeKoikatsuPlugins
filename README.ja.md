# Koikatsu CharaStudio Plugins & Tools

[繁體中文](README.md) ・ [English](README.en.md) ・ **日本語**

> 🤖 **Created by Claude AI** —— 本プロジェクトのコードとドキュメントは Claude（Anthropic の AI）が書きました。Reze は発案・指示・ゲーム内での動作確認を担当しています。

コイカツ **CharaStudio** 用の BepInEx プラグイン一式と、付属の Windows ツールです。
キャラ入れ替え・着替え・カード保存から、複数のシーンカードをムービーとボイス付きの 1 本の作品にまとめ、
ヘッドセットで見るところまで、一連の流れをすべてカバーします。作者：**Reze**

> [!TIP]
> どれも単体で導入できます。一緒に入れると自動で連携します（ヘッドセット内で F7 のムービーを見る、コントローラーで F7 を操作する など）。

---

## 🚀 主な機能

### 👥 F6 — Studio Character Tools
シーン内のキャラを 1 行ずつ管理します。
- **キャラ入れ替え**：4 つの体型モード（通常／旧カードの体型／身長固定／新カードの体型）、服装そのままの入れ替えも可能
- **着替え・アクセ**：着替え時に髪などのアクセを持ち越し、アクセ枠を個別管理、全員に一括適用
- **アクセのグループ拡縮／移動**：複数のアクセにチェックし、XYZ スライダーでサイズと位置をその場で調整。前回の値も適用可能
- **同期する衣装**：削除・拡縮・移動を、他のコーデの同じ枠・同じアイテムにも同期
- **シーンのシェーダーを引き継ぐ**：入れ替え時にシーンの元キャラのシェーダー（MaterialEditor）を新キャラに適用。シェーダーのみでパラメータはそのまま（オフ／確認／自動）
- **ブレンドシェイプ固定**、**コライダー設定の修復**（入れ替え後に髪が吸着される問題）、**全員のポーズリセット**
- **サムネ撮影とカード保存**：キャラカード・コーデカード・ポーズ・全コーデを一括保存、構図も揃います
- **カード合成**：コーデカードのアクセをキャラカードに合成、コーデ一式の移植（kkbridge と併用）

### 🎬 F7 — Studio CutScene
ムービー・ボイス・マップを **Timeline** に同期させます。
- シーンカード 1 枚につき `.cutscene.json` 1 つ。カード読み込み時に自動適用（カードと同じフォルダか `UserData\cutscene`）
- オープニング／つなぎ／エンディング動画。ボイスは対応点で正確に合わせるので、時間の速度トラックがあってもずれません
- **複数のボイス**をその場で切り替え、編集が違うバージョンも自動換算
- 結合カードの**マップと画面効果**をシーンごとに自動切替
- デスクトップのショートカット、VR 視点の記憶、コントローラー操作（F9 が必要）

### 🥽 F9 — Studio VR Tools
CharaStudio を VR で使うための操作一式。**Meta Quest 3 でのみ試用。他のヘッドセット／コントローラーでの動作は保証できません。**
- スティックで移動・回転・上下・周回、左右の手の速度を個別設定
- **ヘッドセットで組み合わせを押すだけでキー割り当て**、重複は警告
- パネルを手元に固定、不透明な下地でヘッドセット内でも読みやすい
- F7 のムービーをヘッドセット内のスクリーンで再生、グリップ＋トリガーで再生リモコンに
- VR でぼやけるエフェクトを自動で非表示、ボタン 1 つでカメラ視点に戻る

### 🧩 kkscenebridge（ツール）
複数のシーンカードを順番に **1 枚へ結合**します。
- カメラ整理、フォルダ分け、Timeline のずらし、カメラ引き継ぎ、テクスチャの重複排除、プラグインデータの番号振り直しを自動で
- F7 の `.cutscene.json` を生成：内蔵プレーヤーで対応点、wav 抽出、音量合わせ、カット済み音声からの逆算
- VNGE 音声、オブジェクトツリー編集（ドラッグ、名前変更、参照を自動修正する削除）
- 動画・音声処理には ffmpeg を使います：`kkscenebridge_ffmpeg.zip` には同梱済み。`kkscenebridge.zip` には入っていないので各自インストール（なくてもその部分が使えないだけ。[対象機能とインストール方法](kkscenebridge/README.ja.md#ffmpeg)）

### 🔗 kkbridge（ツール）
キャラカードのオフライン合成：アクセ追加、コーデ移植、カード修復。F6 のカード合成はこれを使うので、`kkbridge.exe` は `StudioCharTools.zip` に同梱しています。

---

## 📋 必要なもの

| | 必要なもの | 使う側 |
|---|---|---|
| **必須** | [BepInEx 5](https://github.com/BepInEx/BepInEx)（主要プラグイン同梱の [HF Patch](https://github.com/ManlyMarco/KK-HF_Patch) がおすすめ） | 全プラグイン |
| | [KKAPI / ModdingAPI](https://github.com/IllusionMods/IllusionModdingAPI) | ツールバーのアイコン（なくてもホットキーで使えます） |
| F7 | **Timeline**（Joan6694、HF Patch 同梱） | F7 は Timeline 前提 |
| F9 | **[Ermin610/KK_VR](https://github.com/Ermin610/KK_VR)** | CharaStudio の VR 本体 |
| F9 | **[YukyoMoe/KK_VR_CameraSync](https://github.com/YukyoMoe/KK_VR_CameraSync)** | VR 視点をスタジオのカメラに追従、「カメラ視点に戻る」 |
| F9 | SteamVR / OpenVR | CharaStudio を `--vr` か `--studiovr` で起動 |
| 任意 | [MaterialEditor（KK_Plugins）](https://github.com/IllusionMods/KK_Plugins) | F6 の入れ替え時のシェーダー引き継ぎ |
| 任意 | [KKABMX](https://github.com/ManlyMarco/KKABMX)、KKPE | F6 の「新カードの体型」、コライダー修復 |
| 任意 | VNGE | kkscenebridge の VNGE 音声 |
| 任意 | [ffmpeg](https://ffmpeg.org/)（`kkscenebridge_ffmpeg.zip` に同梱。同梱なし版は各自インストール、[手順](kkscenebridge/README.ja.md#ffmpeg)） | kkscenebridge「ムービー音声」タブの wav 抽出、音量合わせ、無音動画変換、対応点の逆算 |

> [!IMPORTANT]
> デスクトップモードでは F9 は完全に静かで、VR プラグインがなくてもエラーは出ません。任意のプラグインがなければその機能が使えないだけです。

---

## 🛠️ インストール

1. **[Releases](../../releases)** から必要な zip をダウンロード（どれも単体で導入できます）
2. **プラグインの zip はゲームのルートフォルダ**（`BepInEx` フォルダがある階層）**で解凍**。DLL は自動的に `BepInEx\plugins\` に入ります
3. `kkscenebridge` の zip は好きな場所に解凍し、中の `kkscenebridge.exe` を実行
4. CharaStudio で **F6 / F7 / F9** を押してパネルを開く

| ダウンロード | 内容 | 解凍先 |
|---|---|---|
| `StudioCharTools.zip` | F6 プラグイン ＋ `kkbridge.exe`（カード合成ツール。ゲームのルートに置かれます） | ゲームのルート |
| `StudioCutScene.zip` | F7 プラグイン | ゲームのルート |
| `StudioVrTools.zip` | F9 プラグイン | ゲームのルート |
| `kkscenebridge_ffmpeg.zip` | kkscenebridge ＋ ffmpeg。解凍すればすぐ使えます | どこでも |
| `kkscenebridge.zip` | kkscenebridge のみ（小さい。ffmpeg を既に持っている場合や、後で exe だけ更新する場合に） | どこでも |

kkscenebridge の zip はどちらか一方で構いません。

設定は `BepInEx\config\reze.studio.*.cfg` です。すべて初期値に戻すには、リポジトリ直下の `重置插件設定.bat` を実行してください
（設定ファイルを移動するだけで、何も削除しません）。

## 🌐 言語

すべて **繁体字中国語 / 英語 / 日本語** に対応：
- プラグイン：各パネル最下部の **Language** ボタン（全プラグインが同時に切り替わります）
- kkscenebridge：「設定」タブ最下部（変更後に再起動）
- kkbridge：「設定」タブ最下部（即座に反映）

## 📖 詳しい説明

| | 中文 | English | 日本語 |
|---|---|---|---|
| F6 Studio Character Tools | [說明](StudioCharTools/README.md) | [Docs](StudioCharTools/README.en.md) | [説明](StudioCharTools/README.ja.md) |
| F7 Studio CutScene | [說明](StudioCutScene/README.md) | [Docs](StudioCutScene/README.en.md) | [説明](StudioCutScene/README.ja.md) |
| F9 Studio VR Tools | [說明](StudioVrTools/README.md) | [Docs](StudioVrTools/README.en.md) | [説明](StudioVrTools/README.ja.md) |
| kkscenebridge | [說明](kkscenebridge/README.md) | [Docs](kkscenebridge/README.en.md) | [説明](kkscenebridge/README.ja.md) |
| kkbridge | [說明](kkbridge/README.md) | [Docs](kkbridge/README.en.md) | [説明](kkbridge/README.ja.md) |

## 👨‍💻 ビルド

- **プラグイン**：`Directory.Build.props` の `KoikatuDir` をゲームフォルダに変更し、Visual Studio で `StudioPlugins.sln` をビルド
  （.NET Framework 3.5。DLL は `BepInEx\plugins\` へ自動コピー）。コマンドラインなら：
  ```
  msbuild StudioPlugins.sln /p:Configuration=Release
  ```
- **ツール**：Python 3.11 以上が必要です。各フォルダの `build.bat` をダブルクリック。kkscenebridge は `run_source.bat` でソースから直接実行もできます。

## 🙏 クレジット

- [Ermin610/KK_VR](https://github.com/Ermin610/KK_VR)、[YukyoMoe/KK_VR_CameraSync](https://github.com/YukyoMoe/KK_VR_CameraSync) —— F9 はこの 2 つの上に成り立っています
- [BepInEx](https://github.com/BepInEx/BepInEx)、[IllusionMods](https://github.com/IllusionMods) の ModdingAPI と KK_Plugins、Joan6694 の Timeline
- [kkloader](https://pypi.org/project/kkloader/) —— kkscenebridge のシーンカード読み書き
- [FFmpeg](https://ffmpeg.org/) —— kkscenebridge の動画・音声処理。`kkscenebridge_ffmpeg.zip` に同梱しているのは無改変のサードパーティ製ビルド（GPL、本プロジェクトの MIT ライセンスの対象外）で、ライセンスとソースへのリンクは zip 内の `ffmpeg\README_ffmpeg.txt` にあります

## 📄 ライセンス

[MIT](LICENSE)
