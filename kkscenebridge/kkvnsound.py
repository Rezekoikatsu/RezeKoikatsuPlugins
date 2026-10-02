# -*- coding: utf-8 -*-
"""kkvnsound.py — 離線把音頻寫進場景卡的 VNGE VNSound 資料

取代「在 CharaStudio 裡用 VNGE 載入音頻 + 跑 A_VNSound_SmartSetup」那一套。

資料放在 KKEx 的兩個條目（格式是從實卡逆向出來的，不是猜的）：

  vnge_sound          [0, {"main": <JSON 字串>, "version": "1.0"}]
  vnge_sound_extdata  [0, {"main": {alias: None, …}, "version": "1.0"}]

vnge_sound 的 JSON：

  {"Spectrum": [], "WndRect": [x, y, w, h],
   "General": {"saveMode": "relpath"},
   "Sound":  {alias: {... "triggerId": <(SFX) 資料夾的 dicKey>, "volume": 0/1,
                      "audioRelPath": "UserData\\audio\\…\\xxx.wav" …}},
   "Group":  {群組名: {"groupTitle": …, "groupSound": [alias, …],
                      "exclusive": true, "randomMode": "Sequence", …}}}

重點：
  * triggerId 是 **dicKey**（節點自己的 ID），不是 RANK。
  * 觸發的機制是「該物件被啟用」——合併工具已經幫每個場景的包裝資料夾與
    (SFX) 寫好 objectEnabled 軌道，所以音頻綁 (SFX) 就會在該場景開始時播。
  * audioRelPath 以「遊戲根目錄」為基準，慣例是 UserData\\audio\\…
"""
from __future__ import annotations

import json
import os
import re

import kkscene2 as S
from kkmsgpack import arr_build, map_build, pack, unpack

SOUND_GUID = "vnge_sound"
EXT_GUID = "vnge_sound_extdata"
SOUND_VER = "1.0"
DEFAULT_WNDRECT = [1360.0, 760.0, 540.0, 300.0]
AUDIO_EXT = (".wav", ".mp3", ".ogg", ".aif", ".aiff")
DEFAULT_REL_PREFIX = "UserData\\audio"
DEFAULT_PREFER = ("[JPN]", "[ENG]", "[AI]")

# saveMode 是**整張卡共用**的，不能一部分相對一部分絕對。
MODE_REL = "relpath"        # audioRelPath = 前綴 + 相對位置
MODE_ABS = "abspath"        # audioFilePath = 完整路徑
MODE_DATA = "filedata"      # audioExt + audioFileData，檔案本體塞進 extdata
MODES = (MODE_REL, MODE_ABS, MODE_DATA)
MODE_LABEL = {MODE_REL: "相對路徑（UserData\\audio\\…）",
              MODE_ABS: "絕對路徑（D:\\…）",
              MODE_DATA: "夾帶在卡片裡（卡片會變很大）"}


def _warn(warn, msg):
    if warn is not None:
        warn.append(msg)


# ============================================================ KKEx 讀寫
def _ensure_entry(kkex, guid, main_value):
    """外掛條目不存在時，照 [0, {main, version}] 的形狀造一個。"""
    body = map_build([
        ("main", pack("main"), main_value),
        ("version", pack("version"), pack(SOUND_VER)),
    ])
    kkex.add_entry(guid, arr_build([pack(0), body]))


def read_sound(scene) -> dict:
    """回傳 vnge_sound 的 JSON（dict）；沒有就回空的骨架。"""
    empty = {"General": {"saveMode": "relpath"}, "Group": {},
             "Spectrum": [], "Sound": {}}
    vb = scene.kkex.get(SOUND_GUID, "main")
    if vb is None:
        return empty
    try:
        raw = unpack(vb)
    except Exception:                                     # noqa: BLE001
        return empty
    if not isinstance(raw, str) or not raw.strip():
        return empty
    try:
        d = json.loads(raw)
    except ValueError:
        return empty
    for k, v in empty.items():
        d.setdefault(k, v)
    return d


