#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""kksceneinfo — 把一張場景卡的「環境」資訊寫成文字報告，用來查 KK / KKS 載入結果不一樣的問題。

報告內容：
  · 場景版本、地圖編號、地圖的位移/旋轉/縮放、地圖顯示開關
  · 卡片裡有哪些外掛的擴充資料（以及大小）—— 對照兩邊裝了哪些外掛，就知道誰只在一邊生效
  · Better Scaling 的旗標、縮放不是 1 的資料夾
  · 物件樹前幾層（種類、名稱、位置、旋轉、縮放）
  · 用到的 mod（UniversalAutoResolver 的 itemInfo，依 GUID 統計）

用法：
  python kksceneinfo.py <場景卡.png> [輸出.txt]
  （沒給輸出檔就寫在卡片旁邊：<卡名>.info.txt）
"""
from __future__ import annotations

import collections
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import kkscene2 as S                     # noqa: E402
from kkmsgpack import unpack             # noqa: E402

TYPE_NAME = {0: "角色", 1: "道具", 2: "燈光", 3: "資料夾", 4: "路線", 5: "相機"}


def v3(d):
    if not isinstance(d, dict):
        return str(d)
    try:
        return "(%s)" % ", ".join("%g" % round(float(d.get(a, 0.0)), 4) for a in "xyz")
    except (TypeError, ValueError):
        return str(d)


def main(path, out=None):
    out = out or (os.path.splitext(path)[0] + ".info.txt")
    sc = S.Scene(path)
    d = sc.sc
    L = []
    w = L.append
    w("場景卡：%s" % path)
    w("版本：%s" % getattr(d, "version", "?"))
    w("地圖編號 map：%s　（-1 = 沒有用遊戲地圖）" % getattr(d, "map", "?"))
    ca = getattr(d, "caMap", None) or {}
    w("地圖位移 caMap：pos %s　rot %s　scale %s" % (v3(ca.get("pos")), v3(ca.get("rot")), v3(ca.get("scale"))))
    w("地圖顯示 mapOption：%s" % getattr(d, "mapOption", "?"))
    for k in ("sunLightType", "aceNo", "aceBlend", "enableAOE", "aoeColor", "aoeRadius", "enableBloom",
              "enableDepth", "enableVignette", "enableFog", "enableSunShafts", "fogHeight", "skyInfo"):
        if hasattr(d, k):
            w("  %s：%s" % (k, getattr(d, k)))
    w("")

    w("== 擴充資料（外掛 GUID：大小）==")
    for g, _ver, val in sc.kkex.items:
        w("  %-60s %8d bytes" % (g, len(val) if isinstance(val, (bytes, bytearray)) else -1))
        try:
            for k, kb, vb in sc.kkex.payload_items(g) or []:
                w("      · %-40s %8d bytes" % (k, len(vb)))
        except Exception:                                   # noqa: BLE001
            pass
    w("")

    try:
        import kkfolderscale as FS
        w("== Better Scaling ==")
        w("  旗標 scaledFoldersFlag：%s" % FS.bs_flag(sc))
        found = FS.scaled_folders(sc)
        w("  縮放不是 1 的資料夾：%d 個" % len(found))
        for p, s, _n in found[:50]:
            w("    %s  %s" % (FS.fmt_scale(s), p))
        w("")
    except Exception as e:                                  # noqa: BLE001
        w("（Better Scaling 檢查失敗：%s）" % e)

    w("== 物件樹（前 3 層）==")
    for n, depth, _parent in S.iter_nodes(sc.objects):
        if depth > 2:
            continue
        dd = n.get("data") or {}
        w("%s[%s] %s　dicKey=%s　pos %s　rot %s　scale %s%s" % (
            "    " * depth, TYPE_NAME.get(n.get("type"), n.get("type")), dd.get("name") or "",
            dd.get("dicKey"), v3(dd.get("position")), v3(dd.get("rotation")), v3(dd.get("scale")),
            "" if dd.get("visible", True) else "　（隱藏）"))
    w("")

    w("== 用到的 mod（UniversalAutoResolver itemInfo，依 GUID）==")
    try:
        cnt = collections.Counter()
        for g in sc.kkex.guids():
            if "universalautoresolver" not in str(g).lower():
                continue
            for k, _kb, vb in sc.kkex.payload_items(g) or []:
                if k != "itemInfo":
                    continue
                for raw in unpack(vb) or []:
                    info = unpack(raw) if isinstance(raw, (bytes, bytearray)) else raw
                    if isinstance(info, dict):
                        cnt[(info.get("ModID"), info.get("Group"), info.get("Category"))] += 1
        for (mid, grp, cat), c in sorted(cnt.items(), key=lambda x: str(x[0])):
            w("  %-50s group=%s category=%s ×%d" % (mid, grp, cat, c))
    except Exception as e:                                  # noqa: BLE001
        w("（讀不到：%s）" % e)

    # ---- 地圖那一支：完整子樹、誰在動它（NodesConstraints / timeline）----
    try:
        import re
        keys = sorted(S.node_dickeys(sc.objects))
        rank = {k: i for i, k in enumerate(keys)}
        roots = [n for n, _d, _p in S.iter_nodes(sc.objects)
                 if n.get("type") == 3 and re.match(r"^\(?map\)?", str((n.get("data") or {}).get("name") or ""), re.I)]
        top = []
        for r in roots:                                     # 只留最外層的那幾個
            sub = {id(x) for x, _, _ in S.iter_nodes([r])}
            if not any(id(r) in {id(x) for x, _, _ in S.iter_nodes([o])} for o in roots if o is not r):
                top.append(r)
        mapnodes = []
        for r in top:
            w("== 地圖子樹：%s ==" % (r["data"].get("name")))
            for n, depth, _p in S.iter_nodes([r]):
                dd = n.get("data") or {}
                mapnodes.append(dd.get("dicKey"))
                w("%s[%s] %s　dicKey=%s RANK=%s　pos %s　rot %s　scale %s%s" % (
                    "  " * depth, TYPE_NAME.get(n.get("type"), n.get("type")), dd.get("name") or "",
                    dd.get("dicKey"), rank.get(dd.get("dicKey")), v3(dd.get("position")), v3(dd.get("rotation")),
                    v3(dd.get("scale")), "" if dd.get("visible", True) else "　（隱藏）"))
                if n.get("type") == 1:
                    bones = dd.get("bones") or {}
                    w("%s    道具 group=%s category=%s no=%s　FK=%s（%d 根骨頭有存值）　動畫 pattern=%s speed=%s time=%s　動態骨骼=%s" % (
                        "  " * depth, dd.get("group"), dd.get("category"), dd.get("no"), dd.get("enable_fk"),
                        len(bones), dd.get("anime_pattern"), dd.get("anime_speed"), dd.get("anime_normalized_time"),
                        dd.get("enable_dynamic_bone")))
                    if dd.get("enable_fk") and bones:
                        for bk in list(bones.keys())[:8]:
                            w("%s      FK %s：%s" % ("  " * depth, bk, str(bones[bk])[:160]))
            w("")
        ranks = {rank[k] for k in mapnodes if k in rank}

        w("== NodesConstraints 裡跟地圖有關的 ==")
        vb = sc.kkex.get("nodesConstraints", "constraints")
        xml = unpack(vb) if vb is not None else ""
        hit = 0
        for m in re.finditer(r"<constraint\b[^>]*?(?:/>|>.*?</constraint>)", xml or "", re.S):
            blk = m.group(0)
            pi = re.search(r'parentObjectIndex="(-?\d+)"', blk)
            ci = re.search(r'childObjectIndex="(-?\d+)"', blk)
            if (pi and int(pi.group(1)) in ranks) or (ci and int(ci.group(1)) in ranks):
                hit += 1
                w("  " + re.sub(r"\s+", " ", blk)[:600])
        w("  共 %d 條" % hit)
        w("")

        w("== timeline 裡動到地圖的軌道 ==")
        tx = sc.timeline_xml() or ""
        hit = 0
        for m in re.finditer(r"<interpolable\b[^>]*>", tx):
            h = m.group(0)
            oi = re.search(r'objectIndex="(-?\d+)"', h)
            if oi and int(oi.group(1)) in ranks:
                hit += 1
                w("  " + h[:400])
        w("  共 %d 條" % hit)
        w("")
    except Exception as e:                                  # noqa: BLE001
        import traceback
        w("（地圖分析失敗：%s）" % traceback.format_exc())

    # ---- 全場：timeline 軌道、NodesConstraints 的對象（用路徑表示）----
    try:
        import re
        keys = sorted(S.node_dickeys(sc.objects))
        bypath = {}
        path = {}
        for n, _d, parent in S.iter_nodes(sc.objects):
            nm = str((n.get("data") or {}).get("name") or "")
            path[id(n)] = (path.get(id(parent), "") if parent is not None else "") + "/" + (nm or ("#%s" % n["data"].get("dicKey")))
            bypath[n["data"].get("dicKey")] = path[id(n)]
        def rp(r):
            try:
                return bypath.get(keys[r], "?")
            except (IndexError, TypeError):
                return "?(RANK %s)" % r

        w("== timeline 軌道（依物件）==")
        tx = sc.timeline_xml() or ""
        cnt = collections.Counter()
        for m in re.finditer(r"<interpolable\b[^>]*>", tx):
            h = m.group(0)
            oi = re.search(r'objectIndex="(-?\d+)"', h)
            ow = re.search(r'owner="([^"]*)"', h)
            iid = re.search(r'\bid="([^"]*)"', h)
            tgt = rp(int(oi.group(1))) if oi else "(全域)"
            cnt[(tgt, ow.group(1) if ow else "?", iid.group(1) if iid else "?")] += 1
        for (tgt, ow, iid), c in sorted(cnt.items()):
            w("  %-70s %-22s %s%s" % (tgt[-70:], ow, iid, "" if c == 1 else " ×%d" % c))
        w("")

        w("== NodesConstraints（parent → child）==")
        vb = sc.kkex.get("nodesConstraints", "constraints")
        xml = unpack(vb) if vb is not None else ""
        for m in re.finditer(r"<constraint\b[^>]*?(?:/>|>.*?</constraint>)", xml or "", re.S):
            blk = m.group(0)
            g = lambda k: (re.search(r'\b%s="([^"]*)"' % k, blk) or [None, ""])[1]
            pi, ci = g("parentObjectIndex"), g("childObjectIndex")
            w("  %s [%s] → %s [%s]　pos=%s rot=%s scale=%s" % (
                rp(int(pi)) if pi.lstrip("-").isdigit() else pi, g("parentTransform"),
                rp(int(ci)) if ci.lstrip("-").isdigit() else ci, g("childTransform"),
                g("position"), g("rotation"), g("scale")))
        w("")
    except Exception:                                       # noqa: BLE001
        import traceback
        w("（timeline / NC 分析失敗：%s）" % traceback.format_exc())

    w("== 幾個小外掛的原始資料 ==")
    for g in ("RSkoi_ComponentUtil", "org.krypto5863.ObjectMap", "keelhauled.itemlayeredit",
              "com.deathweasel.bepinex.studioscenesettings"):
        try:
            for k, _kb, vb in sc.kkex.payload_items(g) or []:
                v = unpack(vb)
                if isinstance(v, (bytes, bytearray)):
                    try:
                        v = unpack(bytes(v))
                    except Exception:                       # noqa: BLE001
                        pass
                w("  %s / %s：%s" % (g, k, str(v)[:800]))
        except Exception as e:                              # noqa: BLE001
            w("  %s：讀不到（%s）" % (g, e))
    w("")

    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(L) + "\n")
    print("已寫出", out)
    return out


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
