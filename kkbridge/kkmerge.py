#!/usr/bin/env python3
"""kkmerge - 恋活角色卡飾品合併命令列工具（單檔、無外部專案依賴）

用法：
    kkmerge.exe --chara 角色卡.png --coord 服裝卡.png --outfit 3 --out 合併後.png
    kkmerge.exe --info  角色卡.png              # 只印出卡片資訊，不改檔案

成功時 exit code 0，最後一行 stdout 是一行 JSON 結果。
失敗時 exit code 非 0，錯誤訊息走 stderr。

唯一的第三方依賴是 msgpack。
"""
from __future__ import annotations

import argparse
import copy
import re
import io
import json
import struct
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import msgpack
from kklang import T, set_lang

VERSION = "1.0.0"
PNG_SIG = b"\x89PNG\r\n\x1a\n"
KNOWN_CHARA = {"【KoiKatuChara】", "【KoiKatuCharaSP】", "【KoiKatuCharaSun】"}
KNOWN_COORD = {"【KoiKatuClothes】", "【KoiKatuClothesSun】"}
BASE_SLOTS = 20
OBJ_ACCESSORY = 2
EMPTY_TYPE = 120


class CardError(Exception):
    pass


# ---------------------------------------------------------------- msgpack 包裝
# use_single_float 是關鍵：C# 存的是 float32(0xca)，msgpack 預設會升成
# float64(0xcb)，遊戲讀回去就壞了。

def pack(obj) -> bytes:
    return msgpack.Packer(use_bin_type=True, use_single_float=True).pack(obj)


def unpack(b):
    return msgpack.unpackb(b, raw=False, strict_map_key=False)


def _skip(buf: bytes, pos: int) -> int:
    """回傳 buf 中從 pos 起一個完整 msgpack 物件之後的位置。"""
    c = buf[pos]
    pos += 1
    if c <= 0x7F or c >= 0xE0 or c in (0xC0, 0xC2, 0xC3):
        return pos
    if 0x80 <= c <= 0x8F:
        for _ in range((c & 0x0F) * 2):
            pos = _skip(buf, pos)
        return pos
    if 0x90 <= c <= 0x9F:
        for _ in range(c & 0x0F):
            pos = _skip(buf, pos)
        return pos
    if 0xA0 <= c <= 0xBF:
        return pos + (c & 0x1F)
    if c == 0xC4:
        return pos + 1 + buf[pos]
    if c == 0xC5:
        return pos + 2 + int.from_bytes(buf[pos:pos + 2], "big")
    if c == 0xC6:
        return pos + 4 + int.from_bytes(buf[pos:pos + 4], "big")
    if c == 0xC7:
        return pos + 2 + buf[pos]
    if c == 0xC8:
        return pos + 3 + int.from_bytes(buf[pos:pos + 2], "big")
    if c == 0xC9:
        return pos + 5 + int.from_bytes(buf[pos:pos + 4], "big")
    fixed = {0xCA: 4, 0xCB: 8, 0xCC: 1, 0xCD: 2, 0xCE: 4, 0xCF: 8,
             0xD0: 1, 0xD1: 2, 0xD2: 4, 0xD3: 8,
             0xD4: 2, 0xD5: 3, 0xD6: 5, 0xD7: 9, 0xD8: 17}
    if c in fixed:
        return pos + fixed[c]
    if c == 0xD9:
        return pos + 1 + buf[pos]
    if c == 0xDA:
        return pos + 2 + int.from_bytes(buf[pos:pos + 2], "big")
    if c == 0xDB:
        return pos + 4 + int.from_bytes(buf[pos:pos + 4], "big")
    if c in (0xDC, 0xDD, 0xDE, 0xDF):
        w = 2 if c in (0xDC, 0xDE) else 4
        n = int.from_bytes(buf[pos:pos + w], "big")
        pos += w
        for _ in range(n * (2 if c in (0xDE, 0xDF) else 1)):
            pos = _skip(buf, pos)
        return pos
    raise CardError(T("未知的 msgpack 型別位元組 0x{0:02x}").format(c))


def _map_header(n: int) -> bytes:
    if n <= 0x0F:
        return bytes([0x80 | n])
    if n <= 0xFFFF:
        return b"\xde" + n.to_bytes(2, "big")
    return b"\xdf" + n.to_bytes(4, "big")


def patch_map(raw: bytes, updates: dict) -> bytes:
    """只重新編碼 updates 裡的 key，其餘位元組原封不動。
    updates 裡原本不存在的 key 會接在 map 末尾，並改寫 map 長度。"""
    c = raw[0]
    if 0x80 <= c <= 0x8F:
        n, p = c & 0x0F, 1
    elif c == 0xDE:
        n, p = int.from_bytes(raw[1:3], "big"), 3
    elif c == 0xDF:
        n, p = int.from_bytes(raw[1:5], "big"), 5
    else:
        raise CardError(T("頂層不是 msgpack map"))
    body, seen = bytearray(), set()
    for _ in range(n):
        ks = p
        p = _skip(raw, p)
        kb = raw[ks:p]
        key = unpack(kb)
        seen.add(key)
        vs = p
        p = _skip(raw, p)
        body += kb
        body += pack(updates[key]) if key in updates else raw[vs:p]
    extra = [k for k in updates if k not in seen]
    for k in extra:
        body += pack(k) + pack(updates[k])
    head = raw[:p - (p - (1 if 0x80 <= c <= 0x8F else 3 if c == 0xDE else 5))]
    return bytes(_map_header(n + len(extra)) + body)


# --------------------------------------------- 位元組層級的 msgpack 手術工具
# 為什麼需要這些：C# 端把某些欄位用「強制 int32(0xd2)」寫出，而 MessagePack-CSharp
# 反序列化成 object 時，fixint -> byte、int32 -> int。Python 重新編碼會把小整數
# 壓成 fixint，用 object 接資料的外掛就會 InvalidCastException。
# 所以凡是不需要改的東西，一律保留原始位元組；要改的欄位也沿用原本的格式寬度。

def arr_header(n: int) -> bytes:
    if n <= 0x0F:
        return bytes([0x90 | n])
    if n <= 0xFFFF:
        return b"\xdc" + n.to_bytes(2, "big")
    return b"\xdd" + n.to_bytes(4, "big")


def arr_split(raw: bytes) -> list:
    """把 msgpack 陣列拆成每個元素的原始位元組切片。"""
    c = raw[0]
    if 0x90 <= c <= 0x9F:
        n, p = c & 0x0F, 1
    elif c == 0xDC:
        n, p = int.from_bytes(raw[1:3], "big"), 3
    elif c == 0xDD:
        n, p = int.from_bytes(raw[1:5], "big"), 5
    else:
        raise CardError(T("不是 msgpack 陣列"))
    out = []
    for _ in range(n):
        st = p
        p = _skip(raw, p)
        out.append(raw[st:p])
    return out