def write_sound(scene, data: dict, ext_data: dict | None = None) -> None:
    """把 JSON 寫回 vnge_sound，並依 Sound 的鍵重建 vnge_sound_extdata。

    ext_data：`filedata` 模式時 {alias: 檔案內容字串}。VNGE 存的是「一個字元一個
    位元組」的字串（IronPython 的 latin-1 風格），msgpack 再用 str 型別 UTF-8 編出去
    —— 所以 46 MB 的 wav 會變成約 70 MB。其餘模式全部是 None。
    """
    txt = json.dumps(data, ensure_ascii=False)
    if scene.kkex.has(SOUND_GUID):
        if not scene.kkex.set(SOUND_GUID, "main", pack(txt)):
            # payload 結構不如預期時整個重造，總比默默不寫好
            scene.kkex.items[scene.kkex.idx[SOUND_GUID]][2] = arr_build([
                pack(0), map_build([
                    ("main", pack("main"), pack(txt)),
                    ("version", pack("version"), pack(SOUND_VER))])])
    else:
        _ensure_entry(scene.kkex, SOUND_GUID, pack(txt))

    ext = dict(ext_data or {}) if ext_data else {}
    for alias in (data.get("Sound") or {}):
        ext.setdefault(alias, None)
    ext = {k: ext.get(k) for k in (data.get("Sound") or {})}
    if scene.kkex.has(EXT_GUID):
        if not scene.kkex.set(EXT_GUID, "main", pack(ext)):
            scene.kkex.items[scene.kkex.idx[EXT_GUID]][2] = arr_build([
                pack(0), map_build([
                    ("main", pack("main"), pack(ext)),
                    ("version", pack("version"), pack(SOUND_VER))])])
    else:
        _ensure_entry(scene.kkex, EXT_GUID, pack(ext))


# ============================================================ 場景槽位
def _sfx_child(wrap_node):
    for c in (wrap_node["data"].get("child") or []):
        if str(c["data"].get("name") or "").startswith("(SFX)"):
            return c
    return None


def is_prepped(scene):
    """整理過的卡在根節點有一個 (CAM) 資料夾。"""
    return any(n["type"] == 3 and (n["data"].get("name") or "") == "(CAM)"
               for n in scene.objects.values())


def _any_sfx(scene):
    """整棵樹裡第一個 (SFX) 資料夾。"""
    for n, _, _ in S.iter_nodes(scene.objects):
        if n["type"] == 3 and str(n["data"].get("name") or "").startswith("(SFX)"):
            return n
    return None


