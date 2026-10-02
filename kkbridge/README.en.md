# kkbridge / kkmerge

[繁體中文](README.md) ・ **English** ・ [日本語](README.ja.md)

Offline character card merging for Koikatsu. `kkbridge.exe` is the GUI; `kkmerge.exe` is the same engine
as a command-line tool. F6 (Studio Character Tools) uses it for its card-merging features.

**Download**: `kkbridge.exe` ships inside `StudioCharTools.zip` on the Releases page (it ends up in the game root).
F6 works through the job folder and only needs `kkbridge.exe`; `kkmerge.exe` is not in Releases — build it yourself
(see Building) if you want the command-line version.

## What it does

| Tab | |
|---|---|
| **Add accessories** | Character card + outfit card → add the outfit card's accessories to the chosen outfit slots (several slots in one go) |
| **Transplant a whole outfit** | Source card + target card → move whole outfits, each to the slot number you choose |
| **Repair card** | Removes leftover extended data pointing at empty accessory slots (fixes "materials look wrong until I switch outfits and back") |
| **Watch jobs** | Watches a folder for `*.job.json` jobs dropped by F6 and processes them |

Drag a PNG onto any card field or use "Load card...". The outfit table lists every slot with its name,
accessory count and slot count; checked rows turn red and bold.

## Language

Traditional Chinese / English / Japanese — the selector is at the bottom of the **Settings** tab and
applies immediately. The choice is saved in `kkbridge_settings.json`. `kkmerge.exe` follows the same file,
or takes `--lang 0|1|2` (0 = Chinese, 1 = English, 2 = Japanese).

## Using it with F6

Run `kkbridge.exe` and start watching in the **Watch jobs** tab. The job folder defaults to
`UserData\chara\female\Temp` on both sides; if you change it, set the same folder in F6's settings. F6 saves the character, drops a job, waits for the result and loads it back. If kkbridge isn't
running, jobs stay in the folder and are processed the next time it starts.

## Job format (short)

UTF-8 `*.job.json` in the watched folder, e.g.

```json
{"op": "append", "chara": "C:\\tmp\\chara.png", "coord": "C:\\...\\outfit.png", "outfit": 3, "out": "C:\\tmp\\merged.png"}
{"op": "transplant", "src": "C:\\tmp\\a.png", "src_outfit": 3, "dst": "C:\\tmp\\b.png", "dst_outfit": 0, "out": "C:\\tmp\\result.png"}
{"op": "clean", "chara": "C:\\tmp\\card.png", "out": "C:\\tmp\\cleaned.png"}
```

A `*.done.json` appears next to it with `{"ok": true, "out": ..., "warnings": [...]}` or
`{"ok": false, "error": ...}`. Check `warnings`: it lists outfit-level plugin data the tool didn't recognize
(and therefore didn't move). See the Chinese README for all fields.

## Building

Put `kkmerge.py`, `kkbridge.py`, `kklang.py`, `kkbridge.ico` and `build.bat` in one folder and double-click
`build.bat` (Python 3.11+). Output: `dist\kkmerge.exe` (~9 MB, command line, for your own scripts) and
`dist\kkbridge.exe` (~40 MB, same engine + PyQt6 GUI, what F6 uses).