def map_split(raw: bytes) -> list:
    """把 msgpack map 拆成 [(key, key_bytes, value_bytes), ...]。"""
    c = raw[0]
    if 0x80 <= c <= 0x8F:
        n, p = c & 0x0F, 1
    elif c == 0xDE:
        n, p = int.from_bytes(raw[1:3], "big"), 3
    elif c == 0xDF:
        n, p = int.from_bytes(raw[1:5], "big"), 5
    else:
        raise CardError(T("不是 msgpack map"))
    out = []
    for _ in range(n):
        ks = p
        p = _skip(raw, p)
        kb = raw[ks:p]
        vs = p
        p = _skip(raw, p)
        out.append((unpack(kb), kb, raw[vs:p]))
    return out


def map_build(items) -> bytes:
    body = b"".join(kb + vb for _, kb, vb in items)
    return _map_header(len(items)) + body


def encode_like(value, orig: bytes) -> bytes:
    """沿用原始欄位的 msgpack 格式位元組重新編碼，維持 C# 端的型別判讀。"""
    c = orig[0]
    if isinstance(value, bool):
        return b"\xc3" if value else b"\xc2"
    if isinstance(value, int):
        table = {0xD0: (">b", 1), 0xD1: (">h", 2), 0xD2: (">i", 4), 0xD3: (">q", 8),
                 0xCC: (">B", 1), 0xCD: (">H", 2), 0xCE: (">I", 4), 0xCF: (">Q", 8)}
        if c in table:
            fmt, _ = table[c]
            try:
                return bytes([c]) + struct.pack(fmt, value)
            except struct.error:
                pass
        return pack(value)
    if isinstance(value, float):
        if c == 0xCB:
            return b"\xcb" + struct.pack(">d", value)
        return b"\xca" + struct.pack(">f", value)
    return pack(value)


def patch_entry(raw: bytes, updates: dict) -> bytes:
    """改 msgpack map 裡的少數欄位，其餘位元組原封不動、格式寬度不變。"""
    items = map_split(raw)
    out = []
    for key, kb, vb in items:
        if key in updates:
            new = updates[key]
            vb = vb if new == unpack(vb) else encode_like(new, vb)
        out.append((key, kb, vb))
    return map_build(out)


def map_drop_insert(raw: bytes, drop_key, ins_key=None, ins_val: bytes = None) -> bytes:
    """在 msgpack map 上移除一個鍵、（可選）插入一個以原始位元組給定的值。"""
    items = [it for it in map_split(raw) if it[0] != drop_key]
    if ins_key is not None and ins_val is not None:
        items.append((ins_key, pack(ins_key), ins_val))
    return map_build(items)


# ---------------------------------------------------------------- 二進位讀寫
class Reader:
    def __init__(self, data: bytes):
        self.d, self.p = data, 0

    def need(self, n):
        if self.p + n > len(self.d):
            raise CardError(T("讀取越界：位置 {0} 需要 {1} 位元組").format(self.p, n))

    def read(self, n):
        self.need(n)
        v = self.d[self.p:self.p + n]
        self.p += n
        return v

    def i32(self):
        return struct.unpack("<i", self.read(4))[0]

    def i64(self):
        return struct.unpack("<q", self.read(8))[0]

    def b7(self):
        r = s = 0
        while True:
            self.need(1)
            byte = self.d[self.p]
            self.p += 1
            r |= (byte & 0x7F) << s
            if not byte & 0x80:
                return r
            s += 7
            if s > 35:
                raise CardError(T("7-bit 變長整數過長"))

    def cs_str(self):
        return self.read(self.b7()).decode("utf-8", "replace")


def w_b7(v: int) -> bytes:
    out = bytearray()
    while v >= 0x80:
        out.append((v & 0x7F) | 0x80)
        v >>= 7
    out.append(v)
    return bytes(out)


def w_str(s: str) -> bytes:
    b = s.encode("utf-8")
    return w_b7(len(b)) + b


def png_len(d: bytes, start=0) -> int:
    if d[start:start + 8] != PNG_SIG:
        raise CardError(T("不是合法的 PNG 開頭"))
    p = start + 8
    while True:
        if p + 8 > len(d):
            raise CardError(T("PNG 資料不完整"))
        ln = struct.unpack(">I", d[p:p + 4])[0]
        typ = d[p + 4:p + 8]
        p += 8 + ln + 4
        if typ == b"IEND":
            return p - start
        if p > len(d):
            raise CardError(T("PNG chunk 長度越界"))


# ---------------------------------------------------------------- 角色卡容器
class CharaCard:
    def __init__(self, path):
        d = Path(path).read_bytes()
        r = Reader(d)
        tl = png_len(d, 0)
        r.p = tl
        self.thumbnail = d[:tl]
        self.product_no = r.i32()
        self.marker = r.cs_str()
        if self.marker not in KNOWN_CHARA:
            raise CardError(T("這不是角色卡（標識為 {0!r}）").format(self.marker))
        self.version = r.cs_str()
        self.face = r.read(r.i32())
        lst = unpack(r.read(r.i32()))
        blob = r.read(r.i64())
        self.order = [(i["name"], i.get("version", "0.0.0")) for i in lst["lstInfo"]]
        self.pos = {i["name"]: (int(i["pos"]), int(i["size"])) for i in lst["lstInfo"]}
        self.blocks = {n: blob[p:p + s] for n, (p, s) in self.pos.items()}

    def to_bytes(self) -> bytes:
        ordered = sorted(self.order, key=lambda nv: self.pos[nv[0]][0])
        buf, newpos = bytearray(), {}
        for name, _ in ordered:
            newpos[name] = len(buf)
            buf += self.blocks[name]
        lst = pack({"lstInfo": [
            {"name": n, "version": v, "pos": newpos[n], "size": len(self.blocks[n])}
            for n, v in self.order]})
        o = io.BytesIO()
        o.write(self.thumbnail)
        o.write(struct.pack("<i", self.product_no))
        o.write(w_str(self.marker))
        o.write(w_str(self.version))
        o.write(struct.pack("<i", len(self.face)))
        o.write(self.face)
        o.write(struct.pack("<i", len(lst)))
        o.write(lst)
        o.write(struct.pack("<q", len(buf)))
        o.write(bytes(buf))
        return o.getvalue()

    def save(self, path):
        Path(path).write_bytes(self.to_bytes())


# ------------------------------------------------- ChaFileCoordinate 資料段
def split_coord(b: bytes) -> dict:
    p = 0

    def i32():
        nonlocal p
        v = struct.unpack("<i", b[p:p + 4])[0]
        p += 4
        return v

    cl = i32(); clothes = b[p:p + cl]; p += cl
    al = i32(); acc = b[p:p + al]; p += al
    flag = b[p:p + 1]; p += 1
    ml = i32(); makeup = b[p:p + ml]; p += ml
    if p != len(b):
        raise CardError(T("coordinate 資料段長度對不上：{0} vs {1}").format(p, len(b)))
    return dict(clothes=clothes, acc=acc, flag=flag, makeup=makeup)


def join_coord(seg: dict, new_acc: bytes) -> bytes:
    return (struct.pack("<i", len(seg["clothes"])) + seg["clothes"]
            + struct.pack("<i", len(new_acc)) + new_acc
            + seg["flag"]
            + struct.pack("<i", len(seg["makeup"])) + seg["makeup"])


