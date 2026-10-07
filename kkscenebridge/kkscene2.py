# -*- coding: utf-8 -*-
"""kkscene2.py — 場景卡的容器操作（樹 + KKEx）

上層工具（kkscenemerge.py）用這裡的東西做整理與合併。
所有「物件參照在哪裡、用哪套座標系」的知識集中在 kkref.py。
"""
import io
import re

import kkcheck                      # 套用 kkloader 的所有修補
import kkref
from kkmsgpack import (arr_build, arr_split, map_build, map_split, pack,
                       patch_entry, patch_key, unpack)

KoikatuSceneData = kkcheck.KoikatuSceneData
KIND = kkcheck.KIND


# ================================================================ KKEx 容器
class KKEx:
    """場景層級 KKEx 的頂層 map，沒動到的外掛完全保留原始位元組。

    頂層是 {guid: [version, {key: value}]}，value 通常又是包在 bin 裡的 msgpack。
    """

    def __init__(self, raw: bytes):
        self.items = [list(t) for t in map_split(raw)] if raw else []
        self.idx = {g: i for i, (g, _, _) in enumerate(self.items)}

    # -- 基本 --
    def has(self, guid):
        return guid in self.idx

    def guids(self):
        return [g for g, _, _ in self.items]

    def _value(self, guid):
        return self.items[self.idx[guid]][2]

    def _parts(self, guid):
        return arr_split(self._value(guid))

    def payload_items(self, guid):
        """回傳 [(key, key_bytes, value_bytes)]，沒有就 None。"""
        if guid not in self.idx:
            return None
        parts = self._parts(guid)
        if len(parts) < 2:
            return None
        try:
            return [list(t) for t in map_split(parts[1])]
        except ValueError:
            return None

    def set_payload(self, guid, items):
        parts = self._parts(guid)
        body = map_build([tuple(i) for i in items])
        self.items[self.idx[guid]][2] = (
            arr_build([parts[0], body] + parts[2:]))

    def get(self, guid, key):
        items = self.payload_items(guid)
        if items is None:
            return None
        for k, _, vb in items:
            if k == key:
                return vb
        return None

    def set(self, guid, key, val_bytes):
        items = self.payload_items(guid)
        if items is None:
            return False
        for it in items:
            if it[0] == key:
                if it[2] == val_bytes:
                    return False
                it[2] = val_bytes
                break
        else:
            items.append([key, pack(key), val_bytes])
        self.set_payload(guid, items)
        return True

    def add_entry(self, guid, raw_value):
        """把另一張卡的整個外掛條目原封不動搬進來。"""
        if guid in self.idx:
            return False
        self.items.append([guid, pack(guid), raw_value])
        self.idx[guid] = len(self.items) - 1
        return True

    def raw_entry(self, guid):
        return self._value(guid)

    def to_bytes(self):
        return map_build([tuple(i) for i in self.items])

    # -- bin 包裝（大部分 payload 是 bin 裡再包一層 msgpack）--
    @staticmethod
    def inner(val_bytes):
        return unpack(val_bytes)

    @staticmethod
    def wrap(b):
        return pack(b)

    def get_inner(self, guid, key):
        vb = self.get(guid, key)
        return None if vb is None else unpack(vb)

    def set_inner(self, guid, key, inner_bytes):
        return self.set(guid, key, pack(inner_bytes))


# ================================================================ 樹
def children_of(node):
    """回傳可直接 append 的 child list；角色節點回傳 None（它的 child 是接點字典）。"""
    d = node["data"]
    ch = d.get("child")
    return ch if isinstance(ch, list) else None


def iter_nodes(objects, depth=0, parent=None):
    """DFS 走遍整棵樹，yield (node, depth, parent)。角色的接點子物件也會走到。"""
    seq = objects.values() if isinstance(objects, dict) else objects
    for node in seq:
        yield node, depth, parent
        d = node["data"]
        ch = d.get("child")
        if isinstance(ch, list):
            yield from iter_nodes(ch, depth + 1, node)
        elif isinstance(ch, dict):
            for lst in ch.values():
                if isinstance(lst, list):
                    yield from iter_nodes(lst, depth + 1, node)
                elif isinstance(lst, dict) and "type" in lst:
                    yield from iter_nodes([lst], depth + 1, node)


def node_dickeys(scene_objects):
    """DFS 順序的節點 dicKey 清單（RANK 空間就是這串排序後的名次）。"""
    return [n["data"]["dicKey"] for n, _, _ in iter_nodes(scene_objects)]


