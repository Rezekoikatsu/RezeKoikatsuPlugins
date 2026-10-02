#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""kkscenemerge.py — 恋活場景卡離線合併工具

    merge(A, B) = A ⧺ remap(B)

A 是先播的那張，B 接在後面。B 的 dicKey 整批 +K、timeline 整條往後平移
A 的時長，兩邊的樹與外掛資料串接，貼圖按內容去重。

用法
    python kkscenemerge.py info  <卡.png>
    python kkscenemerge.py prep  <卡.png> --name "場景A" [--camera <dicKey>] [--out <出.png>]
    python kkscenemerge.py merge <第1張.png> <第2張.png> [第3張.png ...] --out <合併.png> [--gap 秒]

prep 會：只留一台相機（有複數時會先列出來要你指定）、把相機連同它的父資料夾鏈
搬到新的 (CAM) 根資料夾並改名 POV 1..N、其餘全部包進一個以卡片命名的資料夾，
再建 (MAP)(FX)(CHAR)(SFX) 四個空資料夾給你之後自己分類。

參照欄位與座標系的完整對照在 kkref.py；msgpack 位元組手術在 kkmsgpack.py。
"""
import argparse
import hashlib
import os
import re
import struct
import sys

import kkref
import kkscene2 as S
import kktl_scan as TL
from kkmsgpack import (arr_build, arr_split, map_build, map_split, pack,
                       patch_entry, patch_key, unpack)

ME = "com.deathweasel.bepinex.materialeditor"
UAR = "com.bepis.sideloader.universalautoresolver"
# StudioImageEmbed：整個畫面上疊一張圖。FrameData = 外框（疊在最上層的那張），
# BGData = 背景。兩張卡對比出來只差 FrameData，所以「清除外框」只動它。
IMGEMBED = "com.deathweasel.bepinex.studioimageembed"
TNN = "org.njaecha.plugins.treenodenaming"


def log(*a):
    print(*a, file=sys.stderr)


# ================================================================ 小工具
def _inner(kkex, guid, key):
    vb = kkex.get(guid, key)
    if vb is None:
        return None
    try:
        return unpack(vb)
    except Exception:                                       # noqa: BLE001
        return None


def _set_inner(kkex, guid, key, data):
    kkex.set(guid, key, pack(data))


# 外掛的 payload 值有兩種形狀：
#   bin  —— 值是一個 msgpack bin，裡面才是真正的陣列 / map（MaterialEditor、treenodenaming…）
#   raw  —— 值本身就是 msgpack 陣列（UAR 的 itemInfo，元素又各自是 bin 包的 map）
# 這兩層包裝要原樣還回去，否則遊戲讀不到。
def _container(vb):
    o = unpack(vb)
    if o is None:
        return "bin", None                       # nil：當成空的
    if isinstance(o, (bytes, bytearray)):
        return "bin", bytes(o)
    return "raw", vb


def _rewrap(kind, payload):
    return pack(payload) if kind == "bin" else payload


def _elem_map(e):
    """元素可能自己又包一層 bin。回傳 (dict 或 None, 是否包了 bin)"""
    o = unpack(e)
    if isinstance(o, (bytes, bytearray)):
        inner = unpack(bytes(o))
        return (inner if isinstance(inner, dict) else None), True
    return (o if isinstance(o, dict) else None), False


def _elem_patch(e, upd, wrapped):
    if not upd:
        return e
    if wrapped:
        return pack(patch_entry(unpack(e), upd))
    return patch_entry(e, upd)


# ================================================================ 搬運開關（診斷用）
#
# 合併會把十幾種外掛資料從第二張卡搬到底卡。某一項搬壞的時候，光看程式碼
# 分不出是哪一項 —— 但只要能「關掉其中一項再合一次」，一輪就能指出兇手。
#
# 用環境變數控制，這樣 GUI 也能用：
#     set KKSB_SKIP=UAR
#     run_source.bat
#
# 可用的名字（逗號分隔，不分大小寫）：
#     textures, nodesConstraints, kkpe, treenodenaming, MaterialEditor,
#     UAR, LightSettings, itemlayeredit, savecameraobjectfov,
#     RSkoi_ComponentUtil, rendererEditor, objimport
# 特別的：all 會全部跳過（只搬節點，不搬任何外掛資料）
def _skipped(name):
    raw = os.environ.get("KKSB_SKIP", "")
    if not raw.strip():
        return False
    want = {x.strip().lower() for x in raw.split(",") if x.strip()}
    return "all" in want or name.lower() in want


def _skip_note(name, warn):
    msg = f"KKSB_SKIP：{name} 這次沒有搬（診斷模式）"
    if msg not in warn:
        warn.append(msg)
    return 0


def merge_bin_list(dst, src, guid, key, remaps, warn, deep=None):
    """payload[key] 是 msgpack 陣列（可能包在 bin 裡）。把 src 的整批接到 dst 後面。"""
    b_vb = src.get(guid, key)
    if b_vb is None:
        return 0
    try:
        b_kind, b_payload = _container(b_vb)
        if not b_payload:
            return 0
        b_items = arr_split(b_payload)
    except ValueError:
        warn.append(f"{guid}/{key} 不是 msgpack 陣列，未合併")
        return 0
    a_vb = dst.get(guid, key)
    a_items, kind = [], b_kind
    if a_vb is not None:
        try:
            kind, a_payload = _container(a_vb)
            a_items = arr_split(a_payload) if a_payload else []
        except ValueError:
            a_items = []
    # 負數是哨兵值（例如 LightSettingsData 的 ObjectId=-10 是場景燈、
    # realpov 的 CharaId=-1 是沒綁角色），不能重編號；底卡已經有的也不要再搬一份
    base_sentinel = set()
    for e in a_items:
        d, _ = _elem_map(e)
        if d:
            for f in remaps:
                v = d.get(f)
                if isinstance(v, int) and not isinstance(v, bool) and v < 0:
                    base_sentinel.add((f, v))
    moved, skipped = [], 0
    for e in b_items:
        d, wrapped = _elem_map(e)
        if d is None:
            moved.append(e)
            continue
        if any((f, d.get(f)) in base_sentinel for f in remaps):
            skipped += 1
            continue
        upd = {}
        for f, fn in remaps.items():
            v = d.get(f)
            if isinstance(v, int) and not isinstance(v, bool) and v >= 0:
                nv = fn(v)
                if nv is None:
                    break
                upd[f] = nv
        else:
            # 巢狀欄位的重編號（例如 MaterialEditor 動態貼圖的 TexAnimationDef，
            # 它的 frames 每一格都是 [貼圖編號, 起點毫秒, 長度毫秒]）。
            # 只改最外層的 TexID 不夠 —— 每一格的編號也要跟著換，
            # 不然搬過去之後每一格都指到別張貼圖，畫面就是空的。
            for f, fn in (deep or {}).items():
                v = d.get(f)
                if v is None:
                    continue
                nv = fn(v)
                if nv is not None and nv != v:
                    upd[f] = nv
            moved.append(_elem_patch(e, upd, wrapped))
    if skipped:
        warn.append(f"{guid}/{key}：{skipped} 筆是底卡已經有的場景層級項目（負數哨兵），略過")
    dst.set(guid, key, _rewrap(kind, arr_build(a_items + moved)))
    return len(moved)


def merge_bin_map(dst, src, guid, key, keyfn, warn):
    """payload[key] 是 msgpack map（可能包在 bin 裡），鍵是 dicKey。"""
    b_vb = src.get(guid, key)
    if b_vb is None:
        return 0
    try:
        b_kind, b_payload = _container(b_vb)
        if not b_payload:
            return 0
        b_items = map_split(b_payload)
    except ValueError:
        warn.append(f"{guid}/{key} 不是 msgpack map，未合併")
        return 0
    a_vb = dst.get(guid, key)
    a_items, kind = [], b_kind
    if a_vb is not None:
        try:
            kind, a_payload = _container(a_vb)
            a_items = [list(t) for t in (map_split(a_payload) if a_payload else [])]
        except ValueError:
            a_items = []
    n = 0
    have = {k for k, _, _ in a_items}
    for k, kb, vb in b_items:
        nk = keyfn(k) if isinstance(k, int) and not isinstance(k, bool) and k >= 0 else k
        if nk is None or nk in have:
            continue
        a_items.append([nk, patch_key(kb, nk) if isinstance(k, int) else kb, vb])
        have.add(nk)
        n += 1
    dst.set(guid, key, _rewrap(kind, map_build([tuple(i) for i in a_items])))
    return n


def merge_xml(dst, src, guid, key, root_tag, remap_fn, warn):
    """payload[key] = 字串 XML，<root>…</root> 的內容直接串接。"""
    b = src.get(guid, key)
    if b is None:
        return 0
    b_xml = unpack(b)
    if not isinstance(b_xml, str) or not b_xml.strip():
        return 0
    a_vb = dst.get(guid, key)
    a_xml = unpack(a_vb) if a_vb is not None else None
    hb, bb, tb = _split_xml_root(b_xml, root_tag)
    if hb is None:
        warn.append(f"{guid}/{key} 的 XML 不是 <{root_tag}> 包起來的，未合併")
        return 0
    bb = remap_fn(bb)
    if not bb.strip():
        return 0
    if not isinstance(a_xml, str) or not a_xml.strip():
        dst.set(guid, key, pack(hb + bb + tb))
        return 1
    ha, ba, ta = _split_xml_root(a_xml, root_tag)
    if ha is None:
        warn.append(f"{guid}/{key} 底卡的 XML 形狀不對，未合併")
        return 0
    dst.set(guid, key, pack(ha + ba + bb + ta))
    return 1


def _split_xml_root(xml, root_tag):
    """回傳 (開頭標籤, 內容, 結尾標籤)；自閉合的 <tag ... /> 也吃得下。"""
    x = xml.strip()
    m = re.match(rf'^(<{root_tag}\b[^>]*?)/>$', x, re.S)
    if m:
        return m.group(1) + ">", "", f"</{root_tag}>"
    m = re.match(rf'^(<{root_tag}\b[^>]*>)(.*)(</{root_tag}>)$', x, re.S)
    if m:
        return m.group(1), m.group(2), m.group(3)
    return None, None, None


def remap_kkpe_index(xml, fn):
    """kkpe 只有 <itemInfo index="dicKey"> 的 index 是物件編號。

    同一份 XML 裡 <blendShape index="0"> 是 blend shape 的編號（第幾個），不能跟著位移 ——
    以前整份一起重編號，第二張卡物件的 blend shape 全部變成 898、899…（不存在的編號）。
    """
    return re.sub(r'<itemInfo\b[^>]*>',
                  lambda m: remap_xml_attr(m.group(0), ["index"], fn), xml)


def remap_xml_attr(xml, attrs, fn):
    pat = re.compile(r'\b(' + "|".join(attrs) + r')="(-?\d+)"')

    def rep(m):
        v = int(m.group(2))
        if v < 0:
            return m.group(0)
        nv = fn(v)
        return m.group(0) if nv is None else f'{m.group(1)}="{nv}"'
    return pat.sub(rep, xml)


# ================================================================ RendererEditor / OBJImport
# RendererEditor（rendererEditor/xml）：<renderer objectIndex="RANK" …> 每個物件的 renderer 設定
#   （陰影、啟用、材質貼圖換成 BepInEx\plugins\RendererEditor\Textures 裡的圖…）；
#   <textureSettings path=…> 是貼圖本身的設定，跟物件無關。
#   objectIndex 是 RANK（實卡驗證：Silverwolf 原卡 10 / 22 / 152 各指到螢幕、兩個同款物件）。
#   以前完全沒處理：整理（刪節點）之後 RANK 移位，貼圖被貼到別的物件上
#   —— Silverwolf 合併卡 rank 152 變成了 (CHAR) 1 資料夾，角色臉被換成 gameover.png。
# OBJImport（org.njaecha.plugins.objimport）：meshes[i] 是匯入的網格、ids[i] 是套用的物件 dicKey。
#   以前沒搬：第二張卡的匯入網格不見，物件變回原本的形狀（眼鏡變成一張平面貼圖）。
RE_RENDERER = re.compile(r'<renderer\b[^>]*?/>|<renderer\b[^>]*?>.*?</renderer>', re.S)
RE_TEXSET = re.compile(r'<textureSettings\b[^>]*?/>|<textureSettings\b[^>]*?>.*?</textureSettings>', re.S)
OBJIMP = "org.njaecha.plugins.objimport"
RENDED = "rendererEditor"


def _rended_parts(xml):
    """rendererEditor 的 XML → (開頭, [textureSettings…], [renderer…], 結尾)；形狀不對回傳 None。"""
    if not isinstance(xml, str) or not xml.strip():
        return None
    h, b, t = _split_xml_root(xml, "root")
    if h is None:
        return None
    return h, RE_TEXSET.findall(b), RE_RENDERER.findall(b), t


def _rended_path(blk):
    m = re.search(r'\bpath="([^"]*)"', blk)
    return m.group(1) if m else blk


def rended_remap(kkex, rank_fn):
    """rendererEditor 的 objectIndex 全部重編號；對不到（物件被刪）的整筆拿掉。回傳拿掉幾筆。"""
    vb = kkex.get(RENDED, "xml")
    if vb is None:
        return 0
    parts = _rended_parts(unpack(vb))
    if parts is None:
        return 0
    h, texs, rends, t = parts
    kept, dropped = [], 0
    for blk in rends:
        m = re.search(r'\bobjectIndex="(-?\d+)"', blk)
        if m and int(m.group(1)) >= 0 and rank_fn(int(m.group(1))) is None:
            dropped += 1
            continue
        kept.append(remap_xml_attr(blk, ["objectIndex"], rank_fn))
    kkex.set(RENDED, "xml", pack(h + "".join(texs) + "".join(kept) + t))
    return dropped


def merge_rended(da, db, rank_fn, warn):
    """第二張的 rendererEditor 併進底卡：renderer 照 RANK 重編號，貼圖設定按路徑去重。"""
    vb = db.get(RENDED, "xml")
    if vb is None:
        return 0
    pb = _rended_parts(unpack(vb))
    if pb is None:
        return 0
    if not da.has(RENDED):
        # 底卡沒有：整包搬過來再重編號（不然最後「只有這張卡有」會原封不動搬、不重編號）
        da.add_entry(RENDED, db.raw_entry(RENDED))
        rended_remap(da, rank_fn)
        return len(pb[2])
    _hb, texb, rendb, _tb = pb
    rendb = [remap_xml_attr(x, ["objectIndex"], rank_fn) for x in rendb]
    va = da.get(RENDED, "xml")
    pa = _rended_parts(unpack(va)) if va is not None else None
    if pa is None:
        da.set(RENDED, "xml", pack(pb[0] + "".join(texb) + "".join(rendb) + pb[3]))
        return len(rendb)
    ha, texa, renda, ta = pa
    have = {_rended_path(x) for x in texa}
    texa = texa + [x for x in texb if _rended_path(x) not in have]
    da.set(RENDED, "xml", pack(ha + "".join(texa) + "".join(renda + rendb) + ta))
    return len(rendb)


def _objimp_lists(kkex):
    """OBJImport 的 (meshes 元素, ids 元素, meshes 包裝, ids 包裝)；沒有 / 形狀不對回傳 None。"""
    vm, vi = kkex.get(OBJIMP, "meshes"), kkex.get(OBJIMP, "ids")
    if vm is None or vi is None:
        return None
    try:
        km, pm = _container(vm)
        ki, pi = _container(vi)
        ms = arr_split(pm) if pm else []
        ids = arr_split(pi) if pi else []
    except ValueError:
        return None
    if len(ms) != len(ids):
        return None
    return ms, ids, km, ki


def objimp_filter(kkex, alive_fn, key_fn=None):
    """OBJImport：物件被刪的整筆（網格 + id）拿掉，其餘 id 照 key_fn 重編號。回傳拿掉幾筆。"""
    lst = _objimp_lists(kkex)
    if lst is None:
        return 0
    ms, ids, km, ki = lst
    out_m, out_i, dropped = [], [], 0
    for m, e in zip(ms, ids):
        v = unpack(e)
        if isinstance(v, int) and not alive_fn(v):
            dropped += 1
            continue
        out_m.append(m)
        out_i.append(pack(key_fn(v)) if (key_fn and isinstance(v, int)) else e)
    if dropped or key_fn:
        kkex.set(OBJIMP, "meshes", _rewrap(km, arr_build(out_m)))
        kkex.set(OBJIMP, "ids", _rewrap(ki, arr_build(out_i)))
    return dropped


def merge_objimp(da, db, key_fn, warn):
    """第二張的 OBJImport 網格併進底卡，id 照 dicKey 位移。"""
    lb = _objimp_lists(db)
    if lb is None:
        if db.get(OBJIMP, "ids") is not None:
            warn.append("OBJImport 第二張的 meshes / ids 形狀對不上，未合併")
        return 0
    msb, idsb, _kmb, _kib = lb
    if not idsb:
        return 0
    if not da.has(OBJIMP) or da.get(OBJIMP, "ids") is None:
        if da.has(OBJIMP):
            warn.append("OBJImport 底卡的資料區缺 ids，未合併第二張的匯入網格")
            return 0
        da.add_entry(OBJIMP, db.raw_entry(OBJIMP))
        objimp_filter(da, lambda v: True, key_fn)
        return len(idsb)
    la = _objimp_lists(da)
    if la is None:
        warn.append("OBJImport 底卡的 meshes / ids 形狀對不上，未合併")
        return 0
    msa, idsa, kma, kia = la
    new_ids = []
    for e in idsb:
        v = unpack(e)
        new_ids.append(pack(key_fn(v)) if isinstance(v, int) and v >= 0 else e)
    da.set(OBJIMP, "meshes", _rewrap(kma, arr_build(msa + msb)))
    da.set(OBJIMP, "ids", _rewrap(kia, arr_build(idsa + new_ids)))
    return len(idsb)


# ================================================================ 貼圖字典
def texture_index(raw_inner):
    """TextureDictionary 的 bin 內容 -> [(key, value_bytes)]（不解碼貼圖本體）。"""
    if not raw_inner:
        return []
    return [(k, vb) for k, _, vb in map_split(raw_inner)]


def merge_textures(dst, src, warn):
    """按內容去重合併貼圖字典，回傳 src 的 TexID -> dst 的 TexID。

    實卡驗證過 Studio 就是這樣做的：合併後的貼圖集合 == 兩邊的 sha1 聯集。
    """
    a = _inner(dst, ME, "TextureDictionary")
    b = _inner(src, ME, "TextureDictionary")
    if not b:
        return {}
    try:
        a_items = [list(t) for t in (map_split(a) if a else [])]
        b_items = map_split(b)
    except ValueError:
        warn.append("TextureDictionary 解不開，貼圖未合併")
        return {}
    by_hash = {}
    for k, kb, vb in a_items:
        by_hash.setdefault(hashlib.sha1(vb).hexdigest(), k)
    nxt = (max((k for k, _, _ in a_items), default=0) + 1)
    texmap, added = {}, 0
    for k, kb, vb in b_items:
        h = hashlib.sha1(vb).hexdigest()
        if h in by_hash:
            texmap[k] = by_hash[h]
            continue
        texmap[k] = nxt
        by_hash[h] = nxt
        a_items.append([nxt, pack(nxt), vb])
        nxt += 1
        added += 1
    _set_inner(dst, ME, "TextureDictionary", map_build([tuple(i) for i in a_items]))
    log(f"  貼圖：來源 {len(b_items)} 張，去重後新增 {added} 張，"
        f"共 {len(a_items)} 張")
    return texmap


# ================================================================ 合併
def _roots_of(scene):
    """回傳 (包裝資料夾, (CAM) 根資料夾)，找不到就 None。"""
    wrap = cam = None
    for n in scene.objects.values():
        if n["type"] != 3:
            continue
        nm = n["data"].get("name") or ""
        if nm == "(CAM)":
            if cam is None:
                cam = n
        elif wrap is None:
            wrap = n
    return wrap, cam


RE_CONSTRAINT = r'<constraint\b[^>]*?/>|<constraint\b[^>]*?>.*?</constraint>'


def _park_segment(body, rank, dur):
    """從包裝資料夾的停放軌道反推這一段的起訖時間與原位座標。"""
    if rank is None or not body:
        return 0.0, dur, None
    pat = re.compile(r'<interpolable\b[^>]*?objectIndex="' + str(rank) +
                     r'"[^>]*?id="guideObjectPos"[^>]*?>'
                     r'(?:(?!</?interpolable\b).)*?</interpolable>', re.S)
    m = pat.search(body)
    if not m:
        return 0.0, dur, None
    kfs = []
    for k in RE_KEYFRAME.findall(m.group(0)):
        h = k.split(">")[0]
        mt = re.search(r'time="([-\d.eE+]+)"', h)
        vs = [re.search(r'value%s="([-\d.eE+]+)"' % a, h) for a in "XYZ"]
        if not mt or not all(vs):
            continue
        kfs.append((float(mt.group(1)), tuple(float(v.group(1)) for v in vs)))
    if not kfs:
        return 0.0, dur, None

    def far(v):
        return all(abs(x - kkref.PARK_POS) < 1e-3 for x in v)

    start, end, home = None, dur, None
    for t, v in kfs:
        if not far(v):
            if start is None:
                start, home = t, v
        elif start is not None:
            end = t
            break
    if start is None:
        return 0.0, dur, None
    return start, end, home


def scene_segments(scene, warn=None):
    """一張卡裡有幾段場景。已經合併過的卡會有好幾個包裝資料夾。

    回傳 [{wrap, cam, name, start, end, home}]，時間是這張卡自己的時間軸。
    """
    dur = scene.duration() or 0.0
    wraps, cam_root = [], None
    for n in scene.objects.values():
        if n["type"] != 3:
            continue
        if (n["data"].get("name") or "") == "(CAM)":
            cam_root = cam_root or n
        else:
            wraps.append(n)
    cams = ([n for n, _, _ in S.iter_nodes([cam_root]) if n["type"] == 5]
            if cam_root is not None else [])
    by_name = {}
    for c in cams:
        by_name.setdefault(re.sub(r"\s*CAM$", "", c["data"].get("name") or ""), c)
    xml = scene.timeline_xml()
    body = S.split_root(xml)[1] if xml else ""
    ranks = S.rank_map(S.node_dickeys(scene.objects))
    segs = []
    for w in wraps:
        nm = w["data"].get("name") or ""
        st, en, home = _park_segment(body, ranks.get(w["data"]["dicKey"]), dur)
        c = by_name.get(nm)
        if c is None and len(wraps) == 1 and cams:
            c = cams[0]
        segs.append({"wrap": w["data"]["dicKey"], "name": nm, "home": home,
                     "cam": c["data"]["dicKey"] if c is not None else None,
                     "start": st, "end": en})
    segs.sort(key=lambda x: (x["start"], x["end"]))
    if warn is not None and len(segs) > 1 and any(x["home"] is None for x in segs):
        warn.append("這張卡有好幾個包裝資料夾卻找不到停放軌道，"
                    "段落時間只能用整張卡估，請確認")
    return segs


def strip_handover(scene, warn):
    """把「接棒相機 + 它的 NC + 開關軌道」拆掉 —— 再合併時會重生一台。"""
    vb = scene.kkex.get("nodesConstraints", "constraints")
    if vb is None:
        return 0
    xml = unpack(vb)
    if not isinstance(xml, str) or not xml.strip():
        return 0
    h, body, t = _split_xml_root(xml, "constraints")
    if h is None:
        return 0
    ranks = S.rank_map(S.node_dickeys(scene.objects))
    inv = {v: k for k, v in ranks.items()}
    cam_nodes = {n["data"]["dicKey"]: n for n, _ in scene.cameras()}
    # 接棒相機的特徵：被兩條以上的 NC 綁著，而且那些 NC 的 parent 也都是相機。
    # （作者自己把某台相機綁到骨架上的情況不會符合，才不會誤刪）
    hits = {}
    blocks = []
    for m in re.finditer(RE_CONSTRAINT, body, re.S):
        blk = m.group(0)
        mu = re.search(r'uniqueLoadId="(-?\d+)"', blk)
        mc = re.search(r'childObjectIndex="(-?\d+)"', blk)
        mp = re.search(r'parentObjectIndex="(-?\d+)"', blk)
        uid = int(mu.group(1)) if mu else None
        ck = inv.get(int(mc.group(1))) if mc else None
        pk = inv.get(int(mp.group(1))) if mp else None
        blocks.append((uid, ck, pk))
        if ck in cam_nodes and pk in cam_nodes:
            hits.setdefault(ck, []).append(uid)
    victims = [k for k, v in hits.items() if len(v) >= 2]
    if not victims:
        return 0
    drop_uids = {u for k in victims for u in hits[k] if u is not None}
    keep_uids = [u for u, ck, _ in blocks
                 if u is not None and u not in drop_uids and ck not in victims]

    old_nodes = S.node_dickeys(scene.objects)
    pmap = S.parent_map(scene.objects)
    deleted = []
    for dk in victims:
        node = cam_nodes[dk]
        deleted += S.subtree_dickeys(node)
        S.detach(node, pmap[id(node)][1])
    cleanup_deleted(scene, old_nodes, deleted, warn)

    xml2 = scene.timeline_xml()
    if xml2:
        hd, bd, tl = S.split_root(xml2)
        out, last = [], 0
        for m in S.RE_INTERP.finditer(bd):
            blk = m.group(0)
            hm = re.match(r'<interpolable\b[^>]*?>', blk)
            hh = hm.group(0) if hm else blk
            if 'owner="NodesConstraints"' not in hh:
                continue
            mp = re.search(r'parameter="(-?\d+)"', hh)
            if not mp or int(mp.group(1)) not in drop_uids:
                continue
            out.append(bd[last:m.start()])
            last = m.end()
        out.append(bd[last:])
        bd = "".join(out)
        remap = {u: i for i, u in enumerate(keep_uids)}
        bd = S.remap_nc_parameter(bd, lambda x: remap.get(x, x))
        scene.set_timeline_xml(hd + bd + tl)

    vb2 = scene.kkex.get("nodesConstraints", "constraints")
    if vb2 is not None:
        x = unpack(vb2)
        if isinstance(x, str) and x.strip():
            x2, _ = S.renumber_unique_load_id(x)
            scene.kkex.set("nodesConstraints", "constraints", pack(x2))
    log(f"  這張是合併過的卡：拆掉 {len(victims)} 台接棒相機與它們的 NC，"
        "等一下重新綁")
    return len(victims)


def _merge_into(A, B, off, warn, group_name_a=None, group_name_b=None,
                wrap_a=False):
    """把 B 疊到 A 後面。只做搬運，停放軌道與相機接管留到最後統一處理。

    回傳 B 的包裝資料夾 / (CAM) 根資料夾在合併後的 dicKey。
    """
    if A.version != B.version:
        raise SystemExit(f"版本不同（{A.version} vs {B.version}），"
                         f"先用同一個 studio 各存一次再合併")

    a_nodes = S.node_dickeys(A.objects)
    b_nodes_old = S.node_dickeys(B.objects)
    K = max(S.all_dickeys(A.objects)) + 1
    b_wrap, b_cam = _roots_of(B)
    b_wrap_key = b_wrap["data"]["dicKey"] + K if b_wrap else None
    b_cam_key = b_cam["data"]["dicKey"] + K if b_cam else None

    n_nc_a = _count_constraints(A)
    n_items_a = sum(1 for n, _, _ in S.iter_nodes(A.objects) if n["type"] == 1)

    S.remap_tree_dickeys(B.sc, lambda k: k + K)
    for k, v in B.objects.items():
        A.objects[k] = v

    merged_nodes = S.node_dickeys(A.objects)
    rank_m = S.rank_map(merged_nodes)
    rank_a = S.rank_map(a_nodes)
    rank_b = S.rank_map(b_nodes_old)
    inv_a = {v: k for k, v in rank_a.items()}
    inv_b = {v: k for k, v in rank_b.items()}

    def fa_rank(r):
        k = inv_a.get(r)
        return None if k is None else rank_m.get(k)

    def fb_rank(r):
        k = inv_b.get(r)
        return None if k is None else rank_m.get(k + K)

    def fb_dickey(k):
        return k + K

    log(f"  + {len(b_nodes_old)} 個節點（dicKey +{K}），時間平移 +{S.fmt_time(off)}")

    # ---- timeline ----
    a_xml, b_xml = A.timeline_xml(), B.timeline_xml()
    if a_xml and b_xml:
        ah, ab, at = S.split_root(a_xml)
        bh, bb, bt = S.split_root(b_xml)
        ab = S.remap_object_index(ab, fa_rank)
        bb = S.remap_object_index(bb, fb_rank)
        if n_nc_a:
            bb = S.remap_nc_parameter(bb, lambda p: p + n_nc_a)
        if off:
            bb = S.shift_keyframe_times(bb, off)
        ab, gl_a = S.extract_global_tracks(ab)
        bb, gl_b = S.extract_global_tracks(bb)
        globals_xml = ""
        for tid in kkref.GLOBAL_TIMELINE_IDS:
            ha, ia = gl_a.get(tid, (None, ""))
            hb, ib = gl_b.get(tid, (None, ""))
            if ha is None and hb is None:
                continue
            globals_xml += (ha or hb) + ia + ib + "</interpolable>"
        if wrap_a and group_name_a:
            ab = f'<interpolableGroup name="{xml_escape(group_name_a)}">{ab}</interpolableGroup>'
        if group_name_b:
            bb = f'<interpolableGroup name="{xml_escape(group_name_b)}">{bb}</interpolableGroup>'
        A.set_timeline_xml(ah + globals_xml + ab + bb + at)
    elif b_xml:
        warn.append("底卡沒有 timeline，直接用第二張的")

    # ---- KKEx ----
    da, db = A.kkex, B.kkex
    texmap = ({} if _skipped("textures") and _skip_note("textures", warn) is not None
              else merge_textures(da, db, warn))
    counts = {}
    counts["nodesConstraints"] = (
        _skip_note("nodesConstraints", warn) if _skipped("nodesConstraints") else
        merge_xml(
            da, db, "nodesConstraints", "constraints", "constraints",
            lambda x: remap_xml_attr(x, ["parentObjectIndex", "childObjectIndex"],
                                     fb_rank),
            warn))
    vb = da.get("nodesConstraints", "constraints")
    if vb is not None:
        x = unpack(vb)
        if isinstance(x, str) and x.strip():
            x2, n_nc = S.renumber_unique_load_id(x)
            da.set("nodesConstraints", "constraints", pack(x2))
    counts["rendererEditor"] = (
        _skip_note("rendererEditor", warn) if _skipped("rendererEditor") else
        merge_rended(da, db, fb_rank, warn))
    counts["OBJImport"] = (
        _skip_note("objimport", warn) if _skipped("objimport") else
        merge_objimp(da, db, fb_dickey, warn))
    counts["kkpe"] = (
        _skip_note("kkpe", warn) if _skipped("kkpe") else
        merge_xml(da, db, "kkpe", "sceneInfo", "root",
                  lambda x: remap_kkpe_index(x, fb_dickey), warn))
    counts["treenodenaming"] = (
        _skip_note("treenodenaming", warn) if _skipped("treenodenaming") else
        merge_bin_map(da, db, TNN, "names", fb_dickey, warn))
    def _remap_tex_anim(v):
        """TexAnimationDef 的每一格貼圖編號也要重編號。

        形狀：{"framePerSecond": N, "frames": [[貼圖編號, 起點毫秒, 長度毫秒], …]}
        """
        if not isinstance(v, dict):
            return None
        fr = v.get("frames")
        if not isinstance(fr, (list, tuple)):
            return None
        out, hit = [], False
        for it in fr:
            if isinstance(it, (list, tuple)) and it and isinstance(it[0], int) \
                    and not isinstance(it[0], bool) and it[0] >= 0:
                nt = texmap.get(it[0], it[0])
                if nt != it[0]:
                    hit = True
                out.append([nt] + list(it[1:]))
            else:
                out.append(list(it) if isinstance(it, (list, tuple)) else it)
        if not hit:
            return None
        nv = dict(v)
        nv["frames"] = out
        return nv

    n_me = 0
    n_anim = 0
    for key in ([] if _skipped("MaterialEditor") else kkref.ME_LISTS):
        remaps = {"ID": fb_dickey}
        deep = None
        if key == "MaterialTexturePropertyList":
            remaps["TexID"] = lambda t: texmap.get(t, t)
            deep = {"TexAnimationDef": _remap_tex_anim}
        n_me += merge_bin_list(da, db, ME, key, remaps, warn, deep=deep)
    if _skipped("MaterialEditor"):
        _skip_note("MaterialEditor", warn)
    counts["MaterialEditor 條目"] = n_me
    counts["UAR"] = (
        _skip_note("UAR", warn) if _skipped("UAR") else
        merge_bin_list(da, db, UAR, "itemInfo",
                       {"SceneDicKey": fb_dickey,
                        "SceneObjectOrder": lambda o: o + n_items_a}, warn))
    counts["LightSettings"] = (
        _skip_note("LightSettings", warn) if _skipped("LightSettings") else
        merge_bin_list(da, db, "LightSettingsData", "LightSettingsData_lights",
                       {"ObjectId": fb_dickey}, warn))
    counts["itemlayeredit"] = (
        _skip_note("itemlayeredit", warn) if _skipped("itemlayeredit") else
        merge_bin_list(da, db, "keelhauled.itemlayeredit", "SavedLayers",
                       {"ObjectId": fb_dickey}, warn))
    counts["savecameraobjectfov"] = (
        _skip_note("savecameraobjectfov", warn) if _skipped("savecameraobjectfov") else
        merge_bin_map(da, db, "com.rikkibalboa.bepinex.savecameraobjectfov", "cameras",
                      fb_dickey, warn))
    n_rs = 0
    if _skipped("RSkoi_ComponentUtil"):
        _skip_note("RSkoi_ComponentUtil", warn)
    elif db.has("RSkoi_ComponentUtil"):
        for k, _, _ in (db.payload_items("RSkoi_ComponentUtil") or []):
            n_rs += merge_bin_map(da, db, "RSkoi_ComponentUtil", k, fb_dickey, warn)
    counts["RSkoi_ComponentUtil"] = n_rs
    for g in db.guids():
        if not da.has(g):
            da.add_entry(g, db.raw_entry(g))
            warn.append(f"外掛 {g} 只有這張卡有，整包搬過去但參照沒重編號")
    if db.has("com.shallty.shalltyutils") and da.has("com.shallty.shalltyutils"):
        warn.append("com.shallty.shalltyutils 的 guideObjectPickerData 座標系不明，保留底卡的")
    log("    " + "、".join(f"{k}+{v}" for k, v in counts.items() if v))
    return {"wrap": b_wrap_key, "cam": b_cam_key, "shift": K}


def _count_constraints(scene):
    vb = scene.kkex.get("nodesConstraints", "constraints")
    if vb is None:
        return 0
    x = unpack(vb)
    return x.count("<constraint ") if isinstance(x, str) else 0


def _to_scene(src):
    if isinstance(src, S.Scene):
        return src
    if callable(src):
        return src()
    return S.Scene(src)


def _src_label(src, i):
    if isinstance(src, str):
        return os.path.basename(src)
    return getattr(src, "label", None) or f"第 {i} 張"


def clean_broken_timeline(scene, label, warn, known=True, mismatch=False):
    """把「在這個裝機上一定會丟 InvalidCastException」的 timeline 軌道拿掉。

    為什麼要在合併裡做，而不是留給人手動跑 kktl_scan
    ==================================================
    Timeline 載軌道時是拿 (owner, id) 去自己的登記表找 model，各版遊戲登記的
    型別不一樣。KKS 做的卡拿到 KK 播，`tears` / `blush` / `femaleNipples` /
    `itemColor1~3` / `itemAlpha` / `charClothes` 這幾個 id 會在
    `InterpolateBefore` 裡轉型失敗 —— 從第 0 秒、每一幀都丟。

    而例外會讓 Timeline 的整棵 `Recurse` 中斷，所以**排在它後面的軌道
    那一幀通通不會被套用**：停放、資料夾啟用、相機接管全部失效。
    一條壞軌道就足以讓整張合併卡的 timeline 看起來完全沒作用。

    這件事本來是獨立的一步（kktl_scan --drop-known），但它有個結構性的問題：
    **只要有一張輸入卡漏掉，合出來的卡就照炸不誤**，而且錯誤訊息只說得出是哪個
    method、第幾個 lambda，說不出是哪一張卡帶進來的。實際上就發生過 ——
    六張原卡只修了一張，合併後照樣整條 timeline 失效。

    所以改成合併時每張輸入卡都過一次。這裡做才對，不是放在 prep：
    已經整理過的卡（根節點有 (CAM)）會跳過 prep 直接拿來用，放在 prep
    就正好漏掉那一種。

    拿掉會少什麼
    ============
    少掉那幾條軌道原本的效果（道具淡出、換衣服…）。但那些軌道在這個裝機上
    從來沒生效過 —— 它們每一幀都在丟例外，值根本沒被套用 ——
    所以實際上不會少看到任何本來看得到的東西。

    mismatch=True 另外再拿掉「objectIndex 指到不對的節點種類、或指不到節點」
    的軌道。那是另一種成因（指錯東西），預設不開：它會因為 KKPE 那類
    「角色和道具都有」的 id 而誤判，而且合併的 RANK 重編號本來就是對的。
    """
    n = 0
    if mismatch:
        n_mis, mis = TL.drop_mismatch(scene)
        if n_mis:
            n += n_mis
            log(f"  [{label}] 拿掉 {n_mis} 條指錯節點的軌道：" + "、".join(
                sorted({f"{o or '(無)'}:{t}" for o, t, _, _, _ in mis})))
    if known:
        ids = TL.noncore_ids(scene, known_only=True)
        if ids:
            n2, removed = TL.drop(scene, [("Timeline", t) for t in ids])
            if n2:
                n += n2
                cnt = {}
                for _, t in removed:
                    cnt[t] = cnt.get(t, 0) + 1
                log(f"  [{label}] 拿掉 {n2} 條跨版本會丟 InvalidCastException "
                    f"的軌道：" + "、".join(f"{t}×{c}" for t, c in sorted(cnt.items())))
                warn.append(f"{label}：拿掉 {n2} 條會讓 timeline 整條失效的軌道（"
                            + "、".join(sorted(cnt)) + "）")
    return n


def merge_many(paths, out_path, gap=0.01, park=True, park_lead=0.0, group=True,
               camera_switch=True, cam_name=None, nested_cams=True,
               enable_tracks=True, post=None, sync_static=True,
               fov_track=True, shader_type=None,
               tl_clean=False, tl_mismatch=True, nc_enable_tracks=False,
               clear_frame=True, enable_all_tracks=True):
    """把一串已經 prep 好的場景卡按順序接成一張。

    第一張是底卡，其餘照順序接在後面。中間的搬運一張一張疊（省記憶體），
    停放軌道、相機接管、總時長留到最後統一處理。

    **一張卡也可以。** 底下接卡的迴圈跑 paths[1:]，只有一張時它自然不會跑，
    後面每一段（相機接管、停放、啟用軌道、資料夾編號、總時長、存檔）
    本來就是逐段處理的，一段照樣走得完。

    為什麼值得讓它走完：整理（包進 <卡名> 主資料夾、相機鏈搬到 (CAM)、
    生 (MAP)(FX)(CHAR)(SFX)）是合併的前半段，跟有幾張卡無關。單張卡也跑一次的話
    所有卡片的形狀就一致 —— 「添加動畫音頻」靠包裝資料夾認場景、F7 靠它分段，
    一張卡的片子不該因為「只有一段」就變成得另外處理的特例。

    一張卡時自然會發生的事：相機接管需要兩台以上的場景相機才有意義，
    build_camera_switch_n 自己會拒絕並記一筆（不是錯誤，只有一段本來就沒得切）；
    停放和啟用軌道照樣會寫，內容就是「從頭到尾都在場上」。
    """
    if not paths:
        raise SystemExit("至少要一張卡")
    warn = []
    log(f"底卡 {_src_label(paths[0], 1)}" if len(paths) > 1 else
        f"只有一張卡 {_src_label(paths[0], 1)} —— 不接卡，只整理成合併後的形狀")
    A = _to_scene(paths[0])
    # shaderType 是「整張卡一個值」的欄位 —— 合併只留得住底卡的。各段原本不同
    # 的話，後面幾段的材質/FX 會整段用底卡那一套著色（頭髮、眼睛最看得出來），
    # 而原卡單獨打開是好的。這裡把各段的值記下來，最後不一致就講出來。
    shaders = [(1, _src_label(paths[0], 1), getattr(A.sc, "shaderType", None))]
    # 每張原卡一載入時的相機縮放 / FOV（原卡沒有這兩條軌道時用這個值）
    cards = [{"off": 0.0, "label": _src_label(paths[0], 1), "seg0": 0,
              "saved": saved_camera_values(A), "mapinfo": saved_map_info(A)}]
    if tl_clean or tl_mismatch:
        clean_broken_timeline(A, _src_label(paths[0], 1), warn,
                              known=tl_clean, mismatch=tl_mismatch)
    strip_handover(A, warn)
    _, cam0 = _roots_of(A)
    segs = scene_segments(A, warn)
    for j, sg in enumerate(segs, 1):
        sg["name"] = sg["name"] or f"場景{j}"
    cam_roots = [cam0["data"]["dicKey"]] if cam0 else []
    card_end = A.duration() or 0.0
    if len(segs) > 1:
        log(f"  底卡裡本來就有 {len(segs)} 段場景："
            + "、".join(f"{x['name']}（{S.fmt_time(x['start'])}~{S.fmt_time(x['end'])}）"
                        for x in segs))

    for i, p in enumerate(paths[1:], 2):
        log(f"接上第 {i} 張 {_src_label(p, i)}")
        B = _to_scene(p)
        shaders.append((i, _src_label(p, i), getattr(B.sc, "shaderType", None)))
        if tl_clean or tl_mismatch:
            clean_broken_timeline(B, _src_label(p, i), warn,
                                  known=tl_clean, mismatch=tl_mismatch)
        strip_handover(B, warn)
        segs_b = scene_segments(B, warn)
        names = [x["name"] for x in segs]
        for j, sg in enumerate(segs_b, 1):
            nm = sg["name"] or f"場景{i}"
            if nm in names:
                nm = f"{nm} {i}" if j == 1 else f"{nm} {i}-{j}"
            sg["name"] = nm
            names.append(nm)
        if len(segs_b) > 1:
            log(f"  這張裡面有 {len(segs_b)} 段場景："
                + "、".join(x["name"] for x in segs_b))
        b_dur = B.duration() or 0.0
        off = card_end + gap
        cards.append({"off": off, "label": _src_label(p, i), "seg0": len(segs),
                      "saved": saved_camera_values(B), "mapinfo": saved_map_info(B)})
        one_a, one_b = len(segs) == 1, len(segs_b) == 1
        r = _merge_into(A, B, off, warn,
                        group_name_a=(segs[0]["name"] if (i == 2 and one_a) else None),
                        group_name_b=(segs_b[0]["name"] if (group and one_b) else None),
                        wrap_a=(i == 2 and group and one_a))
        K = r["shift"]
        cards[-1]["shift"] = K
        for sg in segs_b:
            segs.append({"wrap": sg["wrap"] + K, "name": sg["name"],
                         "home": sg["home"],
                         "cam": None if sg["cam"] is None else sg["cam"] + K,
                         "start": sg["start"] + off, "end": sg["end"] + off})
        if r["cam"] is not None:
            cam_roots.append(r["cam"])
        card_end = off + b_dur
        del B
    total = card_end
    starts = [x["start"] for x in segs]
    ends = [x["end"] for x in segs]
    wraps = [x["wrap"] for x in segs]
    names = [x["name"] for x in segs]
    log(f"  總時長 {S.fmt_time(total)}（{len(segs)} 段場景）")

    by_key = {n["data"]["dicKey"]: n for n, _, _ in S.iter_nodes(A.objects)}

    # ---- 相機接管 ----
    if camera_switch:
        pre_cs_nodes = S.node_dickeys(A.objects)
        cam = build_camera_switch_n(A, cam_roots,
                                    [x["cam"] for x in segs if x["cam"] is not None],
                                    cam_name, warn, nested=nested_cams)
        if cam:
            # 多餘的 (CAM) 根資料夾被刪掉了 -> 整個 RANK 空間往前縮，
            # 之前搬進來的 timeline / NC / kkpe 參照全部要跟著重編號。
            if cam.get("deleted"):
                stat = cleanup_deleted(A, pre_cs_nodes, cam["deleted"], warn)
                log(f"  收掉 {len(cam['deleted'])} 個空的 (CAM) 根資料夾，"
                    f"RANK 重編號" + (
                        "（" + "、".join(f"{k}{v:+d}" for k, v in stat.items() if v) + "）"
                        if any(stat.values()) else ""))
            ranks = S.rank_map(S.node_dickeys(A.objects))
            n_nc = _count_constraints(A)
            vb = A.kkex.get("nodesConstraints", "constraints")
            x = unpack(vb) if vb is not None else "<constraints version=\"1.6.3\" />"
            h, body, t = _split_xml_root(x, "constraints")
            tracks = ""
            for i, ck in enumerate(cam["cams"]):
                body += NC_TEMPLATE.format(
                    enabled="true" if i == 0 else "false",
                    parent=ranks[ck], child=ranks[cam["new"]], uid=n_nc + i,
                    alias=xml_escape(f'{by_key[ck]["data"].get("name") or names[i]} | POV'))
                # 每個交界時間點，全部的 NC 都下一個關鍵影格：
                # 該上場的那條 true，其餘同一時間全部 false。
                pts = [(0.0, i == 0)]
                for j in range(1, len(cam["cams"])):
                    pts.append((starts[j], i == j))
                tracks += nc_enabled_track(n_nc + i, pts)
            x2, _ = S.renumber_unique_load_id(h + body + t)
            A.kkex.set("nodesConstraints", "constraints", pack(x2))
            xml = A.timeline_xml()
            if xml:
                head, b2, tail = S.split_root(xml)
                A.set_timeline_xml(head + b2 + tracks + tail)
            log(f"  相機切換：{len(cam['cams'])} 條 NC，交界 "
                + "、".join(S.fmt_time(s) for s in starts[1:]) + " 秒接棒")

    n_vis = hide_cameras(A)
    if n_vis:
        log(f"  取消 {n_vis} 台相機的樹狀圖打勾")

    # 使用中的相機：有接管相機就用它，否則用第一段選的那台（單張卡就是整理時選的相機）。
    # 以前單張卡、或原卡裡那台相機本來就沒在使用的，合併完也是沒使用 —— 載入後是自由視角。
    _pref = None
    try:
        _pref = cam["new"] if (camera_switch and cam) else None
    except NameError:
        _pref = None
    act = ensure_active_camera(A, _pref, [x["cam"] for x in segs if x.get("cam") is not None])
    if act:
        log(f"  使用中的相機：{act}")

    # 工具自己寫過軌道的節點，存檔前只同步這些。
    # 一定要在「外太空停放」之前就宣告 —— 停放區塊是第一個往裡面加東西的，
    # 定義晚一步整個 merge 就會 UnboundLocalError。
    sync_dk = set()

    # ---- 外太空停放 ----
    ranks = S.rank_map(S.node_dickeys(A.objects))
    by_key = {n["data"]["dicKey"]: n for n, _, _ in S.iter_nodes(A.objects)}
    if park:
        xml = A.timeline_xml()
        if xml:
            head, body, tail = S.split_root(xml)
            for i, dk in enumerate(wraps):
                if dk is None or dk not in ranks:
                    warn.append(f"第 {i + 1} 張找不到包裝資料夾，沒有停放軌道")
                    continue
                home = segs[i].get("home")
                if home is None:
                    pos = by_key[dk]["data"]["position"]
                    home = (pos["x"], pos["y"], pos["z"])
                far = (kkref.PARK_POS,) * 3
                pts = []
                # 回到原位的時間點可以比「輪到自己」早一點。停放是瞬移
                # （階梯曲線，100,100,100 -> 原位，一格之內跳約 173 單位），
                # DynamicBone 那種靠世界座標位移算慣性的揺れ物會被這一下甩出去，
                # 頭髮看起來就像被什麼吸著、回不到原本的形狀。
                # 提早歸位的那段時間它還沒輪到、畫面上看不到，正好拿來把
                # 揺れ物晃完 —— 鏡頭切過來時已經靜下來了。
                back = starts[i]
                if park_lead > 0 and starts[i] > 0:
                    back = max(0.0, starts[i] - float(park_lead))
                if starts[i] > 0 and back > 0:
                    pts.append((0.0, far))
                    pts.append((back, home))
                else:
                    pts.append((0.0, home))
                if ends[i] < total:
                    pts.append((ends[i], far))
                body, _ = S.remove_track(body, ranks[dk], "guideObjectPos")
                body += S.park_track(ranks[dk], pts)
                sync_dk.add(dk)
            A.set_timeline_xml(head + body + tail)
            log(f"  外太空停放軌道：{len(wraps)} 個包裝資料夾各一條"
                + (f"（提早 {park_lead:g} 秒歸位，讓揺れ物先晃完）"
                   if park_lead > 0 else ""))

    if enable_tracks:
        xml = A.timeline_xml()
        if xml:
            head, body, tail = S.split_root(xml)
            n_en = 0
            for i, dk in enumerate(wraps):
                if dk is None or dk not in ranks:
                    continue
                # (SFX) 一定要在 0 秒 false，不然一載入卡片音頻就被觸發。
                #
                # 但**包裝資料夾本身不用**：第一段在 0 秒就該看得見。
                # 以前這裡兩者共用一組關鍵影格，第一段的主資料夾也變成
                # 「0 秒關、0.001 秒開」—— 載入畫面是空的，而且存檔時的
                # 靜態狀態（節點的 visible）跟 0 秒對不起來，看起來就像
                # 「啟用軌道沒寫進去」。
                on = starts[i] if starts[i] > 0 else 0.0
                en_wrap = [(on, True)]
                if on > 0:
                    en_wrap.insert(0, (0.0, False))
                en_sfx = [(0.0, False), (on if on > 0 else ENABLE_LEAD, True)]
                if ends[i] < total:
                    en_wrap.append((ends[i], False))
                    en_sfx.append((ends[i], False))
                targets = [(dk, en_wrap)]
                for c in (by_key[dk]["data"].get("child") or []):
                    nm = str(c["data"].get("name") or "")
                    if nm.startswith("(SFX)") and c["data"]["dicKey"] in ranks:
                        targets.append((c["data"]["dicKey"], en_sfx))
                for t, pts in targets:
                    body, _ = S.remove_track(body, ranks[t], "objectEnabled")
                    body += object_enabled_track(ranks[t], pts)
                    sync_dk.add(t)
                    n_en += 1
            A.set_timeline_xml(head + body + tail)
            log(f"  物件啟用軌道：{n_en} 條（每個場景的主資料夾與 (SFX)，"
                "沒輪到的時候取消勾選）")

    # ---- 作者自己的 NC 也寫啟用軌道 ----
    #
    # 手動流程裡那顆「盲狙收割」按鈕做的事：把每一條 NC 都加進 timeline，
    # 0 秒依它當下的真實狀態、場景結束時全部關掉。這支工具一直只搬了
    # 改名那一半（nc_rename），收割這一半沒有移植 —— nc_enabled_track()
    # 本來只有相機接棒那幾條 NC 在用。
    #
    # 多段的版本：每條 NC 對到自己那一段
    #     0 秒            false（還沒輪到它）
    #     自己那段開頭     它原本的 enabled
    #     自己那段結尾     false
    #
    # 已經有 constraintEnabled 軌道的 NC 一律跳過 —— 那包含合併自己生的
    # 接棒相機 NC，也包含作者本來就在 timeline 裡手動開關的那幾條。
    # 蓋過去會把人家原本的演出壓掉。
    if nc_enable_tracks:
        xml = A.timeline_xml()
        cvb = A.kkex.get("nodesConstraints", "constraints")
        if xml and cvb is not None:
            seg_of = {}                       # RANK -> 第幾段
            for i, dk in enumerate(wraps):
                if dk is None or dk not in by_key:
                    continue
                for k in S.subtree_dickeys(by_key[dk]):
                    if k in ranks:
                        seg_of[ranks[k]] = i

            head, body, tail = S.split_root(xml)
            taken = {m.group(1) for m in RE_NC_PARAM.finditer(body)}

            n_nc_tr = n_skip_have = n_skip_nohome = 0
            per_seg = {}
            for m in re.finditer(RE_CONSTRAINT, unpack(cvb), re.S):
                blk = m.group(0)
                uid = _attr(blk, "uniqueLoadId")
                if uid is None or uid in taken:
                    n_skip_have += 1
                    continue
                child = _attr_int(blk, "childObjectIndex")
                parent = _attr_int(blk, "parentObjectIndex")
                seg = seg_of.get(child, seg_of.get(parent))
                if seg is None:
                    n_skip_nohome += 1
                    continue
                was_on = (_attr(blk, "enabled") or "").lower() == "true"
                pts = []
                if starts[seg] > 0:
                    pts.append((0.0, False))
                pts.append((starts[seg], was_on))
                if ends[seg] < total:
                    pts.append((ends[seg], False))
                per_seg[seg] = per_seg.get(seg, "") + nc_enabled_track(uid, pts)
                n_nc_tr += 1

            # 軌道要放進該段自己的 timeline 群組裡，不是丟在最外層。
            # 群組是 _merge_into 在接卡的時候包的，名字就是那一段的名稱。
            n_loose = 0
            for seg, tr in sorted(per_seg.items()):
                nb = insert_into_group(body, segs[seg]["name"], tr) if group else None
                if nb is None:
                    body += tr                # 沒群組（--no-group）或找不到 -> 放最外層
                    n_loose += 1
                else:
                    body = nb
            if per_seg:
                A.set_timeline_xml(head + body + tail)
            log(f"  NC 啟用軌道：{n_nc_tr} 條"
                + (f"，分到 {len(per_seg)} 個段落群組" if per_seg else "")
                + (f"（其中 {n_loose} 段找不到群組，放在最外層）" if n_loose else "")
                + (f"（跳過 {n_skip_have} 條已經有軌道的）" if n_skip_have else "")
                + (f"（{n_skip_nohome} 條找不到屬於哪一段，沒動）"
                   if n_skip_nohome else ""))

    # ---- 相機鏈「輪到自己之前」也要待在原點 ----
    #
    # 收尾的歸零在 prep 階段就做了（zero_camera_chain），但那只管自己那段之後。
    # 平移之後每條鏈的第一格在自己那段的開頭，而 timeline 在第一格之前是維持
    # 第一格的值 —— 所以 0 秒到自己開場為止，整條鏈都掛著起始位移。
    # 鏈是巢狀的，這個位移會疊到所有更深的鏈和接管相機上。
    if camera_switch:
        pmap2 = S.parent_map(A.objects)
        by_key2 = {nn["data"]["dicKey"]: nn for nn, _, _ in S.iter_nodes(A.objects)}
        claimed, chains = set(), []
        for i, sg in enumerate(segs):
            c = by_key2.get(sg["cam"]) if sg["cam"] is not None else None
            if c is None:
                continue
            # 由外而內，跳過已經算給前面段落的（同一個資料夾是好幾台相機的祖先）
            chain = [fo for fo in S.ancestors(c, pmap2)][::-1]
            chain = [fo for fo in chain
                     if (fo["data"].get("name") or "") != "(CAM)" and id(fo) not in claimed]
            for fo in chain:
                claimed.add(id(fo))
            if chain:
                chains.append((i, chain))
                for fo in chain:
                    sync_dk.add(fo["data"]["dicKey"])
        n_lead = lead_camera_chain_zero(A, chains, warn, starts=starts)
        if n_lead:
            log(f"  相機鏈前置歸零：{n_lead} 條軌道補上「輪到自己之前待在原點」"
                f"（{len(chains)} 段的鏈）")

    n_ren = renumber_scene_folders(A, segs)
    if n_ren:
        log(f"  資料夾重新編號：{n_ren} 個（(MAP)(FX)(CHAR)(SFX) 與 POV 照最後的播放順序）")

    # ---- 總時長 ----
    xml = A.timeline_xml()
    if xml:
        head, body, tail = S.split_root(xml)
        A.set_timeline_xml(S.set_root_attr(head, "duration", S.fmt_time(total))
                           + body + tail)

    # ---- 場景層級的 shaderType ----
    #
    # 這是「整張卡一個值」的欄位，合併只留得住底卡的。各段原本不同的話
    # （例如第 4、5 張是 1、其餘是 0），後面幾段的 FX / 材質就會用錯一套
    # 著色。沒有辦法隨段落切換，只能整張挑一個，所以做成選填。
    vals = {v for _i, _n, v in shaders if v is not None}
    if len(vals) > 1 and shader_type is None:
        groups = {}
        for i, name, v in shaders:
            groups.setdefault(v, []).append("第 %d 段 %s" % (i, name))
        base_v = shaders[0][2]
        warn.append(
            "各段的 shaderType 不一樣（"
            + "；".join("%s：%s" % (k, "、".join(g)) for k, g in sorted(
                groups.items(), key=lambda kv: (kv[0] is None, kv[0])))
            + "）。整張卡只留得住一個值，現在用的是底卡的 %s —— "
            "值不同的那幾段，材質和 FX 會用錯一套著色（頭髮、眼睛最明顯），"
            "但那幾張卡單獨打開是正常的。要改用另一套就加 --shader-type <值>。"
            % base_v)
        log("  [注意] 各段 shaderType 不一致，整張用底卡的 %s（--shader-type 可改）"
            % base_v)
    if shader_type is not None:
        old_st = getattr(A.sc, "shaderType", None)
        try:
            A.sc.shaderType = int(shader_type)
            log(f"  場景 shaderType：{old_st} -> {int(shader_type)}")
        except Exception as e:                              # noqa: BLE001
            warn.append(f"shaderType 寫不進去：{e}")

    # ---- 相機縮放 / 相機 FOV：每段開頭切成那張原卡的值 ----
    # 放在「啟用相機軌道」和靜態對齊之前；這兩條自己也會強制打勾。
    if fov_track:
        for c in cards:
            sg = segs[c["seg0"]] if c["seg0"] < len(segs) else None
            c["seg_cam"] = sg["cam"] if sg else None
        build_camera_global_tracks(A, cards, total, warn, eps=gap)

    # ---- 外框 ----
    #
    # 外框是 StudioImageEmbed 疊在整個畫面最上層的一張圖（作者錄影時的邊框）。
    # 它是**場景層級**的，所以合併之後會變成底卡那一張框壓在全部三段上面，
    # 而合併卡本來就是要重新運鏡的 —— 框的位置跟新鏡頭對不上。
    #
    # 用兩張只差「有沒有清除外框」的卡對出來的：差異只有
    # com.deathweasel.bepinex.studioimageembed 的 FrameData（有框的是一張
    # 1920x1080 的 PNG，清掉的是 nil）。同一個外掛的 BGData 是背景圖，不動它。
    if clear_frame and A.kkex.has(IMGEMBED):
        cur = A.kkex.get(IMGEMBED, "FrameData")
        if cur is not None and cur != b"\xc0":
            A.kkex.set(IMGEMBED, "FrameData", b"\xc0")     # msgpack nil
            log(f"  外框：已清除（StudioImageEmbed FrameData，原本 "
                f"{format(len(cur), ',')} bytes）")
        else:
            log("  外框：底卡本來就沒有，不用清")

    # ---- 把沒打勾的軌道打開（只限相機和時間流速） ----
    #
    # Timeline 裡每一條軌道前面有個勾，沒勾的那條**整條不作用**。
    # 合併卡是拿來重新運鏡的，所以跟「鏡頭」有關的軌道一定要作用：
    #
    #   * 時間流速（timeScale）
    #   * 相機縮放 / 相機FOV（cameraOZoom / cameraFOV）
    #   * 相機（POV）資料夾上的軌道：(CAM) 資料夾底下的所有節點，
    #     以及各段相機本身和它上面那一串資料夾
    #
    # 實測踩到：Scenecard 的「時間流速」和 POV 1 1 的
    # GO Rotation 在原卡沒打勾，合併卡照抄，那一段的運鏡整個不對。
    #
    # **其他沒打勾的一律保留作者的設定。** 以前是全部打開，結果把作者
    # 故意關掉的也打開了：Charcard 的 MAP 位置、舌頭（TANG）的縮放 ——
    # 舌頭變成 0.326 等比，從臉上穿出來。作者關掉那幾條是有原因的。
    #
    # 只開「有關鍵影格」的 —— 空軌道開了也不會做任何事。
    #
    # 一定要放在靜態對齊**之前**：sync_static_state 會跳過 enabled="false"
    # 的軌道，順序反過來的話剛打開的那幾條就不會被算進載入時的節點值。
    if enable_all_tracks:
        xml = A.timeline_xml()
        if xml:
            # 相機相關節點的 RANK
            nodes_now = list(S.iter_nodes(A.objects))
            by_key3 = {nn["data"]["dicKey"]: nn for nn, _, _ in nodes_now}
            pmap3 = S.parent_map(A.objects)
            ranks3 = S.rank_map(S.node_dickeys(A.objects))
            wrap_set = {dk for dk in wraps if dk is not None}
            cam_dks = set()
            for nn, _, _ in nodes_now:
                chain = S.ancestors(nn, pmap3)
                if any(str(a["data"].get("name") or "").startswith("(CAM)") for a in chain):
                    cam_dks.add(nn["data"]["dicKey"])
            for sg in segs:
                c = by_key3.get(sg["cam"]) if sg.get("cam") is not None else None
                if c is None:
                    continue
                cam_dks.add(c["data"]["dicKey"])
                for a in S.ancestors(c, pmap3):
                    if a["data"]["dicKey"] in wrap_set:
                        break                # 包裝資料夾（整段的根）不算相機
                    cam_dks.add(a["data"]["dicKey"])
            cam_ranks = {str(ranks3[k]) for k in cam_dks if k in ranks3}
            GLOBAL_OK = ("timeScale", "cameraOZoom", "cameraFOV")

            head, body, tail = S.split_root(xml)
            hits, kept = {}, {}
            out, last = [], 0
            for m in S.RE_INTERP.finditer(body):
                blk = m.group(0)
                hm = S.RE_INTERP_HEAD.match(blk)
                if not hm or 'enabled="false"' not in hm.group(0):
                    continue
                if "<keyframe" not in blk:
                    continue            # 空軌道，開了也沒意義
                h = hm.group(0)
                mi = re.search(r'\bid="([^"]*)"', h)
                tid = mi.group(1) if mi else "?"
                mo = re.search(r'\bobjectIndex="(\d+)"', h)
                if mo is None:
                    ok = tid in GLOBAL_OK
                    tag = tid
                else:
                    ok = mo.group(1) in cam_ranks
                    tag = "相機 " + tid
                if not ok:
                    kept[tid] = kept.get(tid, 0) + 1
                    continue
                hits[tag] = hits.get(tag, 0) + 1
                fixed = h.replace('enabled="false"', 'enabled="true"', 1)
                out.append(body[last:m.start()])
                out.append(fixed + blk[hm.end():])
                last = m.end()
            if hits:
                out.append(body[last:])
                A.set_timeline_xml(head + "".join(out) + tail)
                log("  啟用軌道：打開 %d 條原本沒打勾的相機／時間流速軌道（%s）"
                    % (sum(hits.values()),
                       "、".join(f"{k} {v}" for k, v in
                                 sorted(hits.items(), key=lambda x: -x[1]))))
                # 時間流速動到的是整段的播放速度，而配音對應點是按**原本的速度**
                # 量出來的。不擋（這是使用者選的），但一定要講。
                if hits.get("timeScale"):
                    warn.append(
                        f"啟用軌道：打開了 {hits['timeScale']} 條「timeScale」（時間流速）。"
                        "它會改變整段的播放速度 —— 已經量好的 pairs.txt／"
                        "cutscene.json 對應點是按原本的速度量的，配音可能要重量。"
                        "不想要就在設定裡把「啟用相機軌道」關掉重合一次")
            else:
                log("  啟用軌道：相機／時間流速軌道都有打勾，不用動")
            if kept:
                log("  啟用軌道：其他 %d 條沒打勾的保留作者設定（%s）"
                    % (sum(kept.values()),
                       "、".join(f"{k} {v}" for k, v in
                                 sorted(kept.items(), key=lambda x: -x[1]))))

    # ---- 內建地圖：各段不同的話，寫記號讓 F7 播放時切換 ----
    # 放在所有 RANK 相關的步驟之後：記號資料夾用全新的最大 dicKey，
    # 排在 RANK 的最後面，既有的 objectIndex / NC 參照一個都不會位移。
    apply_segment_maps(A, cards, segs, wraps, warn)
    apply_segment_env(A, cards, segs, wraps, warn)

    # ---- 存檔時的靜態狀態對齊 0 秒 ----
    st = sync_static_state(A, only=sync_dk) if sync_static else {}
    if not sync_static:
        log("  靜態狀態對齊 0 秒：這次沒做（--no-sync-static）")
    if any(st.values()):
        log("  靜態狀態對齊 0 秒："
            + "、".join(f"{k} {v} 個" for k, v in st.items() if v))

    # ---- float32 撞在一起的關鍵影格 ----
    # 一定要放在最後：前面每一步都還在動時間（平移、補點、歸零、縮短時長）。
    n_f32, n_tr = dedupe_float32_times(A)
    if n_f32:
        log(f"  關鍵影格去重：{n_tr} 條軌道裡有 {n_f32} 格的時間在遊戲的 float32 "
            f"精度下跟前一格撞在一起，已經拿掉")
        warn.append(f"有 {n_f32} 格關鍵影格因為平移到後段之後、float32 分不出來而被拿掉"
                    f"（不拿掉的話 Timeline 載入時整條軌道會被丟棄）")

    # 存檔前的最後一手（GUI 用它在同一份記憶體裡順便寫音頻，省得再讀一次 1 GB）
    if post is not None:
        try:
            post(A, segs)
        except Exception as e:                              # noqa: BLE001
            warn.append(f"存檔前的後處理失敗：{type(e).__name__}: {e}")

    A.save(out_path)
    size = os.path.getsize(out_path)
    log(f"\n寫出 {out_path}（{size:,} bytes，{len(S.node_dickeys(A.objects))} 個節點）")
    seen = set()
    for w in warn:
        if w not in seen:
            seen.add(w)
            log("[注意] " + w)
    return {"out": out_path, "size": size, "duration": total,
            "warnings": sorted(seen)}


def renumber_scene_folders(scene, segs):
    """合併完成後，把每段場景的 (MAP)(FX)(CHAR)(SFX) 與 POV 資料夾重新編號。

    再合併（拿合併卡當素材）時特別需要：兩張卡裡面都有 (FX) 1，
    要照最後的播放順序重編成 (FX) 1..N。
    """
    by_key = {n["data"]["dicKey"]: n for n, _, _ in S.iter_nodes(scene.objects)}
    n_ren = 0
    for i, sg in enumerate(segs, 1):
        w = by_key.get(sg["wrap"])
        if w is None:
            continue
        for c in (w["data"].get("child") or []):
            nm = c["data"].get("name") or ""
            m = re.match(r"^(\((?:MAP|FX|CHAR|SFX)\))(?: \d+)?$", nm)
            if m and nm != f"{m.group(1)} {i}":
                set_node_name(scene, c, f"{m.group(1)} {i}")
                n_ren += 1
    pmap = S.parent_map(scene.objects)
    seen = set()
    for i, sg in enumerate(segs, 1):
        c = by_key.get(sg["cam"]) if sg["cam"] is not None else None
        if c is None:
            continue
        chain = [f for f in S.ancestors(c, pmap)][::-1]        # 由外而內
        chain = [f for f in chain
                 if (f["data"].get("name") or "") != "(CAM)" and id(f) not in seen]
        for j, f in enumerate(chain, 1):
            seen.add(id(f))
            want = f"POV {j} {i}"
            if (f["data"].get("name") or "") != want:
                set_node_name(scene, f, want)
                n_ren += 1
    return n_ren


def _last_camera(node):
    cs = [n for n, _, _ in S.iter_nodes([node]) if n["type"] == 5]
    return cs[-1] if cs else None


def build_camera_switch_n(scene, cam_roots, cam_keys, cam_name, warn, nested=True):
    """把每張卡的 POV 鏈集中到第一個 (CAM) 底下，並生一台由 NC 接管的新相機。

    cam_roots 是每張輸入卡的 (CAM) 根資料夾；cam_keys 是「每一段場景」的相機
    （已經合併過的卡一張裡面就有好幾台），照播放順序。
    預設照手動流程一層套一層（Studio 的相機鏈只吃這種形狀）。
    """
    by_key = {n["data"]["dicKey"]: n for n, _, _ in S.iter_nodes(scene.objects)}
    roots = [by_key[k] for k in cam_roots if k in by_key]
    cams = [by_key[k] for k in cam_keys if k in by_key]
    if not roots or len(cams) < 2:
        warn.append(f"只找到 {len(roots)} 個 (CAM) 根資料夾、{len(cams)} 台場景相機，"
                    "沒有做相機接管")
        return None

    pmap = S.parent_map(scene.objects)
    base = roots[0]
    lc = _last_camera(base)
    anchor = (pmap[id(lc)][0] if (nested and lc is not None) else base)
    if anchor is None:
        anchor = base
    dropped = []
    for rb in roots[1:]:
        lc_b = _last_camera(rb)
        par_b = pmap[id(lc_b)][0] if lc_b is not None else None
        moved = list(rb["data"]["child"])
        rb["data"]["child"] = []
        anchor["data"]["child"].extend(moved)
        if par_b is rb or par_b is None:
            par_b = anchor
        for k, v in list(scene.objects.items()):
            if v is rb:
                del scene.objects[k]
                dropped.append(rb["data"]["dicKey"])
                break
        if nested:
            anchor = par_b

    name = cam_name
    if not name:
        # 各場景相機都叫 "<場景名> CAM"，先把字尾拿掉再取共同開頭
        bases = [re.sub(r"\s*CAM$", "", c["data"].get("name") or "") for c in cams]
        first = bases[0] or "Camera"
        name = first
        for b in bases[1:]:
            name = _common_name(name, b)
        if len(name) < 3:            # 名字之間沒什麼共同開頭，就用第一台的
            name = first
        name = f"{name} CAM"
        used = {c["data"].get("name") for c in cams}
        k = 2
        while name in used:              # 真的撞到場景相機的名字就加號碼
            name = f"{first} CAM {k}"
            k += 1
    nxt = max(S.all_dickeys(scene.objects)) + 1
    new_cam = {"type": 5, "data": {
        "dicKey": nxt,
        "position": {"x": 0.0, "y": 0.0, "z": 0.0},
        "rotation": {"x": 0.0, "y": 0.0, "z": 0.0},
        "scale": {"x": 1.0, "y": 1.0, "z": 1.0},
        "treeState": 1, "visible": False,
        "name": name, "active": True}}
    anchor["data"]["child"].append(new_cam)
    for c in cams:
        c["data"]["active"] = False
    log(f"  相機接管：{len(cams)} 台場景相機集中到 (CAM) 底下"
        f"（{'一層套一層' if nested else '平行排列'}），新相機 {name!r} dicKey={nxt}")
    return {"cams": [c["data"]["dicKey"] for c in cams], "new": nxt, "name": name,
            "deleted": dropped}



FOV_HEAD = ('<interpolable enabled="true" owner="Timeline" id="cameraFOV"'
            ' bgColorR="1" bgColorG="0.6" bgColorB="0" alias="">')
FOV_KF = ('<keyframe time="{t}" value="{v}">'
          '<curveKeyframe time="0" value="0" inTangent="0" outTangent="1" />'
          '<curveKeyframe time="1" value="1" inTangent="1" outTangent="0" />'
          '</keyframe>')


def camfov_table(scene):
    """{相機 dicKey: FOV}，讀 SaveCameraObjectFOV 的 cameras 表。"""
    out = {}
    kx = scene.kkex
    if not kx.has(CAMFOV):
        return out
    vb = kx.get(CAMFOV, "cameras")
    if vb is None:
        return out
    try:
        _kind, payload = _container(vb)
        for k, _t, v in (map_split(payload) if payload else []):
            fv = unpack(v)
            if isinstance(fv, (int, float)) and not isinstance(fv, bool):
                out[k] = float(fv)
    except ValueError:
        pass
    return out


# ---- 相機縮放 / 相機 FOV：每段開頭切成那一段自己的值 ----
#
# 這兩條是**全域**軌道（owner="Timeline"、沒有 objectIndex），整張卡共用
# 一條。從實卡量到的 id 與格式（Scenecard 系列）：
#     cameraOZoom   相机缩放   = 自由相機的 distance.z
#     cameraFOV     相机FOV    = 相機視野
# 作者用的是「階梯」曲線（第二個 curveKeyframe inTangent="INF"）：
# 這一格的值一直維持到下一格才瞬間跳過去，不會慢慢變。
#
# 規則（使用者定的）：
#   * 每段開頭放一格，值 = 那張原卡一載入時的值；下一段開頭瞬間切換。
#   * 原卡自己就有這條軌道的點 → **一格都不動**（有些作者自己設了運鏡縮放），
#     只在「它第一格比段落開頭晚」時，在段落開頭補一格同值，
#     不然段落開頭到它第一格之間會被上一段的值拉著。
#   * 段內最後一格如果不是階梯，會慢慢滑向下一段的值 → 在下一段開頭前
#     補一格同值的階梯格擋住。
#   * 軌道原本沒打勾的，一律打開。
#
# 原卡沒有這條軌道時，值從哪裡來（依序）：
#   縮放：卡片存檔當下的自由相機 distance.z
#   FOV ：那一段相機物件的 SaveCameraObjectFOV → 卡片存檔當下的 fieldOfView

CAM_GLOBAL_IDS = ("cameraOZoom", "cameraFOV")
CAM_GLOBAL_HEAD = {
    "cameraOZoom": ('<interpolable enabled="true" owner="Timeline" id="cameraOZoom"'
                    ' bgColorR="1" bgColorG="1" bgColorB="1" alias="">'),
    "cameraFOV": ('<interpolable enabled="true" owner="Timeline" id="cameraFOV"'
                  ' bgColorR="1" bgColorG="1" bgColorB="1" alias="">'),
}
CAM_GLOBAL_NAME = {"cameraOZoom": "相機縮放", "cameraFOV": "相機FOV"}
STEP_KF = ('<keyframe time="{t}" value="{v}">'
           '<curveKeyframe time="0" value="0" inTangent="0" outTangent="0" />'
           '<curveKeyframe time="1" value="1" inTangent="INF" outTangent="0" />'
           '</keyframe>')


def _fmt_val(v):
    s = ("%.8f" % float(v)).rstrip("0").rstrip(".")
    return s if s not in ("", "-0") else "0"


def saved_camera_values(scene):
    """卡片存檔當下的自由相機 -> {"cameraOZoom": distance.z, "cameraFOV": fieldOfView}。"""
    out = {"cameraOZoom": None, "cameraFOV": None}
    cd = getattr(scene.sc, "cameraSaveData", None) or {}
    try:
        d = cd.get("distance") or {}
        if d.get("z") is not None:
            out["cameraOZoom"] = float(d["z"])
    except Exception:                                       # noqa: BLE001
        pass
    try:
        f = cd.get("fieldOfView")
        if isinstance(f, (int, float)) and not isinstance(f, bool) and f:
            out["cameraFOV"] = float(f)
    except Exception:                                       # noqa: BLE001
        pass
    return out


# ---- 內建地圖 ----
#
# 內建地圖（左邊「地圖」清單選的那個）是**整張卡一個**的設定：
#   sceneInfo.map（編號，-1 = 沒選）、caMap（地圖的位置/旋轉）、
#   sunLightType、mapOption
# 合併只留得住一份，Timeline 也沒有能切換地圖的軌道。
#
# 做法：合併卡用「第一個有地圖的段落」那一份；每段的包裝資料夾底下放一個
# 記號資料夾 [MAPINFO] map=<編號> sun=<n> opt=<0/1>，位置/旋轉 = 那一段的 caMap。
# F7 播放時看現在是哪一段，照記號把地圖藏起來、顯示、或換成別張。
# 記號是普通的資料夾，在 Studio 裡重新存檔也不會掉。
MAPINFO_PREFIX = "[MAPINFO]"


def saved_map_info(scene):
    sc = scene.sc
    ca = getattr(sc, "caMap", None) or {}

    def v3(d):
        d = d or {}
        return (float(d.get("x", 0.0)), float(d.get("y", 0.0)), float(d.get("z", 0.0)))
    try:
        mp = int(getattr(sc, "map", -1))
    except Exception:                                       # noqa: BLE001
        mp = -1
    has_marker = any(str(n["data"].get("name") or "").startswith(MAPINFO_PREFIX)
                     for n, _, _ in S.iter_nodes(scene.objects))
    # 模組地圖（sideloader）的身分證：UAR 的 mapInfoGUID / mapInfoName…
    # sceneInfo.map 存的是模組自己的編號，載入時 UAR 靠這幾個鍵把它換成本機的編號。
    # 少了它們，編號對不回去，地圖清單看起來有選、實際上根本沒載入。
    uar = [(k, vb) for k, _kb, vb in (scene.kkex.payload_items(UAR) or [])
           if str(k).startswith("mapInfo")]
    env = {}
    for k in ENV_FIELDS:
        try:
            env[k] = repr(getattr(sc, k, None))
        except Exception:                                   # noqa: BLE001
            env[k] = None
    has_env = any(str(n["data"].get("name") or "").startswith(ENV_PREFIX)
                  for n, _, _ in S.iter_nodes(scene.objects))
    ramp_mod = any(str(k).startswith("rampInfo")
                   for k, _kb, _vb in (scene.kkex.payload_items(UAR) or []))
    # 模組的地圖 / 調色（ACE）/ Ramp：卡裡存的是模組自己的編號 + GUID，
    # F7 拿這兩個去問 UAR 本機編號是多少，不必依賴「卡片本身載入的那一張」。
    envtok = _env_tokens(sc)
    g_ace = _uar_str(scene, "filterInfoGUID")
    g_ramp = _uar_str(scene, "rampInfoGUID")
    if g_ace and "aceNo" in envtok:
        envtok["aceNo.g"] = _enc(g_ace)
    if g_ramp and "rampG" in envtok:
        envtok["rampG.g"] = _enc(g_ramp)
    return {"map": mp, "pos": v3(ca.get("pos")), "rot": v3(ca.get("rot")),
            "sun": int(getattr(sc, "sunLightType", 0) or 0),
            "opt": bool(getattr(sc, "mapOption", True)),
            "guid": _uar_str(scene, "mapInfoGUID") if mp >= 0 else None,
            "has_marker": has_marker, "uar": uar, "env": env,
            "envtok": envtok, "has_env": has_env, "ramp_mod": ramp_mod}


def _uar_str(scene, key):
    """UAR 解析資料裡的一個字串（mapInfoGUID / filterInfoGUID / rampInfoGUID…）。沒有 → None。"""
    try:
        vb = scene.kkex.get(UAR, key)
        if vb is None:
            return None
        v = unpack(vb)
        if isinstance(v, (bytes, bytearray)):
            try:
                v = unpack(bytes(v))
            except Exception:                               # noqa: BLE001
                v = bytes(v).decode("utf-8", "replace")
        if isinstance(v, (bytes, bytearray)):
            v = bytes(v).decode("utf-8", "replace")
        v = str(v).strip() if v is not None else ""
        return v or None
    except Exception:                                       # noqa: BLE001
        return None


def _enc(s):
    """資料夾名稱裡用空白分隔欄位，GUID 裡的空白 / % / = 要跳脫（F7 用 Uri.UnescapeDataString 還原）。"""
    out = []
    for ch in str(s):
        if ch == "%" or ch == "=" or ch.isspace():
            out.append("".join("%%%02X" % b for b in ch.encode("utf-8")))
        else:
            out.append(ch)
    return "".join(out)


# 場景層級的畫面效果（「系統 → 畫面效果」「角色燈」那些）。跟地圖一樣整張卡一份，
# 合併卡只留得住底卡的；各段原本不同的話，後面幾段的光影、調色會跟原卡不一樣。
ENV_FIELDS = {
    "aceNo": "調色（ACE）", "aceBlend": "調色強度",
    "enableBloom": "Bloom", "bloomIntensity": "Bloom 強度", "bloomThreshold": "Bloom 閾值",
    "bloomBlur": "Bloom 模糊",
    "enableAOE": "AO", "aoeColor": "AO 顏色", "aoeRadius": "AO 半徑",
    "enableDepth": "景深", "depthFocalSize": "景深焦距", "depthAperture": "景深光圈",
    "enableVignette": "暗角", "enableFog": "霧", "fogColor": "霧顏色",
    "fogHeight": "霧高度", "fogStartDistance": "霧起點",
    "enableSunShafts": "光束", "sunColor": "太陽顏色", "sunThresholdColor": "光束閾值",
    "sunCaster": "光束來源", "enableShadow": "陰影",
    "ambientShadow": "環境陰影色", "ambientShadowG": "環境陰影強度",
    "charaLight": "角色燈", "mapLight": "地圖燈",
    "lineColorG": "描邊顏色", "lineWidthG": "描邊寬度", "rampG": "Ramp",
    "faceNormal": "臉部法線", "faceShadow": "臉部陰影", "skyInfo": "天空",
}


ENV_PREFIX = "[ENV]"
# F7 會在播放時切換的欄位（名稱 = Studio 的 SceneInfo 欄位名）。
# 地圖燈、天空跟地圖綁在一起，不在這裡切。
ENV_SWITCH = [k for k in ENV_FIELDS if k not in ("mapLight", "skyInfo")]


def _env_tokens(sc):
    """把一張卡的畫面效果 / 角色燈光編成 {key: 字串}，寫進 [ENV] 資料夾的名稱。"""
    def col(c):
        c = c or {}
        return "#" + "".join("%02X" % max(0, min(255, int(round(float(c.get(k, 0)) * 255))))
                             for k in ("r", "g", "b", "a"))

    def num(v):
        return ("%.5f" % float(v)).rstrip("0").rstrip(".") or "0"
    out = {}
    for k in ENV_SWITCH:
        v = getattr(sc, k, None)
        if v is None:
            continue
        if k == "charaLight" and isinstance(v, dict):
            out["cl.col"] = col(v.get("color"))
            out["cl.int"] = num(v.get("intensity", 1.0))
            rot = list(v.get("rot") or [0, 0]) + [0, 0]
            out["cl.rx"] = num(rot[0])
            out["cl.ry"] = num(rot[1])
            out["cl.sh"] = "1" if v.get("shadow", True) else "0"
        elif isinstance(v, bool):
            out[k] = "1" if v else "0"
        elif isinstance(v, dict):
            out[k] = col(v)
        elif isinstance(v, int):
            out[k] = str(v)
        elif isinstance(v, float):
            out[k] = num(v)
    return out


def _marker_children(by_key, wraps, s0, s1, prefix):
    """某張卡的段落（s0..s1）底下，名稱以 prefix 開頭的記號資料夾。"""
    out = []
    for si in range(s0, s1):
        dk = wraps[si] if si < len(wraps) else None
        w = by_key.get(dk) if dk is not None else None
        if w is None:
            continue
        for c in (w["data"].get("child") or []):
            if isinstance(c, dict) and str(c["data"].get("name") or "").startswith(prefix):
                out.append((si, c))
    return out


def _parse_marker(name, prefix):
    body = name[len(prefix):].strip()
    kv = {}
    for tok in body.split():
        k, eq, v = tok.partition("=")
        if eq:
            kv[k] = v
    return kv


# [ENV] 記號的寫法（v=3）：每段自己完整，但為了名稱短一點 ——
#   ・欄位用短代號（下表第 2 欄）
#   ・值跟「固定預設值」（第 3 欄）一樣的欄位不寫
# 固定預設值是寫死在合併工具和 F7 裡的常數（大多是 Studio 開新場景的預設），
# 不是「卡片本身存的值」，所以還是跟卡片存了什麼無關，在哪一段存檔都不會壞。
# F7 那邊有一模一樣的表（EnvSwitch.cs 的 Table），兩邊要一起改。
ENV_TABLE = [
    ("aceNo", "ac", "0"), ("aceBlend", "ab", "0"),
    ("enableAOE", "ao", "1"), ("aoeColor", "aoc", "#B4B4B4FF"), ("aoeRadius", "aor", "0.1"),
    ("enableBloom", "bl", "1"), ("bloomIntensity", "bli", "0.4"),
    ("bloomThreshold", "blt", "0.6"), ("bloomBlur", "blb", "0.8"),
    ("enableDepth", "dp", "0"), ("depthFocalSize", "dpf", "0.95"), ("depthAperture", "dpa", "0.6"),
    ("enableVignette", "vg", "1"),
    ("enableFog", "fg", "0"), ("fogColor", "fgc", "#89C1DDFF"), ("fogHeight", "fgh", "1"),
    ("fogStartDistance", "fgs", "0"),
    ("enableSunShafts", "ss", "0"), ("sunThresholdColor", "sst", "#808080FF"),
    ("sunColor", "ssc", "#FFFFFFFF"), ("sunCaster", "sso", "-1"),
    ("enableShadow", "sh", "1"), ("faceNormal", "fn", "0"), ("faceShadow", "fs", "0"),
    ("lineColorG", "lc", "0.65043"), ("ambientShadow", "asc", "#808080FF"),
    ("lineWidthG", "lw", "0.37838"), ("rampG", "rp", "1"), ("ambientShadowG", "as", "0.25889"),
    ("cl.col", "clc", "#FFFFFFFF"), ("cl.int", "cli", "1"), ("cl.rx", "clx", "0"),
    ("cl.ry", "cly", "0"), ("cl.sh", "cls", "1"),
]
ENV_L2S = {l: s_ for l, s_, _d in ENV_TABLE}
ENV_S2L = {s_: l for l, s_, _d in ENV_TABLE}
ENV_DEF = {l: d for l, _s, d in ENV_TABLE}
ENV_L2S.update({"aceNo.g": "ac.g", "rampG.g": "rp.g"})
ENV_S2L.update({"ac.g": "aceNo.g", "rp.g": "rampG.g"})


def _norm_env_val(k, v):
    v = str(v)
    if v.startswith("#") and len(v) == 7:
        v += "FF"
    return v.upper() if v.startswith("#") else v


def _env_name(tok):
    """完整一套（長欄位名）→ 記號名稱 [ENV] v=3 …（短代號、跟固定預設一樣的不寫）。"""
    out = []
    for l, s_, d in ENV_TABLE:
        if l not in tok:
            continue
        v = _norm_env_val(l, tok[l])
        if v == _norm_env_val(l, d):
            continue
        if v.startswith("#") and v.endswith("FF"):
            v = v[:-2]                    # 不透明的顏色省掉 alpha
        out.append("%s=%s" % (s_, v))
    for l in ("aceNo.g", "rampG.g"):
        if tok.get(l):
            out.append("%s=%s" % (ENV_L2S[l], tok[l]))
    return ENV_PREFIX + " v=3" + ("".join(" " + x for x in out))


def env_decode(kv):
    """記號的 key=value（_parse_marker 的結果）→ 完整一套（長欄位名）。
    v=3：短代號、沒寫的補固定預設；v=2：長欄位名、本來就完整。舊版（沒有 v）回傳 None。"""
    ver = kv.get("v")
    if ver == "3":
        full = dict(ENV_DEF)
        for k, v in kv.items():
            if k in ENV_S2L:
                full[ENV_S2L[k]] = v
        return full
    if ver == "2":
        return {k: v for k, v in kv.items() if k != "v"}
    return None


def _shift_caster(v, shift):
    try:
        iv = int(v)
    except ValueError:
        return v
    return str(iv + shift) if iv >= 0 else v


def abs_env_name(old_name, card_tok, shift=0):
    """把一個 [ENV] 記號換成「每段自己完整」的寫法（v=3）。

    old_name   現在的資料夾名稱
    card_tok   這張卡本身存的那一套（saved_map_info()["envtok"]，光束來源已經位移好）
    shift      這張卡併進來時 dicKey 位移了多少（記號名稱裡的光束來源要跟著移）

    舊版記號（[ENV] base / 只寫差異）的意思是「卡片載入時的值 + 差異」——
    卡片載入時的值就是 card_tok，所以換算完 F7 看到的結果跟以前一模一樣。
    已經是 v=2 / v=3 的：解開、位移光束來源、再用 v=3 寫回去。
    """
    old = _parse_marker(old_name, ENV_PREFIX)
    full = env_decode(old)
    if full is not None:
        if "sunCaster" in full:
            full["sunCaster"] = _shift_caster(full["sunCaster"], shift)
        return _env_name(full)
    eff = dict(card_tok)
    for k, v in old.items():
        if k == "sunCaster":
            v = _shift_caster(v, shift)
        eff[k] = v
        if k in ("aceNo", "rampG"):
            eff.pop(k + ".g", None)       # 差異裡的編號是別張卡的，不是這張卡的模組
    return _env_name(eff)


def apply_segment_env(scene, cards, segs, wraps, warn):
    """各段的畫面效果 / 角色燈光不同時，每段放一個 [ENV] 記號給 F7 切換。

    每個記號都存「那一段完整的一套」（[ENV] v=3 ab=0.48 aoc=C8C8C8 cli=0.39 …，
    短代號 + 跟固定預設一樣的不寫，見 ENV_TABLE），
    不靠卡片本身存的值當基準 —— 在 Studio 裡播到第 3 段時存檔，卡片本身的值
    會變成第 3 段的，但每段的記號還是各自完整，重新載入照樣對。
    模組的調色 / Ramp 另外帶 GUID（ac.g / rp.g），F7 自己問 UAR 換成本機編號。
    """
    infos = [c.get("mapinfo") for c in cards]
    if len(infos) < 2 or any(i is None or "envtok" not in i for i in infos):
        return 0
    toks = []
    for i, c in zip(infos, cards):
        t = dict(i["envtok"])
        if "sunCaster" in t:                 # 光束來源是物件 dicKey，合併時跟著位移
            t["sunCaster"] = _shift_caster(t["sunCaster"], c.get("shift", 0))
        toks.append(t)
    base = toks[0]
    if all(t == base for t in toks[1:]) and not any(i["has_env"] for i in infos):
        return 0

    by_key = {n["data"]["dicKey"]: n for n, _, _ in S.iter_nodes(scene.objects)}
    nxt = max(S.all_dickeys(scene.objects)) + 1
    added, conv, labels = 0, 0, set()
    for ci, t in enumerate(toks[1:], 1):
        for k, v in t.items():
            if base.get(k) != v and not k.endswith(".g"):
                labels.add(ENV_FIELDS.get("charaLight" if k.startswith("cl.") else k, k))
    for ci, c in enumerate(cards):
        s0 = c["seg0"]
        s1 = cards[ci + 1]["seg0"] if ci + 1 < len(cards) else len(segs)
        if infos[ci]["has_env"]:
            # 這張本身就是合併卡，段落已經有記號：換成完整一套的寫法（舊版的差異記號
            # 以它自己載入時的值為準換算，新合併卡的第 1 張換了也不影響）
            for si, fo in _marker_children(by_key, wraps, s0, s1, ENV_PREFIX):
                nm = str(fo["data"].get("name") or "")
                fo["data"]["name"] = abs_env_name(nm, toks[ci], c.get("shift", 0))
                conv += 1
            continue
        name = _env_name(toks[ci])
        for si in range(s0, s1):
            dk = wraps[si] if si < len(wraps) else None
            w = by_key.get(dk) if dk is not None else None
            if w is None:
                continue
            ch = w["data"].get("child")
            if not isinstance(ch, list):
                ch = []
                w["data"]["child"] = ch
            ch.append(S.new_folder(nxt, name))
            nxt += 1
            added += 1
    log("  畫面效果 / 角色燈光：各段不一樣（%s）→ 每段放了 %d 個 %s 記號%s（每段各自存完整一套），"
        "F7 播放時切換" % ("、".join(sorted(labels)) or "—", added, ENV_PREFIX,
                         "、換算了 %d 個舊記號" % conv if conv else ""))
    warn.append("各段的畫面效果 / 角色燈光不一樣：F7 播放時會照 [ENV] 記號切換；"
                "沒裝 F7、或直接在 Timeline 裡播的時候，每一段都是卡片本身存的那一套。")
    return added + conv


def _map_key(mi):
    r = lambda t: tuple(round(x, 4) for x in t)             # noqa: E731
    return (mi["map"], mi.get("guid"), r(mi["pos"]), r(mi["rot"]), mi["sun"], mi["opt"])


def _map_name(mp, sun, opt, guid):
    return "%s map=%d sun=%d opt=%d%s" % (MAPINFO_PREFIX, mp, sun, 1 if opt else 0,
                                          (" guid=" + _enc(guid)) if (guid and mp >= 0) else "")


def abs_map_name(old_name, mi):
    """把一個 [MAPINFO] 記號換成不靠「卡片本身存的地圖」的寫法。

    舊版的 scene=1 意思是「就用卡片載入時 Studio 載好的那張」—— 在 Studio 裡
    播到別段時存檔，卡片本身的地圖就換了，scene=1 會指到錯的地圖。
    改成直接寫 map=<模組編號> guid=<模組 GUID>，F7 自己問 UAR 換成本機編號。
    mi = 這張卡本身的 saved_map_info()（scene=1 指的就是它存的那張）。
    沒有 scene=1 的記號本來就是寫死編號，不動。
    回傳 (新名稱, 有沒有改)。
    """
    kv = _parse_marker(old_name, MAPINFO_PREFIX)
    if "guid" in kv or kv.get("scene") != "1":
        return old_name, False
    try:
        sun = int(kv.get("sun", mi["sun"]))
        opt = kv.get("opt", "1" if mi["opt"] else "0") == "1"
    except ValueError:
        sun, opt = mi["sun"], mi["opt"]
    return _map_name(mi["map"], sun, opt, mi.get("guid")), True


def apply_segment_maps(scene, cards, segs, wraps, warn):
    """每段的包裝資料夾底下放 [MAPINFO] 記號（F7 照它切換 / 隱藏地圖）。

    只要有任何一段選了內建地圖就一定放 —— 單張卡、或每段都是同一張地圖也放：
    記號資料夾同時是地圖的開關（取消打勾 = 關地圖）和位置把手，每張卡都一樣比較好管。
    全部都沒選地圖才不放。
    """
    if not cards:
        return 0
    infos = [c.get("mapinfo") for c in cards]
    if any(i is None for i in infos):
        return 0
    prim = next((i for i in infos if i["map"] >= 0), None)
    if prim is None:
        return 0                                  # 全部都沒選地圖
    same = len({_map_key(i) for i in infos}) == 1

    # 卡片本身存的地圖：給沒裝 F7 的時候看。F7 不靠它 —— 每段的記號各自寫了
    # 編號 + GUID，在 Studio 裡播到哪一段存檔都不會壞。
    sc = scene.sc
    sc.map = prim["map"]
    sc.caMap = {"pos": dict(zip("xyz", prim["pos"])), "rot": dict(zip("xyz", prim["rot"])),
                "scale": {"x": 1.0, "y": 1.0, "z": 1.0}}
    sc.sunLightType = prim["sun"]
    sc.mapOption = prim["opt"]
    # 模組地圖的 UAR 解析資料要跟著地圖一起搬過來（見 saved_map_info）
    if prim["uar"]:
        if scene.kkex.has(UAR):
            for k, vb in prim["uar"]:
                scene.kkex.set(UAR, k, vb)
            log("  內建地圖：帶上模組地圖的解析資料（%s）"
                % "、".join(str(k) for k, _ in prim["uar"]))
        else:
            warn.append("內建地圖 #%d 是模組地圖，但合併卡沒有 UAR 資料可以寫入解析資訊 —— "
                        "沒裝 F7 時載入可能對不到本機的地圖（F7 會用記號上的 GUID 自己換算）"
                        % prim["map"])

    by_key = {n["data"]["dicKey"]: n for n, _, _ in S.iter_nodes(scene.objects)}
    nxt = max(S.all_dickeys(scene.objects)) + 1
    added, conv, desc = 0, 0, []
    for ci, c in enumerate(cards):
        mi = infos[ci]
        s0 = c["seg0"]
        s1 = cards[ci + 1]["seg0"] if ci + 1 < len(cards) else len(segs)
        desc.append("第 %d 張 %s" % (ci + 1, ("地圖 #%d%s" % (mi["map"], "（模組）" if mi.get("guid") else ""))
                                     if mi["map"] >= 0 else "無"))
        if mi["has_marker"]:
            # 這張本身就是合併卡，段落已經有記號：scene=1 換成寫死的編號 + GUID
            for si, fo in _marker_children(by_key, wraps, s0, s1, MAPINFO_PREFIX):
                nm = str(fo["data"].get("name") or "")
                nn, ch_ = abs_map_name(nm, mi)
                if ch_:
                    fo["data"]["name"] = nn
                    conv += 1
                else:
                    kv = _parse_marker(nm, MAPINFO_PREFIX)
                    if "guid" not in kv and kv.get("map", "-1") not in ("-1", str(mi["map"])) \
                            and mi.get("guid"):
                        warn.append("第 %d 段的地圖記號 #%s 沒有 GUID：如果那是模組地圖，F7 會載錯；"
                                    "用原卡重新合併或 kkupgrade 修正" % (si + 1, kv.get("map")))
            continue
        for si in range(s0, s1):
            dk = wraps[si] if si < len(wraps) else None
            w = by_key.get(dk) if dk is not None else None
            if w is None:
                warn.append("內建地圖：第 %d 段找不到包裝資料夾，沒有放記號" % (si + 1))
                continue
            f = S.new_folder(nxt, _map_name(mi["map"], mi["sun"], mi["opt"], mi.get("guid")))
            f["data"]["position"] = dict(zip("xyz", mi["pos"]))
            f["data"]["rotation"] = dict(zip("xyz", mi["rot"]))
            ch = w["data"].get("child")
            if not isinstance(ch, list):
                ch = []
                w["data"]["child"] = ch
            ch.append(f)
            nxt += 1
            added += 1
    log("  內建地圖：" + ("每段都一樣" if same else "各段不一樣") + "（%s）→ 每段放了 %d 個 %s 記號%s（編號 + GUID 各自完整），"
        "F7 播放時會照記號切換/隱藏" % ("、".join(desc), added, MAPINFO_PREFIX,
                                   "、換算了 %d 個舊記號" % conv if conv else ""))
    if not same:
        warn.append("內建地圖各段不一樣：合併卡本身只能存一張（#%d），沒裝 F7、或直接在 Timeline "
                    "裡播的時候每一段都會看到這張。兩段用的是不同的地圖時，切換那一下要重新載入地圖，"
                    "會卡一下。" % prim["map"])
    return added + conv


def _kf_is_step(kf):
    cks = re.findall(r'<curveKeyframe\b[^>]*>', kf)
    return bool(cks) and 'inTangent="INF"' in cks[-1]


def build_camera_global_tracks(scene, cards, total, warn, eps=0.01):
    """cards：[{"off": 這張卡在合併卡的起點, "label": 名稱, "seg_cam": 那段相機 dicKey,
                "saved": saved_camera_values()}]

    回傳補了幾格。
    """
    if len(cards) < 2:
        return 0
    xml = scene.timeline_xml()
    if not xml:
        return 0
    head, body, tail = S.split_root(xml)
    camfov = camfov_table(scene)
    offs = [c["off"] for c in cards]

    def card_of(t):
        k = 0
        for i, o in enumerate(offs):
            if o <= t + 1e-6:
                k = i
        return k

    added_total = 0
    for tid in CAM_GLOBAL_IDS:
        # 找出合併後的那一條（_merge_into 已經把各張的格子平移後接在一起）
        found = None
        for m in S.RE_INTERP.finditer(body):
            blk = m.group(0)
            hm = S.RE_INTERP_HEAD.match(blk)
            h = hm.group(0) if hm else blk.split(">", 1)[0] + ">"
            if 'objectIndex="' in h or 'owner="Timeline"' not in h:
                continue
            mi = re.search(r'\bid="([^"]*)"', h)
            if mi and mi.group(1) == tid:
                found = (m, blk, h)
                break

        kfs = []
        if found:
            for kf in RE_KEYFRAME.findall(found[1]):
                mt = re.search(r'\btime="([-\d.eE+]+)"', kf.split(">", 1)[0])
                if mt:
                    kfs.append((float(mt.group(1)), kf))
        own = [[] for _ in cards]
        for t, kf in kfs:
            own[card_of(t)].append((t, kf))

        new_kfs = []
        report = []
        miss = []
        for i, c in enumerate(cards):
            s = c["off"]
            nxt = offs[i + 1] if i + 1 < len(cards) else None
            mine = sorted(own[i], key=lambda x: x[0])
            if mine:
                first_t, first_kf = mine[0]
                v0 = re.search(r'\bvalue="([^"]*)"', first_kf.split(">", 1)[0]).group(1)
                note = "原卡自己的 %d 格" % len(mine)
                if first_t > s + 1e-6:
                    new_kfs.append((s, STEP_KF.format(t=S.fmt_time(s), v=v0)))
                    note += "，開頭補 1 格"
                last_t, last_kf = mine[-1]
                if nxt is not None and not _kf_is_step(last_kf):
                    hold = nxt - eps
                    if hold > last_t + 1e-6:
                        lv = re.search(r'\bvalue="([^"]*)"', last_kf.split(">", 1)[0]).group(1)
                        new_kfs.append((hold, STEP_KF.format(t=S.fmt_time(hold), v=lv)))
                        note += "，段尾補 1 格擋住"
                sv = c["saved"].get(tid)
                report.append("%s 秒 %s（%s%s）" % (
                    S.fmt_time(s), v0, note,
                    "；存檔視角 %s" % _fmt_val(sv) if sv is not None else ""))
                continue
            v, src = None, None
            if tid == "cameraFOV" and c.get("seg_cam") is not None:
                v = camfov.get(c["seg_cam"])
                src = "相機物件 FOV" if v is not None else None
            if v is None:
                v = c["saved"].get(tid)
                src = "存檔視角" if v is not None else None
            if v is None:
                miss.append(i + 1)
                continue
            new_kfs.append((s, STEP_KF.format(t=S.fmt_time(s), v=_fmt_val(v))))
            report.append("%s 秒 %s（%s）" % (S.fmt_time(s), _fmt_val(v), src))

        if not new_kfs and not found:
            continue
        # 同一個時間點作者的格子優先，補的那格不放
        taken = {round(t, 5) for t, _ in kfs}
        new_kfs = [(t, kf) for t, kf in new_kfs if round(t, 5) not in taken]
        allk = sorted(kfs + new_kfs, key=lambda x: x[0])

        if found:
            m, blk, h = found
            h2 = h.replace('enabled="false"', 'enabled="true"', 1)
            track = h2 + "".join(kf for _, kf in allk) + "</interpolable>"
            body = body[:m.start()] + track + body[m.end():]
            if h2 != h:
                report.append("原本沒打勾，已打開")
        else:
            body = CAM_GLOBAL_HEAD[tid] + "".join(kf for _, kf in allk) + "</interpolable>" + body
        added_total += len(new_kfs)
        log("  %s（%s）：%s" % (CAM_GLOBAL_NAME[tid], tid, "；".join(report)))
        if miss:
            warn.append("%s：第 %s 段找不到原卡的值，那一段會沿用前一段的"
                        % (CAM_GLOBAL_NAME[tid], "、".join(str(x) for x in miss)))
    scene.set_timeline_xml(head + body + tail)
    return added_total


def build_fov_track(scene, cam_keys, starts, total, warn, eps=0.01):
    """把各段相機的 FOV 做成一條全域 cameraFOV 軌道。

    為什麼需要：FOV 不是 transform，是 SaveCameraObjectFOV 記在相機物件上的
    靜態值，切到那台相機時才套用。但合併卡從頭到尾都用同一台新相機 ——
    新相機只有一個 FOV，六段全部共用，各段原本的構圖就跑掉了。

    Timeline 有一條全域軌道 cameraFOV 可以直接動主相機的 FOV，
    所以在每段開頭放一格、段尾前 eps 秒再放一格（同值），
    段內維持定值，交界用 eps 秒瞬間切過去。
    """
    if len(cam_keys) < 2:
        return 0
    xml = scene.timeline_xml()
    if not xml:
        return 0
    if 'id="cameraFOV"' in xml:
        warn.append("卡裡本來就有 cameraFOV 軌道，沒有另外產生（保留作者自己的）")
        return 0
    table = camfov_table(scene)
    fovs = [table.get(k) for k in cam_keys]
    known = [f for f in fovs if f is not None]
    if not known:
        warn.append("SaveCameraObjectFOV 裡一台場景相機都沒有 FOV，沒有產生 cameraFOV 軌道")
        return 0
    if len(known) == len(fovs) and len(set(known)) == 1:
        return 0                                   # 各段 FOV 一樣，不用動

    fb = None
    try:
        fb = scene_view(scene)[2]
    except Exception:                                       # noqa: BLE001
        fb = None
    if not isinstance(fb, (int, float)) or not fb:
        fb = known[0]
    fovs = [f if f is not None else float(fb) for f in fovs]

    n = min(len(cam_keys), len(starts))
    kfs = []
    for i in range(n):
        s = starts[i]
        e = starts[i + 1] if i + 1 < n else total
        v = S.fmt_time(fovs[i])
        kfs.append(FOV_KF.format(t=S.fmt_time(s), v=v))
        hold = e - eps
        if hold > s:
            kfs.append(FOV_KF.format(t=S.fmt_time(hold), v=v))
    track = FOV_HEAD + "".join(kfs) + "</interpolable>"
    head, body, tail = S.split_root(xml)
    scene.set_timeline_xml(head + track + body + tail)
    log("  相機 FOV 軌道：" + "、".join(
        f"{S.fmt_time(starts[i])} 秒 -> {S.fmt_time(fovs[i])}" for i in range(n)))
    miss = [i + 1 for i, k in enumerate(cam_keys) if table.get(k) is None]
    if miss:
        warn.append("第 " + "、".join(str(i) for i in miss)
                    + " 段的相機沒有記 FOV，用了卡片存檔當下的視野 "
                    + S.fmt_time(fb))
    return n


def merge(a_path, b_path, out_path, offset=None, park=True, gap=0.01, group=True,
          camera_switch=True, cam_name=None):
    """兩張卡的合併（merge_many 的方便包裝）。"""
    if offset is not None:
        warn = []
        A = S.Scene(a_path)
        gap = offset - (A.duration() or 0.0)
        del A
    return merge_many([a_path, b_path], out_path, gap=gap, park=park, group=group,
                      camera_switch=camera_switch, cam_name=cam_name)


# ================================================================ info
def png_len(d, start=0):
    """卡片最前面那張縮圖 PNG 的長度。"""
    import struct
    if d[start:start + 8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("不是 PNG 開頭")
    p = start + 8
    while True:
        ln = struct.unpack(">I", d[p:p + 4])[0]
        typ = d[p + 4:p + 8]
        p += 8 + ln + 4
        if typ == b"IEND":
            return p - start


def content_end(scene):
    """整張卡最遠的那一格關鍵影格在第幾秒。

    為什麼要另外算：timeline 的 root duration 只是「播放長度」，
    跟實際有沒有內容是兩回事。作者在 Timeline 裡拉長動畫卻沒改播放長度，
    存出來的卡就會宣告 30 秒而內容其實到 74 秒（KKS 匯出的卡特別常見）。

    合併是照宣告時長排時間的，所以這個差距會直接變成「後面幾段全部提早」，
    而且使用者在 Studio 裡拉時間軸看到的是有動作的 —— 兩邊對不起來。
    把真正的內容長度算出來，至少讓人知道該填多少。

    回傳 None = 這張卡沒有 timeline。
    """
    xml = scene.timeline_xml()
    if not xml:
        return None
    body = S.split_root(xml)[1]
    mx = 0.0
    for m in S.RE_INTERP.finditer(body):
        for kf in RE_KEYFRAME.findall(m.group(0)):
            mt = re.search(r'time="([-\d.eE+]+)"', kf)
            if mt:
                try:
                    mx = max(mx, float(mt.group(1)))
                except ValueError:
                    pass
    return mx


def scene_info(path):
    """給 UI 用的卡片摘要（會整張讀進來，大卡要花點時間）。"""
    sc = S.Scene(path)
    nodes = S.node_dickeys(sc.objects)
    alld = S.all_dickeys(sc.objects)
    cams = []
    for n, p in sc.cameras():
        cams.append({"dicKey": n["data"]["dicKey"], "path": p,
                     "active": bool(n["data"].get("active"))})
    wrap, cam_root = _roots_of(sc)
    eye, ang, fov = scene_view(sc)
    out = {"auto_camera": not cams, "view": {"pos": eye, "rot": ang, "fov": fov},
           "camera_path": has_camera_path(sc),
           "content_end": content_end(sc),
           "path": path, "version": sc.version, "duration": sc.duration(),
           "map": saved_map_info(sc)["map"],
           "nodes": len(nodes), "dicKey_max": max(alld) if alld else 0,
           "cameras": cams, "roots": len(sc.objects),
           "prepped": cam_root is not None,
           "wrapper": (wrap["data"].get("name") if wrap else None),
           "size": os.path.getsize(path)}
    del sc
    return out


def info(path):
    sc = S.Scene(path)
    nodes = S.node_dickeys(sc.objects)
    alld = S.all_dickeys(sc.objects)
    print(f"{os.path.basename(path)}")
    print(f"  studio 版本 {sc.version}   檔案 {os.path.getsize(path):,} bytes")
    print(f"  根節點 {len(sc.objects)}   全部節點 {len(nodes)}")
    print(f"  dicKey {min(alld)}..{max(alld)}（含骨架 / IK，共 {len(alld)} 個）")
    d = sc.duration()
    print(f"  timeline 時長 {d}")
    cams = sc.cameras()
    print(f"  相機 {len(cams)} 台：")
    rk = S.rank_map(nodes)
    for n, p in cams:
        k = n["data"]["dicKey"]
        print(f"     dicKey={k:<6} RANK={rk[k]:<6} active={n['data'].get('active')}  {p}")
    if has_camera_path(sc):
        print("  >> 這張卡用 timeline 的相機位置 / 相機旋轉在運鏡"
              "（--camera -1 可以生一台相機把這段路徑接手）")
    if not cams:
        eye, ang, fov = scene_view(sc)
        print(f"  >> 沒有相機，合併時會自動生一台鎖住初始視角："
              f"位置 {eye['x']:.3f}, {eye['y']:.3f}, {eye['z']:.3f}／"
              f"角度 {ang['x']:.3f}, {ang['y']:.3f}, {ang['z']:.3f}"
              + (f"／FOV {fov:g}" if fov else ""))
    if len(cams) > 1:
        print("  >> 有複數相機。合併前請先決定留哪一台，其他刪掉"
              "（prep --camera <dicKey>）。")
    return sc


# ================================================================ CLI
def main(argv=None):
    ap = argparse.ArgumentParser(prog="kkscenemerge")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("info", help="印出卡片資訊")
    p.add_argument("card")

    p = sub.add_parser("prep", help="把原始卡整理成可以合併的形狀")
    p.add_argument("card")
    p.add_argument("--name", required=True, help="包裝資料夾與相機要叫什麼")
    p.add_argument("--camera", type=int, default=None, help="要留下的相機 dicKey")
    p.add_argument("--out", default=None)
    p.add_argument("--no-subfolders", action="store_true",
                   help="不要建 (MAP)(FX)(CHAR)(SFX) 四個空資料夾")
    p.add_argument("--no-nc-rename", action="store_true",
                   help="不要把 NC 的 alias 改成 '<場景名> | …'")
    p.add_argument("--no-zero-chain", action="store_true",
                   help="不要讓 POV 資料夾在場景結束時歸零")
    p.add_argument("--no-auto-camera", action="store_true",
                   help="卡裡沒有相機時不要自動生一台（預設會生，鎖住初始視角）")
    p.add_argument("--duration", type=float, default=None,
                   help="把這張卡的時長改成幾秒（只能比原本長，用來多停一會）")
    p.add_argument("--index", type=int, default=None,
                   help="這張卡在合併順序裡是第幾張；自動建的資料夾會加這個編號"
                        "（(FX) 2、POV 1 2），避免多張卡同名")
    p.add_argument("--exclude", default=None,
                   help="整理時順便把這些 dicKey 的節點（連同子樹與所有參照）"
                        "拿掉，逗號分隔。不填就什麼都不排除。")
    p.add_argument("--drop-nc-path", default=None,
                   help="丟掉 parentPath / childPath 含這個字串的 NC constraint，"
                        "逗號分隔。用來清掉綁到不存在骨架的那幾條，"
                        "例如 --drop-nc-path cf_J_Vagina")
    p.add_argument("--drop-nc-alias", default=None,
                   help="丟掉 alias 含這個字串的 NC constraint，逗號分隔。")

    p = sub.add_parser("merge", help="按順序合併兩張以上已整理好的場景卡")
    p.add_argument("cards", nargs="+", help="照播放順序給，第一張是底卡")
    p.add_argument("--out", required=True)
    p.add_argument("--flat-cams", action="store_true",
                   help="相機鏈平行排列（預設是照手動流程一層套一層）")
    p.add_argument("--gap", type=float, default=0.01,
                   help="兩張卡交界處留的間隙秒數，預設 0.01")
    p.add_argument("--no-enable-tracks", action="store_true",
                   help="不要寫物件啟用軌道（沒輪到的場景取消勾選）")
    p.add_argument("--no-camera-switch", action="store_true",
                   help="不要生新相機、不要把第二張的 POV 鏈搬進來")
    p.add_argument("--cam-name", default=None, help="新相機的名字（預設取兩台相機的共同開頭）")
    p.add_argument("--no-group", action="store_true",
                   help="不要把兩個場景的軌道各包一層 timeline 群組")
    p.add_argument("--shader-type", type=int, default=None,
                   help="把合併卡的場景 shaderType 設成這個值。"
                        "各段原本不同時（例如有的 0 有的 1），FX / 材質的著色會整張用同一套，"
                        "用這個挑要哪一套。不給就保留底卡的。")
    p.add_argument("--no-enable-all-tracks", action="store_true",
                   help="不要把原本沒打勾的相機軌道打開。"
                        "預設只打開時間流速、相機縮放/FOV、以及相機（POV）資料夾上的軌道；"
                        "其他沒打勾的一律保留作者的設定。")
    p.add_argument("--keep-frame", action="store_true",
                   help="保留底卡的外框（StudioImageEmbed 的 FrameData）。"
                        "預設會清掉：外框是作者錄影時疊上去的邊框，"
                        "合併卡是拿來重新運鏡的，留著每一段都會被同一個框壓著。")
    p.add_argument("--no-fov-track", "--no-cam-tracks", dest="no_fov_track",
                   action="store_true",
                   help="不要在每段開頭切換相機縮放（cameraOZoom）和相機FOV（cameraFOV）。"
                        "預設會切：每段開頭放一格那張原卡的值，下一段開頭瞬間換掉；"
                        "原卡自己有的格子不動。")
    p.add_argument("--no-sync-static", action="store_true",
                   help="不要把 timeline 第 0 秒的值寫回節點的靜態座標。"
                        "用來確認「合併卡一載入時鏡位/勾選狀態不對」是不是這一步造成的。")
    p.add_argument("--no-park", action="store_true",
                   help="不要自動產生包裝資料夾的外太空停放軌道。"
                        "停放是瞬移，DynamicBone 那類揺れ物（頭髮最明顯）會被甩到"
                        "回不來 —— 原卡單獨播正常、一合併頭髮就怪，先試這個")
    p.add_argument("--park-lead", type=float, default=0.0,
                   help="讓包裝資料夾比「輪到自己」提早幾秒回到原位（預設 0 = 不提早）。"
                        "提早的那段時間它還沒上場、看不到，正好讓揺れ物晃完再入鏡。"
                        "比 --no-park 溫和：停放照做，只是給頭髮一點安定的時間。"
                        "1~3 秒通常就夠")
    p.add_argument("--tl-clean", action="store_true",
                   help="按 id 整批拿掉這幾種軌道（"
                        + "、".join(sorted(TL.KNOWN_BROKEN)) + "）。"
                        "**預設不開** —— 它是整批砍，同一個 id 的好軌道會一起死："
                        "charClothes 一砍，卡裡「演到一半脫衣服」那段就沒了；"
                        "itemColor1~3 在 kktl_scan 自己的 COMMON 清單裡還被歸成"
                        "『不會有跨版問題』，卻照樣在這份名單裡。"
                        "只有在 mismatch 清法清不乾淨、Timeline 仍然從第 0 秒"
                        "就整棵中斷時才開。")
    p.add_argument("--no-tl-clean", action="store_true",
                   help=argparse.SUPPRESS)      # 舊旗標，現在是預設行為，留著不報錯
    p.add_argument("--no-tl-mismatch", action="store_true",
                   help="連「objectIndex 指到不對的節點種類、或指不到節點」的軌道"
                        "也不要拿掉。預設是會拿掉的 —— 那幾條才是真的在丟 "
                        "InvalidCastException 的，而且只砍指錯的那幾條，"
                        "同 id 的好軌道留著。")
    p.add_argument("--tl-drop-mismatch", action="store_true",
                   help=argparse.SUPPRESS)      # 舊旗標，現在是預設行為，留著不報錯
    p.add_argument("--nc-enable-tracks", action="store_true",
                   help="把作者自己的每一條 NC 也寫進 timeline 的啟用軌道："
                        "0 秒關、自己那段開頭設回它原本的 enabled、那段結尾關掉。"
                        "等於手動流程那顆「盲狙收割」按鈕。"
                        "已經有 constraintEnabled 軌道的 NC 會跳過（包含接棒相機、"
                        "以及作者本來就在 timeline 裡開關的那幾條）。預設不開。")

    a = ap.parse_args(argv)
    if a.cmd == "info":
        info(a.card)
        return 0
    if a.cmd == "prep":
        ex = None
        if a.exclude:
            ex = [int(v) for v in str(a.exclude).replace("，", ",").split(",")
                  if v.strip()]
        def _csv(v):
            if not v:
                return None
            return [x.strip() for x in str(v).replace("，", ",").split(",")
                    if x.strip()]
        prep(a.card, a.name, a.camera, a.out, subfolders=not a.no_subfolders,
             nc_rename=not a.no_nc_rename, zero_chain=not a.no_zero_chain,
             auto_camera=not a.no_auto_camera, duration=a.duration,
             index=a.index, exclude=ex,
             drop_nc_alias=_csv(a.drop_nc_alias),
             drop_nc_path=_csv(a.drop_nc_path))
        return 0
    if a.cmd == "merge":
        merge_many(a.cards, a.out, gap=a.gap, park=not a.no_park,
                   park_lead=a.park_lead,
                   group=not a.no_group, camera_switch=not a.no_camera_switch,
                   cam_name=a.cam_name, nested_cams=not a.flat_cams,
                   enable_tracks=not a.no_enable_tracks,
                   sync_static=not a.no_sync_static,
                   fov_track=not a.no_fov_track,
                   shader_type=a.shader_type,
                   tl_clean=a.tl_clean and not a.no_tl_clean,
                   tl_mismatch=not a.no_tl_mismatch,
                   nc_enable_tracks=a.nc_enable_tracks,
                   clear_frame=not a.keep_frame,
                   enable_all_tracks=not a.no_enable_all_tracks)
        return 0
    return 1



# ================================================================ 刪節點後的參照清理
def _filter_bin_list(kkex, guid, key, keep, warn):
    vb = kkex.get(guid, key)
    if vb is None:
        return 0
    try:
        kind, payload = _container(vb)
        if not payload:
            return 0
        items = arr_split(payload)
    except ValueError:
        return 0
    out = []
    for e in items:
        d, _ = _elem_map(e)
        if d is None or keep(d):
            out.append(e)
    n = len(items) - len(out)
    if n:
        kkex.set(guid, key, _rewrap(kind, arr_build(out)))
    return n


def _filter_bin_map(kkex, guid, key, keep, warn):
    vb = kkex.get(guid, key)
    if vb is None:
        return 0
    try:
        kind, payload = _container(vb)
        if not payload:
            return 0
        items = map_split(payload)
    except ValueError:
        return 0
    out = [t for t in items if keep(t[0])]
    n = len(items) - len(out)
    if n:
        kkex.set(guid, key, _rewrap(kind, map_build(out)))
    return n


def cleanup_deleted(scene, old_nodes, deleted, warn):
    """節點被刪掉之後，把所有指向它們的參照清掉，並重算 RANK 空間。

    存活節點的 dicKey 不變，所以 DICKEY 空間只要「刪」；
    RANK 空間（timeline objectIndex、NC parent/childObjectIndex）整個要重算。
    """
    if not deleted:
        return {}
    new_nodes = S.node_dickeys(scene.objects)
    old_rank = S.rank_map(old_nodes)
    new_rank = S.rank_map(new_nodes)
    inv_old = {v: k for k, v in old_rank.items()}
    dead = set(deleted)

    def rank_fn(r):
        k = inv_old.get(r)
        if k is None or k in dead:
            return None
        return new_rank.get(k)

    kx = scene.kkex
    stat = {}

    # timeline：指向被刪節點的軌道整條拿掉，其餘重新編號
    xml = scene.timeline_xml()
    if xml:
        head, body, tail = S.split_root(xml)
        kept, dropped = [], 0
        last = 0
        for m in S.RE_INTERP.finditer(body):
            blk = m.group(0)
            hm = re.match(r'<interpolable\b[^>]*?>', blk)
            h = hm.group(0) if hm else blk
            mo = re.search(r'objectIndex="(-?\d+)"', h)
            if mo and int(mo.group(1)) >= 0 and rank_fn(int(mo.group(1))) is None:
                kept.append(body[last:m.start()])
                last = m.end()
                dropped += 1
        kept.append(body[last:])
        body = S.remap_object_index("".join(kept), rank_fn)
        scene.set_timeline_xml(head + body + tail)
        stat["timeline 軌道"] = -dropped

    # nodesConstraints
    nc = kx.get("nodesConstraints", "constraints")
    if nc is not None:
        s = unpack(nc)
        if isinstance(s, str) and s.strip():
            h, b, t = _split_xml_root(s, "constraints")
            if h is not None:
                out, dropped = [], 0
                for m in re.finditer(r'<constraint\b[^>]*?/>|<constraint\b[^>]*?>.*?</constraint>',
                                     b, re.S):
                    blk = m.group(0)
                    idx = [int(x) for x in re.findall(
                        r'(?:parent|child)ObjectIndex="(-?\d+)"', blk)]
                    if any(i >= 0 and rank_fn(i) is None for i in idx):
                        dropped += 1
                        continue
                    out.append(blk)
                nb = remap_xml_attr("".join(out),
                                    ["parentObjectIndex", "childObjectIndex"], rank_fn)
                kx.set("nodesConstraints", "constraints", pack(h + nb + t))
                stat["NC constraint"] = -dropped

    # rendererEditor（RANK）：這裡以前沒處理，刪節點之後貼圖會貼到別的物件上
    if kx.get(RENDED, "xml") is not None:
        d = rended_remap(kx, rank_fn)
        stat["rendererEditor"] = -d

    # kkpe：<itemInfo index="dicKey">
    kp = kx.get("kkpe", "sceneInfo")
    if kp is not None:
        s = unpack(kp)
        if isinstance(s, str) and s.strip():
            h, b, t = _split_xml_root(s, "root")
            if h is not None:
                out, dropped = [], 0
                for m in re.finditer(r'<itemInfo\b[^>]*?/>|<itemInfo\b[^>]*?>.*?</itemInfo>',
                                     b, re.S):
                    blk = m.group(0)
                    mi = re.search(r'\bindex="(-?\d+)"', blk)
                    if mi and int(mi.group(1)) in dead:
                        dropped += 1
                        continue
                    out.append(blk)
                kx.set("kkpe", "sceneInfo", pack(h + "".join(out) + t))
                stat["kkpe itemInfo"] = -dropped

    # DICKEY 空間的各表
    if kx.get(OBJIMP, "ids") is not None:
        d = objimp_filter(kx, lambda k: k not in dead)
        if d:
            stat["OBJImport"] = -d
    n = _filter_bin_map(kx, TNN, "names", lambda k: k not in dead, warn)
    if n:
        stat["treenodenaming"] = -n
    n = 0
    for key in kkref.ME_LISTS:
        n += _filter_bin_list(kx, ME, key, lambda d: d.get("ID") not in dead, warn)
    if n:
        stat["MaterialEditor 條目"] = -n
    n = _filter_bin_list(kx, UAR, "itemInfo",
                         lambda d: d.get("SceneDicKey") not in dead, warn)
    if n:
        stat["UAR"] = -n
    n = _filter_bin_list(kx, "LightSettingsData", "LightSettingsData_lights",
                         lambda d: d.get("ObjectId") not in dead, warn)
    if n:
        stat["LightSettings"] = -n
    n = _filter_bin_list(kx, "keelhauled.itemlayeredit", "SavedLayers",
                         lambda d: d.get("ObjectId") not in dead, warn)
    if n:
        stat["itemlayeredit"] = -n
    n = _filter_bin_map(kx, "com.rikkibalboa.bepinex.savecameraobjectfov", "cameras",
                        lambda k: k not in dead, warn)
    if n:
        stat["savecameraobjectfov"] = -n
    if kx.has("RSkoi_ComponentUtil"):
        n = 0
        for k, _, _ in (kx.payload_items("RSkoi_ComponentUtil") or []):
            n += _filter_bin_map(kx, "RSkoi_ComponentUtil", k,
                                 lambda x: x not in dead, warn)
        if n:
            stat["RSkoi_ComponentUtil"] = -n
    return stat


def set_node_name(scene, node, name):
    """改節點名稱：節點本身的 name 欄位 + treenodenaming 都要改。"""
    if "name" in node["data"]:
        node["data"]["name"] = name
    dk = node["data"]["dicKey"]
    kx = scene.kkex
    vb = kx.get(TNN, "names")
    if vb is None:
        return
    try:
        kind, payload = _container(vb)
        items = [list(t) for t in (map_split(payload) if payload else [])]
    except ValueError:
        return
    for it in items:
        if it[0] == dk:
            it[2] = pack(name)
            break
    else:
        items.append([dk, pack(dk), pack(name)])
    kx.set(TNN, "names", _rewrap(kind, map_build([tuple(i) for i in items])))



RE_NC_BLOCK = re.compile(
    r'<constraint\b[^>]*/>|<constraint\b[^>]*>.*?</constraint>', re.S)


def _nc_attr(blk, name):
    m = re.search(r'\b%s="([^"]*)"' % name, blk)
    return m.group(1) if m else ""


def drop_constraints(scene, alias_pats=(), path_pats=(), warn=None):
    """把符合條件的 NodesConstraints constraint 整條拿掉。

    為什麼需要這個：卡片裡可能存著綁到「這台電腦上根本不存在的骨架」的
    constraint（作者存檔時有那個 mod，你沒有）。NC 載入時建到那一條就
    整個迴圈中斷，排在它後面的一條都建不起來。實測到的例子是
    parentPath 走 cf_J_Vagina_* 的那幾條 —— 日誌裡同時有一整排
    "Game Object 'cf_J_Vagina_root' ... is missing"。

    丟掉之後要重新編號成 0..n-1，因為 timeline 的 NC 軌道是用
    parameter 指「第幾條」；少一條而不重編，後面整批會綁到隔壁那條上。

    alias_pats / path_pats 都是「包含就算命中」的子字串。兩個都沒給就什麼都不做。
    """
    warn = [] if warn is None else warn
    alias_pats = [p for p in (alias_pats or ()) if p]
    path_pats = [p for p in (path_pats or ()) if p]
    if not alias_pats and not path_pats:
        return 0
    vb = scene.kkex.get("nodesConstraints", "constraints")
    if vb is None:
        return 0
    xml = unpack(vb)
    if not isinstance(xml, str) or not xml.strip():
        return 0
    blocks = RE_NC_BLOCK.findall(xml)

    def hit(blk):
        al = _nc_attr(blk, "alias")
        if any(p in al for p in alias_pats):
            return True
        for side in ("parentPath", "childPath"):
            v = _nc_attr(blk, side)
            if v and any(p in v for p in path_pats):
                return True
        return False

    remap, keep, n, dropped = {}, [], 0, []
    for b in blocks:
        u = _nc_attr(b, "uniqueLoadId")
        if hit(b):
            remap[u] = None
            dropped.append((u, _nc_attr(b, "alias")))
            continue
        remap[u] = n
        keep.append(re.sub(r'(\buniqueLoadId=")[^"]*(")',
                           lambda m: m.group(1) + str(n) + m.group(2), b, count=1))
        n += 1
    if not dropped:
        return 0

    # 根標籤是 <constraints ...>，開頭跟 <constraint 一樣，一定要用 \b 分開
    m = re.match(r'^(.*?<constraints\b[^>]*>)(.*?)(</constraints>\s*)$', xml, re.S)
    if not m:
        warn.append("NodesConstraints 的 XML 找不到根標籤，這次沒有丟掉任何 constraint")
        return 0
    out_xml = m.group(1) + "".join(keep) + m.group(3)
    if "<constraints" not in out_xml:
        warn.append("產出的 NodesConstraints XML 沒有根標籤，這次沒有動它")
        return 0
    scene.kkex.set("nodesConstraints", "constraints", pack(out_xml))

    tl = scene.timeline_xml()
    if tl:
        h, body, t = S.split_root(tl)
        removed = [0]

        def fix(mm):
            blk = mm.group(0)
            hd = blk.split(">", 1)[0]
            if 'owner="NodesConstraints"' not in hd:
                return blk
            par = re.search(r'\bparameter="(-?\d+)"', hd)
            if not par:
                return blk
            new = remap.get(par.group(1), "keep")
            if new is None:
                removed[0] += 1
                return ""
            if new == "keep":
                return blk
            return blk.replace(hd, re.sub(
                r'(\bparameter=")-?\d+(")',
                lambda q: q.group(1) + str(new) + q.group(2), hd, count=1), 1)

        body = S.RE_INTERP.sub(fix, body)
        scene.set_timeline_xml(h + body + t)
        if removed[0]:
            log(f"  timeline：刪掉 {removed[0]} 條指向被移除 constraint 的軌道")

    for u, al in dropped:
        log(f"  丟掉 constraint uid={u} {al!r}")
    log(f"  剩下 {n} 條，重新編號成 0..{n - 1}")
    return len(dropped)


# ================================================================ prep
def prep(card_path, name, camera_dickey=None, out_path=None, subfolders=True,
         nc_rename=True, zero_chain=True, auto_camera=True, duration=None,
         index=None, exclude=None, drop_nc_alias=None, drop_nc_path=None):
    """整理一張卡並存檔。"""
    sc = prep_scene(card_path, name, camera_dickey, subfolders=subfolders,
                    nc_rename=nc_rename, zero_chain=zero_chain,
                    auto_camera=auto_camera, duration=duration, index=index,
                    exclude=exclude, drop_nc_alias=drop_nc_alias,
                    drop_nc_path=drop_nc_path)
    out_path = out_path or (os.path.splitext(card_path)[0] + "_prep.png")
    sc.save(out_path)
    log(f"\n寫出 {out_path}（{os.path.getsize(out_path):,} bytes）")
    return out_path


SYNC_IDS = ("objectEnabled", "guideObjectPos", "guideObjectRot", "guideObjectScale")


def _kf_at(blk, t):
    """這條軌道在第 t 秒的關鍵影格頭部屬性。

    取值規則跟 Timeline 一樣：第一格之前維持第一格，最後一格之後維持最後一格。
    """
    kfs = []
    for kf in RE_KEYFRAME.findall(blk):
        head = kf.split(">", 1)[0]
        mt = re.search(r'\btime="([-\d.eE+]+)"', head)
        if not mt:
            continue
        try:
            kfs.append((float(mt.group(1)), head))
        except ValueError:
            pass
    if not kfs:
        return None
    kfs.sort(key=lambda x: x[0])
    if t <= kfs[0][0]:
        return kfs[0][1]
    pick = kfs[0][1]
    for kt, head in kfs:
        if kt <= t + 1e-9:
            pick = head
        else:
            break
    return pick


def _vals(head, names):
    out = []
    for n in names:
        m = re.search(r'\b%s="([-\d.eE+]+)"' % n, head)
        if not m:
            return None
        try:
            out.append(float(m.group(1)))
        except ValueError:
            return None
    return out


def sync_static_state(scene, only=None, t=0.0):
    """把節點上「存檔時的值」改成 timeline 在第 t 秒的值。

    為什麼一定要做
    ==============
    Timeline 的軌道要等它自己跑起來才會套用到場景上。**載入卡片的那一刻**，
    Studio 畫面上顯示的是存在節點本身的值 —— 樹狀圖的勾（visible）、
    資料夾的 position / rotation / scale。

    合併只寫軌道、不動節點的話，這兩邊是對不起來的：軌道說「0 秒時第
    2~6 段要關掉、POV 鏈要在原點」，但節點上存的還是原卡的值。結果就是
    載入後該藏的沒藏、POV 資料夾帶著殘留位移、接管相機跟著歪 —— 然後
    只要把時間軸拖動一格，畫面又全部跳回正確的樣子。

    看起來像「工具沒把軌道寫進去」，其實軌道一直都在，只是還沒輪到它跑。

    只碰這四種軌道（SYNC_IDS），而且只碰 guideObjectPath 是空的那些 ——
    非空代表這條軌道控制的是角色底下的骨架／IK 點，不是節點自己的座標，
    寫回 node["data"] 會把角色的位置弄壞。

    only
    ====
    **一定要給。** only 是「合併工具自己寫過軌道的那些節點」的 dicKey 集合
    —— 包裝資料夾、(SFX)、POV 鏈上的資料夾，大約三十個。

    第一版沒有這個參數，結果是整張卡一千多條 guideObject 軌道全部被寫回去
    （position 489 個、rotation 472 個）。那些是原作者自己做的動畫，
    第一格往往不在 0 秒；照「第一格之前維持第一格」去算，等於把場景裡
    每個東西都搬到它自己那一段中途的姿勢。載入畫面因此整個跑掉。

    合併工具要負責的只有它自己加的那些軌道，原卡的動畫不該碰。

    回傳 {"visible": n, "position": n, "rotation": n, "scale": n}。
    """
    out = {"visible": 0, "position": 0, "rotation": 0, "scale": 0}
    xml = scene.timeline_xml()
    if not xml:
        return out
    body = S.split_root(xml)[1]
    ranks = S.rank_map(S.node_dickeys(scene.objects))
    by_rank = {}
    for node, _d, _p in S.iter_nodes(scene.objects):
        r = ranks.get(node["data"]["dicKey"])
        if r is not None:
            by_rank[r] = node

    for m in S.RE_INTERP.finditer(body):
        blk = m.group(0)
        h = blk.split(">", 1)[0]
        if 'owner="Timeline"' not in h or 'enabled="false"' in h:
            continue
        mt = re.search(r'\bid="([^"]*)"', h)
        mo = re.search(r'\bobjectIndex="(\d+)"', h)
        if not mt or not mo or mt.group(1) not in SYNC_IDS:
            continue
        tid = mt.group(1)
        gp = re.search(r'\bguideObjectPath="([^"]*)"', h)
        if tid != "objectEnabled" and gp is not None and gp.group(1):
            continue
        node = by_rank.get(int(mo.group(1)))
        if node is None:
            continue
        if only is not None and node["data"]["dicKey"] not in only:
            continue
        head = _kf_at(blk, t)
        if head is None:
            continue
        d = node["data"]

        if tid == "objectEnabled":
            mv = re.search(r'\bvalue="([^"]*)"', head)
            if mv:
                d["visible"] = (mv.group(1).lower() == "true")
                out["visible"] += 1
        elif tid == "guideObjectPos":
            v = _vals(head, ("valueX", "valueY", "valueZ"))
            if v and isinstance(d.get("position"), dict):
                d["position"] = {"x": v[0], "y": v[1], "z": v[2]}
                out["position"] += 1
        elif tid == "guideObjectScale":
            v = _vals(head, ("valueX", "valueY", "valueZ"))
            if v and isinstance(d.get("scale"), dict):
                d["scale"] = {"x": v[0], "y": v[1], "z": v[2]}
                out["scale"] += 1
        elif tid == "guideObjectRot":
            # 軌道存的是四元數，節點存的是尤拉角（Unity 的 ZXY 順序）
            q = _vals(head, ("valueX", "valueY", "valueZ", "valueW"))
            if q and isinstance(d.get("rotation"), dict):
                ex, ey, ez = _quat_to_euler(tuple(q))
                d["rotation"] = {"x": ex, "y": ey, "z": ez}
                out["rotation"] += 1
    return out


def dedupe_float32_times(scene):
    """拿掉「在 float32 精度下跟前一格撞在一起」的關鍵影格。回傳 (格數, 軌道數)。

    為什麼非做不可
    --------------
    Timeline 把一條軌道的關鍵影格放進 SortedList<float, Keyframe>。
    float 是 32 位元，所以兩個時間只要差距小於該位置的精度就是**同一個 key**，
    第二個 Add 會丟 ArgumentException: element already exists，
    而 Timeline 接到例外的處理是**整條 interpolable 丟掉** ——
    不是少一格，是那個物件那條軌道完全不見。

    合併正是製造這種撞車的元凶：原卡在 118 秒有兩格差 0.000023 秒的影格
    （118 秒的 float32 精度是 1.4e-5，還分得出來），平移到 362 秒之後
    精度變成 4.3e-5，兩格就變成同一個數字了。原卡沒事、合併後才爆，
    而且 log 裡只有一句 Timeline 的錯誤，畫面上是「某個角色的某段動作不見了」。

    實際案例：Charcard 合併卡的 objectIndex=1365 boneRot，
    362.64943 / 362.649453 這種成對的影格有十幾組，整條軌道被 Timeline 丟掉。

    做法是保留第一格、丟掉撞上的那幾格。時間差在 5e-5 以內的兩格，
    值本來就幾乎一樣，丟掉看不出差別 —— 整條軌道不見才看得出來。
    """
    xml = scene.timeline_xml()
    if not xml:
        return 0, 0
    head, body, tail = S.split_root(xml)
    total = tracks = 0

    def fix(m):
        nonlocal total, tracks
        blk = m.group(0)
        # 自閉合的 <interpolable …/> 沒有關鍵影格，碰都不要碰
        if not blk.endswith("</interpolable>"):
            return blk
        hm = re.match(r'<interpolable\b[^>]*?>', blk)
        if not hm:
            return blk
        h = hm.group(0)
        inner = blk[len(h):-len("</interpolable>")]
        out, prev, dropped = [], None, 0
        last = 0
        for km in S.RE_KEYFRAME_TIME.finditer(inner):
            # RE_KEYFRAME_TIME 只認 <keyframe time="…">，不會誤抓 curveKeyframe
            t = float(km.group(2))
            f = struct.unpack("f", struct.pack("f", t))[0]
            if prev is not None and f == prev:
                # 連同這一格整個 <keyframe …>…</keyframe> 一起拿掉
                end = _keyframe_end(inner, km.start())
                if end > km.start():
                    out.append(inner[last:km.start()])
                    last = end
                    dropped += 1
                    continue
            prev = f
        out.append(inner[last:])
        if not dropped:
            return blk
        total += dropped
        tracks += 1
        return h + "".join(out) + "</interpolable>"

    body = S.RE_INTERP.sub(fix, body)
    if total:
        scene.set_timeline_xml(head + body + tail)
    return total, tracks


def _keyframe_end(inner, start):
    """從 <keyframe 的起點找出它的結尾位置（含 /> 或 </keyframe>）。"""
    close = inner.find(">", start)
    if close < 0:
        return -1
    if inner[close - 1] == "/":
        return close + 1
    end = inner.find("</keyframe>", close)
    return (end + len("</keyframe>")) if end >= 0 else -1


def trim_keyframes(body, t_end, eps=1e-6):
    """把 t_end 之後的關鍵影格整個拿掉（`<curveKeyframe>` 是曲線參數，不會被碰到）。

    縮短時長一定要做這件事：只改 root 的 duration 的話，超出去的關鍵影格還在，
    合併之後會被平移到下一個場景的時段裡播出來。
    拿掉之後那條軌道就停在剩下的最後一格上，等於「動畫被剪短」。
    回傳 (新的 body, 拿掉幾格)。
    """
    n = [0]

    def rep(m):
        blk = m.group(0)
        head = blk.split(">", 1)[0]
        mt = re.search(r'\btime="([-\d.eE+]+)"', head)
        if mt and float(mt.group(1)) > t_end + eps:
            n[0] += 1
            return ""
        return blk
    return RE_KEYFRAME.sub(rep, body), n[0]


def trim_report(body, t_end, eps=1e-6):
    """縮短時長會砍掉哪些 id 的關鍵影格、各幾格 —— 只是拿來印，不改東西。

    為什麼要有這個：光講「拿掉 62 格」看不出嚴重性。62 格如果全是相機運鏡，
    沒什麼；如果裡面有角色的骨架/揺れ物軌道，那就是**動畫被攔腰切斷** ——
    那條軌道會停在剩下的最後一格上，角色的頭髮、飾品就卡在半路的姿勢
    回不來，而原卡單獨播是好的（它播得完整段）。
    把 id 列出來，這種事就不會又默默發生一次。
    """
    out = {}
    for m in S.RE_INTERP.finditer(body):
        blk = m.group(0)
        hm = S.RE_INTERP_HEAD.match(blk)
        head = hm.group(0) if hm else blk
        mi = re.search(r'\bid="([^"]*)"', head)
        tid = mi.group(1) if mi else "?"
        cnt = 0
        for km in RE_KEYFRAME.finditer(blk):
            kh = km.group(0).split(">", 1)[0]
            mt = re.search(r'\btime="([-\d.eE+]+)"', kh)
            if mt and float(mt.group(1)) > t_end + eps:
                cnt += 1
        if cnt:
            out[tid] = out.get(tid, 0) + cnt
    return out


def set_scene_duration(scene, dur):
    """改這張卡的 timeline 總時長。加長、縮短都可以。

    加長：內容停在最後一格，後面的場景整批往後挪
    （offset 是照各卡的時長算的），相機鏈歸零也會跟著改到新的結尾。
    縮短：超出新結尾的關鍵影格會被拿掉（見 trim_keyframes），
    動畫等於被剪短，後面的場景整批往前挪。

    回傳 (原本, 新的) 或 None（沒改）。
    """
    if not dur:
        return None
    xml = scene.timeline_xml()
    if not xml:
        return None
    cur = scene.duration() or 0.0
    new = float(dur)
    if abs(new - cur) <= 1e-6:
        return None
    head, body, tail = S.split_root(xml)
    if new < cur:
        detail = trim_report(body, new)
        body, n_cut = trim_keyframes(body, new)
        if n_cut:
            log(f"  縮短時長：拿掉 {n_cut} 個超出 {S.fmt_time(new)} 秒的關鍵影格")
            if detail:
                log("    被切到的軌道："
                    + "、".join(f"{k}×{v}" for k, v in
                                sorted(detail.items(), key=lambda kv: -kv[1])[:12])
                    + ("…" if len(detail) > 12 else ""))
                body_ids = set(detail) - set(kkref.GLOBAL_TIMELINE_IDS) - {
                    "guideObjectPos", "guideObjectRot", "guideObjectScale"}
                if body_ids:
                    log("    [注意] 這裡面有角色/物件自己的軌道（"
                        + "、".join(sorted(body_ids)[:8])
                        + "）—— 那幾條會停在剩下的最後一格，"
                        "頭髮、飾品之類的會卡在半路的姿勢。"
                        "原卡單獨播是好的，因為它播得完整段。"
                        "不想被切就把這一段的時長設成至少涵蓋內容的值。")
    head = S.set_root_attr(head, "duration", S.fmt_time(new))
    scene.set_timeline_xml(head + body + tail)
    return (cur, new)


def prep_scene(card_path, name, camera_dickey=None, subfolders=True,
               nc_rename=True, zero_chain=True, scene=None, warn=None,
               auto_camera=True, duration=None, index=None, exclude=None,
               drop_nc_alias=None, drop_nc_path=None):
    """把作者的原始卡整理成可以合併的形狀。

      1. 只留一台相機，其餘連同變空的父資料夾一起刪掉
      2. 留下的相機連同它的父資料夾鏈，搬到新的根資料夾 (CAM) 底下，
         鏈上的資料夾由外而內改名 POV 1..N，相機本身改成卡片名稱
      3. 其餘所有根節點包進一個叫 <name> 的資料夾，並建 (MAP)(FX)(CHAR)(SFX)
    """
    warn = [] if warn is None else warn
    sfx = f" {index}" if index else ""       # 場景順序後綴，避免多張卡的資料夾同名
    sc = scene if scene is not None else S.Scene(card_path)
    ch = set_scene_duration(sc, duration)
    if ch:
        log(f"  時長 {S.fmt_time(ch[0])} -> {S.fmt_time(ch[1])} 秒"
            f"（這張多停 {S.fmt_time(ch[1] - ch[0])} 秒，後面的場景一起延後）")
    n_nc = drop_constraints(sc, drop_nc_alias, drop_nc_path, warn)
    if n_nc:
        log(f"  依照 --drop-nc 丟掉 {n_nc} 條 constraint")
    old_nodes = S.node_dickeys(sc.objects)
    path_blocks = pop_camera_lock_tracks(sc)
    cams = sc.cameras()
    auto_cam = False
    use_path = bool(path_blocks) and (camera_dickey == PATH_CAM or not cams)
    if camera_dickey == PATH_CAM:
        camera_dickey = None
    if use_path:
        node, eye, ang, fov = add_view_camera(sc, name)
        n_t = transplant_camera_path(sc, node, path_blocks)
        camera_dickey = node["data"]["dicKey"]
        auto_cam = True
        cams = sc.cameras()
        old_nodes = S.node_dickeys(sc.objects)
        pos = node["data"]["position"]
        log(f"  這張卡是用 timeline 的相機路徑在運鏡，生一台相機把 {n_t} 條軌道"
            f"整段接手（dicKey={node['data']['dicKey']}，"
            f"起點 {pos['x']:.3f}, {pos['y']:.3f}, {pos['z']:.3f}）"
            + (f"／FOV {fov:g}" if fov else ""))
        if len(cams) > 1:
            log(f"  （這張卡原本還有 {len(cams) - 1} 台相機，會一起刪掉）")
    elif not cams:
        if not auto_camera:
            raise SystemExit(f"{os.path.basename(card_path or name)} 裡沒有相機"
                             "（勾選「沒有相機時自動新增」才會自動生一台）")
        node, eye, ang, fov = add_view_camera(sc, name)
        auto_cam = True
        cams = sc.cameras()
        old_nodes = S.node_dickeys(sc.objects)
        log(f"  這張卡沒有相機，自動生一台鎖住初始視角"
            f"（dicKey={node['data']['dicKey']}，"
            f"位置 {eye['x']:.3f}, {eye['y']:.3f}, {eye['z']:.3f}／"
            f"角度 {ang['x']:.3f}, {ang['y']:.3f}, {ang['z']:.3f}"
            + (f"／FOV {fov:g}" if fov else "") + "）")
    if path_blocks and not use_path:
        log(f"  砍掉 {len(path_blocks)} 條會鎖死相機的軌道"
            "（timeline 的相機位置 / 相機旋轉）")
        warn.append("這張卡的 timeline 相機位置 / 相機旋轉軌道被砍掉了，"
                    "那段運鏡不會保留（選「timeline 相機路徑」才會接手）")
    n_vis = hide_cameras(sc)
    if n_vis:
        log(f"  {n_vis} 台相機原本在樹狀圖打著勾，已取消（避免相機物件浮在半空中）")
    pmap = S.parent_map(sc.objects)

    if len(cams) > 1 and camera_dickey is None:
        print(f"{os.path.basename(card_path)} 有 {len(cams)} 台相機，"
              f"請用 --camera <dicKey> 指定要留哪一台：")
        for n, p in cams:
            chain = [a["data"].get("name") for a in S.ancestors(n, pmap)][::-1]
            print(f"   --camera {n['data']['dicKey']:<6} active={n['data'].get('active')}"
                  f"  {'/'.join(chain + [str(n['data'].get('name'))])}")
        raise SystemExit(2)

    keep = None
    for n, _ in cams:
        if camera_dickey is None or n["data"]["dicKey"] == camera_dickey:
            keep = n
            break
    if keep is None:
        raise SystemExit(f"找不到 dicKey={camera_dickey} 的相機")
    log(f"  保留相機 dicKey={keep['data']['dicKey']}")
    if not keep["data"].get("active"):
        # 選來用的相機一定要是「使用中」—— 原卡裡沒在用的話，合併完載入也不會切到它
        keep["data"]["active"] = True
        log("  這台相機原本沒有在使用，改成使用中")

    # ---- 1. 刪掉其他相機 + 變空的父資料夾 ----
    deleted = []

    # ---- 1a. 手動排除的節點 ----
    #
    # 為什麼會有這個開關：某些物件在合併卡裡會讓 NodesConstraints 在載入時
    # 整個迴圈中斷，排在它後面的 constraint 一條都建不起來。實測到的例子是
    # 液體模組的 "Cum Generator"（BetterPenetration 有 patch NC）。
    # 這種東西沒有通用的辨識方法，所以做成選填：沒填就完全不動，
    # 行為跟以前一模一樣。
    if exclude:
        want = set(int(v) for v in exclude)
        pmap_ex = S.parent_map(sc.objects)
        hit = set()
        for n, _d, _p in list(S.iter_nodes(sc.objects)):
            dk = n["data"]["dicKey"]
            if dk not in want or dk in hit:
                continue
            sub = S.subtree_dickeys(n)
            deleted += sub
            hit.update(sub)
            ent = pmap_ex.get(id(n))
            if ent:
                S.detach(n, ent[1])
            log(f"  排除 dicKey={dk}"
                f"{'（連同底下共 %d 個節點）' % len(sub) if len(sub) > 1 else ''}")
        miss = want - hit
        if miss:
            warn.append("--exclude 給的這些 dicKey 卡片裡沒有："
                        + "、".join(str(v) for v in sorted(miss)))

    for n, _ in cams:
        if n is keep:
            continue
        deleted += S.subtree_dickeys(n)
        parent, container = pmap[id(n)]
        S.detach(n, container)
        # 往上收：父資料夾空了就一起刪
        while parent is not None and parent["type"] == 3:
            ch = parent["data"].get("child")
            if ch:
                break
            gp, gc = pmap[id(parent)]
            deleted.append(parent["data"]["dicKey"])
            S.detach(parent, gc)
            parent = gp
    if deleted:
        log(f"  刪掉 {len(deleted)} 個節點（多餘的相機與變空的資料夾）")

    # ---- 2. (CAM) 根資料夾 ----
    pmap = S.parent_map(sc.objects)
    chain = S.ancestors(keep, pmap)[::-1]          # 由外而內
    nxt = max(S.all_dickeys(sc.objects)) + 1
    cam_root = S.new_folder(nxt, "(CAM)")
    nxt += 1
    if chain:
        top = chain[0]
        _, container = pmap[id(top)]
        S.detach(top, container)
        cam_root["data"]["child"].append(top)
        for i, f in enumerate(chain, 1):
            set_node_name(sc, f, f"POV {i}{sfx}")
    else:
        _, container = pmap[id(keep)]
        S.detach(keep, container)
        cam_root["data"]["child"].append(keep)
    cam_label = f"{name} CAM"
    set_node_name(sc, keep, cam_label)
    log(f"  (CAM) 底下的父子鏈："
        f"{' > '.join('POV %d%s' % (i, sfx) for i in range(1, len(chain) + 1))}"
        f" > {cam_label}" if chain else f"  (CAM) 底下直接放相機 {cam_label}")

    # ---- 3. 包裝資料夾 ----
    rest = list(sc.objects.values())
    wrapper = S.new_folder(nxt, name)
    nxt += 1
    if subfolders:
        for sub in ("(MAP)", "(FX)", "(CHAR)", "(SFX)"):
            wrapper["data"]["child"].append(S.new_folder(nxt, sub + sfx))
            nxt += 1
    wrapper["data"]["child"].extend(rest)
    sc.objects = type(sc.objects)([(wrapper["data"]["dicKey"], wrapper),
                                   (cam_root["data"]["dicKey"], cam_root)])
    log(f"  包裝資料夾 {name!r}（dicKey={wrapper['data']['dicKey']}）"
        f"，底下 {len(rest)} 個原本的根節點")

    # ---- 4. 新節點的名字寫進 treenodenaming ----
    for n, _, _ in S.iter_nodes(sc.objects):
        if n["data"]["dicKey"] >= cam_root["data"]["dicKey"]:
            set_node_name(sc, n, n["data"].get("name") or "")

    # ---- 5. 清掉被刪節點的參照 + 重算 RANK ----
    stat = cleanup_deleted(sc, old_nodes, deleted, warn)
    for k, v in stat.items():
        log(f"  {k}：{v}")

    # ---- 5b. 相機鏈在場景結束時歸零 ----
    if zero_chain and chain:
        t_end = sc.duration()
        if t_end:
            n = zero_camera_chain(sc, chain, t_end, warn)
            log(f"  相機鏈歸零：{len(chain)} 個 POV 資料夾在 {S.fmt_time(t_end)} 秒瞬間回到原點"
                f"（動到 {n} 條軌道）")
        else:
            warn.append("這張卡沒有 timeline，相機鏈沒有歸零")

    # ---- 6. NC alias 改名 ----
    if nc_rename:
        n = rename_nc_aliases(sc, name, warn)
        if n:
            log(f"  NC alias 改名：{n} 條 -> '{name} | …'")

    for w in warn:
        log("[注意] " + w)
    return sc


# ================================================================ NC alias 重新命名
_XML_ESC = {"&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;"}


def xml_escape(s):
    return "".join(_XML_ESC.get(c, c) for c in s)


def xml_unescape(s):
    for k, v in (("&quot;", '"'), ("&gt;", ">"), ("&lt;", "<"), ("&amp;", "&")):
        s = s.replace(k, v)
    return s


def display_names(scene):
    """dicKey -> 顯示名稱。treenodenaming 優先，其次節點自己的 name 欄位。"""
    out = {}
    for n, _, _ in S.iter_nodes(scene.objects):
        d = n["data"]
        out[d["dicKey"]] = d.get("name") or ""
    vb = scene.kkex.get(TNN, "names")
    if vb is not None:
        try:
            _, payload = _container(vb)
            for k, _, v in (map_split(payload) if payload else []):
                nm = unpack(v)
                if isinstance(nm, str) and nm.strip():
                    out[k] = nm
        except ValueError:
            pass
    return out


def rename_nc_aliases(scene, wrapper_name, warn):
    """把 NC 的 alias 改成 `<場景名> | <原始名稱>`，並確保不會有重複。

    取名順序（前面取不到才往下退）：
      1. 原本的 alias
      2. parentPath 的最後一段（骨架路徑，例如 cf_n_height）
      3. 父節點的顯示名稱（去掉尾巴的數字後綴）
      4. childPath 的最後一段
      5. NC <流水號>

    作者的原始卡本來就有不少沒命名的（場景1 有 29 條、場景2 有 19 條 alias 是
    空字串），也有同名的（Left Hand | Charcard 就有 9 條），所以最後再加流水號
    去重，面板上才分得開。
    """
    vb = scene.kkex.get("nodesConstraints", "constraints")
    if vb is None:
        return 0
    xml = unpack(vb)
    if not isinstance(xml, str) or not xml.strip():
        return 0
    head, body, tail = _split_xml_root(xml, "constraints")
    if head is None:
        warn.append("nodesConstraints 的 XML 形狀不對，alias 沒有改名")
        return 0
    nodes = S.node_dickeys(scene.objects)
    inv = {v: k for k, v in S.rank_map(nodes).items()}
    names = display_names(scene)
    used = {}
    n_derived = [0]
    seq = [0]

    def last_seg(path):
        return xml_unescape(path).rstrip("/").rsplit("/", 1)[-1] if path else ""

    def one(m):
        blk = m.group(0)
        ma = re.search(r'\balias="([^"]*)"', blk)
        if not ma:
            return blk
        seq[0] += 1
        base = xml_unescape(ma.group(1)).strip()
        if not base:
            n_derived[0] += 1
            pp = re.search(r'\bparentPath="([^"]*)"', blk)
            base = last_seg(pp.group(1) if pp else "")
        if not base:
            mp = re.search(r'\bparentObjectIndex="(-?\d+)"', blk)
            if mp:
                dk = inv.get(int(mp.group(1)))
                base = re.sub(r" \d+$", "", names.get(dk, "") or "").strip()
        if not base:
            cp = re.search(r'\bchildPath="([^"]*)"', blk)
            base = last_seg(cp.group(1) if cp else "")
        if not base:
            base = f"NC {seq[0]}"
        alias = f"{wrapper_name} | {base}"
        n = used.get(alias, 0) + 1
        used[alias] = n
        if n > 1:                                   # 同名的加流水號
            alias = f"{alias} {n}"
        return blk[:ma.start(1)] + xml_escape(alias) + blk[ma.end(1):]

    body2 = re.sub(r'<constraint\b[^>]*?/>|<constraint\b[^>]*?>.*?</constraint>',
                   one, body, flags=re.S)
    scene.kkex.set("nodesConstraints", "constraints", pack(head + body2 + tail))
    dup = sum(v - 1 for v in used.values() if v > 1)
    if n_derived[0] or dup:
        warn.append(f"NC alias：{n_derived[0]} 條原本沒有名字（作者就沒命名），"
                    f"已改用骨架/節點名稱補上；另有 {dup} 條同名，加了流水號")
    return seq[0]



# ================================================================ 相機接管（步驟 6）
def _first_camera(node):
    for n, _, _ in S.iter_nodes([node]):
        if n["type"] == 5:
            return n
    return None


def _common_name(a, b):
    """兩台相機名字的共同開頭，例如 Scenecard_(1) / _(2) -> Scenecard"""
    i = 0
    while i < min(len(a), len(b)) and a[i] == b[i]:
        i += 1
    return (a[:i].rstrip(" _-|(（[") or "Camera")


def build_camera_switch(scene, cam_name, warn):
    """把第二張的 POV 鏈掛進第一張的相機鏈，並生一台由 NC 接管的新相機。

    照實卡（步驟 6）的做法：
      (CAM)                          <- 底卡的
        POV 1 > POV 2 > POV 3
          [場景1 相機]
          POV 1 > POV 2 > POV 3 > POV 4      <- 第二張的整串搬進來
            [場景2 相機]
            [新相機]                          <- 新生的，active=true
    第二張原本的 (CAM) 根資料夾刪掉。
    """
    roots = [n for n in scene.objects.values()
             if n["type"] == 3 and (n["data"].get("name") or "") == "(CAM)"]
    if len(roots) < 2:
        warn.append(f"找不到兩個 (CAM) 根資料夾（找到 {len(roots)} 個），沒有做相機接管")
        return None
    ra, rb = roots[0], roots[1]
    cam_a, cam_b = _first_camera(ra), _first_camera(rb)
    if cam_a is None or cam_b is None:
        warn.append("(CAM) 裡找不到相機，沒有做相機接管")
        return None
    pmap = S.parent_map(scene.objects)
    par_a = pmap[id(cam_a)][0]
    par_b = pmap[id(cam_b)][0]
    if par_a is None or par_b is None:
        warn.append("相機沒有父資料夾，沒有做相機接管")
        return None

    # 第二張的 POV 鏈整串搬到第一張相機的同層
    moved = list(rb["data"]["child"])
    rb["data"]["child"] = []
    par_a["data"]["child"].extend(moved)
    if par_b is rb:                 # 第二張的相機本來就直接掛在 (CAM) 底下
        par_b = par_a
    for k, v in list(scene.objects.items()):
        if v is rb:
            del scene.objects[k]
            break

    name = cam_name or _common_name(cam_a["data"].get("name") or "",
                                    cam_b["data"].get("name") or "")
    nxt = max(S.all_dickeys(scene.objects)) + 1
    new_cam = {"type": 5, "data": {
        "dicKey": nxt,
        "position": {"x": 0.0, "y": 0.0, "z": 0.0},
        "rotation": {"x": 0.0, "y": 0.0, "z": 0.0},
        "scale": {"x": 1.0, "y": 1.0, "z": 1.0},
        "treeState": 1, "visible": False,
        "name": name, "active": True}}
    par_b["data"]["child"].append(new_cam)
    cam_a["data"]["active"] = False
    cam_b["data"]["active"] = False
    log(f"  相機接管：新相機 {name!r}（dicKey={nxt}）掛在 {par_b['data'].get('name')!r} 底下，"
        f"第二張的 (CAM) 資料夾已移除")
    return {"cam_a": cam_a["data"]["dicKey"], "cam_b": cam_b["data"]["dicKey"],
            "new": nxt, "name": name,
            "name_a": cam_a["data"].get("name") or "場景1",
            "name_b": cam_b["data"].get("name") or "場景2"}


# NC constraint 的模板 —— 欄位照實卡抄。originalParent* 在
# positionChangeFactor / rotationChangeFactor / scaleChangeFactor 都是 1、
# offset 中性、damp 與 smooth 時間都是 0 的情況下會完全抵消
# （NodesConstraints.cs 的 UpdatePosition/UpdateRotation：
#   targetPos = parent.TransformPoint(movement * (factor-1) + offset) = parent.position
#   targetRot = originalParentRotation * inverse(originalParentRotation) * parent.rotation），
# 所以填單位值即可；scale 不能填 0（程式會拿它當除數）。
NC_TEMPLATE = (
    '<constraint enabled="{enabled}" parentObjectIndex="{parent}" parentPath=""'
    ' childObjectIndex="{child}" childPath="" uniqueLoadId="{uid}"'
    ' position="true" mirrorPosition="false"'
    ' positionOffsetX="0" positionOffsetY="0" positionOffsetZ="0"'
    ' rotation="true" mirrorRotation="false" lookAt="false"'
    ' rotationOffsetW="1" rotationOffsetX="0" rotationOffsetY="0" rotationOffsetZ="0"'
    ' scale="true" mirrorScale="false" scaleOffsetX="1" scaleOffsetY="1" scaleOffsetZ="1"'
    ' originalParentPositionX="0" originalParentPositionY="0" originalParentPositionZ="0"'
    ' originalParentRotationX="0" originalParentRotationY="0" originalParentRotationZ="0"'
    ' originalParentRotationW="1"'
    ' originalParentScaleX="1" originalParentScaleY="1" originalParentScaleZ="1"'
    ' positionChangeFactor="1" rotationChangeFactor="1" scaleChangeFactor="1"'
    ' positionDamp="0" rotationDamp="0" scaleDamp="0"'
    ' positionLocksX="true" positionLocksY="true" positionLocksZ="true"'
    ' rotationLocksX="true" rotationLocksY="true" rotationLocksZ="true"'
    ' scaleLocksX="true" scaleLocksY="true" scaleLocksZ="true"'
    ' posSmoothConnectionTime="0" posSmoothDisConnectionTime="0"'
    ' rotSmoothConnectionTime="0" rotSmoothDisConnectionTime="0"'
    ' scaleSmoothConnectionTime="0" scaleSmoothDisConnectionTime="0"'
    ' resetOriginalPosition="true" resetOriginalRotation="true" resetOriginalScale="true"'
    ' alias="{alias}" dynamic="false" />')

NC_CURVE = ('<curveKeyframe time="0" value="0" inTangent="0" outTangent="1" />'
            '<curveKeyframe time="1" value="1" inTangent="1" outTangent="0" />')


ENABLE_LEAD = 0.001      # 第一段的啟用軌道在這個時間點才打開


def object_enabled_track(object_index, points):
    """物件啟用（樹狀圖的勾）軌道：沒輪到的場景整個取消勾選。"""
    kfs = "".join(f'<keyframe time="{S.fmt_time(t)}" value="{"true" if v else "false"}">'
                  f'{NC_CURVE}</keyframe>' for t, v in points)
    return ('<interpolable enabled="true" owner="Timeline"'
            f' objectIndex="{object_index}" id="objectEnabled"'
            f' bgColorR="1" bgColorG="1" bgColorB="1" alias="">{kfs}</interpolable>')


RE_GROUP_TOK = re.compile(r'<interpolableGroup\b[^>]*?>|</interpolableGroup>')


def insert_into_group(body, name, tracks):
    """把 tracks 塞進名字叫 name 的 <interpolableGroup> 裡（結尾標籤之前）。

    為什麼要深度計數：來源卡本來就可能有自己的群組，一路巢狀下去。
    用 '.*?</interpolableGroup>' 會在第一個內層結尾就收掉，軌道會被塞錯層。

    找不到那個群組就回 None —— 呼叫端自己決定要不要退回放在最外層
    （例如 --no-group 跑出來的卡根本沒有群組）。
    """
    for m in RE_GROUP_TOK.finditer(body):
        tok = m.group(0)
        if tok.startswith("</") or tok.endswith("/>"):
            continue
        if ('name="%s"' % xml_escape(name)) not in tok:
            continue
        depth = 1
        for m2 in RE_GROUP_TOK.finditer(body, m.end()):
            t2 = m2.group(0)
            if t2.endswith("/>"):
                continue                      # 自我結束，不影響深度
            if t2.startswith("</"):
                depth -= 1
                if depth == 0:
                    return body[:m2.start()] + tracks + body[m2.start():]
            else:
                depth += 1
        return None
    return None


RE_NC_PARAM = re.compile(
    r'<interpolable\b(?=[^>]*owner="NodesConstraints")'
    r'(?=[^>]*id="constraintEnabled")[^>]*?\bparameter="([^"]*)"')


def _attr(blk, name):
    m = re.search(r'\b%s="([^"]*)"' % name, blk)
    return m.group(1) if m else None


def _attr_int(blk, name):
    v = _attr(blk, name)
    try:
        return int(v)
    except (TypeError, ValueError):
        return None


def nc_enabled_track(parameter, points):
    kfs = "".join(f'<keyframe time="{S.fmt_time(t)}" value="{"true" if v else "false"}">'
                  f'{NC_CURVE}</keyframe>' for t, v in points)
    return ('<interpolable enabled="true" owner="NodesConstraints" id="constraintEnabled"'
            f' parameter="{parameter}" bgColorR="1" bgColorG="1" bgColorB="1"'
            f' alias="">{kfs}</interpolable>')


# ================================================================ 相機鏈歸零
GUIDE_TRACKS = {
    "guideObjectPos": ('valueX="0" valueY="0" valueZ="0"',
                       '<curveKeyframe time="0" value="0" inTangent="0" outTangent="0" />'
                       '<curveKeyframe time="1" value="1" inTangent="0" outTangent="0" />'),
    "guideObjectRot": ('valueX="0" valueY="0" valueZ="0" valueW="1"',
                       '<curveKeyframe time="0" value="0" inTangent="0" outTangent="1" />'
                       '<curveKeyframe time="1" value="1" inTangent="1" outTangent="0" />'),
    "guideObjectScale": ('valueX="1" valueY="1" valueZ="1"',
                         '<curveKeyframe time="0" value="0" inTangent="0" outTangent="1" />'
                         '<curveKeyframe time="1" value="1" inTangent="1" outTangent="0" />'),
}

RE_KEYFRAME = re.compile(r'<keyframe\b[^>]*/>|<keyframe\b[^>]*>(?:(?!</?keyframe\b).)*?</keyframe>',
                         re.S)


def _euler_to_quat(x, y, z):
    """Unity 的 Quaternion.Euler：先 Z、再 X、最後 Y（q = qy * qx * qz）。"""
    import math

    def mul(a, b):
        ax, ay, az, aw = a
        bx, by, bz, bw = b
        return (aw * bx + ax * bw + ay * bz - az * by,
                aw * by - ax * bz + ay * bw + az * bx,
                aw * bz + ax * by - ay * bx + az * bw,
                aw * bw - ax * bx - ay * by - az * bz)
    hx, hy, hz = (math.radians(v) / 2 for v in (x, y, z))
    qx = (math.sin(hx), 0.0, 0.0, math.cos(hx))
    qy = (0.0, math.sin(hy), 0.0, math.cos(hy))
    qz = (0.0, 0.0, math.sin(hz), math.cos(hz))
    return mul(mul(qy, qx), qz)


def _quat_to_euler(q):
    """Unity 的 Quaternion.eulerAngles（ZXY）：四元數 -> 角度。"""
    import math
    x, y, z, w = q
    n = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    x, y, z, w = x / n, y / n, z / n, w / n
    # 旋轉矩陣 M = Ry * Rx * Rz
    m12 = 2.0 * (y * z - w * x)
    m10 = 2.0 * (x * y + w * z)
    m11 = 1.0 - 2.0 * (x * x + z * z)
    m02 = 2.0 * (x * z + w * y)
    m22 = 1.0 - 2.0 * (x * x + y * y)
    sx = max(-1.0, min(1.0, -m12))
    ax = math.asin(sx)
    if abs(sx) > 0.9999995:                    # 萬向鎖
        ay = math.atan2(-2.0 * (x * z - w * y), 1.0 - 2.0 * (y * y + z * z))
        az = 0.0
    else:
        ay = math.atan2(m02, m22)
        az = math.atan2(m10, m11)
    return tuple((math.degrees(v) + 360.0) % 360.0 for v in (ax, ay, az))


def _quat_rotate(q, v):
    """用四元數轉一個向量。"""
    qx, qy, qz, qw = q
    vx, vy, vz = v
    # t = 2 * (q.xyz X v)
    tx = 2.0 * (qy * vz - qz * vy)
    ty = 2.0 * (qz * vx - qx * vz)
    tz = 2.0 * (qx * vy - qy * vx)
    return (vx + qw * tx + (qy * tz - qz * ty),
            vy + qw * ty + (qz * tx - qx * tz),
            vz + qw * tz + (qx * ty - qy * tx))


def scene_view(scene):
    """卡片存檔當下的自由相機視角 -> (眼睛位置, 角度, FOV)。

    Studio 的自由相機：rotation = Euler(rotate)，
    position = pos + Euler(rotate) * distance（CameraControl.CameraUpdate）。
    """
    cd = getattr(scene.sc, "cameraSaveData", None) or {}
    pos = cd.get("position") or {"x": 0.0, "y": 0.0, "z": 0.0}
    rot = cd.get("rotation") or {"x": 0.0, "y": 0.0, "z": 0.0}
    dis = cd.get("distance") or {"x": 0.0, "y": 0.0, "z": 0.0}
    q = _euler_to_quat(rot["x"], rot["y"], rot["z"])
    dx, dy, dz = _quat_rotate(q, (dis["x"], dis["y"], dis["z"]))
    eye = {"x": float(pos["x"]) + dx, "y": float(pos["y"]) + dy,
           "z": float(pos["z"]) + dz}
    ang = {"x": float(rot["x"]), "y": float(rot["y"]), "z": float(rot["z"])}
    return eye, ang, cd.get("fieldOfView")


CAM_LOCK_IDS = ("cameraPos", "cameraRot")
TRACK_FROM_CAM = {"cameraPos": "guideObjectPos", "cameraRot": "guideObjectRot"}
PATH_CAM = -1        # camera_dickey 傳這個 = 用 timeline 的相機路徑生一台相機


def _global_cam_tracks(body):
    """在 timeline body 裡找「相機位置 / 相機旋轉」這兩條全域軌道。"""
    for m in S.RE_INTERP.finditer(body):
        blk = m.group(0)
        hm = re.match(r'<interpolable\b[^>]*?>', blk)
        h = hm.group(0) if hm else blk
        if "objectIndex=" in h:
            continue
        mid = re.search(r'\bid="([^"]*)"', h)
        if not mid or mid.group(1) not in CAM_LOCK_IDS:
            continue
        yield m, blk, h, mid.group(1)


def has_camera_path(scene):
    """這張卡是不是用 timeline 的相機位置 / 相機旋轉在運鏡。"""
    xml = scene.timeline_xml()
    if not xml:
        return False
    _, body, _ = S.split_root(xml)
    for _ in _global_cam_tracks(body):
        return True
    return False


def pop_camera_lock_tracks(scene):
    """把「相機位置 / 相機旋轉」全域軌道從 timeline 抽出來，回傳 {id: 整段 xml}。

    這兩條是全域的：一旦存在就會把主相機整個鎖死，而且合併之後會影響到
    所有場景的相機。抽掉之後可以原封不動改掛到某台相機物件上（見
    transplant_camera_path），這樣同一段運鏡就只影響自己那一段。
    """
    xml = scene.timeline_xml()
    if not xml:
        return {}
    head, body, tail = S.split_root(xml)
    found, out, last = {}, [], 0
    for m, blk, _h, cid in _global_cam_tracks(body):
        found[cid] = blk
        out.append(body[last:m.start()])
        last = m.end()
    if not found:
        return {}
    out.append(body[last:])
    scene.set_timeline_xml(head + "".join(out) + tail)
    return found


def transplant_camera_path(scene, node, blocks):
    """把抽出來的相機路徑原封不動改掛到相機物件上。

    全域的 cameraPos / cameraRot 記的就是主相機的世界座標與世界四元數
    （已對照實卡驗證），而相機物件被啟用時 Studio 直接把主相機設成該物件的
    世界座標，所以只要相機掛在沒有位移的父節點下，把關鍵影格（含曲線）
    整段搬成 guideObjectPos / guideObjectRot 就是同一段運鏡。
    """
    rank = S.rank_map(S.node_dickeys(scene.objects))[node["data"]["dicKey"]]
    xml = scene.timeline_xml()
    if not xml:
        return 0
    head, body, tail = S.split_root(xml)
    n = 0
    for cid, blk in blocks.items():
        tid = TRACK_FROM_CAM[cid]
        hm = re.match(r'<interpolable\b[^>]*?>', blk)
        h = hm.group(0) if hm else ""
        inner = blk[len(h):]                      # 關鍵影格 + </interpolable>
        en = re.search(r'enabled="([^"]*)"', h)
        body += (f'<interpolable enabled="{en.group(1) if en else "true"}"'
                 f' owner="Timeline" objectIndex="{rank}" id="{tid}"'
                 f' guideObjectPath="" bgColorR="1" bgColorG="1" bgColorB="1"'
                 f' alias="">{inner}')
        n += 1
        # 節點本身的座標對齊第一格，軌道還沒開始時才不會跳
        k = RE_KEYFRAME.search(inner)
        if k:
            v = {a: float(b) for a, b in re.findall(
                r'value([XYZW])="([-\d.eE+]+)"', k.group(0).split(">")[0])}
            if cid == "cameraPos" and {"X", "Y", "Z"} <= set(v):
                node["data"]["position"] = {"x": v["X"], "y": v["Y"], "z": v["Z"]}
            elif cid == "cameraRot" and {"X", "Y", "Z", "W"} <= set(v):
                ex, ey, ez = _quat_to_euler((v["X"], v["Y"], v["Z"], v["W"]))
                node["data"]["rotation"] = {"x": ex, "y": ey, "z": ez}
    scene.set_timeline_xml(head + body + tail)
    return n


def hide_cameras(scene):
    """所有相機物件都取消樹狀圖的打勾（visible），不然會有東西浮在半空中。"""
    n = 0
    for node, _ in scene.cameras():
        if node["data"].get("visible"):
            node["data"]["visible"] = False
            n += 1
    return n


CAMFOV = "com.rikkibalboa.bepinex.savecameraobjectfov"


def set_camera_fov(scene, dickey, fov):
    """把某台相機物件的 FOV 寫進 SaveCameraObjectFOV 的 cameras 表。

    這個外掛記的是 {相機 dicKey: FOV}，切到那台相機時會把 FOV 套上去。
    手動在 Studio 裡新增相機時外掛也是這樣寫，所以自動生的相機也要有。
    """
    kx = scene.kkex
    if not kx.has(CAMFOV) or fov is None:
        return False
    vb = kx.get(CAMFOV, "cameras")
    kind, items = None, []
    if vb is not None:
        try:
            kind, payload = _container(vb)
            items = [list(t) for t in (map_split(payload) if payload else [])]
        except ValueError:
            kind, items = None, []
    for it in items:
        if it[0] == dickey:
            it[2] = pack(float(fov))
            break
    else:
        items.append([dickey, pack(dickey), pack(float(fov))])
    body = map_build([tuple(i) for i in items])
    kx.set(CAMFOV, "cameras", _rewrap(kind, body) if kind is not None else body)
    return True


def ensure_active_camera(scene, prefer=None, fallback=()):
    """讓場景裡剛好一台相機是「使用中」（OICameraInfo.active；載入時 Studio 切到它）。

    prefer 存在就用它；否則照 fallback 的順序找第一台存在的。
    都沒有就不動。回傳一句說明（沒改就回傳 None）。
    """
    cams = [n for n, _, _ in S.iter_nodes(scene.objects) if n["type"] == 5]
    if not cams:
        return None
    keys = {c["data"]["dicKey"]: c for c in cams}
    pick = None
    for k in ([prefer] if prefer is not None else []) + list(fallback):
        if k in keys:
            pick = keys[k]
            break
    if pick is None:
        return None
    before = [c["data"]["dicKey"] for c in cams if c["data"].get("active")]
    for c in cams:
        c["data"]["active"] = c is pick
    if before == [pick["data"]["dicKey"]]:
        return None
    return "%s（dicKey %d）" % (pick["data"].get("name"), pick["data"]["dicKey"])


def add_view_camera(scene, name):
    """沒有相機的卡（純過場 / 靜態場景）：生一台鎖住初始視角的相機。

    相機物件被啟用時，Studio 直接把主相機 SetPositionAndRotation 成這個物件的
    世界座標（OCICamera.SetActive），所以把眼睛位置 / 角度寫進節點就等於鎖住
    存檔當下看到的畫面。
    """
    eye, ang, fov = scene_view(scene)
    nxt = max(S.all_dickeys(scene.objects)) + 1
    node = {"type": 5, "data": {
        "dicKey": nxt,
        "position": eye,
        "rotation": ang,
        "scale": {"x": 1.0, "y": 1.0, "z": 1.0},
        "treeState": 1, "visible": False,
        "name": name, "active": False}}
    scene.objects[nxt] = node
    set_camera_fov(scene, nxt, fov)
    return node, eye, ang, fov


def _node_track_value(node, tid):
    d = node["data"]
    if tid == "guideObjectPos":
        p = d["position"]
        return f'valueX="{S.fmt_time(p["x"])}" valueY="{S.fmt_time(p["y"])}" valueZ="{S.fmt_time(p["z"])}"'
    if tid == "guideObjectScale":
        p = d["scale"]
        return f'valueX="{S.fmt_time(p["x"])}" valueY="{S.fmt_time(p["y"])}" valueZ="{S.fmt_time(p["z"])}"'
    r = d["rotation"]
    q = _euler_to_quat(r["x"], r["y"], r["z"])
    return (f'valueX="{S.fmt_time(q[0])}" valueY="{S.fmt_time(q[1])}"'
            f' valueZ="{S.fmt_time(q[2])}" valueW="{S.fmt_time(q[3])}"')


def lead_camera_chain_zero(scene, chains, warn, hold=0.01, starts=None):
    """讓每條相機鏈在「輪到自己之前」待在原點。

    zero_camera_chain 管的是另外半邊 ——「這段結束之後別去污染後面」。
    這支補的是它漏掉的前半邊，而那個漏洞長這樣：

    時間平移之後，第 N 段那條鏈的第一格關鍵影格落在自己那段的開頭
    （例如 82.52 秒）。timeline 在第一格之前**是維持第一格的值**，不是原點。
    於是從 0 秒一路到 82.52 秒，這條鏈都掛著它那段的起始位移。

    合併卡的相機鏈是一層套一層的，所以那個位移會加到所有比它深的鏈上，
    而接管相機就坐在最深處。段數越多疊得越兇。

    使用者看到的現象：卡一載入（timeline 停在 0 秒）打開樹狀圖，
    除了第一段以外每個 POV 資料夾都不在原點 —— 「POV 資料夾都沒有歸零」。

    做法跟收尾那邊對稱：0 秒補一格單位值、第一格前 0.01 秒再補一格單位值，
    原本的第一格原封不動。中間那 0.01 秒就是瞬間切進去。

    chains 是 [(段落序號, [資料夾節點…])]，由呼叫端決定哪些資料夾算在哪一段 ——
    相機鏈是巢狀的，同一個資料夾會是好幾台相機的祖先，得由外而內只認一次。

    starts 是每一段的起點（合併後的時間）。**一定要給。**

    血淚：第一版沒有 starts，改用「這條軌道的第一格在 0 秒之後」當判斷 ——
    看起來等價，其實不是。作者本來就常常不在 0 秒放第一格：實測
    Scenecard 第 1 段的 POV EDIT，guideObjectPos 的第一格在 59.05 秒，
    而 timeline 在第一格之前是**維持第一格的值**，所以原卡從 0 到 59 秒
    整段都掛著 (-1.192, 0.910, -0.380) —— 那正是作者擺好的 POV 位置。

    第一段的起點就是 0 秒，沒有「輪到自己之前」可言，但舊判斷看到
    「第一格 59.05 > 0」就往 0 秒蓋了一格單位值，把那個位移直接抹掉。
    症狀：合併卡一載入，第 1 段的 POV 資料夾全部是 0,0,0，鏡頭完全不對，
    而第 2、3 段正常（它們本來就該在輪到之前歸零）。

    現在改成看**段落的起點**：
      起點是 0（第一段）        → 整條跳過，一格都不補
      起點在 0 之後             → 0 秒和「起點前 0.01 秒」補單位值，
                                  再把第一格的值補在**起點**上，
                                  這樣「起點到第一格」那一段維持原卡的值，
                                  不會從原點慢慢飄過去。
    """
    if starts is None:
        starts = []
    xml = scene.timeline_xml()
    if not xml:
        return 0
    head, body, tail = S.split_root(xml)
    ranks = S.rank_map(S.node_dickeys(scene.objects))
    n = 0
    for seg_i, nodes in chains:
        t0 = starts[seg_i] if 0 <= seg_i < len(starts) else 0.0
        if t0 <= 1e-6:
            continue            # 第一段：它的「輪到自己」就是從 0 秒開始
        for node in nodes:
            r = ranks.get(node["data"]["dicKey"])
            if r is None:
                continue
            for tid, (ident, curve) in GUIDE_TRACKS.items():
                pat = re.compile(
                    r'<interpolable\b[^>]*?objectIndex="' + str(r) + r'"[^>]*?id="' + tid +
                    r'"[^>]*?>(?:(?!</?interpolable\b).)*?</interpolable>', re.S)
                m = pat.search(body)
                if not m:
                    continue
                blk = m.group(0)
                kfs = RE_KEYFRAME.findall(blk)
                if not kfs:
                    continue
                timed = []
                for kf in kfs:
                    mt = re.search(r'time="([-\d.eE+]+)"', kf)
                    if mt:
                        timed.append((float(mt.group(1)), kf))
                if not timed:
                    continue
                timed.sort(key=lambda x: x[0])
                first, first_kf = timed[0]
                lead = t0 - hold
                if lead <= 1e-6:
                    continue
                hm = re.match(r'<interpolable\b[^>]*?>', blk)
                if not hm:
                    continue
                add = ('<keyframe time="0" %s>%s</keyframe>' % (ident, curve)
                       + '<keyframe time="%s" %s>%s</keyframe>'
                       % (S.fmt_time(lead), ident, curve))
                # 作者的第一格晚於這一段的起點時，原卡在「起點到第一格」之間
                # 維持的是第一格的值（timeline 的 hold-before-first）。
                # 不補這一格的話，那一段會從原點慢慢飄到第一格，而不是定在那裡。
                if first > t0 + 1e-6:
                    vals = re.sub(r'^<keyframe\s+time="[^"]*"\s*', "",
                                  first_kf.split(">")[0]).strip()
                    add += '<keyframe time="%s" %s>%s</keyframe>' % (
                        S.fmt_time(t0), vals, curve)
                blk = blk[:hm.end()] + add + blk[hm.end():]
                body = body[:m.start()] + blk + body[m.end():]
                n += 1
    scene.set_timeline_xml(head + body + tail)
    return n


def zero_camera_chain(scene, chain_nodes, t_end, warn, hold=0.01):
    """讓相機鏈的資料夾在場景結束的那一刻瞬間歸零。

    不歸零的話，合併後第二張的 POV 鏈是掛在第一張 POV 鏈底下的，
    第一張的鏡頭運動會整個疊到第二張的相機上（畫面就跑掉了）。

    做法跟實卡一樣：在 t_end-0.01 補一格「維持原值」，t_end 那格設成
    位移 0 / 旋轉單位四元數 / 縮放 1，中間那 0.01 秒就是瞬間切換。
    """
    xml = scene.timeline_xml()
    if not xml:
        return 0
    head, body, tail = S.split_root(xml)
    ranks = S.rank_map(S.node_dickeys(scene.objects))
    n_add, n_new = 0, 0
    dropped_kfs = []                # (節點名, 軌道, 幾格, 最遠到幾秒)
    for node in chain_nodes:
        r = ranks.get(node["data"]["dicKey"])
        if r is None:
            continue
        for tid, (ident, curve) in GUIDE_TRACKS.items():
            pat = re.compile(
                r'<interpolable\b[^>]*?objectIndex="' + str(r) + r'"[^>]*?id="' + tid +
                r'"[^>]*?>(?:(?!</?interpolable\b).)*?</interpolable>', re.S)
            m = pat.search(body)
            if m:
                blk = m.group(0)
                kfs = RE_KEYFRAME.findall(blk)
                if not kfs:
                    continue

                # 先砍掉「超過這張卡時長」的關鍵影格，再補歸零。
                #
                # timeline 的「時長」只是播放長度；作者留在時長之後的關鍵影格
                # 在原卡永遠播不到，是死資料。但合併時整段會平移，那些死資料就
                # 落進**下一張卡的區間**，而且排在我們補的歸零後面 —— 歸零直接失效。
                #
                # 實際踩過：Scenecard 第二張卡的 POV M3 旋轉軌道，
                # 最後一格在 82.16069 而卡片時長是 82.0（超出 0.16 秒）。
                # 合併後歸零那格落在 190.01、那顆死資料落在 190.17，
                # 於是 190.17 之後整條軌道停在 (-0.417,-0.151,0.087,0.892)，
                # 第三、第四段的鏡頭全部跟著歪掉，而且畫面上看不出是哪裡來的。
                cut = t_end - hold
                keep, drop = [], []
                for kf in kfs:
                    kt = float(re.search(r'time="([-\d.eE+]+)"', kf).group(1))
                    (keep if kt <= cut + 1e-9 else drop).append((kt, kf))
                if not keep:
                    # 整條都在界外（很罕見）：留第一格並把它拉回 0 秒當基準值，
                    # 這樣至少還有東西可以「維持原值」，不會變成空軌道。
                    kt, kf = drop.pop(0)
                    keep.append((0.0, re.sub(r'^(<keyframe\s+time=")[^"]*"',
                                             r'\g<1>0"', kf, count=1)))
                for _, kf in drop:
                    blk = blk.replace(kf, "", 1)
                if drop:
                    dropped_kfs.append((node["data"].get("name") or "?", tid,
                                        len(drop), max(t for t, _ in drop)))

                lt, last = keep[-1]
                vals = re.sub(r'^<keyframe\s+time="[^"]*"\s*', "", last.split(">")[0]).strip()
                add = ""
                if lt < cut - 1e-6:
                    add += f'<keyframe time="{S.fmt_time(cut)}" {vals}>{curve}</keyframe>'
                add += f'<keyframe time="{S.fmt_time(t_end)}" {ident}>{curve}</keyframe>'
                blk = blk[:-len("</interpolable>")] + add + "</interpolable>"
                body = body[:m.start()] + blk + body[m.end():]
                n_add += 1
            else:
                cur = _node_track_value(node, tid)
                track = (f'<interpolable enabled="true" owner="Timeline" objectIndex="{r}"'
                         f' id="{tid}" guideObjectPath="" bgColorR="1" bgColorG="1"'
                         f' bgColorB="1" alias="">'
                         f'<keyframe time="0" {cur}>{curve}</keyframe>'
                         f'<keyframe time="{S.fmt_time(t_end - hold)}" {cur}>{curve}</keyframe>'
                         f'<keyframe time="{S.fmt_time(t_end)}" {ident}>{curve}</keyframe>'
                         f'</interpolable>')
                body += track
                n_new += 1
    scene.set_timeline_xml(head + body + tail)
    if n_new:
        warn.append(f"相機鏈歸零：有 {n_new} 條軌道原本不存在，已用資料夾目前的座標新建")
    for nm, tid, cnt, mx in dropped_kfs:
        warn.append(f"相機鏈歸零：{nm} 的 {tid} 有 {cnt} 格關鍵影格超過卡片時長"
                    f"（最遠 {mx:.5f}，時長 {t_end:.5f}），已刪除 —— "
                    f"留著的話合併後會蓋掉歸零，後面每一段的鏡頭都會跟著歪")
    if dropped_kfs:
        # 這是最容易踩的坑：卡片宣告 30 秒、實際運鏡畫到 120 秒，
        # 用宣告時長去歸零就等於把後面 90 秒的運鏡整段剪掉，
        # 載入後鏡位就跟原卡完全不一樣。GUI 會替每一段填實際長度，
        # 所以不會踩到；命令列忘了給 --duration 就會。
        need = max(mx for _n, _t, _c, mx in dropped_kfs)
        total = sum(c for _n, _t, c, _m in dropped_kfs)
        warn.append(f"相機鏈歸零：總共剪掉 {total} 格運鏡。這張卡的鏡頭畫到 "
                    f"{need:.2f} 秒，但時長只宣告 {t_end:.2f} 秒 —— "
                    f"要保留完整運鏡，prep 時加上 --duration {need:.2f}"
                    f"（或在 GUI 把這一段的時長設成至少這個值）")
    return n_add + n_new

if __name__ == "__main__":
    sys.exit(main())