def scene_slots(scene, warn=None, name=None):
    """列出卡片裡的每一段場景：包裝資料夾 + 它的 (SFX)。

    回傳 [{index, name, wrap, sfx, sfx_name, start, end}]，index 從 1 起算，
    順序＝播放順序（用停放軌道的起始時間排，排不出來就用樹的順序）。

    **沒整理過的卡（根節點沒有 (CAM)）當成一整段**——它的根資料夾是作者自己的
    分類（FX / MAP / キャラクター…），不是場景段落，拆開來配音頻只會配到亂七八糟。
    """
    if not is_prepped(scene):
        sfx = _any_sfx(scene)
        if sfx is not None:
            tgt, tname = sfx["data"]["dicKey"], str(sfx["data"].get("name"))
        else:
            roots = [n for n in scene.objects.values() if n["type"] == 3]
            node = roots[0] if roots else next(iter(scene.objects.values()), None)
            if node is None:
                _warn(warn, "這張卡是空的")
                return []
            tgt = node["data"]["dicKey"]
            tname = f'{node["data"].get("name") or "（無名）"}（沒有 (SFX)，'
            tname += "改綁這個資料夾）"
            _warn(warn, "這張卡沒整理過也沒有 (SFX) 資料夾，觸發改綁 "
                        f'「{node["data"].get("name") or "（無名）"}」——'
                        "它一載入就是啟用狀態，所以音頻會在載入時就播。")
        return [{"index": 1,
                 "name": name or "整張場景",
                 "wrap": None, "sfx": tgt, "sfx_name": tname,
                 "start": 0.0, "end": scene.duration()}]

    by_key = {n["data"]["dicKey"]: n for n, _, _ in S.iter_nodes(scene.objects)}
    order, times = [], {}
    try:
        import kkscenemerge as KM
        segs = KM.scene_segments(scene)
        order = [s["wrap"] for s in segs]
        times = {s["wrap"]: (s.get("start"), s.get("end")) for s in segs}
    except Exception:                                     # noqa: BLE001
        order = []
    if not order:
        order = [n["data"]["dicKey"] for n in scene.objects.values()
                 if n["type"] == 3 and (n["data"].get("name") or "") != "(CAM)"]

    out = []
    for i, dk in enumerate(order, 1):
        w = by_key.get(dk)
        if w is None:
            continue
        sfx = _sfx_child(w)
        if sfx is None:
            _warn(warn, f"「{w['data'].get('name')}」底下沒有 (SFX) 資料夾，"
                        f"音頻會改綁主資料夾")
        st, en = times.get(dk, (None, None))
        out.append({
            "index": i,
            "name": str(w["data"].get("name") or ""),
            "wrap": dk,
            "sfx": (sfx["data"]["dicKey"] if sfx is not None else dk),
            "sfx_name": (str(sfx["data"].get("name")) if sfx is not None
                         else "（無，用主資料夾）"),
            "start": st, "end": en,
        })
    return out


# ============================================================ 補 (SFX) 資料夾
def add_sfx_folder(scene, slot, name=None, warn=None):
    """在某一段場景底下補一個 (SFX) 資料夾，並把啟用軌道一起寫好。

    為什麼要連軌道一起寫：VNSound 的觸發是「被綁的物件從沒啟用變成啟用」。
    光建一個資料夾、它從頭到尾都是啟用狀態的話，音頻只會在載入卡片時響一次，
    不會在該段場景開始時才播。

    新節點的 dicKey 取 `max(全部 dicKey) + 1` —— 排序後落在最後，
    所以既有的 RANK 一個都不會動（見 HANDOFF §3.1）。

    回傳新資料夾的 dicKey。
    """
    import kkscenemerge as KM

    by_key = {n["data"]["dicKey"]: n for n, _, _ in S.iter_nodes(scene.objects)}
    parent = by_key.get(slot.get("wrap")) if slot.get("wrap") is not None else None

    dk = max(S.all_dickeys(scene.objects)) + 1
    node = S.new_folder(dk, name or "(SFX)")
    if parent is not None:
        ch = parent["data"].get("child")
        if not isinstance(ch, list):
            _warn(warn, f"「{slot['name']}」不是資料夾，不能在底下加 (SFX)")
            return None
        ch.append(node)
    else:
        scene.objects[dk] = node                 # 沒有包裝資料夾就放根層

    xml = scene.timeline_xml()
    if not xml:
        _warn(warn, "這張卡沒有 timeline，(SFX) 建好了但寫不了啟用軌道 —— "
                    "音頻會在載入卡片時就播")
        slot["sfx"] = dk
        slot["sfx_name"] = name or "(SFX)"
        return dk

    head, body, tail = S.split_root(xml)
    ranks = S.rank_map(S.node_dickeys(scene.objects))
    r = ranks[dk]
    total = scene.duration() or 0.0
    st = slot.get("start") or 0.0
    en = slot.get("end")
    pts = [(0.0, False), (st if st > 0 else KM.ENABLE_LEAD, True)]
    if en is not None and total and en < total - 1e-6:
        pts.append((float(en), False))
    body, _ = S.remove_track(body, r, "objectEnabled")
    body += KM.object_enabled_track(r, pts)
    scene.set_timeline_xml(head + body + tail)

    slot["sfx"] = dk
    slot["sfx_name"] = name or "(SFX)"
    slot["sfx_added"] = True
    return dk


