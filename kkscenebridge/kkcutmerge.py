#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
kkcutmerge.py — 把好幾張卡各自的 .cutscene.json 接成一份（給合併後的那張卡用）

場景卡合併的時候，每張卡如果已經做好 F7（StudioCutScene）的設定檔，
這支把那幾份設定照同樣的順序接起來，合併完的卡不用重新量對應點。

--------------------------------------------------------------------------
怎麼接
--------------------------------------------------------------------------
F7 的設定檔原本是「一張卡、每個配音版本一個音檔、一支共用來源影片」。
兩張卡各有各的音檔和影片，這裡**不去動那些檔**，只接設定：

  1. **時間軸**：第 n 張卡的 tracks / cuts / pairs 的 timeline 秒數，
     整批加上那張卡在合併後的卡裡的起點（kkscenemerge 回報的 off）。
     音檔秒數、影片秒數都還是各張卡原本的。

  2. **音訊**：兩張以上有配音時，每條音軌自己帶「這一段每個配音版本用哪個檔」（files）
     和它的配音對照表（maps）。播放時 F7 播到哪一段就換哪個檔。
     只有一張卡有配音的話，直接沿用它原本的 variantFiles。

  3. **影片**：來源影片不只一支時，每段過場自己帶來源影片（source），
     照原本的 videoStart / videoEnd 播；過場期間要沿用哪條音軌的音檔寫在 track。

  音軌帶 files、過場帶 source 的設定檔需要 StudioCutScene 1.14.0 以上。

  另外，接在中間的「開場」「片尾」改成「過場」——
  插件看到 ending 播完會把整部停下來，那只該發生在最後一張。

這裡只有 json 的數學，不碰場景卡本身、不碰音檔影片，也不依賴 Qt 或 ffmpeg。
"""

import json
import os
import re

NAME_MAX = 18          # 插件面板的配音按鈕寬度有限
NEED_PLUGIN = "1.14.0"  # 音軌自己帶音檔（files）、過場自己帶來源片（source）要這一版的 F7


class CutMergeError(Exception):
    pass


# ---------------------------------------------------------------- 讀檔

def _escape_loose_backslash(txt):
    out, i, n = [], 0, len(txt)
    while i < n:
        c = txt[i]
        if c != "\\":
            out.append(c)
            i += 1
            continue
        nxt = txt[i + 1] if i + 1 < n else ""
        if nxt in '"\\/bfnrt' or (nxt == "u" and re.match(r"[0-9a-fA-F]{4}", txt[i + 2:i + 6] or "")):
            out.append(txt[i:i + 2])
            i += 2
        else:
            out.append("\\\\")
            i += 1
    return "".join(out)


def read_json(path):
    """跟「添加動畫音頻」分頁同一套寬鬆讀法：容忍 // 註解、多餘的逗號、沒跳脫的反斜線。"""
    with open(path, encoding="utf-8-sig") as f:
        txt = f.read()
    txt = re.sub(r"//[^\n]*", "", txt)
    txt = re.sub(r",(\s*[}\]])", r"\1", txt)
    try:
        return json.loads(txt, strict=False)
    except Exception:
        return json.loads(_escape_loose_backslash(txt), strict=False)


def find_json(card, dirs=()):
    """這張卡的設定檔：先找指定的資料夾（通常是 UserData\\cutscene），再找卡片旁邊。沒有回傳空字串。

    只認 <卡名>.cutscene.json —— 同名的 <卡名>.view.json 是 F7 存的視角檔，不是設定檔。
    """
    if not card:
        return ""
    stem = os.path.splitext(os.path.basename(card))[0]
    for d in list(dirs) + [os.path.dirname(os.path.abspath(card))]:
        if not d:
            continue
        p = os.path.normpath(os.path.join(d, stem + ".cutscene.json"))
        if os.path.isfile(p):
            return p
    return ""


# ---------------------------------------------------------------- 配音版本

