# Studio CutScene (F7)

[繁體中文](README.md) ・ **English** ・ [日本語](README.ja.md)

> 🤖 **Created by Claude AI** — the code and documentation in this project were written by Claude (Anthropic's AI); Reze came up with the ideas, directed the work and tested everything in-game.

Plays transition videos and voice tracks in sync with **Timeline**. Press **F7** for the panel.

Each scene card gets a `.cutscene.json` describing which video plays at which point on the
timeline, and which audio file covers which stretch. Loading a scene card auto-loads the
matching config.

> **Requires the Timeline plugin** (Joan6694's). This plugin is built entirely around Timeline
> and does almost nothing without it.

<!-- demo video coming later -->

## Requirements

Only the **mandatory** ones are listed. Common plugins nearly everyone already has (KKAPI, More Accessories, ABMX…) are not listed separately.

| Required | Why |
|---|---|
| [BepInEx 5](https://github.com/BepInEx/BepInEx) | The plugin framework. Bundled with every Koikatsu repack |
| **Timeline** by Joan6694 | This plugin is built entirely around Timeline and does almost nothing without it. Bundled in HF Patch, or see [Joan6694 on Patreon](https://www.patreon.com/joan6694) |

## The config file

**The filename must match the scene card**: `MyScene.png` → `MyScene.cutscene.json`. It is looked up in two places:

1. **Next to the scene card** (same folder)
2. `UserData\cutscene\` (folder list is configurable, semicolon-separated, searched recursively)

If the card is identified but has no matching config, the panel stays blank and tells you what to name
the file. Only when the card can't be identified at all does it fall back to matching the timeline length.
"Search again now" on the panel searches again.

Configs can be generated with the "Cutscene audio" tab of **[kkscenebridge](../kkscenebridge/README.en.md)**,
or written by hand in the format below.

### Roughly

```jsonc
{
  "videoFile": "UserData/cutscene/source.mp4",   // shared source video
  "tracks": [                                     // audio tracks = scene segments
    { "from": 0.0,   "to": 235.0,  "audio": "@", "offset": 0 },
    { "from": 235.0, "to": 509.01, "audio": "@", "offset": 0 }
  ],
  "cuts": [                                       // transitions
    { "t": 0.0,    "kind": "opening",    "videoStart": 0,  "videoEnd": 8 },
    { "t": 235.01, "kind": "transition", "videoStart": 8,  "videoEnd": 14 }
  ]
}
```

- Each `tracks` entry becomes a "Scene N start" button on the panel
- `cuts` `kind` is `opening` / `transition` / `ending`
- `audio` of `"@"` uses the currently selected voice variant, `"@name"` picks one, anything else is a path
- Tracks can carry `anchorT` / `anchorA` parallel arrays as correspondence points. You **must** use these when the scene has a time-scale track, or drift accumulates
- `//` and `/* */` comments and trailing commas are accepted, because these files get hand-edited

`example.cutscene.json` is a template you can edit directly.

**Voice versions**: `variants` lists the voice versions (e.g. Japanese and Chinese dubs), switchable live on
the panel. When a version has a different edit (missing intro, say), `variantMaps` stores "main voice time →
this version's time" and switching converts automatically. Versions as long as the main voice need no map.

kkscenebridge also writes a `pairs` array into the json holding each segment's head and tail
correspondence points. If a segment's `anchors` array ends up empty (which happens when the
generator can't solve the curve), the plugin fills the head and tail back in from `pairs`,
interpolates a straight line, and warns in orange on the panel. Without that fallback the
segment plays from 0 s of the audio file — the segments either side sound fine and only the
middle one is off, which is very hard to diagnose.

## The panel

- Play / Pause / Stop / Play from start (including the opening), plus a draggable seek bar
- **Segment list** — each transition can be test-played on its own, skipped, or reset. Each "Scene N start" plays from that segment and does **not** replay that segment's own transition
- **Skip all cutscenes** — for this session only; not written to the config
- **Auto replay** — whether to play again when the timeline wraps back to 0 (default off, otherwise the whole thing loops forever)
- **Hotkeys** — desktop hotkeys for play, pause, stop, replay, previous/next scene, forward, rewind and skip
- **Quick test** — paste a video and an audio path and watch, without touching the config
- **VR viewpoints** — see below
- **Advanced section** — stall compensation, color space, diagnostics. Hidden by default; enable "Show Advanced Section" in the cfg

## Map and screen-effect switching

In cards merged with kkscenebridge, segments that use different built-in maps or screen effects each get a
`[MAPINFO]` / `[ENV]` marker folder. F7 switches the map (mod maps included) and effects to match the
segment being played:

- **Unchecking a marker folder (or a parent) turns that segment's map off**; moving the marker folder moves the map
- If you replace or delete the map by hand in Studio, that segment won't switch back (it applies again on the next segment or when the card reloads)
- Without F7 the card still plays; every segment just shows the same map

## VR viewpoints (needs F9)

With Studio VR Tools installed you can save a headset viewpoint per scene segment:

- In VR, trigger + X saves the viewpoint and also writes it to `<card name>.view.json` next to the card
- Crossing into the next segment during playback moves the view there automatically
- A card with no audio tracks counts as one segment and stores one viewpoint

The panel also has save / delete / apply buttons so you can do this without a headset.

## Controller transport (needs F9)

In VR, hold **grip + trigger** and that hand becomes this plugin's remote:

| Input | Action |
|---|---|
| Y | Pause / play (skips the current transition if one is playing) |
| Stick right / left | Seek forward / back 1 second, repeats while held |
| Stick up, held 1 s | Replay |
| Stick down, held 1 s | Stop |

## Troubleshooting

**Transport buttons do nothing** — check the log line `[CutScene] Timeline 掃描：` and confirm
the chosen type is `Timeline.Timeline`. With several plugins whose names contain "Timeline"
loaded, the wrong one can be picked; the plugin scores candidates and re-scans automatically
when a seek fails.

**The wrong segment plays** — usually the wrong config matched. Check the filename matches
the scene card.

**Audio drifts after a stall** — "max delta time" in the advanced section lets Timeline
count the full pause, at the cost of a huge FixedUpdate catch-up in that frame. On heavy
scenes it makes things worse. Left alone by default.

## Settings

**Language** and **Reset to defaults** at the bottom of the panel, or edit
`BepInEx\config\reze.studio.cutscene.cfg`.
