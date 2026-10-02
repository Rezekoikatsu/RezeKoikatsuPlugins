#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""kkvariantmap —— 各個配音版本之間的時間對照表。

為什麼需要這支
==============
cutscene.json 裡的 anchors 是「timeline 秒數 → 音檔秒數」。
作者放出的各配音版本如果是同一套剪輯重新上音，每一版的秒數都一樣，
一份 anchors 通吃 —— 這是常態，Charcard A / Charcard B / Charcard C 都是這樣
（各版 wav 連檔案大小都一模一樣）。

但偶爾有例外。Charcard 就是：

    [JPN]Scenecard @VoiceA.wav    174.8 秒 ← 跟來源影片一樣長
    [JPN]Scenecard @VoiceB.wav   150.2 秒
    [ENG]Scenecard @VoiceC.wav    150.2 秒

而且量到的差（timeline 46.650 → 72.550 / 60.775，差 11.775 秒）
跟總長差（24.6 秒）對不起來 —— 代表不是單純少了開頭，
是兩邊各自剪掉不同的地方，差距會一路變。一個固定偏移救不了。

所以再疊一層折線：**主配音的秒數 → 這一版的秒數**。

    timeline ──(track.anchors)──> 主配音秒數 ──(這裡的對照表)──> 該版秒數

好處是不必為每個配音重量一整套點：慢動作那種形狀已經在 anchors 裡、各版共用，
每一版只要 2 個點就能定出平移＋尺度，1 個點是純平移，0 個點就是同步。

點從哪裡來
==========
自動  kkaudioalign.probe_map 把基準檔切成幾十個視窗，一個一個去另一版裡找。
      兩版底下的音效／音樂是同一條時才有效（同一支片重新上人聲通常是）。
手動  在 Studio 裡找一個好認的瞬間，兩版各記一次秒數。

兩者可以混：自動掃出來的當底，某一段不信任就自己補一個點蓋過去。

側檔
====
存在卡片旁邊的 <卡片>.variants.json，跟 pairs.txt 同一個地位：

    {
      "ref": "VoiceA",
      "maps": {
        "VoiceB": [[72.550, 60.775], ...],
        "VoiceC":  [[72.550, 60.775], ...]
      }
    }

產生 cutscene.json 時會被讀進去，寫成 refVariant / variantMaps。

用法
    看現況（含每一段的覆蓋檢查與建議點位）
        python kkvariantmap.py <卡片.png>

    自動掃一個版本
        python kkvariantmap.py <卡片.png> --ref-file <主配音.wav> --scan "VoiceB=<另一版.wav>"

    手動加一個點（主配音秒數, 該版秒數）
        python kkvariantmap.py <卡片.png> --point "VoiceB=01:12.550,01:00.775"