def all_dickeys(scene_objects):
    """節點 + 骨架 + IK + route 點，全部 dicKey。"""
    out = set()
    for n, _, _ in iter_nodes(scene_objects):
        d = n["data"]
        out.add(d["dicKey"])
        for grp in ("bones", "ik_targets"):
            for v in (d.get(grp) or {}).values():
                if isinstance(v, dict) and "dicKey" in v:
                    out.add(v["dicKey"])
        lat = d.get("lookAtTarget")
        if isinstance(lat, dict) and "dicKey" in lat:
            out.add(lat["dicKey"])
        for rp in (d.get("route_points") or d.get("routePoints") or []):
            if isinstance(rp, dict) and "dicKey" in rp:
                out.add(rp["dicKey"])
    return out


def remap_tree_dickeys(scene, fn):
    """把整棵樹（含骨架 / IK / lookAtTarget / route 點）的 dicKey 換掉。

    fn: old -> new。根字典的鍵也一起換。
    """
    for n, _, _ in iter_nodes(scene.objects):
        d = n["data"]
        d["dicKey"] = fn(d["dicKey"])
        for grp in ("bones", "ik_targets"):
            for v in (d.get(grp) or {}).values():
                if isinstance(v, dict) and "dicKey" in v:
                    v["dicKey"] = fn(v["dicKey"])
        lat = d.get("lookAtTarget")
        if isinstance(lat, dict) and "dicKey" in lat:
            lat["dicKey"] = fn(lat["dicKey"])
        for rp in (d.get("route_points") or d.get("routePoints") or []):
            if isinstance(rp, dict) and "dicKey" in rp:
                rp["dicKey"] = fn(rp["dicKey"])
    scene.objects = type(scene.objects)(
        (v["data"]["dicKey"], v) for v in scene.objects.values())


def rank_map(dickeys):
    """dicKey -> RANK（在全部節點 dicKey 排序後的名次，0 起算）。"""
    return {k: i for i, k in enumerate(sorted(dickeys))}


# ================================================================ 場景
class Scene:
    def __init__(self, path=None, data=None):
        if data is not None:
            self.sc = data
        else:
            self.sc = KoikatuSceneData.load(io.BytesIO(open(path, "rb").read()))
        self.path = path
        self.kkex = KKEx(getattr(self.sc, "mod_raw", b"") or b"")

    # -- 基本資訊 --
    @property
    def objects(self):
        return self.sc.objects

    @objects.setter
    def objects(self, v):
        self.sc.objects = v

    @property
    def version(self):
        return self.sc.version

    def node_list(self):
        return [n for n, _, _ in iter_nodes(self.sc.objects)]

    def cameras(self):
        out = []
        path = {}
        for n, depth, parent in iter_nodes(self.sc.objects):
            nm = n["data"].get("name") or ""
            path[id(n)] = (path.get(id(parent), "") + "/" + str(nm)) if parent is not None else "/" + str(nm)
            if n["type"] == 5:
                out.append((n, path[id(n)]))
        return out

    def duration(self):
        xml = self.timeline_xml()
        if not xml:
            return None
        m = re.match(r'<root[^>]*\bduration="([\d.eE+-]+)"', xml)
        return float(m.group(1)) if m else None

    # -- timeline --
    def timeline_xml(self):
        v = self.kkex.get("timeline", "sceneInfo")
        return None if v is None else unpack(v)

    def set_timeline_xml(self, xml):
        self.kkex.set("timeline", "sceneInfo", pack(xml))

    # -- 存檔 --
    def save(self, path):
        self.sc.mod_raw = self.kkex.to_bytes()
        with open(path, "wb") as f:
            f.write(bytes(self.sc))
        return path

    def to_bytes(self):
        self.sc.mod_raw = self.kkex.to_bytes()
        return bytes(self.sc)


# ================================================================ timeline XML
RE_ROOT = re.compile(r'^(<root\b[^>]*>)(.*)(</root>)$', re.S)
RE_INTERP = re.compile(r'<interpolable\b[^>]*?/>'
                       r'|<interpolable\b[^>]*?>(?:(?!</?interpolable\b).)*?</interpolable>', re.S)
RE_KEYFRAME_TIME = re.compile(r'(<keyframe\s+time=")([-\d.eE+]+)(")')
RE_OBJIDX = re.compile(r'(\bobjectIndex=")(-?\d+)(")')


def split_root(xml):
    m = RE_ROOT.match(xml.strip())
    if not m:
        raise ValueError("timeline sceneInfo 不是預期的 <root>…</root>")
    return m.group(1), m.group(2), m.group(3)


