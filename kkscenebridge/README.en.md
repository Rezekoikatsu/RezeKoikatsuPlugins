# kkscenebridge — Koikatsu scene card merger

[繁體中文](README.md) ・ **English** ・ [日本語](README.ja.md)

> 🤖 **Created by Claude AI** — the code and documentation in this project were written by Claude (Anthropic's AI); Reze came up with the ideas, directed the work and tested everything in-game.

Joins several CharaStudio scene cards into one card, in order, and then handles the voices,
cutscene videos and object tree of the result. The formerly separate **kkbridge** (character-card
merging and the job watcher F6 relies on) now lives in this program too, so there is only one exe to run. What used to take a whole evening by hand in Studio
(deleting cameras, wrapping folders, shifting Timeline, handing cameras over, wiring VNGE audio)
becomes: add cards, set the order, press Merge.

> [!CAUTION]
> **Do not merge too many scenes at once.** The merged card gets very large and the game may run out of memory while loading it, freezing or crashing.

## Tabs

| Tab | What it does |
|---|---|
| **Merge scenes** | Joins scene cards in order (with a single card it only prepares it) |
| **Cutscene audio** | Generates the `.cutscene.json` for F7 (Studio CutScene): cutscene videos, voices, sync points |
| **VNGE audio** | Adds voices per segment with VNGE's VNSound (no F7 needed) |
| **Organize** | Edits the card's object tree directly: drag, new folder, rename, hide, delete, transform |
| **Chara card merge** | The former kkbridge: add accessories to a character card, transplant whole outfits, repair cards; the job watcher F6 (StudioCharTools) uses for merging |
| **Settings** | Paths, advanced options, language |

## Merge scenes

1. Drag scene cards into the list (or "Add cards…"); list order = playback order
2. Pick one camera to keep for each card
3. Type a common name in "Folder name" and press Enter; wrapper folders become `name_(1)`, `name_(2)`…
4. For VNGE audio press "Match all audio" or each row's "Pick audio…" (skip it otherwise)
5. Press "Merge"

The tool then does all of this:

- **Prepares each card**: keeps only the chosen camera (moving it with its parent chain into `(CAM)`),
  wraps everything else into `<name>_(n)`, and creates empty `(MAP)` `(FX)` `(CHAR)` `(SFX)` folders
- **Joins the cards**: shifts every later card's object IDs, moves its whole Timeline to follow on,
  and de-duplicates textures by content
- **Carries plugin data over and remaps IDs**: Timeline, NodesConstraints, KKPE, MaterialEditor,
  RendererEditor, OBJImport, TreeNodeNaming, LightSettings, ItemLayerEdit, SaveCameraObjectFOV,
  Sideloader (mod objects), VNGE audio
- **Takes turns**: segments that aren't playing are unchecked with object-enable tracks, and can be
  parked far away so they don't interfere
- **Camera takeover**: a new camera switches to the next segment's camera at each boundary; cards
  without a camera can get one locked to their initial view
- **Maps and screen effects**: when segments use different built-in maps or effects, each segment gets
  a `[MAPINFO]` / `[ENV]` marker folder. With F7 installed they switch automatically during playback;
  **unchecking a marker folder turns that segment's map off**
- Already-prepared cards (with `(CAM)` at the root) are used as-is
- (1.1.4) **Merged scene name**: type the file name of the merged card under "Options"; leave it empty for the automatic
  `<common name>_merge_<time>.png`. An existing file is only overwritten after you confirm, and a name equal to a source card
  is refused; the F7 config file uses the same name
- (1.1.4) **Cards saved by different studio versions can be merged together** (Koikatsu saves 1.0.4.2, Sunshine saves 1.1.2.1):
  an extra row appears under "Options" where you must choose which version the merged card is saved as.
  Newer: everything from the older cards is kept; open it with the game / plugins that can open the newer source card.
  Older: fields that only exist in the newer version are not saved (item animation pattern, sky settings, shader type); the log lists what was dropped.
  Either way, check in game whether the characters and items from the other version are recognised
- (1.1.3) KKPE colliders: a "Dynamic Bone Collider" item affects **every dynamic bone of any character that is not in its list**.
  After merging, characters of the other segments are not in the list, so their hair / breasts / skirt get pulled by a collider
  that belongs to another segment. The merge now adds "disabled" entries for the other segments' characters and items.
  Characters that appear in several segments with the same hair / outfit / accessories are fully covered; for characters with
  different hair or accessories only the common bones can be listed (the card does not contain the bone list), these are named
  in the log — use F6 "Repair collider bindings" after loading if something is still pulled.
- (1.1.2) Merging cards that are themselves merged results works again. Before 1.1.2 every segment except the first of each
  input card ended up with two camera-chain keyframes at the same time, and Timeline drops such a track entirely on load
  ("only the first scene of each card has the right camera"). Delete the output made with the old version and merge the inputs again.
- (1.1.1) Only the folders that actually move with the camera go into `(CAM)`. If the camera sits inside a scene-wide
  folder that also holds characters / map / effects (e.g. `General > Chara, Effects, CAM > c1 > … > camera`), that folder
  stays where it is; a stand-in folder with the same position / rotation is added on top of the camera chain so the view does not change.
- (1.1.1) Camera-chain keyframes are clipped to 0 … card duration. Keyframes the author left before 0 s or after the duration
  are removed, but a computed keyframe is added at the boundary, so the camera move inside the duration is identical to the original card.

### Joining F7 configs (cutscene.json)

When the cards already have their own configs from the Cutscene audio tab (the Status column adds
"has F7 config"), tick "Join F7 configs (cutscene.json) too" and the configs are joined into one after
merging — **no need to measure sync points again**.

- **Audio and video files are left untouched**: the joined config records which audio file and which
  source video each card uses, and F7 switches files as playback moves from card to card (the next
  audio file is read in the background shortly before the boundary). No ffmpeg, no extra files.
  Such a config needs **F7 (StudioCutScene) 1.14.0 or later**
- If two or more cards have voices, a voice pairing window appears after pressing "Merge": each row is
  one voice version of the merged scene (one button on the F7 panel); pick one version per card. A card
  with fewer versions can reuse the same one
- If the original cards' audio or video files are moved or renamed, the joined config can no longer find
  them (the voice button turns red on the F7 panel) — just join again
- Cards without a config can be mixed in; that part simply has no voice or cutscenes
- The junction between two cards is flagged in the config, and F7 draws that chapter marker on its seek bar in **red**
  (scenes inside one card are blue; needs F7 1.15.2+, older versions still play it and just draw everything blue)
- **Don't regenerate a joined config in the Cutscene audio tab** (it asks first): edit the original
  cards' configs and join again
- The cards' `.view.json` (view settings) are not joined

The Length cell is editable: longer = hold the last frame, shorter = keyframes past the end are removed.
If a card's animation actually runs past its declared length, the cell turns red and tells you what to enter.

## Cutscene audio (generating F7 configs)

The steps are shown at the top of the tab: **① Merge scenes → ② Pick video / voices → ③ Measure sync points → ④ Generate config**.

- **Videos and voices**: drag in the original video and each voice version; checked audio becomes a
  voice version, and the video marked `★source` supplies the cutscene pictures
- **Sync points**: find the frame in the built-in player, "Capture this time" puts the video time in the
  table, and the timeline time is copied from the game's Timeline panel. At least two points per segment
- **Auto-set by length**: when an audio (or video) file is about as long as the scene card (within 5%,
  with at least 1.5 s and at most 6 s of tolerance), it plays alongside the scene from start to end —
  drop the file in while the sync point table is empty and the start and end of every segment are lined
  up automatically. If the lengths don't match, the "Auto-set by length" button offers to force it
  (don't, if the audio contains an intro or cutscenes)
- **Generate cutscene.json**: writes `UserData\cutscene\<card>.cutscene.json` (F7 also looks next to the scene card)

Helpers:

- Extract wav from videos; match the loudness of all voice versions (EBU R128)
- Convert the source video into a silent work video with dense keyframes (more accurate seeking)
- **Derive sync points from pre-cut audio** — old VNGE cards have their audio cut into pieces; comparing
  those with the original audio measures the sync points automatically
- **Voice mapping** — when one voice version has a different edit (missing intro, etc.), measure a
  conversion table so F7 converts on the fly when switching voices

The video/audio processing above uses **ffmpeg** — `kkscenebridge_ffmpeg.zip` already includes it; otherwise see [ffmpeg](#ffmpeg) below.

## VNGE audio

Load a merged card and press "Auto-match": audio files are found in the audio folder by wrapper folder
name (e.g. `CharcardPT1` → `_(1)`). Each segment gets one group triggered by its `(SFX)`; with several files
in one segment, one is 100% and the rest 0%, so you can switch live in Studio.

The save mode applies to the whole card: relative path (default; files must be under the audio root),
absolute path, or embedded in the card.

## Organize

- Drag, new folder, rename, visibility, position/rotation/scale — none of it affects Timeline or NC
- **Delete**: items are only marked; on save they're deleted and every Timeline/NC reference is fixed
- Items under a character's attach point can only move within that attach point
- By default a `<card>.png.bak` backup is made before saving over the card

## Chara card merge (formerly kkbridge)

The formerly separate `kkbridge.exe` has been folded in as a whole; its features and job format are unchanged.
**F6 (StudioCharTools) "Add accessories", "Swap keeping outfit" and "bring back hair accessories" need
`kkscenebridge.exe` running with "Watch jobs" on this tab watching** (by default it starts with the program).

- **Add accessories**: character card + outfit card → tick the outfit slots to receive the accessories → Run. Cards can be dragged in
- **Transplant a whole outfit**: moves whole outfits from a source character card to a target card; the right-hand column picks the target slot. An outfit card's accessories can be added first
- **Repair card**: removes leftover extended data pointing at empty accessory slots (cards where one outfit's materials break until you switch outfits back and forth)
- **Watch jobs**: F6 drops `*.job.json` into this folder (default `UserData\chara\female\Temp`); a `*.done.json` appears next to it when finished
  - (1.1.5) To use F6's card merging in Koikatsu Sunshine as well: fill in "Koikatsu Sunshine root folder" on the Settings tab; that game's `UserData\chara\female\Temp` is then watched too, with nothing else to set up
- **Merge settings**: default folders for opening cards, output folder, whether Pushup and the skin overlay (KSOX) follow the outfit, automatic cleanup, auto-watch. The game folder and the language come from the Settings tab on the far right
- Settings live under `bridge` in `kkscenebridge_settings.json`. On first start, an old `kkbridge_settings.json` (next to the exe or in the game root) is imported if found
- Outfit cards without plugin data (saved from vanilla clothes) can now be read; they used to fail with a "read out of range" error
- The old `kkbridge.exe` is no longer needed; if both run and watch the same folder, each job is done twice

## Settings

- **Game folder**: the other paths (cards, output, audio root) default to locations under it and follow it
- **Advanced**: the defaults are the usual choice; hover any option for an explanation
- **Language / 語言 / 言語**: at the bottom; restart after changing

Settings are saved in `kkscenebridge_settings.json` next to the exe.

## ffmpeg

Releases has two downloads; the only difference is whether ffmpeg is included:

| Download | Contains | For |
|---|---|---|
| **`kkscenebridge_ffmpeg.zip`** | `kkscenebridge.exe` + `ffmpeg\` (`ffmpeg.exe`, `ffprobe.exe`) | Works right after extracting; you don't want to install ffmpeg yourself |
| **`kkscenebridge.zip`** | `kkscenebridge.exe` only | You already have ffmpeg, or don't need the video/audio processing (much smaller) |

Both extract to a single `kkscenebridge` folder that can live anywhere. For later versions just replace
`kkscenebridge.exe` and keep the `ffmpeg` folder (so the small `kkscenebridge.zip` is enough for updates).

The bundled ffmpeg is an unmodified third-party build. It is not part of kkscenebridge and not covered by its
MIT license; its license (GPL) and source links are in `ffmpeg\README_ffmpeg.txt`. To use another version,
just replace the `ffmpeg` folder.

Without ffmpeg, only the video/audio processing in the Cutscene audio tab stops working —
**Merge scenes, VNGE audio, Organize, Chara card merge and Settings are unaffected**.

### Features that need ffmpeg (all in the Cutscene audio tab)

| Feature | Buttons |
|---|---|
| Extract wav from videos | "Extract wav from checked videos", "Extract wav (pick files…)" |
| Match loudness across voice versions | "Match loudness…", the "Match loudness after extracting" checkbox |
| Convert to a silent work video | "Make ★source a silent work video", "Batch silent convert (pick files…)" |
| Derive sync points from pre-cut audio | "Start matching" in the "Derive from pre-cut audio…" window |
| Voice mapping auto-scan | "Auto-scan all needed versions" / "Scan only the selected one" in the "Voice mapping…" window |

### Checks that are skipped (features still work)

- The "same-edit check" (comparing total lengths) when adding files is skipped
- Measuring video/voice lengths when generating `cutscene.json`: wav headers are read directly; the video is only measured
  if it's open in the built-in player, otherwise the report lacks the "does the ending cover it" check

### Works without ffmpeg

The built-in player, capturing sync points by hand, reading/writing `pairs.txt`, and **generating `cutscene.json`**.
Prepare the voices as wav files yourself (converted from the video with any other software) and the whole workflow still works.

### Installing ffmpeg yourself (for `kkscenebridge.zip`; pick one)

**Option A: winget (built into Windows 10 / 11, easiest)**

1. Search the Start menu for "Terminal" or "Command Prompt" and open it
2. Type this line and press Enter:
   ```
   winget install --id Gyan.FFmpeg -e
   ```
3. When it's done, **close and reopen kkscenebridge** (programs only see the new PATH after a restart)

**Option B: download manually and put it next to kkscenebridge (no PATH changes)**

1. Download **`ffmpeg-release-essentials.zip`** from <https://www.gyan.dev/ffmpeg/builds/>
2. Unzip it and **rename** the extracted folder (e.g. `ffmpeg-8.0-essentials_build`) **to `ffmpeg`**
3. Put that folder next to `kkscenebridge.exe`, so you have `kkscenebridge\ffmpeg\bin\ffmpeg.exe`
4. Restart kkscenebridge

**Check**: the bottom of the Settings tab shows `ffmpeg：<path>` when it's found; red "not found" text means it isn't.

> Use a **GPL** build such as essentials / full. Builds labeled LGPL lack libx264, so "silent work video" conversion fails.

## Requirements

- Windows. The zips from Releases need no Python install
- The video/audio processing in the Cutscene audio tab needs [ffmpeg](https://ffmpeg.org/): included in `kkscenebridge_ffmpeg.zip`, or install it yourself; see [ffmpeg](#ffmpeg) above
- Cutscene videos and map switching in merged cards need **F7 (Studio CutScene)** in the game;
  VNGE audio needs VNGE

## Notes

- **Keep your original cards.** The tool never modifies them, but you'll want them if you need to redo a merge
- Large cards (over 1 GB) take minutes to read and write; the window not responding is normal
- `shaderType` is one value per card, so a merge keeps only one; the log shows a `[注意]` line when segments differ
- Plugin data present in only one card is copied over as a whole, but its IDs are not remapped (listed in the log)
- Technical log lines from the merge engine are in Chinese; the UI and main messages are translated

## Running from source / building

```
run_source.bat          run from source (installs missing packages)
build.bat               package into dist\kkscenebridge.exe with PyInstaller
```

Dependencies: `pip install kkloader==0.1.23 msgpack PyQt6 numpy` (plus `pyinstaller` to build)

Command line (no UI):

```
python kkscenemerge.py info  <card.png>
python kkscenemerge.py prep  <card.png> --name "Scene A" [--camera <dicKey>] --out <out.png>
python kkscenemerge.py merge <card1> <card2> [card3 ...] --out <merged.png>
python kkcutmerge.py --part <card1.png>=0 --part <card2.png>=<start sec> --card <merged.png>
python kkmerge.py --help        command-line version of the chara card merge (add accessories / transplant / repair)
```

## Files

| File | Contents |
|---|---|
| `kkscenebridge.py` | Main window, Merge and Settings tabs |
| `kksblang.py` | UI translation table (Chinese / English / Japanese) |
| `kkscenemerge.py` | Prepare and merge engine |
| `kkscene2.py` / `kkmsgpack.py` | Scene card I/O, byte-level msgpack handling |
| `kkref.py` | Which plugin fields point at objects, and with which ID scheme |
| `kkcheck.py` | Round-trip verification |
| `kktl_scan.py` | Timeline track checks |
| `kkcuttab.py` / `kkcutscene*.py` / `kkaudioalign.py` / `kkvariantmap.py` | Cutscene audio tab and config generation |
| `kkcutmerge.py` | Joins each card's `cutscene.json` into one when merging scenes |
| `kkbridgetab.py` / `kkmerge.py` / `kklang.py` | Chara card merge tab (the former kkbridge), its merge engine and its translation table |
| `kkaudiotab.py` / `kkvnsound.py` | VNGE audio tab |
| `kktreetab.py` | Organize tab |
| `kkffmpeg.py` | Finds ffmpeg (the `ffmpeg\` folder next to the exe → PATH) and keeps it from flashing console windows |
