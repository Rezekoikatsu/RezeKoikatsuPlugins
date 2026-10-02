# Studio Character Tools (F6)

[繁體中文](README.md) ・ **English** ・ [日本語](README.ja.md)

> 🤖 **Created by Claude AI** — the code and documentation in this project were written by Claude (Anthropic's AI); Reze came up with the ideas, directed the work and tested everything in-game.

A scene-character manager for CharaStudio. Press **F6** to open the character list —
one row per character on stage — and swap cards, change outfits, manage accessories,
lock blendshapes, repair colliders, and capture thumbnails, all from one place.

## Requirements

Only the **mandatory** ones are listed. Common plugins nearly everyone already has (KKAPI, More Accessories, ABMX…) are not listed separately.

| Required | Why |
|---|---|
| [BepInEx 5](https://github.com/BepInEx/BepInEx) | The plugin framework. Bundled with every Koikatsu repack |
| (none) | F6 has no hard prerequisites. Missing ABMX, KKPE or MaterialEditor just costs you those features |

## Main panel

Characters in the scene are split into a female and a male column. Each row has:

| Element | What it does |
|---|---|
| Checkbox | Same as the eye toggle in the workspace: shows/hides the character |
| Character name | Click to open this character's action row: swap, swap keeping outfit, add accessories, reset pose, save character / outfit / all outfits / pose, accessory slots |
| Select | Selects this character in the workspace on the left |
| Change outfit | Opens the outfit card picker, then asks whether to bring back hair and other accessories |
| Colliders | Opens the collider binding repair side panel |
| Blendshapes | Opens the blendshape lock side panel |

The top of each column also has group actions: swap all, swap all keeping outfits, save all cards.

### Four body modes for swapping

The hard part of swapping is requests like "change the face, keep the body". So after pressing Swap you
first choose how to handle the body:

1. **Normal swap** — take the new card as is
2. **Keep old body** — new card, but body shape and ABMX all stay as the original character
3. **Lock height** — only height is locked to the original; everything else comes from the new card
4. **Keep new body** — use the new card's body, but keep the ABMX bones that decide joint positions
   (limb lengths, height scale, …) so the existing pose doesn't drift

**Swap keeping outfit** uses the same four modes, then puts the original clothes back on (optionally
bringing hair accessories from another outfit card).

> [!IMPORTANT]
> "Swap keeping outfit", "bring back hair accessories after changing outfit" and "Add accessories" all rely on
> `kkbridge.exe` to merge cards, so **start `kkbridge.exe` before using them** (see [Card merging](#card-merging-needs-kkbridge) below).
> Without it these features wait until they time out; everything else keeps working.

> [!TIP]
> Before "Swap keeping outfit", **remove the original character's hair** first (accessory slots → Remove).
> The original hair accessories are part of the outfit, so otherwise they stay on the new character along with the clothes.

<!-- video:f6-swap-normal -->
<!-- video:f6-swap-keep -->

Swapping can automatically keep the character name, reapply the pose, restore collider bindings and apply a
same-name blendshape preset (each toggled in Settings). The "ABMX bones kept by Keep new body" rule is
editable in Settings: comma-separated wildcards, a leading `-` excludes. Bones outside the rule (face, chest)
use the new card's own ABMX instead of being cleared.

By default a swap keeps the scene's expression (eyebrows / eyes / mouth, open amount, blink, blush, tears, gaze).
To use the expression saved in the new card instead, turn off "Keep expression when swapping" in Settings.

### Changing outfit and hair accessories (bringing hair back needs kkbridge)

Press "Change outfit" on a character's row and pick an outfit card. Many characters have hair built from accessories;
put on a different outfit card and the whole accessory set is replaced, so the hair disappears.
That is why, after the outfit card, you are asked whether to **bring the hair back**: pick an outfit card that only
contains hair / hair accessories and its accessories are merged back onto the character.
This step is done by `kkbridge.exe`, which has to be running.

<!-- video:f6-outfit-nohair -->
<!-- video:f6-outfit-hair -->

### Carrying the scene's shaders over on swap (needs MaterialEditor)

A scene card's lighting and post-processing are usually tuned for the original character's shaders
(a full xukmi setup, for example). Swap in a card that uses a different shader set and the colours and
shading stop matching the scene.

**Apply scene character's shaders on swap** in Settings has three states:

| Option | Behaviour |
|---|---|
| Off | Nothing changes; the new character keeps the shaders from its own card |
| Ask (default) | After you pick the card, before the swap happens: Apply shaders / Don't apply / Cancel swap |
| Auto | Always applies them |

Only the **shader** is changed: for the body, face, eye whites, eyes, eyebrows, teeth, tongue, eyeliner,
nose line and tear meshes, material 0 gets the shader the original character used on the same part.
Parameters, colours, textures and reflections all stay the new character's own. Parts are matched by
renderer, not by material name, so it works across different head mods. Without MaterialEditor this
feature quietly does nothing.

## Side panels

- **Accessory manager** — lists every accessory on the character; toggle them, copy to another character. The top row shows, hides or removes All / Main / Sub accessories at once (remove needs a second click within 3 s)
- **Blendshape lock** — pins chosen blendshapes so a swap or outfit change can't overwrite them. Three modes: Fixed, Range and Scale (multiplies whatever the expression writes — handy when a face mod's expression keys are too strong or weak; can be applied to a whole page at once). "Only KKPE-edited" lists just the keys KKPE's blendshape editor shows in purple. "Diagnostics" at the bottom dumps the current blendshapes to a text file (this character / all characters)
- **Collider repair** — after a swap, DynamicBone colliders often stay bound to the old character; this rebinds them in one click
- **Settings** — every option, plus the batch operations

### Group scale / move for accessories

Hair built from a dozen accessories is tedious to resize one piece at a time. **Group scale** in the
accessory manager adjusts every ticked accessory at once:

- Tick the accessories in the list, or use "Tick this group" / "Tick head/hair"
- **Pivot**: "Own anchor" (default — each accessory scales around its own anchor) or "Head bone"
  (the whole group scales around the head, so the pieces keep their relative layout)
- **Scale**: separate X / Y / Z sliders; "Uniform" moves all three together; "Reset scale" goes back to 100%
- **Position**: X / Y / Z sliders — left/right, up/down, forward/back when the pivot is the head bone; "Reset pos" goes back to 0
- Dragging a slider previews live. Nothing is written until you press **Confirm**; the sliders then reset so
  you can keep adjusting. "Cancel" throws away the current adjustment
- **Undo last confirm** reverts the most recent confirmed adjustment
- **Use last values** puts the last confirmed values back on the sliders (press Confirm again to save) —
  handy when several characters should end up the same size

### Synced outfits

**Synced outfits** at the top of the accessory manager lists every outfit slot of the character (School Uniform,
Going Home, Gym Clothes, Swimsuit, Club, Casual, Pajamas). Click one so it turns red to select it; from then on,
**removing, scaling and moving** in the current outfit is applied to the selected outfits too.

- Only the same slot holding the same item is synced, so a different accessory that happens to sit in the same slot of another outfit is never touched
- If an outfit can't be synced (the slot is empty or holds something else), a line at the bottom says "Outfits that could not be synced: …"

## Thumbnails and card saving

Characters can be saved straight from the panel as a character card, outfit card or pose, with a thumbnail taken on the spot:

- **Save all outfits** — saves every outfit of this character as its own outfit card, then switches back
- Waits for physics to settle first (hair and chest DynamicBones), time adjustable
- Can show only that character (others hidden for the shot), optionally hiding male characters too
- Framing can be centered on the head, with the head's height in the frame and the frame size (as a multiple of head-to-foot distance) adjustable
- Thumbnail height limit 0 = no scaling

Once set, a whole batch of characters comes out with consistent framing.

## Batch operations

Settings has buttons that do the same thing to **every** character in the scene:

- Reset all poses (fixes "stretched after swapping")
- Repair collider bindings
- Add accessories (one outfit card applied to everyone)

## Card merging (needs kkbridge)

Merges a coordinate card's accessories into a character card using the companion external
tool **kkbridge**.

- `kkbridge.exe` ships inside `StudioCharTools.zip`; after extracting in the game root it sits there, next to `BepInEx`
- Before merging, start `kkbridge.exe` and begin watching in its **Watch jobs** tab; if it isn't running, merges time out and everything else keeps working
- The job folder defaults to `UserData\chara\female\Temp`; with defaults on both sides there is nothing to set. If you change it, F6's Settings and kkbridge must point at the same folder
- When kkbridge sits in the game root it recognises the game by itself and starts watching — nothing to set up; if you keep it elsewhere, set the game root once in its **Settings** tab
- Its source is in this repo under [`kkbridge/`](../kkbridge/)

## Settings

The **Settings** button at the bottom right of the panel. **Language** and
**Reset to defaults** are at the very bottom of that window. You can also use
BepInEx's ConfigurationManager or edit `BepInEx\config\reze.studio.chartools.cfg`.

## Known limitations

- "Keep new body" works by matching ABMX bone names against a rule. Cards using unusual bones may need the rule adjusted.
- Shader carry-over only swaps the shader itself. If the new card had values tuned for its original shader (xukmi-only parameters, say), you may want to touch them up afterwards.
- Card merging needs an external exe; this plugin only invokes it and marshals the files.