def sfx_points_text(slot, total):
    """給 UI 顯示用：這個 (SFX) 的啟用軌道會長什麼樣。"""
    st = slot.get("start") or 0.0
    en = slot.get("end")
    out = ["0 秒 取消勾選", f"{st if st > 0 else 0.001:g} 秒 勾選"]
    if en is not None and total and en < total - 1e-6:
        out.append(f"{float(en):g} 秒 取消勾選")
    return " → ".join(out)


# ============================================================ 音檔命名
RE_TAG = re.compile(r'^\[(.*?)\]')
RE_PT = re.compile(r'^(.*?)[\s_]*P[Tt]\s*(\d+)$')
RE_WRAP_NO = re.compile(r'[_\s]*\((\d+)\)\s*$')


def audio_tag(stem: str) -> str:
    """[JPN]CharcardPT1_@VoiceA -> '[JPN]'；沒標籤回空字串。"""
    m = RE_TAG.match(stem)
    return f"[{m.group(1)}]" if m else ""


def audio_core(stem: str) -> str:
    """[JPN]CharcardPT1_@VoiceA -> 'CharcardPT1'（跟 SmartSetup 的分組規則一致）。"""
    core = RE_TAG.sub("", stem, count=1)
    core = re.sub(r'_.*$', '', core).strip()
    return core


def split_pt(core: str):
    """'CharcardPT1' -> ('Charcard', 1)；沒有 PT 序號回 (core, None)。"""
    m = RE_PT.match(core)
    if m:
        return m.group(1).strip(" _-"), int(m.group(2))
    return core, None


def wrapper_no(name: str):
    """'Scenecard_(1)' -> 1；沒有編號回 None。"""
    m = RE_WRAP_NO.search(name or "")
    return int(m.group(1)) if m else None


def wrapper_base(name: str) -> str:
    return RE_WRAP_NO.sub("", name or "").strip(" _-")


def scan_audio(folder: str):
    """列出資料夾（單層）裡的音檔，照檔名排序。"""
    if not folder or not os.path.isdir(folder):
        return []
    out = [os.path.join(folder, f) for f in sorted(os.listdir(folder))
           if f.lower().endswith(AUDIO_EXT)]
    return out


def find_audio_folder(root: str, slot_names) -> str | None:
    """在 audio 根目錄底下找名字跟包裝資料夾相符的資料夾。

    Scenecard_(1..4) -> 共同名稱 Scenecard -> 找同名（不分大小寫）的資料夾。
    """
    if not root or not os.path.isdir(root):
        return None
    bases = {wrapper_base(n).lower() for n in slot_names if n}
    bases.discard("")
    if not bases:
        return None
    hits = []
    for dirpath, dirnames, _files in os.walk(root):
        for d in dirnames:
            if d.lower() in bases:
                hits.append(os.path.join(dirpath, d))
    if hits:
        return sorted(hits, key=len)[0]
    # 退一步：資料夾名「包含」某個夠長的 base。
    # 只走這個方向 —— 反過來（base 包含資料夾名）會讓「MAP 2nd」去命中叫「2」的
    # 資料夾，然後配出一堆完全不相干的音檔。
    for dirpath, dirnames, _files in os.walk(root):
        for d in dirnames:
            dl = d.lower()
            for bs in bases:
                if len(bs) >= 4 and bs in dl:
                    hits.append(os.path.join(dirpath, d))
    return sorted(hits, key=len)[0] if hits else None