def set_root_attr(head, name, value):
    if re.search(rf'\b{name}="', head):
        return re.sub(rf'(\b{name}=")[^"]*(")', lambda m: m.group(1) + str(value) + m.group(2), head)
    return head[:-1] + f' {name}="{value}">'


def fmt_time(t):
    s = f"{t:.6f}".rstrip("0").rstrip(".")
    return s if s else "0"


def shift_keyframe_times(xml, offset):
    """只動 <keyframe time="…">，不動 <curveKeyframe>（那是 0~1 的曲線參數）。"""
    return RE_KEYFRAME_TIME.sub(
        lambda m: m.group(1) + fmt_time(float(m.group(2)) + offset) + m.group(3), xml)


def remap_object_index(xml, fn):
    def rep(m):
        v = int(m.group(2))
        if v < 0:
            return m.group(0)
        nv = fn(v)
        return m.group(0) if nv is None else m.group(1) + str(nv) + m.group(3)
    return RE_OBJIDX.sub(rep, xml)


RE_INTERP_HEAD = re.compile(r'<interpolable\b[^>]*?>')
RE_INTERP_BLOCK = re.compile(r'<interpolable\b[^>]*?(?:/>|>.*?</interpolable>)', re.S)
RE_KF_INT_VALUE = re.compile(r'(<keyframe\b[^>]*?\bvalue=")(-?\d+)(")')


def remap_me_texture_keys(xml, texmap):
    """MaterialEditor 的「貼圖」軌道（owner="MaterialEditor" id="textureProperty"）：
    每個關鍵影格的 value 是場景貼圖字典裡的編號。合併時第二張卡的貼圖會被重新編號，
    這裡的編號也要跟著換，不然關鍵影格會指到別張貼圖。

    回傳 (新的 xml, 改了幾格)。只動這一種軌道，其他軌道一個字都不碰。
    """
    if not texmap or 'id="textureProperty"' not in xml:
        return xml, 0
    n = [0]

    def blk(m):
        s = m.group(0)
        head = s[:s.index(">") + 1]
        if 'owner="MaterialEditor"' not in head or 'id="textureProperty"' not in head:
            return s

        def kf(k):
            v = int(k.group(2))
            nv = texmap.get(v, v)
            if nv != v:
                n[0] += 1
            return k.group(1) + str(nv) + k.group(3)
        return RE_KF_INT_VALUE.sub(kf, s)
    return RE_INTERP_BLOCK.sub(blk, xml), n[0]


def remap_nc_parameter(xml, fn):
    """只動 owner="NodesConstraints" 的 parameter —— 那是「第幾條 constraint」。

    KKPE 的 parameter 是骨架路徑字串，不能碰。
    """
    def rep(m):
        h = m.group(0)
        if 'owner="NodesConstraints"' not in h:
            return h
        return re.sub(r'(\bparameter=")(-?\d+)(")',
                      lambda mm: mm.group(1) + str(fn(int(mm.group(2)))) + mm.group(3), h)
    return RE_INTERP_HEAD.sub(rep, xml)


def renumber_unique_load_id(xml):
    """把 <constraint> 的 uniqueLoadId 重新編成 0..n-1（timeline 的 parameter 就是這個序號）。"""
    n = [0]

    def rep(m):
        blk = m.group(0)
        out = re.sub(r'(\buniqueLoadId=")(-?\d+)(")',
                     lambda mm: mm.group(1) + str(n[0]) + mm.group(3), blk)
        n[0] += 1
        return out
    return re.sub(r'<constraint\b[^>]*?/>|<constraint\b[^>]*?>.*?</constraint>',
                  rep, xml, flags=re.S), n[0]


def is_global_track(head):
    """owner="Timeline" 且沒有 objectIndex 且 id 在三條全域軌道裡。"""
    if 'owner="Timeline"' not in head or 'objectIndex="' in head:
        return False
    m = re.search(r'\bid="([^"]*)"', head)
    return bool(m and m.group(1) in kkref.GLOBAL_TIMELINE_IDS)


def extract_global_tracks(body):
    """把全域軌道從 body 抽出來，回傳 (剩下的 body, {id: (head, inner)})。"""
    found = {}
    pieces = []
    last = 0
    for m in RE_INTERP.finditer(body):
        blk = m.group(0)
        head = re.match(r'<interpolable\b[^>]*?>', blk)
        head = head.group(0) if head else blk
        if not is_global_track(head):
            continue
        tid = re.search(r'\bid="([^"]*)"', head).group(1)
        inner = blk[len(head):-len("</interpolable>")] if blk.endswith("</interpolable>") else ""
        found[tid] = (head, inner)
        pieces.append(body[last:m.start()])
        last = m.end()
    pieces.append(body[last:])
    return "".join(pieces), found


