# Studio VR Tools (F9)

[繁體中文](README.md) ・ [English](README.en.md) ・ [日本語](README.ja.md)

Everything CharaStudio needs in VR. Press **F9** for the panel.

> Requires a VRGIN-family VR plugin (e.g. KKCharaStudioVRPlugin).
> **In desktop mode this plugin is completely silent** — every VR feature disables itself
> and nothing errors.

## Requirements

Only the **mandatory** ones are listed. Common plugins nearly everyone already has (KKAPI, More Accessories, ABMX…) are not listed separately.

| Required | Why |
|---|---|
| [BepInEx 5](https://github.com/BepInEx/BepInEx) | The plugin framework. Bundled with every Koikatsu repack |
| **[Ermin610/KK_VR](https://github.com/Ermin610/KK_VR)** | VR itself for CharaStudio (VRGIN family). Without it every VR feature in F9 silently disables |
| **[YukyoMoe/KK_VR_CameraSync](https://github.com/YukyoMoe/KK_VR_CameraSync)** | Keeps the VR view following the studio camera (including Timeline playback). F9's "return to camera view" and cutscene alignment rely on it. Goes in `BepInEx\plugins\KK_VR_CameraSync\` |
| **SteamVR / OpenVR** | And CharaStudio launched with `--vr` or `--studiovr` |

## Controller

| Input | Action |
|---|---|
| Stick | Move |
| Grip + stick | Turn in place (pivot at your eyes) |
| Grip + X / Y | Down / up |
| Trigger + stick | Orbit; vertical and horizontal each get a selectable axis, pivot at eyes or origin |
| Trigger + X | Save the current viewpoint |
| Trigger + Y | Return to the saved viewpoint |
| Y (alone) | Main panel: in place → mounted to hand → hidden → repeat |
| Stick click | Return to camera view (undoes the movement you added) |
| **Grip + trigger** (held) | Transport mode, see below |

Left and right hand move speeds are set separately. Every binding is editable under **Controls**:
press "Set" on a row, then **press the combination right in the headset** (grip and trigger work as modifiers,
a full stick push counts too); it's saved when you let go. Two actions on the same combination get a warning.
The top-right button switches back to the old dropdown editor.

### Transport mode (needs F7)

Hold grip + trigger and that hand becomes a remote for Studio CutScene. Movement and
turning are suspended on that hand while held, so you don't scrub and rotate at once.

| Input | Action |
|---|---|
| Y | Pause / play (skips the current transition if one is playing) |
| Stick right / left | Seek forward / back 1 second, repeats while held |
| Stick up, held 1 s | Replay |
| Stick down, held 1 s | Stop |

Up and down deliberately need a full push held for a second — replay and stop discard your
current playback state, so a misfire costs more than a stray seek.
Seek step, repeat speed, hold time and stick threshold are adjustable under "Playback control" in **Controls**,
and the whole mode can be turned off.

## UI legibility

IMGUI is semi-transparent in a headset and nearly unreadable. This section fixes that:

- **Opaque window skin** — replaces the GUI skin; toggles get solid fills and white borders so on and off are clearly different
- **Opaque backing** — puts a solid plate behind the panel
- **Mount panel to hand** — sticks the panel to a controller; distance, scale, tilt, lift and flip are all adjustable

These settings apply to the F6 and F7 panels too — all three plugins share one appearance setting.

## Auto-hide objects in VR

Some effect objects look fine on a monitor but turn the headset into a red blur (Two Tone
Fog, Depth of Field and similar post effects are the usual suspects). This section
**unchecks them automatically when you enter VR** and checks them back when you leave:

- Matches by **name**, not ID — Sideloader renumbers mod items at runtime, so the IDs in a scene card don't match the running session. Names are stable
- Works on folders, including the numbered `(FX) 1`, `(FX) 2` folders that merged scenes produce
- The **`(FX)` folder** gets its own toggle, plus a setting for whether to hide it in VR
- "Add selected to list" adds whatever object or folder you currently have selected
- Turning the feature off re-checks what it unchecked and leaves everything else alone

## Cutscenes in the headset (needs F7)

When F7 plays a transition video, this puts a panel in the headset showing it. Distance,
width and whether it follows your head are adjustable. It can also draw only the video and
kill post-processing while playing — a red-looking video in VR is almost always post FX.

## Return to camera view

After moving around in VR, this returns you to the viewpoint bound to the studio camera. It
works by undoing exactly the displacement you added, so it holds even mid-camera-move. With
KK_VR_CameraSync installed it can also ask that plugin to realign (on by default).

## Settings

Bottom right of the panel has **Controls** and **Settings**; both windows have
**Language** and **Reset to defaults** at the bottom. You can also edit
`BepInEx\config\reze.studio.vrtools.cfg`.

The last line in **Controls** shows live which buttons you are pressing — press one and you
know which id to bind, without taking the headset off to read a log.

## Troubleshooting

**No controller response / no pointer** — check the last line in Controls. "Nothing
detected" means VRGIN hasn't handed the controllers over yet (normal for a few seconds
after a scene loads). If it never appears, something else may have taken the SteamVR
device slots.

**`VR Manager has not been created yet!` spam in desktop mode** — an older bug, caused by
reading VRGIN's lazy singleton with no VR running, which made VRGIN create the object.
Fixed.
