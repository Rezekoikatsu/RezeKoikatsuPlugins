#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""kkfolderscale — 把場景卡裡「資料夾的縮放」重設為 1, 1, 1（可批次）

為什麼需要
----------
原版 Studio 的資料夾不能縮放：存檔裡雖然有縮放值，載入時根本不套用。
Better Scaling 預設開著「Scale folders」，讓資料夾也能縮放 —— 於是舊場景裡那些
本來沒作用的縮放值突然生效，裡面的東西就變大變小、跑位。載入時它會提示：

    [BetterScaling] Scaled folders found in old scene! If something is displaced,
    check for folders with scales other than [1, 1, 1] ...

這種場景裡資料夾的縮放值本來就沒作用，改回 1 跟作者當初看到的完全一樣，
而且之後不管 Scale folders 開或關都會長一樣。

怎麼判斷能不能改（照 Better Scaling 1.2.2.11 自己的判斷方式，反組譯 OnSceneLoad 確認）
------------------------------------------------------------------------------------
Better Scaling 存檔時會在場景擴充資料 starstorm.betterscaling 裡寫一個旗標
BetterScalingData_scaledFoldersFlag（存檔當下 Scale folders 開或關）：

    · 沒有這個旗標（舊場景、沒裝 Better Scaling 時存的）→ 資料夾縮放從沒生效過，改成 1 安全
    · 旗標是「關」→ 存檔時資料夾縮放也沒生效，改成 1 安全
    · 旗標是「開」→ 作者是在資料夾可縮放的環境下做的，縮放可能是故意的，**預設不改**

只改資料夾（type 3）的 scale，位置、旋轉、其他物件一概不動；
不新增也不刪節點，所以 timeline、NodesConstraints 的參照（RANK）完全不受影響。
"""
from __future__ import annotations

import os
import shutil

import kkscene2 as S
from kkmsgpack import unpack

FOLDER_TYPE = 3
BS_GUID = "starstorm.betterscaling"
BS_FLAG = "BetterScalingData_scaledFoldersFlag"
EPS = 1e-6


def bs_flag(scene):
    """Better Scaling 的旗標：None＝沒有（舊場景）；True／False＝存檔當下 Scale folders 開／關。"""
    try:
        guid = BS_GUID if scene.kkex.has(BS_GUID) else next(
            (g for g in scene.kkex.guids() if isinstance(g, str) and "betterscaling" in g.lower()), None)
        if guid is None:
            return None
        for k, _kb, vb in scene.kkex.payload_items(guid) or []:
            if k != BS_FLAG:
                continue
            v = unpack(vb)
            if isinstance(v, (bytes, bytearray)):      # 有些外掛把值再包一層 bin
                v = unpack(bytes(v))
            return bool(v)
    except Exception:                                   # noqa: BLE001
        pass
    return None


def _scale(node):
    s = (node.get("data") or {}).get("scale")
    if not isinstance(s, dict):
        return None
    try:
        return tuple(float(s.get(a, 1.0)) for a in "xyz")
    except (TypeError, ValueError):
        return None


def scaled_folders(scene):
    """[(路徑, (x, y, z), node)]：縮放不是 1, 1, 1 的資料夾。"""
    out = []
    path = {}
    for n, _depth, parent in S.iter_nodes(scene.objects):
        nm = str((n.get("data") or {}).get("name") or "")
        base = path.get(id(parent), "") if parent is not None else ""
        path[id(n)] = base + "/" + nm
        if n.get("type") != FOLDER_TYPE:
            continue
        sc = _scale(n)
        if sc is not None and any(abs(v - 1.0) > EPS for v in sc):
            out.append((path[id(n)], sc, n))
    return out


def reset(found):
    for _p, _s, n in found:
        n["data"]["scale"] = {"x": 1.0, "y": 1.0, "z": 1.0}
    return len(found)


def fmt_scale(s):
    return "[%s]" % ", ".join(("%g" % round(v, 4)) for v in s)


def process(path, apply=False, force=False, backup=True, out_path=None):
    """處理一張卡。回傳 dict：
        status: "clean" 沒有要改的 / "fixed" 改好了 / "would" 只檢查、需要改 /
                "skip_flag" Better Scaling 標記為故意縮放、沒改 / "error"
        flag, folders [(路徑, 縮放)], message
    """
    r = {"path": path, "status": "error", "flag": None, "folders": [], "message": ""}
    try:
        sc = S.Scene(path)
    except Exception as e:                              # noqa: BLE001
        r["message"] = "讀不了（不是場景卡？）：%s" % e
        return r
    r["flag"] = bs_flag(sc)
    found = scaled_folders(sc)
    r["folders"] = [(p, s) for p, s, _ in found]
    if not found:
        r["status"] = "clean"
        return r
    if r["flag"] is True and not force:
        r["status"] = "skip_flag"
        return r
    if not apply:
        r["status"] = "would"
        return r
    reset(found)
    dst = out_path or path
    if backup and os.path.isfile(dst):
        bak = dst + ".bak"
        if not os.path.isfile(bak):
            shutil.copy2(dst, bak)
    sc.save(dst)
    r["status"] = "fixed"
    return r


def collect(paths, recursive=True):
    """檔案和資料夾混在一起丟進來：資料夾展開成底下所有 .png。重複的去掉，保持順序。"""
    out, seen = [], set()
    for p in paths:
        if os.path.isdir(p):
            walker = os.walk(p) if recursive else [(p, [], os.listdir(p))]
            for root, _dirs, files in walker:
                for f in sorted(files):
                    if f.lower().endswith(".png"):
                        fp = os.path.join(root, f)
                        if fp not in seen:
                            seen.add(fp)
                            out.append(fp)
        elif os.path.isfile(p) and p.lower().endswith(".png") and p not in seen:
            seen.add(p)
            out.append(p)
    return out


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser(description="把場景卡裡資料夾的縮放重設為 1, 1, 1")
    ap.add_argument("paths", nargs="+", help="場景卡或資料夾")
    ap.add_argument("--apply", action="store_true", help="真的改（預設只檢查）")
    ap.add_argument("--force", action="store_true", help="Better Scaling 標記為故意縮放的也改")
    ap.add_argument("--no-backup", action="store_true")
    a = ap.parse_args()
    for p in collect(a.paths):
        r = process(p, apply=a.apply, force=a.force, backup=not a.no_backup)
        print(r["status"], p, r["message"])
        for fp, s in r["folders"]:
            print("    ", fmt_scale(s), fp)