def merge_global_tracks(body, tracks):
    """把抽出來的全域軌道關鍵影格併進 body 裡同名的軌道；沒有就整條接在最後。"""
    if not tracks:
        return body
    remaining = dict(tracks)
    out = []
    last = 0
    for m in RE_INTERP.finditer(body):
        blk = m.group(0)
        hm = re.match(r'<interpolable\b[^>]*?>', blk)
        head = hm.group(0) if hm else blk
        if not is_global_track(head):
            continue
        tid = re.search(r'\bid="([^"]*)"', head).group(1)
        if tid not in remaining:
            continue
        _, inner = remaining.pop(tid)
        if blk.endswith("</interpolable>"):
            new = blk[:-len("</interpolable>")] + inner + "</interpolable>"
        else:                                   # 原本是空軌道 <interpolable … />
            new = head[:-2] + ">" + inner + "</interpolable>"
        out.append(body[last:m.start()])
        out.append(new)
        last = m.end()
    out.append(body[last:])
    tail = "".join(h + i + "</interpolable>" for h, i in remaining.values())
    return "".join(out) + tail


def remove_track(body, object_index, track_id):
    """把指定物件的某條軌道整條從 body 拿掉，回傳 (新 body, 拿掉幾條)。"""
    out, last, n = [], 0, 0
    for m in RE_INTERP.finditer(body):
        blk = m.group(0)
        hm = re.match(r'<interpolable\b[^>]*?>', blk)
        head = hm.group(0) if hm else blk
        if f'objectIndex="{object_index}"' not in head:
            continue
        mid = re.search(r'\bid="([^"]*)"', head)
        if not mid or mid.group(1) != track_id:
            continue
        out.append(body[last:m.start()])
        last = m.end()
        n += 1
    out.append(body[last:])
    return "".join(out), n


def park_track(object_index, points):
    """外太空停放軌道。points = [(時間, (x,y,z)), …]"""
    kfs = "".join(
        f'<keyframe time="{fmt_time(t)}" valueX="{fmt_time(p[0])}"'
        f' valueY="{fmt_time(p[1])}" valueZ="{fmt_time(p[2])}">{kkref.PARK_CURVE}</keyframe>'
        for t, p in points)
    return (f'<interpolable enabled="true" owner="Timeline" objectIndex="{object_index}"'
            f' id="guideObjectPos" guideObjectPath="" bgColorR="1" bgColorG="1"'
            f' bgColorB="1" alias="">{kfs}</interpolable>')


# ================================================================ 樹的手術
def parent_map(scene_objects):
    """{id(node): (parent_node 或 None, 裝著它的容器)}；容器是 list 或根 dict。"""
    out = {}

    def walk(container, parent):
        seq = container.values() if isinstance(container, dict) else container
        for node in list(seq):
            out[id(node)] = (parent, container)
            ch = node["data"].get("child")
            if isinstance(ch, list):
                walk(ch, node)
            elif isinstance(ch, dict):
                for lst in ch.values():
                    if isinstance(lst, list):
                        walk(lst, node)
    walk(scene_objects, None)
    return out


def detach(node, container):
    """把節點從它的容器拿掉。"""
    if isinstance(container, dict):
        for k, v in list(container.items()):
            if v is node:
                del container[k]
                return True
        return False
    try:
        container.remove(node)
        return True
    except ValueError:
        return False


def subtree_dickeys(node):
    return [n["data"]["dicKey"] for n, _, _ in iter_nodes([node])]


def new_folder(dickey, name, children=None, tree_state=1):
    """treeState：0 = 展開（Open），1 = 折疊（Close）。新資料夾預設折疊。"""
    return {"type": 3, "data": {
        "dicKey": dickey,
        "position": {"x": 0.0, "y": 0.0, "z": 0.0},
        "rotation": {"x": 0.0, "y": 0.0, "z": 0.0},
        "scale": {"x": 1.0, "y": 1.0, "z": 1.0},
        "treeState": tree_state, "visible": True,
        "name": name, "child": children if children is not None else []}}


def ancestors(node, pmap):
    """由近到遠的祖先節點。"""
    out = []
    p = pmap.get(id(node), (None, None))[0]
    while p is not None:
        out.append(p)
        p = pmap.get(id(p), (None, None))[0]
    return out