"""
from __future__ import annotations

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

_BIDI = "‎‏‪‫‬‭‮⁦⁧⁨⁩﻿"


def clean_path(p):
    p = (p or "").strip()
    p = "".join(c for c in p if c not in _BIDI).strip()
    if len(p) >= 2 and p[0] == p[-1] and p[0] in "\"'":
        p = p[1:-1]
    return p.strip()


# ---------------------------------------------------------------- 側檔

def side_path(card):
    """<卡片>.variants.json —— 跟 <卡片>.pairs.txt 放在一起。

    為什麼是放卡片旁邊而不是 UserData\\cutscene：
    這是**量出來的原始資料**，跟 pairs.txt 同一個地位 —— 換一張卡就是另一套。
    UserData\\cutscene 放的是產物（cutscene.json），插件只讀那裡，
    而對照表早就被寫進那份 json 的 variantMaps 了，插件不需要側檔。
    側檔的用途只有一個：下次要改的時候還讀得回來。
    """
    base, _ = os.path.splitext(clean_path(card))
    return base + ".variants.json"


def find_side(card, extra_dirs=()):
    """找側檔實際在哪。卡片旁邊優先，找不到就到 extra_dirs（通常是輸出資料夾）找。

    會多找一個地方是因為「側檔應該放哪」不是一眼就對的事 ——
    有人會順手放到 UserData\\cutscene 去（產物都在那裡）。
    找不到就當成沒有，回主位置讓上層拿去寫。
    """
    p = side_path(card)
    if os.path.isfile(p):
        return p
    stem = os.path.basename(p)
    for d in (extra_dirs or ()):
        if not d:
            continue
        q = os.path.join(d, stem)
        if os.path.isfile(q):
            return q
    return p


def json_path(card, extra_dirs=()):
    """這張卡已經產生出來的 <卡片>.cutscene.json 在哪。找不到回空字串。"""
    base, _ = os.path.splitext(clean_path(card))
    stem = os.path.basename(base) + ".cutscene.json"
    cands = [base + ".cutscene.json"]
    cands += [os.path.join(d, stem) for d in (extra_dirs or ()) if d]
    for p in cands:
        if os.path.isfile(p):
            return p
    return ""


def load_from_json(path):
    """從已經產生的 cutscene.json 把對照表讀回來。

    這才是對照表真正的家 —— 產生流程一定會把 refVariant / variantMaps
    寫進去（插件就是讀那裡），所以再另外存一份側檔只是多一個會不同步的東西。
    """
    try:
        with open(path, encoding="utf-8-sig") as f:
            d = json.load(f)
    except Exception:                                       # noqa: BLE001
        return "", {}
    if not isinstance(d, dict):
        return "", {}
    ref = str(d.get("refVariant") or "")
    maps = {}
    for k, v in (d.get("variantMaps") or {}).items():
        pts = clean_points(v)
        if pts:
            maps[str(k)] = pts
    return ref, maps


def load_maps(card, extra_dirs=()):
    """取得 (主配音, 對照表, 來源路徑)。

    順序：先看有沒有側檔（舊流程留下來的、或手動放的），沒有就回頭讀
    已經產生的 cutscene.json。兩個都沒有就回空的。
    """
    p = find_side(card, extra_dirs)
    if os.path.isfile(p):
        ref, maps = load_side(card, extra_dirs)
        return ref, maps, p
    j = json_path(card, extra_dirs)
    if j:
        ref, maps = load_from_json(j)
        return ref, maps, (j if maps else "")
    return "", {}, ""


def save_maps(card, ref, maps, extra_dirs=()):
    """把對照表存回去。回傳 (路徑, 種類)，種類是 "json" 或 "side"。

    有 cutscene.json 就直接改那一份（只動 refVariant / variantMaps，其餘原封不動）——
    不另外生一個 variants.json。還沒產生過 json 的話才退回側檔，
    否則量了半天的東西沒地方放。
    """
    j = json_path(card, extra_dirs)
    if j:
        with open(j, encoding="utf-8-sig") as f:
            d = json.load(f)
        d["refVariant"] = ref or ""
        d["variantMaps"] = {k: clean_points(v) for k, v in (maps or {}).items()
                            if k != ref and clean_points(v)}
        tmp = j + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(d, f, ensure_ascii=False, indent=2)
            f.write("\n")
        os.replace(tmp, j)
        # 側檔如果還在，那是舊流程留下來的，留著只會跟 json 打架
        s = find_side(card, extra_dirs)
        if os.path.isfile(s):
            try:
                os.remove(s)
            except OSError:
                pass
        return j, "json"
    return save_side(card, ref, maps, extra_dirs), "side"


def load_side(card, extra_dirs=()):
    """回傳 (主配音名稱, {配音名稱: [[主配音秒, 該版秒], …]})。沒有側檔就回空的。"""
    p = find_side(card, extra_dirs)
    if not os.path.isfile(p):
        return "", {}
    try:
        with open(p, encoding="utf-8-sig") as f:
            d = json.load(f)
    except Exception:                                       # noqa: BLE001
        return "", {}
    if not isinstance(d, dict):
        return "", {}
    ref = str(d.get("ref") or "")
    maps = {}
    for k, v in (d.get("maps") or {}).items():
        pts = clean_points(v)
        if pts:
            maps[str(k)] = pts
    return ref, maps


def save_side(card, ref, maps, extra_dirs=()):
    # 已經有側檔的話就寫回原地，不要在另一個資料夾又生一份 ——
    # 兩份不同步的對照表比沒有還糟。
    p = find_side(card, extra_dirs)
    out = {"ref": ref or "",
           "maps": {k: clean_points(v) for k, v in (maps or {}).items()
                    if clean_points(v)}}
    with open(p, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=2)
        f.write("\n")
    return p


# ---------------------------------------------------------------- 對照表本身

def clean_points(pts):
    """排序、去掉重複的 x、丟掉倒退的點。

    兩邊都必須嚴格遞增 —— 時間只會往前走。倒退的點一定是量錯或抄錯，
    留著的話插件在那一帶會來回硬 seek，聽起來就是「那一小段一直重播」。
    插件端也擋了一次，這裡先擋是為了讓報告看得到「丟了幾個」。
    """
    out = []
    try:
        raw = sorted((float(a), float(b)) for a, b in (pts or []))
    except (TypeError, ValueError):
        return []
    for a, b in raw:
        if out and abs(a - out[-1][0]) < 1e-6:
            out[-1] = [round(a, 3), round(b, 3)]
            continue
        if out and b < out[-1][1] - 1e-6:
            continue
        out.append([round(a, 3), round(b, 3)])
    return out


def map_at(pts, x):
    """主配音的第 x 秒 → 這一版的第幾秒。跟插件的 VariantSec 同一套規矩。

    兩端之外用最靠近的那一段斜率外推，不夾住 —— 對照點通常只量在中間，
    頭尾一定落在區間外，夾住的話開頭結尾就歪了。
    """
    n = len(pts or [])
    if n == 0:
        return float(x)
    if n == 1:
        return pts[0][1] + (x - pts[0][0])
    if x <= pts[0][0]:
        return pts[0][1] + (x - pts[0][0]) * _slope(pts[0], pts[1])
    for i in range(n - 1):
        if x <= pts[i + 1][0]:
            return pts[i][1] + (x - pts[i][0]) * _slope(pts[i], pts[i + 1])
    return pts[-1][1] + (x - pts[-1][0]) * _slope(pts[-2], pts[-1])


def _slope(p, q):
    dx = q[0] - p[0]
    return 1.0 if abs(dx) < 1e-6 else (q[1] - p[1]) / dx


def leave_one_out(pts):
    """拿掉一個點、用其餘的點預測它。回傳 [(主配音秒, 實際, 誤差 或 None)]。

    三個點以下這個數字沒有判斷力（拿掉一個就只剩平移），照樣列出來但標明。
    """
    out = []
    n = len(pts)
    for i in range(n):
        rest = pts[:i] + pts[i + 1:]
        d = None if len(rest) < 1 else map_at(rest, pts[i][0]) - pts[i][1]
        out.append((pts[i][0], pts[i][1], d))
    return out


# ---------------------------------------------------------------- 自動掃

def scan(ref_file, other_file, probes=24, win=20.0, log=print):
    """互相關掃出整條對照曲線。回傳 (點, 失敗原因)。

    掃不出來不是錯誤 —— 兩版如果連底下的音效音樂都各做各的，
    波形就真的無關，那只能手動量。所以回傳原因讓上層自己決定怎麼講。
    """
    import kkaudioalign as A
    ref_file, other_file = clean_path(ref_file), clean_path(other_file)
    if not os.path.isfile(ref_file):
        return [], "找不到主配音檔案：%s" % ref_file
    if not os.path.isfile(other_file):
        return [], "找不到這個版本的檔案：%s" % other_file
    if os.path.normcase(os.path.abspath(ref_file)) \
            == os.path.normcase(os.path.abspath(other_file)):
        return [], "跟主配音是同一個檔案，不需要對照表"
    try:
        _, groups = A.probe_map(ref_file, other_file, n_probes=probes,
                                win=win, log=log)
    except Exception as e:                                  # noqa: BLE001
        return [], "掃描失敗：%s" % e
    if not groups:
        return [], ("兩版之間沒有足夠的共同成分（底下的音效／音樂不是同一條），"
                    "只能手動量幾個點")
    return clean_points(A.map_anchors(groups, A.duration(ref_file))), ""


def file_duration(path):
    try:
        import kkaudioalign as A
        return A.duration(clean_path(path))
    except Exception:                                       # noqa: BLE001
        return None


# ---------------------------------------------------------------- 檢查與建議

def check(report, name, pts, dur=None, ref_dur=None):
    """一個配音版本的覆蓋檢查。

    report 是 kkcutscene.build_config 回傳的那一份（每一段的 v0 / v1 是
    **主配音**的秒數），所以可以直接拿去問「這一段的對照點夠不夠」。

    回傳 (problems, suggestions)：
      problems    要講給人聽的話
      suggestions [(段index, 主配音秒, 預測的該版秒, 原因)] —— 建議去量的位置
    """
    problems, sug = [], []
    segs_ok = [r for r in report if r.get("v0") is not None]
    if not segs_ok:
        return problems, sug

    if not pts:
        problems.append("配音「%s」沒有任何對照點 —— 會被當成跟主配音同秒數。"
                        "兩版剪輯一樣的話這是對的；不一樣的話整段都會偏。" % name)
        if dur and ref_dur and abs(dur - ref_dur) > 0.5:
            problems.append("而且它的長度是 %.2f 秒、主配音是 %.2f 秒（差 %+.2f）——"
                            "剪輯明顯不同，這一版一定要量對照點"
                            % (dur, ref_dur, dur - ref_dur))
            for r in segs_ok:
                mid = (r["v0"] + r["v1"]) / 2.0
                sug.append((r["seg"]["index"], mid, map_at(pts, mid),
                            "這一段完全沒有對照點"))
        return problems, sug

    lo, hi = pts[0][0], pts[-1][0]

    if len(pts) == 1:
        problems.append("配音「%s」只有 1 個對照點 —— 只能平移、修不了尺度。"
                        "兩版如果是不同的剪輯，離那個點越遠偏得越多。"
                        "在場景的另一端再量一個點。" % name)

    for r in segs_ok:
        v0, v1 = r["v0"], r["v1"]
        inside = [p for p in pts if v0 - 1e-6 <= p[0] <= v1 + 1e-6]
        idx = r["seg"]["index"]
        if not inside:
            where = ("在所有對照點之前" if v1 < lo else
                     "在所有對照點之後" if v0 > hi else "夾在兩個對照點之間")
            reason = ("第 %d 段%s，整段是%s出來的"
                      % (idx, where, "外推" if (v1 < lo or v0 > hi) else "內插"))
            if v1 < lo or v0 > hi:
                problems.append(reason + " —— 外推的誤差沒有上限，補一個點在這一段裡面")
                sug.append((idx, (v0 + v1) / 2.0, map_at(pts, (v0 + v1) / 2.0),
                            "外推區，誤差沒有上限"))
            elif len(pts) < 2:
                sug.append((idx, (v0 + v1) / 2.0, map_at(pts, (v0 + v1) / 2.0),
                            "整段沒有對照點"))
        elif len(inside) == 1 and len(pts) < 2:
            sug.append((idx, v1 - (v1 - v0) * 0.15, map_at(pts, v1 - (v1 - v0) * 0.15),
                        "這一段只有 1 個點，補一個在另一端"))

    # 倍率：兩版都是同一部片，整體速度應該差不多。差太多多半是某個點量錯。
    for a, b in zip(pts, pts[1:]):
        dx = b[0] - a[0]
        if dx <= 0.5:
            continue
        rate = (b[1] - a[1]) / dx
        if not (0.85 <= rate <= 1.15):
            problems.append("配音「%s」在主配音 %.2f ~ %.2f 秒之間的倍率是 %.4f —— "
                            "同一部片的兩個版本不該差這麼多，"
                            "這兩個點之中有一個量錯了（最常見的是對到重複的台詞）"
                            % (name, a[0], b[0], rate))

    if len(pts) >= 3:
        worst = max((abs(d) for _t, _v, d in leave_one_out(pts) if d is not None),
                    default=0.0)
        if worst > 0.30:
            problems.append("配音「%s」的留一驗證最大誤差 %.2f 秒 —— "
                            "在誤差最大的那個點附近再補一個" % (name, worst))

    if dur:
        end = map_at(pts, max(r["v1"] for r in segs_ok))
        if end > dur + 0.5:
            problems.append("配音「%s」照對照表算到 %.2f 秒，但檔案只有 %.2f 秒 —— "
                            "最後那一段會播不出聲音，對照點有問題"
                            % (name, end, dur))
        v_first = min(r["v0"] for r in segs_ok)
        start = map_at(pts, v_first)
        if start < -0.5:
            tail = ("最前面那個對照點在主配音 %.2f 秒，整個開頭是外推出來的 —— "
                    "在第一段裡面補一個點" % lo) if v_first < lo else \
                   "開頭那個對照點量錯了"
            problems.append("配音「%s」照對照表算出負的起點（%.2f 秒）—— %s"
                            % (name, start, tail))
    return problems, sug


def print_report(report, ref, maps, durs=None, out=print):
    """把所有配音版本的狀況印成一段報告。回傳 problems 清單。"""
    durs = durs or {}
    allp = []
    if not maps:
        return allp
    out("")
    out("配音對照（主配音：%s）" % (ref or "（沒指定，用第一個）"))
    for name in sorted(maps):
        pts = maps[name]
        out("")
        out("  == %s ==  %d 個對照點%s"
            % (name, len(pts),
               ("，檔案長 %.2f 秒" % durs[name]) if durs.get(name) else ""))
        for a, b in pts[:12]:
            out("     主配音 %9.3f  →  這一版 %9.3f   （差 %+7.3f）" % (a, b, b - a))
        if len(pts) > 12:
            out("     …還有 %d 個" % (len(pts) - 12))
        p, sug = check(report, name, pts, durs.get(name), durs.get(ref))
        allp += p
        if sug:
            out("     建議再量的位置:")
            for idx, x, y, why in sug:
                out("       段 %d   主配音 %.3f  →  預估這一版 %.3f    （%s）"
                    % (idx, x, y, why))
    return allp


# ---------------------------------------------------------------- CLI

def _parse_kv(tok):
    name, _, rest = str(tok).partition("=")
    return name.strip(), rest.strip()


def main(argv=None):
    import kkcutscene as K

    ap = argparse.ArgumentParser(prog="kkvariantmap")
    ap.add_argument("card", help="場景卡（側檔就放在它旁邊）")
    ap.add_argument("--ref", default=None, help="主配音的名稱")
    ap.add_argument("--ref-file", default=None, help="主配音的音檔（--scan 要用）")
    ap.add_argument("--scan", action="append", default=[],
                    help="自動掃一個版本，格式 名稱=檔案路徑；可以重複")
    ap.add_argument("--point", action="append", default=[],
                    help="手動加一個點，格式 名稱=主配音秒,該版秒；可以重複")
    ap.add_argument("--clear", action="append", default=[],
                    help="清掉某個版本的所有對照點")
    ap.add_argument("--probes", type=int, default=24)
    ap.add_argument("--win", type=float, default=20.0)
    a = ap.parse_args(argv)
    a.card = clean_path(a.card)

    ref, maps, src = load_maps(a.card)
    if src:
        print("目前的對照表來自 %s" % src)
    if a.ref:
        ref = a.ref
    dirty = bool(a.ref)

    for tok in a.clear:
        name = tok.strip()
        if maps.pop(name, None) is not None:
            print("清掉配音「%s」的對照點" % name)
            dirty = True

    for tok in a.scan:
        name, path = _parse_kv(tok)
        if not name or not path:
            print("[跳過] --scan 要寫成 名稱=檔案路徑：%s" % tok)
            continue
        if not a.ref_file:
            print("[跳過] --scan 需要 --ref-file 指定主配音的音檔")
            continue
        print()
        print("=== 自動掃：%s ===" % name)
        pts, why = scan(a.ref_file, path, probes=a.probes, win=a.win)
        if not pts:
            print("  掃不出來：%s" % why)
            continue
        maps[name] = pts
        dirty = True
        print("  得到 %d 個對照點" % len(pts))

    for tok in a.point:
        name, rest = _parse_kv(tok)
        parts = [x.strip() for x in rest.replace("，", ",").split(",") if x.strip()]
        if not name or len(parts) != 2:
            print("[跳過] --point 要寫成 名稱=主配音秒,該版秒：%s" % tok)
            continue
        try:
            x, y = K.parse_time(parts[0]), K.parse_time(parts[1])
        except ValueError as e:
            print("[跳過] %s：%s" % (tok, e))
            continue
        maps[name] = clean_points((maps.get(name) or []) + [[x, y]])
        dirty = True
        print("配音「%s」加點：主配音 %.3f → %.3f" % (name, x, y))

    # 有卡片就順便做覆蓋檢查 —— 光看點的清單看不出「哪一段沒被蓋到」
    report = []
    try:
        class _A:
            pass
        shim = _A()
        shim.segments = None
        shim.card = a.card
        segs = K.load_segments(shim)
        pairs_file = os.path.splitext(a.card)[0] + ".pairs.txt"
        pairs = K.read_pairs(pairs_file) if os.path.isfile(pairs_file) else []
        byseg, _orphan = K.assign_pairs(segs, pairs)
        _tracks, report = K.build_tracks(segs, byseg)
    except BaseException as e:                              # noqa: BLE001
        print("[注意] 讀不到卡片的段落（%s）—— 只列對照點，不做覆蓋檢查" % e)

    durs = {}
    if a.ref_file:
        d = file_duration(a.ref_file)
        if d and ref:
            durs[ref] = d
    for tok in a.scan:
        name, path = _parse_kv(tok)
        d = file_duration(path)
        if d:
            durs[name] = d

    problems = print_report(report, ref, maps, durs)
    print()
    if problems:
        print("檢查結果：")
        for m in problems:
            print("  ! " + m)
    elif maps:
        print("檢查結果：沒發現問題。")
    else:
        print("目前沒有任何配音對照表 —— 所有版本都會被當成跟主配音同秒數。")

    if dirty:
        p, kind = save_maps(a.card, ref, maps)
        print()
        if kind == "json":
            print("已寫回 %s（refVariant / variantMaps）—— 插件直接讀得到。" % p)
        else:
            print("這張卡還沒有 cutscene.json，先存成側檔：%s" % p)
            print("下次產生 cutscene.json 時會自動帶進去，然後側檔就會被收掉。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