def load_coord_card(path) -> dict:
    d = Path(path).read_bytes()
    r = Reader(d)
    r.p = png_len(d, 0)
    out = {"productNo": r.i32(), "marker": r.cs_str()}
    if out["marker"] not in KNOWN_COORD:
        raise CardError(T("這不是服裝卡（標識為 {0!r}）").format(out['marker']))
    out["version"] = r.cs_str()
    out["name"] = r.cs_str()
    seg = split_coord(r.read(r.i32()))
    out["accessory"] = unpack(seg["acc"])
    out["clothes"] = unpack(seg["clothes"])
    tag = r.cs_str()
    if tag != "KKEx":
        raise CardError(T("服裝卡尾端不是 KKEx 而是 {0!r}").format(tag))
    out["kkex_ver"] = r.i32()
    out["kkex"] = unpack(r.read(r.i32()))
    return out


# ---------------------------------------------- MoreAccessories XML 產生
_AX = ("x", "y", "z")


def _f(v) -> str:
    s = repr(float(v))
    return s[:-2] if s.endswith(".0") else s


def part_to_xml(part: dict) -> ET.Element:
    e = ET.Element("accessory")
    e.set("type", str(part["type"]))
    if part["type"] == EMPTY_TYPE:
        return e
    e.set("id", str(part["id"]))
    e.set("parentKey", part["parentKey"])
    d0, d1, flat = part["addMove"]          # 交錯陣列 = [維度0, 維度1, 攤平]
    for i in range(d0):
        for j in range(d1):
            vec = flat[i * d1 + j]
            for k, ax in enumerate(_AX):
                e.set(f"addMove{i}{j}{ax}", _f(vec[k]))
    for i, col in enumerate(part["color"]):
        for k, ch in enumerate("rgba"):
            e.set(f"color{i}{ch}", _f(col[k]))
    e.set("hideCategory", str(part["hideCategory"]))
    e.set("noShake", "true" if part.get("noShake") else "false")
    return e


# ---------------------------------------------------------------- 主流程
def merge(chara_path, coord_path, outfit_index, out_path,
          src_slots=None, clean=True, log=print) -> dict:
    card = CharaCard(chara_path)
    coord = load_coord_card(coord_path)
    if "KKEx" not in card.blocks:
        raise CardError(T("角色卡沒有 KKEx 區塊，沒有任何外掛資料可以合併"))
    kkex_raw = card.blocks["KKEx"]
    kkex = unpack(kkex_raw)

    src_parts = coord["accessory"]["parts"]
    if src_slots is None:
        src_slots = [i for i, p in enumerate(src_parts) if p["type"] != EMPTY_TYPE]
    if not src_slots:
        raise CardError(T("服裝卡裡沒有任何飾品"))

    outfits = [split_coord(b) for b in unpack(card.blocks["Coordinate"])]
    if not 0 <= outfit_index < len(outfits):
        raise CardError(T("換裝編號超出範圍：{0}，這張卡有 {1} 套").format(outfit_index, len(outfits)))
    accs = [unpack(o["acc"]) for o in outfits]

    # 擴充欄位寬度是全域的：以 MoreAccessories XML 每個 accessorySet 的長度為準。
    # 各套換裝的 parts 陣列允許把尾端的空欄位裁掉，所以長度可以不一致，
    # 但它必定是 XML 序列的前綴。沒有 MoreAccessories 就退回用最長的那套推算。
    # 擴充欄位寬度是「每套換裝各自」的，不同套可以不一樣寬（實卡確認過）。
    # parts 陣列允許把尾端的空欄位裁掉，但必定是該套 XML 序列的前綴。
    ma_root = None
    ma_sets = {}
    if "moreAccessories" in kkex:
        ma_root = ET.fromstring(kkex["moreAccessories"][1]["additionalAccessories"])
        ma_sets = {int(e.get("type")): e for e in ma_root if e.tag == "accessorySet"}
    tgt_node = ma_sets.get(outfit_index)
    n_extra = len(list(tgt_node)) if tgt_node is not None else 0
    base = BASE_SLOTS + max(n_extra, len(accs[outfit_index]["parts"]) - BASE_SLOTS, 0)
    slot_map = {s: base + k for k, s in enumerate(src_slots)}
    new_total = base + len(src_slots)
    log(T("outfit{0} 飾品欄位 {1} -> {2}（搬入 {3} 個）；其他套不動").format(outfit_index, base, new_total, len(src_slots)))

    empty = None
    for a in accs:
        for p in a["parts"]:
            if p["type"] == EMPTY_TYPE:
                empty = copy.deepcopy(p)
                break
        if empty:
            break
    if empty is None:
        empty = copy.deepcopy(next(p for p in src_parts if p["type"] == EMPTY_TYPE))

    # 1) 各套先補齊到目前的全域寬度，再一起接上新欄位；
    #    只有目標那套填入真的飾品，其餘填空欄位。
    tgt = accs[outfit_index]["parts"]
    while len(tgt) < base:                      # 先補齊到這一套自己的寬度
        tgt.append(copy.deepcopy(empty))
    tgt.extend(copy.deepcopy(src_parts[s]) for s in src_slots)
    new_coord_block = pack([join_coord(o, pack(a)) for o, a in zip(outfits, accs)])

    # 2) MoreAccessories：XML + ShowAccessories + visibility 同步
    if ma_root is not None:
        ma = kkex["moreAccessories"][1]
        if tgt_node is None:
            tgt_node = ET.SubElement(ma_root, "accessorySet")
            tgt_node.set("type", str(outfit_index))
        for child in list(tgt_node):            # 只重建目標那一套，其他原樣
            tgt_node.remove(child)
        for part in tgt[BASE_SLOTS:]:
            tgt_node.append(part_to_xml(part))
        widest = max([len(list(e)) for e in ma_root if e.tag == "accessorySet"] or [0])
        vis = next((e for e in ma_root if e.tag == "visibility"), None)
        if vis is not None:
            while len(list(vis)) < widest:
                ET.SubElement(vis, "visible").set("value", "true")
        ma["additionalAccessories"] = ET.tostring(ma_root, encoding="unicode")
        if "ShowAccessories" in ma:
            show = unpack(ma["ShowAccessories"]) or []
            if len(show) < widest:
                ma["ShowAccessories"] = pack(show + [True] * (widest - len(show)))

    # 3) Sideloader：accessory{N} -> outfit{T}.accessory{M}
    n_uar = 0
    if "com.bepis.sideloader.universalautoresolver" in kkex:
        uar = kkex["com.bepis.sideloader.universalautoresolver"][1]
        src_uar = coord["kkex"].get(
            "com.bepis.sideloader.universalautoresolver", [0, {}])[1].get("info") or []
        for blob in src_uar:
            d = unpack(blob)
            prop = d.get("Property", "")
            if not prop.startswith("accessory"):
                continue
            head, rest = prop.split(".", 1)
            old = int(head[len("accessory"):])
            if old not in slot_map:
                continue
            d["Property"] = f"outfit{outfit_index}.accessory{slot_map[old]}.{rest}"
            uar.setdefault("info", []).append(pack(d))
            n_uar += 1
    log(T("Sideloader 條目 +{0}").format(n_uar))

    # 4) MaterialEditor：Slot / CoordinateIndex / TexID 平移
    ME = "com.deathweasel.bepinex.materialeditor"
    tex_map, n_me = {}, 0
    if ME in coord["kkex"]:
        me_src = coord["kkex"][ME][1]
        me_dst = kkex.setdefault(ME, [0, {}])[1]
        tex_dst = unpack(me_dst["TextureDictionary"]) if me_dst.get("TextureDictionary") else {}
        tex_src = unpack(me_src["TextureDictionary"]) if me_src.get("TextureDictionary") else {}
        nxt = (max(tex_dst) + 1) if tex_dst else 1
        for k in sorted(tex_src):
            tex_map[k] = nxt
            tex_dst[nxt] = tex_src[k]
            nxt += 1
        if tex_dst:
            me_dst["TextureDictionary"] = pack(tex_dst)
        for key in ("RendererPropertyList", "ProjectorPropertyList",
                    "MaterialFloatPropertyList", "MaterialKeywordPropertyList",
                    "MaterialColorPropertyList", "MaterialTexturePropertyList",
                    "MaterialShaderList", "MaterialCopyList"):
            sv = me_src.get(key)
            if not sv:
                continue
            moved = []
            for d in unpack(sv):
                if d.get("ObjectType") != OBJ_ACCESSORY or d.get("Slot") not in slot_map:
                    continue
                d = dict(d)
                d["Slot"] = slot_map[d["Slot"]]
                d["CoordinateIndex"] = outfit_index
                if d.get("TexID") in tex_map:
                    d["TexID"] = tex_map[d["TexID"]]
                moved.append(d)
            if moved:
                dv = me_dst.get(key)
                me_dst[key] = pack((unpack(dv) if dv else []) + moved)
                n_me += len(moved)
    log(T("MaterialEditor 貼圖 +{0}、條目 +{1}").format(len(tex_map), n_me))

    # 5) HairAccessoryCustomizer（服裝卡是 slot，角色卡是 coord -> slot）
    HAC = "com.deathweasel.bepinex.hairaccessorycustomizer"
    n_hac = 0
    if HAC in coord["kkex"]:
        src = unpack(coord["kkex"][HAC][1]["CoordinateHairAccessories"]) or {}
        dst_pay = kkex.setdefault(HAC, [0, {"HairAccessories": pack({})}])[1]
        dst = unpack(dst_pay.get("HairAccessories") or pack({})) or {}
        bucket = dst.setdefault(outfit_index, {})
        for s, v in src.items():
            if s in slot_map:
                bucket[slot_map[s]] = v
                n_hac += 1
        dst_pay["HairAccessories"] = pack(dst)

    # 6) DynamicBoneEditor
    DBE = "com.deathweasel.bepinex.dynamicboneeditor"
    n_dbe = 0
    if DBE in coord["kkex"]:
        src = unpack(coord["kkex"][DBE][1]["AccessoryDynamicBoneData"]) or []
        dst_pay = kkex.setdefault(DBE, [0, {"AccessoryDynamicBoneData": pack([])}])[1]
        dst = unpack(dst_pay.get("AccessoryDynamicBoneData") or pack([])) or []
        for d in src:
            if d.get("Slot") not in slot_map:
                continue
            d = dict(d)
            d["Slot"] = slot_map[d["Slot"]]
            d["CoordinateIndex"] = outfit_index
            dst.append(d)
            n_dbe += 1
        dst_pay["AccessoryDynamicBoneData"] = pack(dst)
    log(f"HairAccessoryCustomizer +{n_hac}、DynamicBoneEditor +{n_dbe}")

    # 7) 寫回：KKEx 只換有動到的 GUID，其餘外掛位元組原封不動
    touched = ["moreAccessories", "com.bepis.sideloader.universalautoresolver",
               ME, HAC, DBE]
    card.blocks["KKEx"] = patch_map(kkex_raw, {g: kkex[g] for g in touched if g in kkex})
    card.blocks["Coordinate"] = new_coord_block
    orphans = clean_orphans(card, log) if clean else {}
    card.save(out_path)
    return {"ok": True, "out": str(out_path), "outfit": outfit_index,
            "slots_added": len(src_slots), "slot_total": new_total,
            "slot_map": {str(k): v for k, v in slot_map.items()},
            "textures_added": len(tex_map), "materialeditor_entries": n_me,
            "sideloader_entries": n_uar, "hac_entries": n_hac,
            "dynamicbone_entries": n_dbe, "orphans_removed": orphans,
            "size_bytes": Path(out_path).stat().st_size}