def part_variants(cfg):
    """這份設定的配音版本。回傳 (vars, ref, active)：

    vars    [(名稱, 檔案)]，照設定檔裡的順序
    ref     主配音（anchors 的秒數是照它量的）
    active  設定檔裡預設選的那個
    """
    names = [str(x) for x in (cfg.get("variantNames") or [])]
    files = [str(x) for x in (cfg.get("variantFiles") or [])]
    out = [(n, f) for n, f in zip(names, files) if f]
    have = [n for n, _ in out]
    ref = str(cfg.get("refVariant") or "")
    if ref not in have:
        ref = have[0] if have else ""
    active = str(cfg.get("activeVariant") or "")
    if active not in have:
        active = ref
    return out, ref, active


def make_parts(cards, dirs=()):
    """cards＝[卡片路徑 或 {"card":…, "json":…}]，照播放順序。回傳 parts（還沒有 off）。"""
    parts = []
    for c in cards:
        if isinstance(c, dict):
            card, jp = c.get("card") or c.get("path") or "", c.get("json") or ""
        else:
            card, jp = c, ""
        jp = jp or find_json(card, dirs)
        cfg, err = None, ""
        if jp:
            try:
                cfg = read_json(jp)
                if not isinstance(cfg, dict) or not any(k in cfg for k in ("tracks", "cuts", "variantFiles")):
                    cfg, err = None, "不是 cutscene 設定檔"
            except Exception as e:                           # noqa: BLE001
                cfg, err = None, "讀不進來：%s" % e
        v, ref, active = part_variants(cfg) if cfg else ([], "", "")
        parts.append({"card": card, "json": jp, "cfg": cfg, "error": err,
                      "vars": v, "ref": ref, "active": active, "off": 0.0, "dur": None})
    return parts


def audio_parts(parts):
    return [i for i, p in enumerate(parts) if p["cfg"] and p["vars"]]


def _short(name):
    name = (name or "").replace("=", "-").strip()
    return name[:NAME_MAX] or "v"


def _unique(name, used):
    n, i = name, 2
    while n in used:
        suffix = "_%d" % i
        n = name[:NAME_MAX - len(suffix)] + suffix
        i += 1
    used.add(n)
    return n


def default_rows(parts):
    """預設的版本配對。回傳 [{"name": 合併後的配音名稱, "pick": {卡的index: 那張卡的配音名稱}}]。

    第一列：各卡目前選的那個版本；第一張卡選的名字如果每張卡都有（例如同一位配音員），就全部用它。
    其餘：同名的配在一起，沒有同名的照順序配，版本比較少的卡用它第一列的那個補。
    """
    aud = audio_parts(parts)
    if not aud:
        return []
    first = parts[aud[0]]
    common = None
    for nm in [first["active"], first["ref"]] + [n for n, _ in first["vars"]]:
        if nm and all(nm in [n for n, _ in parts[i]["vars"]] for i in aud):
            common = nm
            break
    row0 = {i: (common or parts[i]["active"] or parts[i]["ref"]) for i in aud}
    rows = [{"pick": row0}]
    rest = {i: [n for n, _ in parts[i]["vars"] if n != row0[i]] for i in aud}
    # 先照名稱配（至少兩張卡都有這個名字才算）
    seen = []
    for i in aud:
        for n in rest[i]:
            if n not in seen:
                seen.append(n)
    for n in seen:
        hit = [i for i in aud if n in rest[i]]
        if len(hit) >= 2:
            rows.append({"pick": {i: (n if i in hit else row0[i]) for i in aud}})
            for i in hit:
                rest[i].remove(n)
    # 剩下的照順序
    k = 0
    while any(len(v) > k for v in rest.values()):
        rows.append({"pick": {i: (rest[i][k] if len(rest[i]) > k else row0[i]) for i in aud}})
        k += 1
    used = set()
    for r in rows:
        names = []
        for i in aud:
            if r["pick"][i] not in names:
                names.append(r["pick"][i])
        r["name"] = _unique(_short(names[0] if len(names) == 1 else "+".join(names)), used)
    return rows