def auto_match(slots, files):
    """把音檔配到場景槽位。

    回傳 ({slot_index: [path, …]}, [配不出去的 path])。

    規則（就是 SmartSetup 的反向操作）：
      * 檔名有 PT 序號 -> 找包裝資料夾編號相同的那一段；沒有編號就用第幾段。
      * 檔名沒有 PT 序號 -> 只有一段就全給它；多段的話按 base 名稱比對。
    """
    # PT 序號 -> 場景。有 _(n) 編號的用編號，沒編號的才退回「第幾段」。
    #
    # 注意不能兩個都試：只丟一段 Scenecard_(2) 進來時，PT1 會因為
    # 「第 1 段」而被收進去 —— 那一段明明只該收 PT2。
    by_no = {}
    numbered = [(s, wrapper_no(s["name"])) for s in slots]
    if any(n is not None for _s, n in numbered):
        for s, n in numbered:
            if n is not None:
                by_no.setdefault(n, s["index"])
        for s, n in numbered:                      # 沒編號的補位，不搶已占用的號碼
            if n is None:
                by_no.setdefault(s["index"], s["index"])
    else:
        by_no = {s["index"]: s["index"] for s in slots}

    out, left = {}, []
    for p in files:
        stem = os.path.splitext(os.path.basename(p))[0]
        _base, pt = split_pt(audio_core(stem))
        tgt = None
        if pt is not None:
            tgt = by_no.get(pt)
        elif len(slots) == 1:
            tgt = slots[0]["index"]
        else:
            bl = _base.lower()
            for s in slots:
                if bl and bl in wrapper_base(s["name"]).lower():
                    tgt = s["index"]
                    break
        if tgt is None:
            left.append(p)
        else:
            out.setdefault(tgt, []).append(p)
    return out, left


def pick_primary(paths, prefer=DEFAULT_PREFER):
    """依偏好標籤挑一個當 100%（找不到就第一個）。"""
    if not paths:
        return None
    for tag in prefer:
        for p in paths:
            if audio_tag(os.path.splitext(os.path.basename(p))[0]).upper() == tag.upper():
                return p
    return paths[0]


# ============================================================ 寫入
def sound_entry(alias, trigger_id, volume, mode=MODE_REL,
                rel_path=None, abs_path=None, ext=None):
    """一個音頻條目。欄位依 saveMode 不同（三種都是照實卡逐欄比對出來的）：

        relpath  -> audioRelPath
        abspath  -> audioFilePath
        filedata -> audioExt + audioFileData（本體在 vnge_sound_extdata）
    """
    e = {
        "audioTitle": alias,
        "lipSyncId": None,
        "dopplerLevel": 0.0,
        "pitch": 1.0,
        "spatialBlend": 0.0,
        "maxDistance": 200.0,
        "autoRewind": True,
        "position": {"__tuple__": True, "list": [0.0, 0.0, 0.0]},
        "audioType": "BGM",
        "triggerId": trigger_id,
        "priority": 128,
        "loop": False,
        "parentId": None,
        "volume": float(volume),
        "panStereo": 0.0,
        "playOnAwake": False,
        "minDistance": 10.0,
    }
    if mode == MODE_ABS:
        e["audioFilePath"] = abs_path
    elif mode == MODE_DATA:
        e["audioExt"] = ext or ".wav"
        e["audioFileData"] = 1
    else:
        e["audioRelPath"] = rel_path
    return e


def group_entry(title, aliases):
    return {
        "autoNext": False,
        "lipSyncId": None,
        "triggerId": None,
        "groupTitle": title,
        "groupSound": list(aliases),
        "exclusive": True,
        "randomMode": "Sequence",
    }


def _parts(p):
    return [x for x in re.split(r'[\\/]+', str(p or "")) if x not in ("", ".")]