# ============================================================ 整套換裝移植
# 以換裝索引為鍵的外掛資料（payload[key] 是 {換裝索引: 任意內容}）
COORD_DICT = [
    ("Accessory_States", "CoordinateData"),
    ("Additional_Card_Info", "CoordinateInfo"),
    ("com.deathweasel.bepinex.clothingunlocker", "ClothingUnlocked"),
    ("com.deathweasel.bepinex.moreoutfits", "CoordinateNames"),
    ("com.deathweasel.bepinex.hairaccessorycustomizer", "HairAccessories"),
    ("KCOX", "Overlays"),
    ("AAAAAAAAAAAA_Data", "accParents"),
    ("org.njaecha.plugins.anal", "Graphs"),
]
PUSHUP_COORD = [("com.deathweasel.bepinex.pushup", "Pushup_BraData"),
                ("com.deathweasel.bepinex.pushup", "Pushup_TopData")]

# 以清單存放、每筆自帶換裝索引欄位的外掛資料
COORD_LIST = [
    ("madevil.kk.ass", "TriggerPropertyList"),
    ("nakay.kk.ClothingBlendShape", "ClothesDataSet"),
    ("AAAAAAAAAAAA_Data", "ParentRules"),
    ("com.deathweasel.bepinex.dynamicboneeditor", "AccessoryDynamicBoneData"),
    ("org.njaecha.plugins.dbde", "AccessoryEdits"),
]
COORD_FIELDS = ("CoordinateIndex", "Coordinate", "coordinate")

# 換裝索引編在 payload 的 key 名稱裡（例如 slots0 / meshes0 / slots1 ...）
COORD_KEYNAME = [
    ("org.njaecha.plugins.objimport", ("slots{}", "meshes{}")),
]
_KEYNAME_RE = re.compile(r"^([A-Za-z_][A-Za-z_]*)(\d+)$")

ME_GUID = "com.deathweasel.bepinex.materialeditor"
ME_LISTS = ("RendererPropertyList", "ProjectorPropertyList",
            "MaterialFloatPropertyList", "MaterialKeywordPropertyList",
            "MaterialColorPropertyList", "MaterialTexturePropertyList",
            "MaterialShaderList", "MaterialCopyList")
ME_OUTFIT_TYPES = (1, 2)           # 1=Clothing 2=Accessory（3=Hair、4=Character 屬於人）

# 已知但目前不搬的換裝層級資料，會列在結果的 warnings 裡
SAFE_IGNORE = ("orange.spork.advikplugin",)   # 已確認與換裝無關


def _coord_field(entry: dict):
    for f in COORD_FIELDS:
        if f in entry:
            return f
    return None


