# Koikatsu CharaStudio Plugins & Tools

[繁體中文](README.md) ・ **English** ・ [日本語](README.ja.md)

> 🤖 **Created by Claude AI** — the code and documentation in this project were written by Claude (Anthropic's AI); Reze came up with the ideas, directed the work and tested everything in-game.

A set of BepInEx plugins for Koikatsu **CharaStudio**, plus a few companion Windows tools.
From swapping characters, changing outfits and saving cards, to joining several scene cards into one piece with
cutscene videos and voices — and then watching it in a headset — the whole pipeline is here. Author: **Reze**

> [!NOTE]
> **Koikatsu Sunshine is supported too**: F6 and F7 have Sunshine builds (the downloads with `_KKS` in the name), and kkscenebridge handles cards from both games. F9 (VR) is Koikatsu-only for now.

> [!TIP]
> Each one installs on its own. Installed together they connect automatically (watch F7's cutscenes in the headset,
> drive F7 playback from the controller, …).

---

## 🚀 Key Features

### 👥 F6 — Studio Character Tools
A manager for the characters in your scene, one row each.
- **Swap characters** with four body modes (normal / keep old body / lock height / keep new body), or swap while keeping the outfit
- **Outfits & accessories**: bring hair and other accessories along when changing outfits; manage slots one by one or apply to everyone
- **Group scale / move for accessories**: tick several accessories and resize or move them together with live XYZ sliders; reuse the last values
- **Synced outfits**: removing, scaling and moving can be mirrored to the same slot and same item in other outfits
- **Carry the scene's shaders over**: on swap, apply the original character's shaders (MaterialEditor) to the new one — shaders only, parameters untouched (Off / Ask / Auto)
- **Blendshape lock**, **collider binding repair** (hair getting grabbed after a swap), **reset all poses**
- **Thumbnails and card saving**: character cards, outfit cards, poses, every outfit at once — with consistent framing
- **Card merging**: merge an outfit card's accessories into a character card, transfer whole outfits (with kkscenebridge's "Chara card merge" tab)

### 🎬 F7 — Studio CutScene
Keeps cutscene videos, voices and maps in sync with **Timeline**.
- One `.cutscene.json` per scene card, applied automatically on load (next to the card or in `UserData\cutscene`)
- Opening / transition / ending videos; voice tracks aligned with sync points, so even time-scale tracks don't drift
- **Multiple voice versions**, switched live; versions with a different edit are converted automatically
- **Maps and screen effects** in merged cards switch per segment
- Desktop hotkeys, remembered VR viewpoints, controller transport (with F9)

### 🥽 F9 — Studio VR Tools
Everything for using CharaStudio in VR. **Only tried on a Meta Quest 3; other headsets/controllers are not guaranteed to work.**
- Stick locomotion, turning, up/down, orbiting; left and right hand speeds set separately
- **Bind controls by pressing the combination right in the headset**, with conflict warnings
- Panels mounted on your hand with an opaque backing, readable in the headset
- F7 cutscenes on a screen in the headset; grip + trigger turns a hand into a playback remote
- Auto-hides FX objects that turn into a blur in VR; one button back to the camera view