def normalize_rows(parts, rows):
    """把介面送回來的配對補齊：名稱不能重複、挑的版本要存在（不存在就用那張卡目前選的）。"""
    aud = audio_parts(parts)
    if not aud:
        return []
    rows = [dict(name=r.get("name") or "", pick=dict(r.get("pick") or {})) for r in (rows or [])]
    if not rows:
        return default_rows(parts)
    out, used = [], set()
    for j, r in enumerate(rows):
        pick = {}
        for i in aud:
            p = parts[i]
            names = [n for n, _ in p["vars"]]
            v = r["pick"].get(i, r["pick"].get(str(i)))
            if v in names:
                pick[i] = v
            elif j == 0:
                pick[i] = p["active"] if p["active"] in names else p["ref"]
            else:
                pick[i] = out[0]["pick"][i]
        nm = _short(r["name"]) if r["name"].strip() else _short("+".join(dict.fromkeys(pick[i] for i in aud)))
        out.append({"name": _unique(nm, used), "pick": pick})
    return out


# ---------------------------------------------------------------- 對照表

def _clean_map(pts):
    """跟插件同一套：依主配音秒數排序，主配音嚴格遞增、這一版不能倒退。"""
    p = sorted((float(a), float(b)) for a, b in (pts or []))
    out = []
    for a, b in p:
        if out and (a - out[-1][0] <= 1e-6 or b - out[-1][1] <= -1e-6):
            continue
        out.append((a, b))
    return out


def map_eval(pts, x):
    """主配音秒 → 這一版的秒數（跟插件的 VariantSec 一樣：兩端用最近那一段的斜率外推）。"""
    n = len(pts)
    if n == 0:
        return x
    if n == 1:
        return pts[0][1] + (x - pts[0][0])

    def sl(p, q):
        dt = q[0] - p[0]
        return 1.0 if abs(dt) < 1e-6 else (q[1] - p[1]) / dt
    if x <= pts[0][0]:
        return pts[0][1] + (x - pts[0][0]) * sl(pts[0], pts[1])
    for k in range(n - 1):
        if x <= pts[k + 1][0]:
            return pts[k][1] + (x - pts[k][0]) * sl(pts[k], pts[k + 1])
    return pts[-1][1] + (x - pts[-1][0]) * sl(pts[-2], pts[-1])


# ---------------------------------------------------------------- 接

def _fwd(p):
    return (p or "").replace("\\", "/")