def rel_path_of(path, audio_root, rel_prefix=DEFAULT_REL_PREFIX):
    """把絕對路徑換成遊戲相對路徑（UserData\\audio\\…）。

    自己拆字串而不用 os.path.relpath —— 這樣在 Linux 上也能正確處理
    Windows 路徑（容器裡跑測試用），比對跟 Windows 一樣不分大小寫。
    """
    pp = _parts(path)
    rp = _parts(audio_root)
    if rp and len(pp) > len(rp) and \
            [x.lower() for x in pp[:len(rp)]] == [x.lower() for x in rp]:
        rel = "\\".join(pp[len(rp):])
    else:
        rel = pp[-1] if pp else ""
    pre = "\\".join(_parts(rel_prefix))
    return f"{pre}\\{rel}" if pre else rel


def apply_audio(scene, plan, audio_root=None, rel_prefix=DEFAULT_REL_PREFIX,
                clear=True, warn=None, mode=MODE_REL):
    """把音頻寫進場景卡。

    plan: [{"slot": <scene_slots 的那個 dict>,
            "group": 群組名稱（預設＝包裝資料夾名稱）,
            "files": [絕對路徑, …],
            "primary": 要設 100% 的那個路徑（None＝自動挑）}]

    mode: relpath / abspath / filedata —— **整張卡共用一個**，VNGE 的 saveMode
    就是全域設定，不能一部分相對一部分絕對。

    回傳統計 dict。
    """
    if mode not in MODES:
        raise ValueError(f"不認得的 saveMode：{mode}")
    data = read_sound(scene)
    data.setdefault("Spectrum", [])
    data.setdefault("WndRect", list(DEFAULT_WNDRECT))
    data["General"] = dict(data.get("General") or {})
    old_mode = data["General"].get("saveMode")
    data["General"]["saveMode"] = mode
    if not clear and old_mode and old_mode != mode:
        _warn(warn, f"卡片原本是 {old_mode}，現在改成 {mode}；"
                    "舊的音頻欄位對不上新模式，建議勾「寫入前清掉卡片原有的音頻」")
    ext_data = {}
    if clear:
        data["Sound"] = {}
        data["Group"] = {}
    sounds = data.setdefault("Sound", {})
    groups = data.setdefault("Group", {})

    n_snd = n_grp = n_bytes = 0
    for item in plan:
        slot = item["slot"]
        files = [f for f in (item.get("files") or []) if f]
        if not files:
            continue
        title = (item.get("group") or slot["name"] or f"場景{slot['index']}").strip()
        primary = item.get("primary") or pick_primary(files)
        trig = slot.get("sfx")
        aliases = []
        for p in files:
            stem = os.path.splitext(os.path.basename(p))[0]
            alias, k = stem, 1
            while alias in sounds:
                alias = f"{stem}_{k}"
                k += 1
            vol = 1.0 if (len(files) == 1 or p == primary) else 0.0
            exists = os.path.isfile(p)
            if not exists:
                _warn(warn, f"找不到音檔：{p}")
            blob = None
            if mode == MODE_DATA:
                if exists:
                    with open(p, "rb") as fh:
                        blob = fh.read().decode("latin-1")
                else:
                    blob = ""
                ext_data[alias] = blob
            sounds[alias] = sound_entry(
                alias, trig, vol, mode=mode,
                rel_path=rel_path_of(p, audio_root, rel_prefix),
                abs_path=os.path.abspath(p).replace("/", "\\"),
                ext=os.path.splitext(p)[1].lower() or ".wav")
            aliases.append(alias)
            n_snd += 1
            n_bytes += len(blob) if blob else 0
        if title in groups:
            groups[title]["groupSound"].extend(aliases)
        else:
            groups[title] = group_entry(title, aliases)
            n_grp += 1

    write_sound(scene, data, ext_data if mode == MODE_DATA else None)
    if mode == MODE_DATA and n_bytes:
        _warn(warn, f"夾帶模式：卡片會多出大約 {n_bytes * 1.5 / 1024 / 1024:.0f} MB"
                    "（存檔時每個位元組會被 UTF-8 編成約 1.5 倍）")
    return {"sounds": n_snd, "groups": n_grp, "bytes": n_bytes, "mode": mode,
            "slots": len([p for p in plan if p.get("files")])}


