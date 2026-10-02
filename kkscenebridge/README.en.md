# kkscenebridge — Koikatsu scene card merger

[繁體中文](README.md) ・ **English** ・ [日本語](README.ja.md)

Joins several CharaStudio scene cards into one card, in order, and then handles the voices,
cutscene videos and object tree of the result. What used to take a whole evening by hand in Studio
(deleting cameras, wrapping folders, shifting Timeline, handing cameras over, wiring VNGE audio)
becomes: add cards, set the order, press Merge.

<!-- demo video coming later -->

## Tabs

| Tab | What it does |
|---|---|
| **Merge scenes** | Joins scene cards in order (with a single card it only prepares it) |
| **Cutscene audio** | Generates the `.cutscene.json` for F7 (Studio CutScene): cutscene videos, voices, sync points |
| **VNGE audio** | Adds voices per segment with VNGE's VNSound (no F7 needed) |
| **Organize** | Edits the card's object tree directly: drag, new folder, rename, hide, delete, transform |
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

The Length cell is editable: longer = hold the last frame, shorter = keyframes past the end are removed.
If a card's animation actually runs past its declared length, the cell turns red and tells you what to enter.

## Cutscene audio (generating F7 configs)

The steps are shown at the top of the tab: **① Merge scenes → ② Pick video / voices → ③ Measure sync points → ④ Generate config**.

- **Videos and voices**: drag in the original video and each voice version; checked audio becomes a
  voice version, and the video marked `★source` supplies the cutscene pictures
- **Sync points**: find the frame in the built-in player, "Capture this time" puts the video time in the
  table, and the timeline time is copied from the game's Timeline panel. At least two points per segment
- **Generate cutscene.json**: writes `UserData\cutscene\<card>.cutscene.json` (F7 also looks next to the scene card)

Helpers:

- Extract wav from videos; match the loudness of all voice versions (EBU R128)
- Convert the source video into a silent work video with dense keyframes (more accurate seeking)
- **Derive sync points from pre-cut audio** — old VNGE cards have their audio cut into pieces; comparing
  those with the original audio measures the sync points automatically
- **Voice mapping** — when one voice version has a different edit (missing intro, etc.), measure a
  conversion table so F7 converts on the fly when switching voices

The video/audio processing above uses **ffmpeg**, which is not included — install it yourself; see [ffmpeg](#ffmpeg-install-it-yourself) below.

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

## Settings

- **Game folder**: the other paths (cards, output, audio root) default to locations under it and follow it
- **Advanced**: the defaults are the usual choice; hover any option for an explanation
- **Language / 語言 / 言語**: at the bottom; restart after changing

Settings are saved in `kkscenebridge_settings.json` next to the exe.

## ffmpeg (install it yourself)

kkscenebridge **does not ship ffmpeg**. Without it, only the video/audio processing in the Cutscene audio tab
stops working — **Merge scenes, VNGE audio, Organize and Settings are unaffected**.

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

### Installing (pick one)

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

- Windows. The `kkscenebridge.exe` from Releases needs no Python install
- The video/audio processing in the Cutscene audio tab needs [ffmpeg](https://ffmpeg.org/) installed separately; see [ffmpeg](#ffmpeg-install-it-yourself) above
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
| `kkaudiotab.py` / `kkvnsound.py` | VNGE audio tab |
| `kktreetab.py` | Organize tab |
| `kkffmpeg.py` | Finds ffmpeg (the `ffmpeg\` folder next to the exe → PATH) and keeps it from flashing console windows |