def build(parts, rows=None, out_card="", log=print):
    """
    parts：make_parts() 的結果，每個要先填好 off（那張卡在合併後的 timeline 起點）和 dur（那張卡的時長，可以是 None）。
    回傳 (cfg, warnings)。沒有任何一張卡有設定檔時回傳 (None, [...])。
    """
    warn = []
    have = [i for i, p in enumerate(parts) if p["cfg"]]
    if not have:
        return None, ["沒有任何一張卡有 cutscene.json，沒有東西可以接"]
    for i, p in enumerate(parts):
        if p.get("error"):
            warn.append("第 %d 張的設定檔 %s：%s，這張當成沒有設定" % (i + 1, os.path.basename(p["json"]), p["error"]))
        elif not p["cfg"]:
            warn.append("第 %d 張（%s）沒有 cutscene.json，那一段不會有配音和過場" % (i + 1, os.path.basename(p["card"])))
    for i in have:
        if str(parts[i]["cfg"].get("pathMode") or "abs") == "rel":
            warn.append("第 %d 張的設定檔用的是相對路徑（pathMode=rel），接出來的設定檔路徑可能對不到；"
                        "請先把那份設定檔的音檔、影片改成完整路徑" % (i + 1))

    aud = audio_parts(parts)
    multi = len(aud) >= 2                 # 兩張以上有配音 → 每條音軌帶自己的音檔
    rows = normalize_rows(parts, rows) if multi else []
    files_of = {i: dict(parts[i]["vars"]) for i in aud}

    # ---- 以第一份設定當底（播放相關的設定照它的）----
    base = parts[have[0]]["cfg"]
    cfg = {k: v for k, v in base.items()
           if k not in ("tracks", "cuts", "pairs", "pairsFile", "sourceCard", "videoFile",
                        "variantNames", "variantFiles", "activeVariant", "refVariant",
                        "variantMaps", "mergedFrom", "mergedMedia", "requires")}
    cfg.setdefault("version", 1)
    cfg.setdefault("enabled", True)

    # ---- 配音 ----
    row_of = {}                      # (卡, 那張卡的配音名稱) → 合併後的名稱（"@名稱" 的音軌用）
    if multi:
        names = [r["name"] for r in rows]
        cfg["variantNames"] = names
        # 最上層的檔案只有「沒有自己帶音檔的音軌」才會用到；留第一張卡的，數量跟名稱對齊
        cfg["variantFiles"] = [_fwd(files_of[aud[0]][r["pick"][aud[0]]]) for r in rows]
        cfg["refVariant"] = names[0]
        act = ""
        a0 = parts[aud[0]]["active"]
        for r in rows:
            if r["pick"][aud[0]] == a0:
                act = r["name"]
                break
        cfg["activeVariant"] = act or names[0]
        cfg["variantMaps"] = {}          # 對照表跟著每條音軌走（maps）
        for r in rows:
            for i in aud:
                row_of.setdefault((i, r["pick"][i]), r["name"])
        for i in aud:
            for nm in sorted({r["pick"][i] for r in rows}):
                if not os.path.isfile(files_of[i][nm]):
                    warn.append("第 %d 張的配音「%s」不在了：%s" % (i + 1, nm, files_of[i][nm]))
    elif aud:
        c0 = parts[aud[0]]["cfg"]
        for k in ("variantNames", "variantFiles", "activeVariant", "refVariant", "variantMaps"):
            if k in c0:
                cfg[k] = c0[k]
        row_of = {(aud[0], n): n for n, _ in parts[aud[0]]["vars"]}
    else:
        cfg.update(variantNames=[], variantFiles=[], activeVariant="", refVariant="", variantMaps={})

    # ---- 影片：來源片只有一支就放最上層；不只一支 → 每段過場自己帶 source ----
    def shared_cuts(c):
        return [x for x in (c.get("cuts") or [])
                if not x.get("video") and float(x.get("videoStart", -1)) >= 0]
    sources = []
    for i in have:
        v = parts[i]["cfg"].get("videoFile") or ""
        if v and shared_cuts(parts[i]["cfg"]):
            if os.path.normcase(os.path.abspath(v)) not in [os.path.normcase(os.path.abspath(x)) for x in sources]:
                sources.append(v)
    own_source = len(sources) >= 2
    cfg["videoFile"] = _fwd(sources[0]) if len(sources) == 1 else ""

    tracks, cuts, pairs = [], [], []
    for i in have:
        p = parts[i]
        c = p["cfg"]
        off = float(p.get("off") or 0.0)
        end = off + float(p["dur"]) if p.get("dur") else None

        for tr in (c.get("tracks") or []):
            t = dict(tr)
            a, b = float(tr.get("from", 0.0)) + off, float(tr.get("to", 0.0)) + off
            if end is not None:
                if a >= end - 1e-6:
                    warn.append("第 %d 張的時長被縮短了，%.2f 秒之後的音軌拿掉" % (i + 1, a - off))
                    continue
                b = min(b, end)
            au = str(tr.get("audio", "@"))
            if au.startswith("@") and len(au) > 1:
                au = "@" + row_of.get((i, au[1:]), cfg["variantNames"][0] if cfg.get("variantNames") else au[1:])
            t["from"], t["to"], t["audio"] = round(a, 3), round(b, 3), au
            # 只平移 timeline；音檔秒數是這張卡自己那個音檔的，不動
            t["anchors"] = [[round(float(x) + off, 3), round(float(y), 3)]
                            for x, y in (tr.get("anchors") or [])]
            if multi and i in aud and au.startswith("@"):
                # 這一段自己的音檔：合併後的每個配音版本 → 這張卡挑的那個檔
                t["files"] = {r["name"]: _fwd(files_of[i][r["pick"][i]]) for r in rows}
                mp = {}
                for r in rows:
                    nm = r["pick"][i]
                    m = (c.get("variantMaps") or {}).get(nm) if nm != p["ref"] else None
                    if m:
                        mp[r["name"]] = [[round(x, 3), round(y, 3)] for x, y in _clean_map(m)]
                t.pop("maps", None)
                if mp:
                    t["maps"] = mp
                t["_part"] = i
            tracks.append(t)

        for cu in (c.get("cuts") or []):
            x = dict(cu)
            t = float(cu.get("t", 0.0)) + off
            if end is not None and t > end + 0.05:
                continue
            x["t"] = round(t, 3)
            kind = str(cu.get("kind") or "transition")
            # 開場只有整部的第一張才是開場；片尾只有最後一張才是片尾（插件播完 ending 會整部停下來）
            # （後面還有沒設定檔的卡時，最後一份設定的片尾也不能算片尾 —— 不然播到那裡就整部停了）
            if (kind == "opening" and (i != 0 or off > 0.05)) or (kind == "ending" and i != len(parts) - 1):
                kind = "transition"
            x["kind"] = kind
            track_audio = bool(cu.get("useTrackAudio", True)) and not cu.get("audio")
            if aud and i not in aud and track_audio:
                # 這張卡自己沒有配音：過場不能去沿用別張卡的音軌，改放影片自己的聲音
                x["useTrackAudio"] = False
                track_audio = False
            if multi and track_audio and i in aud:
                x["_part"] = i
            if own_source and not cu.get("video") and float(cu.get("videoStart", -1)) >= 0:
                src = c.get("videoFile") or ""
                x["source"] = _fwd(src)
                if not (src and os.path.isfile(src)):
                    m = "第 %d 張的來源影片不在了（%s），那張卡的過場播不出來" % (i + 1, src or "沒寫")
                    if m not in warn:
                        warn.append(m)
            cuts.append(x)

        for row in (c.get("pairs") or []):
            try:
                pairs.append([round(float(row[0]) + off, 3), round(float(row[1]), 3),
                              (str(row[2]) if len(row) > 2 else "")])
            except Exception:                                # noqa: BLE001
                continue

    tracks.sort(key=lambda t: t["from"])
    cuts.sort(key=lambda x: x["t"])
    pairs.sort()
    # 過場期間要沿用哪一條音軌的音檔（它那張卡的、時間上最靠近的那一條）
    for x in cuts:
        i = x.pop("_part", None)
        if i is None:
            continue
        mine = [k for k, t in enumerate(tracks) if t.get("_part") == i]
        if not mine:
            continue
        before = [k for k in mine if tracks[k]["from"] <= x["t"] + 0.05]
        x["track"] = before[-1] if before else mine[0]
    for t in tracks:
        t.pop("_part", None)

    cfg["tracks"], cfg["cuts"], cfg["pairs"] = tracks, cuts, pairs
    cfg["sourceCard"] = os.path.abspath(out_card) if out_card else ""
    cfg["pairsFile"] = ""
    cfg["mergedFrom"] = [{"card": _fwd(p["card"]), "json": _fwd(p["json"]),
                          "offset": round(float(p.get("off") or 0.0), 3)}
                         for p in parts]
    if any("files" in t for t in tracks) or any(x.get("source") for x in cuts):
        cfg["requires"] = "StudioCutScene %s" % NEED_PLUGIN
    if multi:
        log("  [F7] 配音 %d 個版本，各卡用各自的音檔（播放時照時間軸換檔，需要 F7 %s 以上）："
            % (len(rows), NEED_PLUGIN)
            + "、".join("%s＝%s" % (r["name"], "＋".join(r["pick"][i] for i in aud)) for r in rows))
    if own_source:
        log("  [F7] 過場的來源影片 %d 支，各段照原本的區間播" % len(sources))
    return cfg, warn