def auto_apply(scene, audio_root, rel_prefix=DEFAULT_REL_PREFIX, audio_dir=None,
               log=None, warn=None, clear=True, mode=MODE_REL):
    """一步到位：找資料夾 → 自動配對 → 寫進卡片。合併流程的 post hook 用這個。"""
    log = log or (lambda *a: None)
    slots = scene_slots(scene, warn)
    if not slots:
        log("  [音頻] 卡片裡找不到場景段落，跳過")
        return None
    folder = audio_dir or find_audio_folder(audio_root,
                                            [s["name"] for s in slots])
    if not folder or not os.path.isdir(folder):
        log(f"  [音頻] 在 {audio_root} 底下找不到對應的音檔資料夾，跳過")
        return None
    files = scan_audio(folder)
    assign, left = auto_match(slots, files)
    if not assign:
        log(f"  [音頻] {folder} 裡的 {len(files)} 個音檔一個都配不上，跳過")
        return None
    plan = build_plan(slots, assign)
    res = apply_audio(scene, plan, audio_root=audio_root,
                      rel_prefix=rel_prefix, clear=clear, warn=warn, mode=mode)
    log(f"  [音頻] {folder}")
    for item in plan:
        s = item["slot"]
        pri = item["primary"] or pick_primary(item["files"])
        names = ", ".join(
            ("★" if f == pri else "") + os.path.basename(f) for f in item["files"])
        log(f"    [{s['index']}] {s['name']} → 群組「{item['group']}」"
            f" trigger={s['sfx']}：{names or '（沒有配到音檔）'}")
    for f in left:
        log(f"    [配不出去] {os.path.basename(f)}")
    log(f"  [音頻] 共 {res['sounds']} 個音頻、{res['groups']} 個群組（★＝100%）")
    return res


def clear_audio(scene):
    """把卡片裡的音頻與群組全部清掉（vnge_sound / extdata 留著，變成空的）。"""
    data = read_sound(scene)
    n = len(data.get("Sound") or {})
    g = len(data.get("Group") or {})
    data["Sound"] = {}
    data["Group"] = {}
    write_sound(scene, data)
    return {"sounds": n, "groups": g}


def is_under(path, root):
    """path 是不是在 root 底下（不分大小寫，自己拆字串，跨平台）。"""
    if not root:
        return False
    pp = [x.lower() for x in _parts(path)]
    rp = [x.lower() for x in _parts(root)]
    return bool(rp) and len(pp) > len(rp) and pp[:len(rp)] == rp


def save_atomic(scene, out):
    """先寫到同資料夾的 .tmp 再換過去 —— 中途爆掉不會留下半殘的卡。"""
    out = os.path.abspath(out)
    tmp = out + ".kkvnsound.tmp"
    try:
        scene.save(tmp)
        os.replace(tmp, out)
    except Exception:
        if os.path.exists(tmp):
            try:
                os.remove(tmp)
            except OSError:
                pass
        raise
    return out


def build_plan(slots, assign, group_names=None, primaries=None):
    """把 {slot_index: [paths]} 攤成 apply_audio 要的 plan。"""
    group_names = group_names or {}
    primaries = primaries or {}
    plan = []
    for s in slots:
        files = assign.get(s["index"]) or []
        plan.append({"slot": s,
                     "group": group_names.get(s["index"]) or s["name"],
                     "files": files,
                     "primary": primaries.get(s["index"])})
    return plan


# ============================================================ CLI
def _fmt_t(v):
    return "—" if v is None else f"{v:.2f}"