### 🧩 kkscenebridge (tool)
**Joins several scene cards into one**, in order.
- ⚠️ **Do not merge too many scenes at once** — the game may run out of memory while loading the result
- Sorts out cameras, wraps folders, shifts Timeline, hands cameras over, de-duplicates textures, remaps plugin data
- Generates F7's `.cutscene.json`: built-in player for sync points, wav extraction, loudness matching, deriving points from pre-cut audio
- F7 configs that the individual cards already have can be **joined into one** — audio and video files are not merged (needs F7 1.14.0 or later)
- **Chara card merge** (the former kkbridge, now built in): add accessories, transfer outfits, repair cards; F6's card merging relies on this tab's "Watch jobs"
- VNGE audio, and an object tree editor (drag, rename, delete with automatic reference fixes)
- Video/audio processing uses ffmpeg: `kkscenebridge_ffmpeg.zip` already includes it; `kkscenebridge.zip` does not, so install it yourself (without it only that part is missing — [which features, how to install](kkscenebridge/README.en.md#ffmpeg))

### 🔗 kkbridge (merged into kkscenebridge)
Offline character card merging: add accessories, transfer outfits, repair cards. **Since v1.0.1 it is the "Chara card merge" tab of `kkscenebridge.exe`**, so there is only one exe to run;
`StudioCharTools.zip` no longer contains `kkbridge.exe`, and F6's card merging needs `kkscenebridge.exe` running instead. The source of the standalone version is still in [`kkbridge/`](kkbridge/).

If you would rather not use kkscenebridge, the standalone `kkbridge.exe` from [v1.0.0](https://github.com/Rezekoikatsu/RezeKoikatsuPlugins/releases/tag/v1.0.0) (inside its `StudioCharTools.zip`) still works with the current F6 — just don't run both at the same time, or every job is done twice.

---

## 📋 Requirements

| | Needs | Used by |
|---|---|---|
| **Required** | [BepInEx 5](https://github.com/BepInEx/BepInEx) (easiest via [HF Patch](https://github.com/ManlyMarco/KK-HF_Patch), which includes the common plugins) | all plugins |
| | [KKAPI / ModdingAPI](https://github.com/IllusionMods/IllusionModdingAPI) | toolbar icons (hotkeys work without it) |
| F7 | **Timeline** (Joan6694, included in HF Patch) | F7 is built around it |
| F9 | **[Ermin610/KK_VR](https://github.com/Ermin610/KK_VR)** | the CharaStudio VR plugin itself |
| F9 | **[YukyoMoe/KK_VR_CameraSync](https://github.com/YukyoMoe/KK_VR_CameraSync)** | VR view follows the studio camera; "back to camera view" |
| F9 | SteamVR / OpenVR | start CharaStudio with `--vr` or `--studiovr` |
| Optional | [MaterialEditor (KK_Plugins)](https://github.com/IllusionMods/KK_Plugins) | F6's scene shader carry-over on swap |
| Optional | [KKABMX](https://github.com/ManlyMarco/KKABMX), KKPE | F6's "keep new body", collider repair |
| Optional | VNGE | kkscenebridge's VNGE audio |
| Optional | [ffmpeg](https://ffmpeg.org/) (included in `kkscenebridge_ffmpeg.zip`; otherwise install it yourself, [steps](kkscenebridge/README.en.md#ffmpeg)) | wav extraction, loudness matching, silent video conversion and sync-point derivation in kkscenebridge's Cutscene audio tab |

> [!IMPORTANT]
> In desktop mode F9 stays completely silent — no errors without a VR plugin. A missing optional plugin only disables that feature.

---

## 🛠️ Installation

1. Download the zips you need from **[Releases](../../releases)** (each one installs on its own)
2. **Extract the plugin zips in the game's root folder** (the one that contains `BepInEx`); the DLLs land in `BepInEx\plugins\` by themselves
3. Extract a `kkscenebridge` zip anywhere and run the `kkscenebridge.exe` inside
4. In CharaStudio press **F6 / F7 / F9** to open the panels

| Download | Contains | Extract to |
|---|---|---|
| `StudioCharTools.zip` | F6 plugin (card merging needs `kkscenebridge.exe` running) | game root |
| `StudioCutScene.zip` | F7 plugin | game root |
| `StudioVrTools.zip` | F9 plugin | game root |
| `StudioCharTools_KKS.zip` | F6 plugin, **Koikatsu Sunshine build** | Sunshine's game root |
| `StudioCutScene_KKS.zip` | F7 plugin, **Koikatsu Sunshine build** | Sunshine's game root |
| `kkscenebridge_ffmpeg.zip` | kkscenebridge + ffmpeg, works right after extracting | anywhere |
| `kkscenebridge.zip` | kkscenebridge only (small; for when you already have ffmpeg, or just want to update the exe later) | anywhere |

Pick one of the two kkscenebridge zips.

The DLLs of the two games are **not interchangeable**: Koikatsu uses the ones without `_KKS`, Koikatsu Sunshine the ones with `_KKS`. The plugins they rely on are the Sunshine counterparts (KKSAPI, Timeline, KKSPE, KKSABMX, KKS_MaterialEditor…; all bundled in HF Patch for KKS).
To use F6's card merging in Sunshine, fill in "Koikatsu Sunshine root folder" on kkscenebridge's Settings tab.

Settings live in `BepInEx\config\reze.studio.*.cfg`. To reset everything, run `重置插件設定.bat` from the repo root
(it only moves the config files; nothing is deleted).

## 🌐 Language

Everything supports **Traditional Chinese / English / Japanese**:
- Plugins: the **Language** button at the bottom of each panel (all plugins switch together)
- kkscenebridge: bottom of the Settings tab (restart after changing)

## 📖 Documentation

| | 中文 | English | 日本語 |
|---|---|---|---|
| F6 Studio Character Tools | [說明](StudioCharTools/README.md) | [Docs](StudioCharTools/README.en.md) | [説明](StudioCharTools/README.ja.md) |
| F7 Studio CutScene | [說明](StudioCutScene/README.md) | [Docs](StudioCutScene/README.en.md) | [説明](StudioCutScene/README.ja.md) |
| F9 Studio VR Tools | [說明](StudioVrTools/README.md) | [Docs](StudioVrTools/README.en.md) | [説明](StudioVrTools/README.ja.md) |
| kkscenebridge | [說明](kkscenebridge/README.md) | [Docs](kkscenebridge/README.en.md) | [説明](kkscenebridge/README.ja.md) |
| kkbridge (old standalone version) | [說明](kkbridge/README.md) | [Docs](kkbridge/README.en.md) | [説明](kkbridge/README.ja.md) |

## 👨‍💻 Building

- **Plugins**: set `KoikatuDir` in `Directory.Build.props` to your game folder and build `StudioPlugins.sln` in Visual Studio
  (.NET Framework 3.5; the DLLs are copied into `BepInEx\plugins\`). Or from the command line:
  ```
  msbuild StudioPlugins.sln /p:Configuration=Release
  ```
- **Koikatsu Sunshine builds**: the `StudioCharTools.KKS` and `StudioCutScene.KKS` projects in the solution compile the same
  sources against Sunshine's assemblies (.NET Framework 4.7.2). Set `KoikatsuSunshineDir` in `Directory.Build.props` to your
  Sunshine folder; if you don't have Sunshine, just unload those two projects in Visual Studio.
- **Tools**: need Python 3.11+. Double-click `build.bat` in each folder. kkscenebridge can also run straight from source with `run_source.bat`.

## 🙏 Credits

- [Ermin610/KK_VR](https://github.com/Ermin610/KK_VR) and [YukyoMoe/KK_VR_CameraSync](https://github.com/YukyoMoe/KK_VR_CameraSync) — F9 builds on these
- [BepInEx](https://github.com/BepInEx/BepInEx), [IllusionMods](https://github.com/IllusionMods)' ModdingAPI and KK_Plugins, Joan6694's Timeline
- [kkloader](https://pypi.org/project/kkloader/) — scene card I/O in kkscenebridge
- [FFmpeg](https://ffmpeg.org/) — video/audio processing in kkscenebridge. `kkscenebridge_ffmpeg.zip` bundles an unmodified third-party build (GPL, not covered by this project's MIT license); its license and source links are in `ffmpeg\README_ffmpeg.txt` inside the zip

## 📄 License

[MIT](LICENSE)