def write_pairs(path, pairs):
    with open(path, "w", encoding="utf-8") as f:
        f.write("# timeline        影片／主配音      （由 kkcutmerge 從各張卡的設定接起來）\n")
        for t, v, n in pairs:
            f.write("%-16.3f  %-16.3f%s\n" % (t, v, ("  # " + n) if n else ""))


def merge(parts, out_card, out_json, rows=None, log=print):
    """整套做完：算出設定、寫 json（只有一張卡有配音時順便寫卡片旁邊的 pairs.txt）。回傳 dict(json, warnings, cfg)。"""
    cfg, warn = build(parts, rows, out_card, log)
    if cfg is None:
        return {"json": "", "warnings": warn}
    os.makedirs(os.path.dirname(out_json) or ".", exist_ok=True)
    # 每條音軌各自的音檔時，pairs 的音訊秒數是「各張卡自己的音檔」的，混在一份 pairs.txt 裡沒辦法拿去別的地方用
    per_track = any("files" in t for t in cfg["tracks"])
    if cfg["pairs"] and out_card and not per_track:
        pf = os.path.splitext(out_card)[0] + ".pairs.txt"
        try:
            write_pairs(pf, cfg["pairs"])
            cfg["pairsFile"] = os.path.abspath(pf)
        except Exception as e:                               # noqa: BLE001
            warn.append("pairs.txt 寫不出來：%s" % e)
    with open(out_json, "w", encoding="utf-8") as f:
        f.write(json.dumps(cfg, ensure_ascii=False, indent=2) + "\n")
    log("  [F7] 已寫出 %s（%d 段音軌／%d 段過場／%d 個配音版本）"
        % (out_json, len(cfg["tracks"]), len(cfg["cuts"]), len(cfg.get("variantNames") or [])))
    return {"json": out_json, "warnings": warn, "cfg": cfg}