def _payload(kkex, guid):
    v = kkex.get(guid)
    return v[1] if isinstance(v, list) and len(v) > 1 and isinstance(v[1], dict) else None


def _outfit_slot_map(card) -> list:
    """回傳每套換裝的 (有效欄位數, 空欄位集合)。"""
    out = []
    for b in unpack(card.blocks["Coordinate"]):
        parts = unpack(split_coord(b)["acc"])["parts"]
        out.append((len(parts),
                    {i for i, p in enumerate(parts) if p["type"] == EMPTY_TYPE}))
    return out


# 以 (換裝索引, 飾品欄位) 索引、需要一起清理的外掛
SLOT_SCOPED = [
    (ME_GUID, ME_LISTS, "CoordinateIndex", "Slot", True),
    ("com.deathweasel.bepinex.dynamicboneeditor", ("AccessoryDynamicBoneData",),
     "CoordinateIndex", "Slot", False),
]


def clean_orphans(card, log=print) -> dict:
    """清掉指向空飾品欄位的擴充資料。

    這種殘渣會讓 MaterialEditor 取到 null 物件，ShaderSwapper 的 hook 直接
    NullReferenceException，整個載入 coroutine 中止，之後的貼圖全部不會套用。
    """
    if "KKEx" not in card.blocks:
        return {}
    D = KKExRaw(card.blocks["KKEx"])
    slots = _outfit_slot_map(card)
    removed = {}

    def dead(ci, sl):
        if ci is None or sl is None or not 0 <= ci < len(slots):
            return False
        n, empty = slots[ci]
        return sl >= n or sl in empty

    for guid, keys, cfield, sfield, acc_only in SLOT_SCOPED:
        if not D.has(guid):
            continue
        for key in keys:
            raw = D.get(guid, key)
            if raw is None or raw == b"\xc0":
                continue
            items = arr_split(unpack(raw))
            keep = []
            for e in items:
                d = unpack(e)
                if (not acc_only or d.get("ObjectType") == OBJ_ACCESSORY) and \
                        dead(d.get(cfield), d.get(sfield)):
                    removed[f"{guid}/{key}"] = removed.get(f"{guid}/{key}", 0) + 1
                    continue
                keep.append(e)
            if len(keep) != len(items):
                D.set(guid, key,
                      pack(arr_header(len(keep)) + b"".join(keep)) if keep else b"\xc0")

    # HairAccessoryCustomizer：{換裝: {欄位: ...}}
    HAC = "com.deathweasel.bepinex.hairaccessorycustomizer"
    raw = D.get(HAC, "HairAccessories")
    if raw is not None:
        table = unpack(unpack(raw)) or {}
        n = 0
        for ci in list(table):
            for sl in list(table[ci]):
                if dead(ci, sl):
                    del table[ci][sl]
                    n += 1
        if n:
            removed[f"{HAC}/HairAccessories"] = n
            D.set(HAC, "HairAccessories", pack(pack(table)))

    if removed:
        card.blocks["KKEx"] = D.to_bytes()
        for k, v in sorted(removed.items()):
            log(T("清除指向空欄位的殘渣：{0} -{1} 筆").format(k, v))
    return removed


class KKExRaw:
    """在位元組層級操作 KKEx 頂層 map，沒動到的外掛完全保留原始位元組。"""

    def __init__(self, raw: bytes):
        self.items = [list(t) for t in map_split(raw)]
        self.idx = {g: i for i, (g, _, _) in enumerate(self.items)}

    def has(self, guid):
        return guid in self.idx

    def _value(self, guid):
        return self.items[self.idx[guid]][2]

    def payload(self, guid):
        if guid not in self.idx:
            return None
        parts = arr_split(self._value(guid))
        return parts[1] if len(parts) > 1 else None

    def get(self, guid, key):
        """取 payload 裡某個 key 的原始位元組值。"""
        pr = self.payload(guid)
        if pr is None:
            return None
        for k, _, vb in map_split(pr):
            if k == key:
                return vb
        return None

    def set(self, guid, key, val_bytes, src_raw_value=None):
        """設定 payload 裡某個 key；guid 不存在時用 src_raw_value 整包建立。"""
        if guid not in self.idx:
            if src_raw_value is None:
                return False
            self.items.append([guid, pack(guid), src_raw_value])
            self.idx[guid] = len(self.items) - 1
        parts = arr_split(self._value(guid))
        pr = parts[1]
        items = [list(t) for t in map_split(pr)]
        for it in items:
            if it[0] == key:
                if it[2] == val_bytes:
                    return False                    # 內容沒變，不動它
                it[2] = val_bytes
                break
        else:
            items.append([key, pack(key), val_bytes])
        new_pay = map_build([tuple(i) for i in items])
        self.items[self.idx[guid]][2] = arr_header(len(parts)) + parts[0] + new_pay + \
            b"".join(parts[2:])
        return True

    def to_bytes(self):
        return map_build([tuple(i) for i in self.items])


def _bin_wrap(data: bytes) -> bytes:
    return pack(data)


