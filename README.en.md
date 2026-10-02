# Koikatsu CharaStudio Plugins & Tools

[繁體中文](README.md) ・ **English** ・ [日本語](README.ja.md)

A set of BepInEx plugins for Koikatsu **CharaStudio**, plus a few companion Windows tools.
From swapping characters, changing outfits and saving cards, to joining several scene cards into one piece with
cutscene videos and voices — and then watching it in a headset — the whole pipeline is here. Author: **Reze**

<!-- demo video coming later -->

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
- **Card merging**: merge an outfit card's accessories into a character card, transfer whole outfits (with kkbridge)

### 🎬 F7 — Studio CutScene
Keeps cutscene videos, voices and maps in sync with **Timeline**.
- One `.cutscene.json` per scene card, applied automatically on load (next to the card or in `UserData\cutscene`)
- Opening / transition / ending videos; voice tracks aligned with sync points, so even time-scale tracks don't drift
- **Multiple voice versions**, switched live; versions with a different edit are converted automatically
- **Maps and screen effects** in merged cards switch per segment
- Desktop hotkeys, remembered VR viewpoints, controller transport (with F9)

### 🥽 F9 — Studio VR Tools
Everything for using CharaStudio in VR.
- Stick locomotion, turning, up/down, orbiting; left and right hand speeds set separately
- **Bind controls by pressing the combination right in the headset**, with conflict warnings
- Panels mounted on your hand with an opaque backing, readable in the headset
- F7 cutscenes on a screen in the headset; grip + trigger turns a hand into a playback remote
- Auto-hides FX objects that turn into a blur in VR; one button back to the camera view

### 🧩 kkscenebridge (tool)
**Joins several scene cards into one**, in order.
- Sorts out cameras, wraps folders, shifts Timeline, hands cameras over, de-duplicates textures, remaps plugin data
- Generates F7's `.cutscene.json`: built-in player for sync points, wav extraction, loudness matching, deriving points from pre-cut audio
- VNGE audio, and an object tree editor (drag, rename, delete with automatic reference fixes)
- Video/audio processing needs ffmpeg installed separately (without it only that part is missing — [which features, how to install](kkscenebridge/README.en.md#ffmpeg-install-it-yourself))

### 🔗 kkbridge / kkmerge (tool)
Offline character card merging: add accessories, transfer outfits, repair cards. F6's merge feature uses it.

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
| Optional | [ffmpeg](https://ffmpeg.org/) (install yourself, [steps](kkscenebridge/README.en.md#ffmpeg-install-it-yourself)) | wav extraction, loudness matching, silent video conversion and sync-point derivation in kkscenebridge's Cutscene audio tab |

> [!IMPORTANT]
> In desktop mode F9 stays completely silent — no errors without a VR plugin. A missing optional plugin only disables that feature.

---

## 🛠️ Installation

1. Download what you need from **[Releases](../../releases)**
2. Plugins: put the `.dll` files into the game's `BepInEx\plugins\`
3. Tools: put `kkscenebridge.exe`, `kkbridge.exe` and `kkmerge.exe` anywhere and double-click
4. In CharaStudio press **F6 / F7 / F9** to open the panels

Settings live in `BepInEx\config\reze.studio.*.cfg`. To reset everything, run `重置插件設定.bat` from the repo root
(it only moves the config files; nothing is deleted).

## 🌐 Language

Everything supports **Traditional Chinese / English / Japanese**:
- Plugins: the **Language** button at the bottom of each panel (all plugins switch together)
- kkscenebridge: bottom of the Settings tab (restart after changing)
- kkbridge: bottom of the Settings tab (applies immediately)

## 📖 Documentation

| | 中文 | English | 日本語 |
|---|---|---|---|
| F6 Studio Character Tools | [說明](StudioCharTools/README.md) | [Docs](StudioCharTools/README.en.md) | [説明](StudioCharTools/README.ja.md) |
| F7 Studio CutScene | [說明](StudioCutScene/README.md) | [Docs](StudioCutScene/README.en.md) | [説明](StudioCutScene/README.ja.md) |
| F9 Studio VR Tools | [說明](StudioVrTools/README.md) | [Docs](StudioVrTools/README.en.md) | [説明](StudioVrTools/README.ja.md) |
| kkscenebridge | [說明](kkscenebridge/README.md) | [Docs](kkscenebridge/README.en.md) | [説明](kkscenebridge/README.ja.md) |
| kkbridge / kkmerge | [說明](kkbridge/README.md) | [Docs](kkbridge/README.en.md) | [説明](kkbridge/README.ja.md) |

## 👨‍💻 Building

- **Plugins**: set `KoikatuDir` in `Directory.Build.props` to your game folder and build `StudioPlugins.sln` in Visual Studio
  (.NET Framework 3.5; the DLLs are copied into `BepInEx\plugins\`). Or from the command line:
  ```
  msbuild StudioPlugins.sln /p:Configuration=Release
  ```
- **Tools**: need Python 3.11+. Double-click `build.bat` in each folder. kkscenebridge can also run straight from source with `run_source.bat`.

## 🙏 Credits

- [Ermin610/KK_VR](https://github.com/Ermin610/KK_VR) and [YukyoMoe/KK_VR_CameraSync](https://github.com/YukyoMoe/KK_VR_CameraSync) — F9 builds on these
- [BepInEx](https://github.com/BepInEx/BepInEx), [IllusionMods](https://github.com/IllusionMods)' ModdingAPI and KK_Plugins, Joan6694's Timeline
- [kkloader](https://pypi.org/project/kkloader/) — scene card I/O in kkscenebridge
- [FFmpeg](https://ffmpeg.org/) — video/audio processing in kkscenebridge (installed separately, not distributed with this project)

## 📄 License

[MIT](LICENSE)