# ---------------------------------------------------------------- CLI

def main(argv=None):
    import argparse
    ap = argparse.ArgumentParser(
        prog="kkcutmerge",
        description="把幾份 cutscene.json 接成一份（音檔、影片不動）。場景卡本身用 kkscenemerge 接；"
                    "這裡要給每張卡在合併後的 timeline 起點。")
    ap.add_argument("--part", action="append", required=True, metavar="卡片.png=起點秒[=設定檔.json]",
                    help="照播放順序給。設定檔不寫就找卡片同名的 .cutscene.json")
    ap.add_argument("--card", required=True, help="合併後的卡片路徑（只拿來命名和寫進 sourceCard）")
    ap.add_argument("-o", "--out", help="輸出的 cutscene.json（預設：卡片同名）")
    ap.add_argument("--dir", action="append", default=[], help="找設定檔的資料夾（可以給好幾個）")
    a = ap.parse_args(argv)
    cards, offs = [], []
    for tok in a.part:
        bits = tok.split("=")
        cards.append({"card": bits[0], "json": bits[2] if len(bits) > 2 else ""})
        offs.append(float(bits[1]) if len(bits) > 1 and bits[1] else 0.0)
    parts = make_parts(cards, a.dir)
    for p, o in zip(parts, offs):
        p["off"] = o
    out = a.out or os.path.splitext(a.card)[0] + ".cutscene.json"
    res = merge(parts, a.card, out)
    for w in res["warnings"]:
        print("[注意] " + w)
    return 0 if res["json"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