def main(argv=None):
    import argparse
    ap = argparse.ArgumentParser(prog="kkvnsound")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("list", help="列出卡片裡的場景段落與目前的音頻")
    p.add_argument("card")

    p = sub.add_parser("apply", help="依場景名稱自動配對音檔並寫進卡片")
    p.add_argument("card")
    p.add_argument("--audio-dir", help="音檔所在資料夾（不給就從 --audio-root 自動找）")
    p.add_argument("--audio-root", default="", help="音頻根目錄，算相對路徑用")
    p.add_argument("--rel-prefix", default=DEFAULT_REL_PREFIX)
    p.add_argument("--out", help="輸出卡片（不給就原地覆寫）")
    p.add_argument("--keep", action="store_true", help="保留卡片原本的音頻")
    p.add_argument("--dry-run", action="store_true")

    a = ap.parse_args(argv)

    # 先把路徑都檢查完再讀卡 —— 1 GB 的卡讀完才發現輸出資料夾不存在很浪費
    if not os.path.isfile(a.card):
        print(f"找不到場景卡：{a.card}")
        return 2
    if a.cmd == "apply":
        out = a.out or a.card
        d = os.path.dirname(os.path.abspath(out))
        if not os.path.isdir(d):
            print(f"輸出資料夾不存在：{d}")
            return 2
        if a.audio_root and not os.path.isdir(a.audio_root):
            print(f"音頻根目錄不存在：{a.audio_root}")
            return 2
        if a.audio_dir and not os.path.isdir(a.audio_dir):
            print(f"音檔資料夾不存在：{a.audio_dir}")
            return 2

    sc = S.Scene(a.card)
    warn = []
    slots = scene_slots(sc, warn)

    if a.cmd == "list":
        data = read_sound(sc)
        print(f"{os.path.basename(a.card)}  saveMode="
              f"{(data.get('General') or {}).get('saveMode')}")
        print(f"場景段落 {len(slots)} 段：")
        for s in slots:
            print(f"  [{s['index']}] {s['name']}  (SFX)dicKey={s['sfx']}"
                  f"  {_fmt_t(s['start'])}~{_fmt_t(s['end'])} 秒")
        print(f"音頻 {len(data.get('Sound') or {})} 個、"
              f"群組 {len(data.get('Group') or {})} 個：")
        for g, gv in (data.get("Group") or {}).items():
            print(f"  群組 {g}")
            for al in gv.get("groupSound") or []:
                sv = (data.get("Sound") or {}).get(al) or {}
                print(f"    {al:<34} vol={sv.get('volume')}"
                      f" trigger={sv.get('triggerId')}  {sv.get('audioRelPath')}")
        for w in warn:
            print("[注意]", w)
        return 0

    root = a.audio_root or ""
    folder = a.audio_dir or find_audio_folder(root, [s["name"] for s in slots])
    if not folder:
        print("找不到音檔資料夾，請用 --audio-dir 指定")
        return 2
    if not root:
        root = folder
    files = scan_audio(folder)
    print(f"音檔資料夾：{folder}（{len(files)} 個）")
    assign, left = auto_match(slots, files)
    plan = build_plan(slots, assign)
    for item in plan:
        s = item["slot"]
        print(f"  [{s['index']}] {s['name']}  群組「{item['group']}」"
              f"  trigger={s['sfx']}")
        pri = item["primary"] or pick_primary(item["files"])
        for f in item["files"]:
            print(f"       {'100%' if f == pri else '  0%'}  "
                  f"{rel_path_of(f, root, a.rel_prefix)}")
        if not item["files"]:
            print("       （沒有配到音檔）")
    for f in left:
        print(f"  [配不出去] {f}")
    if a.dry_run:
        return 0

    res = apply_audio(sc, plan, audio_root=root, rel_prefix=a.rel_prefix,
                      clear=not a.keep, warn=warn)
    out = a.out or a.card
    save_atomic(sc, out)
    print(f"寫出 {out}（音頻 {res['sounds']} 個、群組 {res['groups']} 個、"
          f"{os.path.getsize(out):,} bytes）")
    for w in warn:
        print("[注意]", w)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