def transplant(src_path, src_outfit, dst_path, dst_outfit, out_path,
               pushup="follow_outfit", skin_overlay="keep_target",
               prune=True, clean=True, log=print) -> dict:
    """把 src 的第 src_outfit 套換裝整套移植到 dst 的第 dst_outfit 套。

    原則：沒有真的改到的外掛，位元組原封不動；有改的也只重寫必要欄位，
    並沿用原本的整數格式寬度（C# 端 object 反序列化會看格式決定型別）。
    """
    src = CharaCard(src_path)
    dst = CharaCard(dst_path)
    S = KKExRaw(src.blocks["KKEx"]) if "KKEx" in src.blocks else KKExRaw(pack({}))
    D = KKExRaw(dst.blocks["KKEx"])

    s_out = [split_coord(b) for b in unpack(src.blocks["Coordinate"])]
    d_out = [split_coord(b) for b in unpack(dst.blocks["Coordinate"])]
    if not 0 <= src_outfit < len(s_out):
        raise CardError(T("來源換裝編號超出範圍：{0}（來源有 {1} 套）").format(src_outfit, len(s_out)))
    if not 0 <= dst_outfit < len(d_out):
        raise CardError(T("目標換裝編號超出範圍：{0}（目標有 {1} 套）").format(dst_outfit, len(d_out)))
    warnings, touched = [], set()

    # ---- 1. Coordinate 區塊 ----
    s_acc = unpack(s_out[src_outfit]["acc"])
    d_accs = [unpack(o["acc"]) for o in d_out]
    ma_xml = D.get("moreAccessories", "additionalAccessories")
    d_root = ET.fromstring(unpack(ma_xml)) if ma_xml else None
    d_sets = {int(e.get("type")): e for e in d_root if e.tag == "accessorySet"} \
        if d_root is not None else {}
    old_w = len(list(d_sets[dst_outfit])) if dst_outfit in d_sets \
        else max(0, len(d_accs[dst_outfit]["parts"]) - BASE_SLOTS)
    total = len(s_acc["parts"])
    new_width = total - BASE_SLOTS
    empty = next((copy.deepcopy(p) for a in d_accs for p in a["parts"]
                  if p["type"] == EMPTY_TYPE),
                 None) or copy.deepcopy(
        next(p for p in s_acc["parts"] if p["type"] == EMPTY_TYPE))

    d_accs[dst_outfit] = s_acc
    d_out[dst_outfit] = dict(s_out[src_outfit])
    dst.blocks["Coordinate"] = pack(
        [join_coord(o, pack(a)) for o, a in zip(d_out, d_accs)])
    log(T("換裝 {0} -> {1}；該套飾品欄位 {2} -> {3}（其他套不動）").format(src_outfit, dst_outfit, BASE_SLOTS + old_w, total))

    # ---- 2. MoreAccessories XML ----
    if d_root is not None:
        node = d_sets.get(dst_outfit)
        if node is None:
            node = ET.SubElement(d_root, "accessorySet")
            node.set("type", str(dst_outfit))
        for child in list(node):                # 只重建目標那一套
            node.remove(child)
        for part in s_acc["parts"][BASE_SLOTS:]:
            node.append(part_to_xml(part))
        widest = max([len(list(e)) for e in d_root if e.tag == "accessorySet"] or [0])
        vis = next((e for e in d_root if e.tag == "visibility"), None)
        if vis is not None:
            while len(list(vis)) < widest:
                ET.SubElement(vis, "visible").set("value", "true")
        if D.set("moreAccessories", "additionalAccessories",
                 pack(ET.tostring(d_root, encoding="unicode"))):
            touched.add("moreAccessories")
        sa_raw = D.get("moreAccessories", "ShowAccessories")
        if sa_raw is not None:
            sa = unpack(unpack(sa_raw)) or []
            if len(sa) < widest:
                D.set("moreAccessories", "ShowAccessories",
                      pack(pack(sa + [True] * (widest - len(sa)))))

    # ---- 3. Sideloader：outfit{dst}. 的條目整批換成來源的 ----
    UAR = "com.bepis.sideloader.universalautoresolver"
    n_uar = 0
    d_info_raw = D.get(UAR, "info")
    if d_info_raw is not None:
        pre_d, pre_s = f"outfit{dst_outfit}.", f"outfit{src_outfit}."
        keep = [e for e in arr_split(d_info_raw)
                if not unpack(unpack(e)).get("Property", "").startswith(pre_d)]
        s_info_raw = S.get(UAR, "info")
        for e in (arr_split(s_info_raw) if s_info_raw else []):
            inner = unpack(e)
            d = unpack(inner)
            prop = d.get("Property", "")
            if not prop.startswith(pre_s):
                continue
            keep.append(_bin_wrap(
                patch_entry(inner, {"Property": pre_d + prop[len(pre_s):]})))
            n_uar += 1
        if D.set(UAR, "info", arr_header(len(keep)) + b"".join(keep)):
            touched.add(UAR)
    log(T("Sideloader 條目：換上 {0} 筆").format(n_uar))

    # ---- 4. MaterialEditor ----
    n_me, tex_added, tex_pruned = 0, 0, 0
    if D.has(ME_GUID):
        d_tex_raw = D.get(ME_GUID, "TextureDictionary")
        s_tex_raw = S.get(ME_GUID, "TextureDictionary") if S.has(ME_GUID) else None
        d_tex = [list(t) for t in map_split(unpack(d_tex_raw))] if d_tex_raw else []
        s_tex = {k: vb for k, _, vb in (map_split(unpack(s_tex_raw)) if s_tex_raw else [])}
        nxt = (max(t[0] for t in d_tex) + 1) if d_tex else 1
        tex_map, new_lists = {}, {}
        for key in ME_LISTS:
            dv = D.get(ME_GUID, key)
            if dv is None or dv == b"\xc0":
                cur = []
            else:
                cur = [e for e in arr_split(unpack(dv))
                       if not (unpack(e).get("CoordinateIndex") == dst_outfit
                               and unpack(e).get("ObjectType") in ME_OUTFIT_TYPES)]
            sv = S.get(ME_GUID, key) if S.has(ME_GUID) else None
            for e in (arr_split(unpack(sv)) if (sv and sv != b"\xc0") else []):
                d = unpack(e)
                if d.get("CoordinateIndex") != src_outfit or \
                        d.get("ObjectType") not in ME_OUTFIT_TYPES:
                    continue
                upd = {"CoordinateIndex": dst_outfit}
                tid = d.get("TexID")
                if tid is not None and tid in s_tex:
                    if tid not in tex_map:
                        tex_map[tid] = nxt
                        d_tex.append([nxt, pack(nxt), s_tex[tid]])
                        nxt += 1
                    upd["TexID"] = tex_map[tid]
                cur.append(patch_entry(e, upd))
                n_me += 1
            new_lists[key] = pack(arr_header(len(cur)) + b"".join(cur)) if cur else b"\xc0"
        tex_added = len(tex_map)
        if prune:
            used = set()
            for key, blob in new_lists.items():
                if blob == b"\xc0":
                    continue
                for e in arr_split(unpack(blob)):
                    t = unpack(e).get("TexID")
                    if t is not None:
                        used.add(t)
            before = len(d_tex)
            d_tex = [t for t in d_tex if t[0] in used]
            tex_pruned = before - len(d_tex)
        for key, blob in new_lists.items():
            if D.set(ME_GUID, key, blob):
                touched.add(ME_GUID)
        if D.set(ME_GUID, "TextureDictionary",
                 pack(map_build([tuple(t) for t in d_tex])) if d_tex else b"\xc0"):
            touched.add(ME_GUID)
    log(T("MaterialEditor：條目 {0} 筆、貼圖 +{1} / 清掉 {2} 張孤兒").format(n_me, tex_added, tex_pruned))

    # ---- 5. KSOX 皮膚 / 臉 / 眼睛 overlay：屬於「人」，預設不搬 ----
    n_ks = 0
    d_lk_raw = D.get("KSOX", "Lookup")
    if d_lk_raw is not None:
        lk = unpack(unpack(d_lk_raw)) or {}
        if skin_overlay == "follow_outfit" and S.get("KSOX", "Lookup"):
            s_lk = unpack(unpack(S.get("KSOX", "Lookup"))) or {}
            lk.pop(dst_outfit, None)
            if src_outfit in s_lk:
                used = {v for m in lk.values() for v in m.values()}
                nx = max(list(used) + [0]) + 1
                newmap = {}
                for kind, tid in s_lk[src_outfit].items():
                    blob = S.get("KSOX", f"_TextureID_{tid}")
                    if blob is None:
                        continue
                    D.set("KSOX", f"_TextureID_{nx}", blob)
                    newmap[kind] = nx
                    nx += 1
                    n_ks += 1
                if newmap:
                    lk[dst_outfit] = newmap
            D.set("KSOX", "Lookup", pack(pack(lk)))
            touched.add("KSOX")
            log(T("KSOX 皮膚 overlay：跟著服裝搬了 {0} 張").format(n_ks))
        elif dst_outfit not in lk and lk:
            lk[dst_outfit] = copy.deepcopy(next(iter(lk.values())))
            D.set("KSOX", "Lookup", pack(pack(lk)))
            touched.add("KSOX")
            log(T("KSOX：目標第 {0} 套原本沒有 overlay，已從其他套補上").format(dst_outfit))
        else:
            log(T("KSOX 皮膚 / 眼睛 overlay：保留目標人物的（不隨服裝移植）"))

    # ---- 6. 以換裝索引為鍵的外掛：整段原始位元組搬過去 ----
    table = list(COORD_DICT) + (list(PUSHUP_COORD) if pushup == "follow_outfit" else [])
    for guid, key in table:
        d_raw_v, s_raw_v = D.get(guid, key), S.get(guid, key)
        if d_raw_v is None and s_raw_v is None:
            continue
        src_item = None
        if s_raw_v is not None:
            for k, _, vb in map_split(unpack(s_raw_v)):
                if k == src_outfit:
                    src_item = vb
                    break
        if d_raw_v is None:
            if src_item is None or not S.has(guid):
                continue
            D.set(guid, key, pack(map_build([(dst_outfit, pack(dst_outfit), src_item)])),
                  src_raw_value=S._value(guid))
            touched.add(guid)
            continue
        inner = unpack(d_raw_v)
        new_inner = map_drop_insert(inner, dst_outfit,
                                    dst_outfit if src_item else None, src_item)
        if new_inner != inner and D.set(guid, key, pack(new_inner)):
            touched.add(guid)

    # ---- 7. 以清單存放、每筆帶換裝索引的外掛 ----
    for guid, key in COORD_LIST:
        d_raw_v, s_raw_v = D.get(guid, key), S.get(guid, key)
        if d_raw_v is None and s_raw_v is None:
            continue
        s_items = arr_split(unpack(s_raw_v)) if s_raw_v else []
        d_items = arr_split(unpack(d_raw_v)) if d_raw_v else []
        field = next((_coord_field(unpack(e)) for e in d_items + s_items), None)
        if field is None:
            continue
        moved = [e for e in s_items if unpack(e).get(field) == src_outfit]
        if not moved and d_raw_v is not None and \
                not any(unpack(e).get(field) == dst_outfit for e in d_items):
            continue                                  # 兩邊都沒東西，別動它
        keep = [e for e in d_items if unpack(e).get(field) != dst_outfit]
        keep += [patch_entry(e, {field: dst_outfit}) for e in moved]
        blob = pack(arr_header(len(keep)) + b"".join(keep))
        if d_raw_v is None:
            D.set(guid, key, blob, src_raw_value=S._value(guid))
            touched.add(guid)
        elif D.set(guid, key, blob):
            touched.add(guid)

    # ---- 7b. 換裝索引編在 key 名稱裡的外掛（OBJ 網格匯入等）----
    for guid, patterns in COORD_KEYNAME:
        s_vals = {}
        for pat in patterns:
            v = S.get(guid, pat.format(src_outfit)) if S.has(guid) else None
            if v is not None:
                s_vals[pat] = v
        if s_vals:
            if D.has(guid):
                for pat, v in s_vals.items():
                    if D.set(guid, pat.format(dst_outfit), v):
                        touched.add(guid)
            else:                                   # 目標卡沒裝過，整個建起來
                items = [(pat.format(dst_outfit), pack(pat.format(dst_outfit)), v)
                         for pat, v in s_vals.items()]
                sver = arr_split(S._value(guid))[0]
                D.items.append([guid, pack(guid),
                                arr_header(2) + sver + map_build(items)])
                D.idx[guid] = len(D.items) - 1
                touched.add(guid)
            log(T("{0}：搬入第 {1} 套的資料 -> 第 {2} 套").format(guid, src_outfit, dst_outfit))
        elif D.has(guid):                            # 來源沒有，就把目標那套清空
            for pat in patterns:
                if D.get(guid, pat.format(dst_outfit)) is not None:
                    if D.set(guid, pat.format(dst_outfit), pack(pack([]))):
                        touched.add(guid)

    # ---- 8. 來源卡上有、但不在支援清單裡的換裝層級資料，明講出來 ----
    known = {g for g, _ in COORD_DICT + COORD_LIST + PUSHUP_COORD} | \
        {g for g, _ in COORD_KEYNAME} | \
        {ME_GUID, "KSOX", "moreAccessories", UAR} | set(SAFE_IGNORE)
    for guid, _, vb in S.items:
        if guid in known:
            continue
        try:
            parts = arr_split(vb)
            pay = unpack(parts[1]) if len(parts) > 1 else None
        except Exception:                                    # noqa: BLE001
            continue
        if not isinstance(pay, dict):
            continue
        kn = [k2 for k2 in pay if isinstance(k2, str) and _KEYNAME_RE.match(k2)
              and int(_KEYNAME_RE.match(k2).group(2)) < len(s_out)]
        if len({_KEYNAME_RE.match(k2).group(1) for k2 in kn}) and len(kn) >= 2:
            warnings.append(T("{0} 的 key 名稱帶換裝索引（{1}…），不在支援清單中，未搬移").format(guid, ', '.join(sorted(kn)[:4])))
            continue
        for k2, val in pay.items():
            if not isinstance(val, (bytes, bytearray)):
                continue
            try:
                o = unpack(val)
            except Exception:                                # noqa: BLE001
                continue
            if (isinstance(o, dict) and o and all(isinstance(x, int) for x in o)
                    and max(o) < len(s_out) + 8) or \
               (isinstance(o, list) and o and isinstance(o[0], dict)
                    and _coord_field(o[0]) is not None):
                warnings.append(T("{0}/{1} 看起來是換裝層級資料，不在支援清單中，未搬移").format(guid, k2))

    dst.blocks["KKEx"] = D.to_bytes()
    orphans = clean_orphans(dst, log) if clean else {}
    dst.save(out_path)
    for w in warnings:
        log(T("[注意] {0}").format(w))
    return {"ok": True, "out": str(out_path), "src_outfit": src_outfit,
            "dst_outfit": dst_outfit, "slot_total": total,
            "materialeditor_entries": n_me, "textures_added": tex_added,
            "textures_pruned": tex_pruned, "sideloader_entries": n_uar,
            "ksox_textures": n_ks, "pushup": pushup,
            "skin_overlay": skin_overlay,
            "plugins_touched": sorted(touched), "warnings": warnings,
            "orphans_removed": orphans,
            "size_bytes": Path(out_path).stat().st_size}


def info(path) -> dict:
    d = Path(path).read_bytes()
    r = Reader(d)
    r.p = png_len(d, 0)
    r.i32()
    marker = r.cs_str()
    if marker in KNOWN_COORD:
        c = load_coord_card(path)
        parts = c["accessory"]["parts"]
        return {"ok": True, "type": "coordinate", "marker": marker,
                "name": c["name"], "slots": len(parts),
                "accessories": sum(1 for p in parts if p["type"] != EMPTY_TYPE),
                "used_slots": [i for i, p in enumerate(parts) if p["type"] != EMPTY_TYPE],
                "plugins": sorted(c["kkex"].keys())}
    card = CharaCard(path)
    segs = [split_coord(b) for b in unpack(card.blocks["Coordinate"])]
    accs = [unpack(s["acc"]) for s in segs]
    kkex = unpack(card.blocks["KKEx"]) if "KKEx" in card.blocks else {}

    # 換裝名稱：MoreOutfits 會存 {索引: 名稱}，沒改過名的就不在裡面
    names = {}
    mo = kkex.get("com.deathweasel.bepinex.moreoutfits")
    if isinstance(mo, list) and len(mo) > 1 and isinstance(mo[1], dict):
        try:
            names = unpack(mo[1]["CoordinateNames"]) or {}
        except Exception:                                    # noqa: BLE001
            names = {}
    fallback = [T("制服1"), T("制服2"), T("私服"), T("泳裝"), T("體操服"), T("部活"), T("浴衣")]

    detail = []
    for i, a in enumerate(accs):
        used = [j for j, p in enumerate(a["parts"]) if p["type"] != EMPTY_TYPE]
        clothes = unpack(segs[i]["clothes"])
        detail.append({
            "index": i,
            "name": names.get(i) or (fallback[i] if i < len(fallback) else T("第 {0} 套").format(i + 1)),
            "named": i in names,
            "slots": len(a["parts"]),
            "accessories": len(used),
            "clothes_ids": [c.get("id") for c in clothes.get("parts", [])],
        })
    return {"ok": True, "type": "character", "marker": card.marker,
            "outfits": len(accs), "slots_per_outfit": [len(a["parts"]) for a in accs],
            "outfit_detail": detail,
            "blocks": {n: len(b) for n, b in card.blocks.items()},
            "plugins": sorted(kkex.keys())}


def _emit(res: dict, path, ok: bool):
    txt = json.dumps(res, ensure_ascii=False, indent=2)
    if path:
        try:
            Path(path).write_text(txt, encoding="utf-8")
        except Exception as e:                               # noqa: BLE001
            print(T("結果檔寫不出來 {0}：{1}").format(path, e), file=sys.stderr)
    print(txt if not ok else json.dumps(res, ensure_ascii=False),
          file=(sys.stdout if ok else sys.stderr))


def _early_lang(argv):
    """在建 parser 之前就把語言定下來。

    argparse 的 help 字串是在 add_argument 的當下求值的，所以「先 parse
    --lang 再設語言」是來不及的 —— 那時候 help 已經是中文了。只好自己
    先掃一遍 argv。

    掃不到就去讀 kkbridge 的設定檔：從介面改過語言之後，直接下指令跑
    kkmerge 也會是同一種語言，不用再記得加參數。讀不到就中文。
    """
    argv = argv or []
    for i, a in enumerate(argv):
        if a == "--lang" and i + 1 < len(argv):
            try:
                return int(argv[i + 1])
            except ValueError:
                pass
        elif a.startswith("--lang="):
            try:
                return int(a.split("=", 1)[1])
            except ValueError:
                pass
    try:
        base = (Path(sys.executable).parent if getattr(sys, "frozen", False)
                else Path(__file__).resolve().parent)
        cfg = json.loads((base / "kkbridge_settings.json").read_text("utf-8"))
        return int(cfg.get("lang", 0) or 0)
    except Exception:                                    # noqa: BLE001
        return 0


def main(argv=None) -> int:
    set_lang(_early_lang(list(sys.argv[1:] if argv is None else argv)))
    ap = argparse.ArgumentParser(prog="kkmerge", description=T("恋活角色卡飾品合併"))
    ap.add_argument("--version", action="version", version=VERSION)
    ap.add_argument("--lang", type=int, choices=[0, 1, 2],
                    help=T("介面語言：0=繁體中文 1=English 2=日本語"))
    ap.add_argument("--chara", help=T("目標角色卡 png"))
    ap.add_argument("--coord", help=T("來源服裝卡 png"))
    ap.add_argument("--outfit", type=int, default=0, help=T("要合併到第幾套換裝（0 起算）"))
    ap.add_argument("--out", help=T("輸出角色卡 png"))
    ap.add_argument("--slots", help=T("只搬指定欄位，逗號分隔，例：0,3,7"))
    ap.add_argument("--info", help=T("印出卡片資訊後結束"))
    ap.add_argument("--op", choices=["append", "transplant", "clean"], default="append",
                    help=T("append=服裝卡飾品附加（預設）；transplant=整套換裝移植"))
    ap.add_argument("--src", help=T("transplant：來源角色卡"))
    ap.add_argument("--src-outfit", type=int, default=0, help=T("transplant：來源第幾套"))
    ap.add_argument("--dst", help=T("transplant：目標角色卡"))
    ap.add_argument("--dst-outfit", type=int, default=0, help=T("transplant：目標第幾套"))
    ap.add_argument("--pushup", choices=["follow_outfit", "keep_target"],
                    default="follow_outfit", help=T("胸托參數跟著服裝走還是保留目標人物的"))
    ap.add_argument("--skin-overlay", choices=["keep_target", "follow_outfit"],
                    default="keep_target",
                    help=T("KSOX 皮膚/臉/眼睛 overlay：預設保留目標人物的"))
    ap.add_argument("--no-clean", action="store_true",
                    help=T("不要清除指向空飾品欄位的殘留擴充資料"))
    ap.add_argument("--no-prune", action="store_true", help=T("不清除沒人引用的孤兒貼圖"))
    ap.add_argument("--quiet", action="store_true", help=T("只輸出最後那行 JSON"))
    ap.add_argument("--result", help=T("把結果 JSON 另外寫到這個檔案（UTF-8）。呼叫端不必處理 stdout 編碼，讀檔就好"))
    a = ap.parse_args(argv)

    log = (lambda *x: None) if a.quiet else (lambda *x: print(*x, file=sys.stderr))
    try:
        if a.info:
            _emit(info(a.info), a.result, True)
            return 0
        if a.op == "clean":
            if not (a.chara and a.out):
                ap.error(T("clean 需要 --chara / --out"))
            c = CharaCard(a.chara)
            rm = clean_orphans(c, log)
            c.save(a.out)
            _emit({"ok": True, "out": a.out, "orphans_removed": rm,
                   "size_bytes": Path(a.out).stat().st_size}, a.result, True)
            return 0
        if a.op == "transplant":
            if not (a.src and a.dst and a.out):
                ap.error(T("transplant 需要 --src / --dst / --out"))
            res = transplant(a.src, a.src_outfit, a.dst, a.dst_outfit, a.out,
                             pushup=a.pushup, skin_overlay=a.skin_overlay,
                             prune=not a.no_prune, clean=not a.no_clean, log=log)
            _emit(res, a.result, True)
            return 0
        if not (a.chara and a.coord and a.out):
            ap.error(T("需要 --chara / --coord / --out（或改用 --info）"))
        slots = [int(s) for s in a.slots.split(",")] if a.slots else None
        res = merge(a.chara, a.coord, a.outfit, a.out, slots,
                    clean=not a.no_clean, log=log)
        _emit(res, a.result, True)
        return 0
    except Exception as e:                                   # noqa: BLE001
        _emit({"ok": False, "error": f"{type(e).__name__}: {e}"},
              getattr(a, "result", None), False)
        return 1


if __name__ == "__main__":
    sys.exit(main())
