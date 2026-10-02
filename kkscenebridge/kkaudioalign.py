#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""kkaudioalign — 從「已經切好的配音片段」反推它在原始音檔裡的位置。

為什麼需要這支工具
------------------
以前用 VNGE 做卡片時，音檔是一段一段切好的：PT1 給場景 1、PT2 給場景 2……
新的插件不切割 —— 它直接播原始長音檔，靠對應點（pairs）把時間軸對上去。
所以要轉過來，只缺一件事：每個切好的片段，頭尾各落在原檔的第幾秒。

那個數字本來要一個一個手動去量（開兩個播放器來回比對），
這支工具改用互相關自動量出來：

    片段 PT1 的頭 → 原檔 00:30.000
    片段 PT1 的尾 → 原檔 01:20.000
    場景 1 的時間軸是 0 ~ 47.5 秒
    ⇒ 對應點   0.000 → 30.000
               47.500 → 80.000

怎麼量的
--------
粗找
    把兩邊都換成能量包絡（10 毫秒一格），做**正規化**互相關掃過整個原檔。
    資料量只有原本的 1/80，掃半小時的音檔也就一瞬間，
    而且對音量差、重新編碼、輕微的 EQ 都不敏感。

    正規化這件事不能省：沒正規化的互相關就只是內積，
    原檔裡任何一段「比較大聲」的地方都可能贏過真正對上的位置。
    除以視窗自己的能量之後，比的才是波形的形狀。

精修
    在粗找的位置 ±0.5 秒內改用 8 kHz 原始波形再相關一次，做到毫秒級。

頭和尾**分開各對一次**，這是刻意的
    如果片段真的是原檔的一刀切，尾巴量到的位置必然等於「頭 + 片段長度」。
    對不上就代表中間被動過 —— 剪掉了一塊、或者變速。
    那種片段不能只用頭尾兩個點描述，工具會把偏差量報出來讓人自己判斷。
    （這也是為什麼不用「頭的位置 + 長度」直接推尾巴：那樣永遠不會發現問題。）

需求：numpy + PATH 上的 ffmpeg / ffprobe（跟 kkcutscene_prep 一樣）。

用法
----
    # 最常見：一個原檔 + 一整個資料夾的 PT 片段 + 場景卡
    python kkaudioalign.py --orig bak/原音頻.wav \\
                           --parts-dir . --match "[JPN]" \\
                           --card Scenecard.png -o Scenecard.pairs.txt

    # 手動指定順序（第一個給場景 1，第二個給場景 2…）
    python kkaudioalign.py --orig bak/原音頻.wav \\
                           --part PT1.wav --part PT2.wav \\
                           --segments 0,47.5 47.6,120.0
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import kkcutscene_prep as P                                    # noqa: E402

SR = P.SR              # 8000，精修用
HOP = 0.010            # 包絡每格 10 毫秒 → 粗找的解析度
ENV_SR = 1.0 / HOP     # 100 Hz


# ---------------------------------------------------------------- 互相關

def ncc(needle, hay):
    """needle 放在 hay 每個位置的正規化互相關。

    回傳長度 len(hay) - len(needle) + 1 的陣列，值域大致在 [-1, 1]，
    1 代表波形完全一樣（差一個正的縮放與平移）。
    """
    n, m = len(needle), len(hay)
    if n < 2 or m < n:
        return None
    nd = needle - needle.mean()
    nn = float(np.sqrt(float((nd * nd).sum())))
    if nn <= 0:
        return None                      # 整段都是同一個值（通常是全靜音）

    L = 1
    while L < m + n:
        L *= 2
    cc = np.fft.irfft(np.fft.rfft(hay, L) * np.conj(np.fft.rfft(nd, L)), L)
    cc = cc[:m - n + 1]

    # 每個視窗自己的標準差（用前綴和一次算完，不要用迴圈）。
    # needle 已經去掉平均值，所以 hay 視窗的平均值在內積裡自動被消掉，
    # 這裡只需要把「視窗的能量」除掉。
    cs = np.concatenate(([0.0], np.cumsum(hay)))
    cs2 = np.concatenate(([0.0], np.cumsum(hay * hay)))
    s = cs[n:m + 1] - cs[:m - n + 1]
    s2 = cs2[n:m + 1] - cs2[:m - n + 1]
    var = np.maximum(s2 - s * s / n, 1e-12)
    return cc / (np.sqrt(var) * nn)


def peak(c, guard):
    """回傳 (位置_含次取樣點, 峰值, 峰值 / 護欄外次高峰)。

    峰比才是「這個答案可不可信」的指標：峰值本身可以因為整段都是類似的環境音
    而普遍偏高，但「主峰比其他地方高多少」不會。
    """
    i = int(np.argmax(c))
    pk = float(c[i])

    lo, hi = max(0, i - guard), min(len(c), i + guard + 1)
    rest = np.concatenate((c[:lo], c[hi:]))
    second = float(rest.max()) if rest.size else 0.0
    ratio = (pk / second) if second > 1e-9 else float("inf")

    frac = 0.0                                   # 拋物線內插，補到格子之間
    if 0 < i < len(c) - 1:
        y0, y1, y2 = float(c[i - 1]), float(c[i]), float(c[i + 1])
        den = y0 - 2.0 * y1 + y2
        if abs(den) > 1e-12:
            frac = max(-1.0, min(1.0, 0.5 * (y0 - y2) / den))
    return i + frac, pk, ratio


# ---------------------------------------------------------------- 定位

def duration(path):
    i = P.info(path)
    if not i or not i.get("dur"):
        raise RuntimeError("讀不到長度: %s" % path)
    return float(i["dur"])


def locate(orig_path, orig_env, part_path, t0, w):
    """把 part 的 [t0, t0+w) 這一段，找出它在原檔的起點（秒）。

    回傳 (秒, 峰值, 峰比)，找不到回傳 None。
    """
    seg = P.load_mono(part_path, start=t0, dur=w)
    if len(seg) < SR:                     # 不到一秒，沒什麼好對的
        return None

    c = ncc(P.envelope(seg, hop=HOP), orig_env)
    if c is None or len(c) < 3:
        return None
    idx, pk, ratio = peak(c, guard=int(0.5 / HOP))
    coarse = idx * HOP

    # 精修：只在粗找的位置附近重新載一小段原檔，用 8 kHz 波形再對一次。
    # 整支原檔都用波形對的話，光是 FFT 就要好幾百 MB 記憶體，沒必要。
    pad = 0.5
    hs = max(0.0, coarse - pad)
    try:
        hay = P.load_mono(orig_path, start=hs, dur=w + 2 * pad)
    except Exception:
        return coarse, pk, ratio
    c2 = ncc(seg, hay)
    if c2 is not None and len(c2) > 2:
        i2, pk2, ratio2 = peak(c2, guard=int(0.05 * SR))
        fine = hs + i2 / SR
        # 精修結果跑太遠就不採信 —— 那代表這一小段裡有更像的地方，
        # 但粗找是看整首的，粗找比較可信。
        if abs(fine - coarse) <= pad and pk2 > 0.20:
            return fine, pk2, ratio2
    return coarse, pk, ratio


def align_part(orig_path, orig_env, part_path, win=90.0):
    """量一個片段的頭尾各落在原檔的第幾秒。"""
    dur = duration(part_path)
    if dur <= 12.0:
        w, single = dur, True             # 太短，頭尾分不開，只對一次
    else:
        w, single = min(win, dur / 2.0), False

    head = locate(orig_path, orig_env, part_path, 0.0, w)
    tail = None if single else locate(orig_path, orig_env, part_path, dur - w, w)

    out = dict(path=part_path, dur=dur, single=single,
               start=None, end=None, drift=None,
               head=head, tail=tail, ok=False, why="")
    if head is None:
        out["why"] = "對不出頭段（檔案太短或全是靜音？）"
        return out

    out["start"] = head[0]
    if tail is None:
        # 只能用「頭 + 長度」推尾巴。這個尾巴沒有被驗證過，
        # 中間若有剪接也看不出來 —— 報告會講清楚。
        out["end"] = head[0] + dur
        out["ok"] = True
        out["why"] = "片段太短，尾端是用長度推的，沒有獨立驗證"
        return out

    out["end"] = tail[0] + w
    out["drift"] = (out["end"] - out["start"]) - dur
    out["ok"] = True
    return out


# ---------------------------------------------------------------- 片段收集

def part_key(path):
    """從檔名撈 PT 編號當排序依據，撈不到就退回檔名。"""
    stem = os.path.splitext(os.path.basename(path))[0]
    m = re.search(r"(?:PT|Pt|pt|part|Part|_)(\d+)", stem)
    return (0, int(m.group(1)), stem) if m else (1, 0, stem)


AUDIO_EXT = (".wav", ".flac", ".ogg", ".m4a", ".mp3", ".aac", ".opus")


def collect_parts(folder, match=""):
    out = []
    for f in sorted(os.listdir(folder)):
        if not f.lower().endswith(AUDIO_EXT):
            continue
        if match and match.lower() not in f.lower():
            continue
        out.append(os.path.join(folder, f))
    out.sort(key=part_key)
    return out


# ---------------------------------------------------------------- 主流程

def run_align(orig, parts, segs=None, win=90.0, log=print, assign=None):
    """量完所有片段，回傳 (結果列表, 對應點列表, 問題列表)。

    segs   卡片上的段落清單；給了就一併算出對應點，沒給就只回報位置。
    assign 逐一指定「第 i 個片段對到哪一段」，長度要跟 parts 一樣，
           元素是 segs 裡的那個 dict（或 None 表示這個片段不用）。
           不給就照順序配（第一個片段配第一段…）——
           介面上是用下拉選單讓人自己指定，因為 PT 編號跟卡片上的段落順序
           不見得一致，照順序猜錯了完全看不出來。
    """
    problems = []
    log("原檔: %s" % orig)
    od = duration(orig)
    log("      長度 %s" % P_fmt(od))
    log("載入原檔的能量包絡…（%.0f 秒的音訊）" % od)
    orig_env = P.envelope(P.load_mono(orig), hop=HOP)

    results = []
    for i, p in enumerate(parts, 1):
        log("")
        log("[%d/%d] %s" % (i, len(parts), os.path.basename(p)))
        r = align_part(orig, orig_env, p, win=win)
        r["index"] = i
        results.append(r)
        if not r["ok"]:
            log("    對不出來：%s" % r["why"])
            problems.append("片段 %d（%s）對不出位置" % (i, os.path.basename(p)))
            continue
        hp, hr = r["head"][1], r["head"][2]
        log("    頭 %s   （吻合度 %.3f，峰比 %s）"
            % (P_fmt(r["start"]), hp, fmt_ratio(hr)))
        if r["tail"]:
            tp, tr = r["tail"][1], r["tail"][2]
            log("    尾 %s   （吻合度 %.3f，峰比 %s）"
                % (P_fmt(r["end"]), tp, fmt_ratio(tr)))
            log("    片段長 %.3f s，原檔上跨 %.3f s，差 %+.3f s"
                % (r["dur"], r["end"] - r["start"], r["drift"]))
        else:
            log("    尾 %s   （用長度推的）" % P_fmt(r["end"]))
        if r["why"]:
            log("    注意：%s" % r["why"])

        # --- 可信度檢查 ---
        for tag, h in (("頭", r["head"]), ("尾", r["tail"])):
            if h is None:
                continue
            if h[1] < 0.30 or h[2] < 1.5:
                problems.append(
                    "片段 %d 的%s對得不好（吻合度 %.3f、峰比 %s）—— "
                    "可能不是同一個來源，或那一段幾乎是靜音"
                    % (i, tag, h[1], fmt_ratio(h[2])))
        if r["drift"] is not None and abs(r["drift"]) > 0.15:
            problems.append(
                "片段 %d 在原檔上跨了 %.3f 秒、自己卻只有 %.3f 秒（差 %+.3f）—— "
                "這不是單純的一刀切：中間被剪掉一塊或變過速。"
                "頭尾兩個點描述不了它，這一段要自己補中間的對應點"
                % (i, r["end"] - r["start"], r["dur"], r["drift"]))

    # 順序合理性：切出來的片段在原檔上應該是往前走的
    prev = None
    for r in results:
        if not r["ok"]:
            continue
        if prev is not None and r["start"] < prev - 0.05:
            problems.append(
                "片段 %d 的起點（%s）比前一段還早 —— "
                "PT 編號跟場景順序可能對不上" % (r["index"], P_fmt(r["start"])))
        prev = r["start"]

    pairs = []
    if segs or assign:
        if assign is None:
            ok = [r for r in results if r["ok"]]
            if len(ok) != len(segs):
                problems.append("卡片有 %d 段場景，但對到位置的片段有 %d 個 —— "
                                "數量不一致，請確認 PT 檔案挑對了"
                                % (len(segs), len(ok)))
            todo = list(zip(ok, segs))
        else:
            todo = [(r, s) for r, s in zip(results, assign) if r["ok"] and s]

        used = {}
        for r, s in todo:
            nm = os.path.basename(r["path"])
            tag = "第%d段" % s["index"]
            pairs.append((s["start"], r["start"], "%s 頭 → %s（自動對齊）" % (nm, tag)))
            pairs.append((s["end"], r["end"], "%s 尾 → %s（自動對齊）" % (nm, tag)))
            # 同一段被兩個片段指到的話，兩組點會互相矛盾，段內的速率會被扯歪
            if s["index"] in used:
                problems.append("第 %d 段同時被「%s」和「%s」指到 —— "
                                "一段只能對一個片段"
                                % (s["index"], used[s["index"]], nm))
            used[s["index"]] = nm

        # 每一段的「時間軸長度 ÷ 音訊長度」＝ 這一段的平均時間流速。
        # 明顯偏離 1 的段落是慢動作/快轉，那種段落只有頭尾兩個對應點通常不夠，
        # 產生 json 之後要看留一驗證的結果決定要不要補中間的點。
        if todo:
            log("")
            log("各段的平均時間流速（時間軸長度 ÷ 音訊長度）:")
            for r, s in todo:
                span = s["end"] - s["start"]
                if span <= 0 or r["dur"] <= 0:
                    continue
                rate = span / r["dur"]
                mark = ""
                if abs(rate - 1.0) > 0.25:
                    mark = "   ← 偏離 1 很多，段內若有起伏，兩個點描述不了"
                elif abs(rate - 1.0) > 0.05:
                    mark = "   ← 有整體慢放/快放"
                log("  第%d段  時間軸 %7.2f s ÷ 音訊 %8.3f s = %.4f%s"
                    % (s["index"], span, r["dur"], rate, mark))

        # 片段長度跟段落的時間軸長度差太多，多半是配錯段
        for r, s in todo:
            span = s["end"] - s["start"]
            if span > 0 and r["dur"] > 0:
                rate = span / r["dur"]
                if not (0.3 <= rate <= 3.0):
                    problems.append(
                        "「%s」長 %.1f 秒，卻配到長 %.1f 秒的第 %d 段（差了 %.1f 倍）—— "
                        "確認一下是不是配錯段"
                        % (os.path.basename(r["path"]), r["dur"], span,
                           s["index"], max(rate, 1 / rate)))
        pairs.sort()

    summary = coverage(results, od, log)
    return results, pairs, problems, summary


def coverage(results, orig_dur, log=print, tol=0.05):
    """看切好的片段有沒有把原檔整個蓋滿。

    蓋滿代表什麼：原檔裡的每一秒都屬於某一個場景段落，
    沒有任何一段是「卡片上沒有、只能放影片」的東西 ——
    也就是沒有開場動畫、沒有過場、沒有片尾。
    那種卡片根本不需要影片檔，插件只要播音軌就好。

    沒蓋滿的地方就是缺口，每一個缺口就是一段要用影片補的過場。
    """
    ok = [r for r in results if r["ok"]]
    out = dict(full=False, head=None, tail=None, gaps=[], orig_dur=orig_dur)
    if not ok:
        return out
    ok = sorted(ok, key=lambda r: r["start"])

    out["head"] = ok[0]["start"]
    out["tail"] = orig_dur - ok[-1]["end"]
    for a, b in zip(ok, ok[1:]):
        out["gaps"].append((a["index"], b["index"], b["start"] - a["end"]))
    out["full"] = (abs(out["head"]) <= tol
                   and abs(out["tail"]) <= tol
                   and all(abs(g) <= tol for _, _, g in out["gaps"]))

    log("")
    log("覆蓋率（切好的片段有沒有把原檔蓋滿）:")
    log("  原檔開頭 → 第一個片段   %+.3f s" % out["head"])
    for i, j, g in out["gaps"]:
        log("  片段 %d 尾 → 片段 %d 頭     %+.3f s" % (i, j, g))
    log("  最後一個片段 → 原檔結尾 %+.3f s" % out["tail"])
    if out["full"]:
        log("")
        log("  ★ 片段完整蓋滿原檔，中間沒有任何缺口。")
        log("    代表這張卡沒有開場動畫、沒有過場、也沒有片尾 ——")
        log("    **不需要影片檔**。產生 cutscene.json 時不用指定 ★來源影片，")
        log("    只要把配音勾起來就好，插件會純粹用音軌跟著時間軸走。")
    else:
        holes = []
        if abs(out["head"]) > tol:
            holes.append("開場 %.3f s" % out["head"])
        for i, j, g in out["gaps"]:
            if abs(g) > tol:
                holes.append("片段 %d→%d 之間 %.3f s" % (i, j, g))
        if abs(out["tail"]) > tol:
            holes.append("片尾 %.3f s" % out["tail"])
        log("")
        log("  有缺口：%s" % "、".join(holes))
        log("    這些是卡片上沒有、要用影片補的段落，所以還是需要 ★來源影片。")
    return out


def P_fmt(v):
    v = float(v)
    m, s = divmod(v, 60.0)
    h, m = divmod(int(m), 60)
    return ("%d:%02d:%06.3f" % (h, m, s)) if h else ("%02d:%06.3f" % (m, s))


def fmt_ratio(r):
    return "∞" if r == float("inf") else "%.2f" % r


def write_pairs(path, pairs):
    with open(path, "w", encoding="utf-8") as f:
        f.write("# timeline        影片              說明\n")
        f.write("# 由 kkaudioalign 自動產生（切好的配音片段 → 原始音檔位置）\n")
        for t, v, n in pairs:
            f.write("%-17s %-17s %s\n" % (P_fmt(t), P_fmt(v), ("# " + n) if n else ""))


# ------------------------------------------------- 兩個「完整版本」之間的對照

def locate_near(other_path, other_dur, ref_path, t, w, center, pad=3.0):
    """把 ref 的 [t, t+w) **只**拿去 other 的 center 附近找。

    為什麼需要這個：全域搜尋是在整支檔案裡挑最高的峰。兩個版本配音不同時，
    真正對上的那個峰本來就不高，很容易被別處的巧合蓋過去 —— 這就是為什麼
    全域搜尋會吐出「位移 −243 秒」那種答案。
    已經有候選位移之後，問題就從「全世界哪裡最像」變成「是不是這裡」，
    後者好回答太多，也不會被遠處的巧合干擾。

    用能量包絡比（跟全域搜尋同一個尺度），數字才能直接跟第一階段對照。
    """
    if t + w > other_dur + pad:
        return None
    seg = P.load_mono(ref_path, start=t, dur=w)
    if len(seg) < SR:
        return None
    hs = max(0.0, center - pad)
    hay = P.load_mono(other_path, start=hs, dur=w + 2 * pad)
    if len(hay) < len(seg) + SR // 10:
        return None
    c = ncc(P.envelope(seg, hop=HOP), P.envelope(hay, hop=HOP))
    if c is None or len(c) < 3:
        return None
    i, pk, ratio = peak(c, guard=int(0.5 / HOP))
    return hs + i * HOP, pk, ratio


def probe_map(ref, other, n_probes=24, win=20.0, log=print,
              min_ncc=0.15, min_ratio=2.0, tol=0.05,
              near_ncc=0.20, near_tol=0.25, weak_tol=0.30):
    """量出「基準音檔的第 t 秒」對應到「另一個版本的第幾秒」。

    用途：同一部片的不同配音版本，如果其中一個沒跟著做最後的剪輯，
    兩邊的時間軸就對不上。這個函式在基準檔上均勻取幾十個視窗，
    一個一個去另一個版本裡找，得到一串 (基準秒, 對方秒) 的對照點。

    能不能對得起來，取決於兩個版本有沒有共同的底（音效、音樂、環境音）。
    純人聲的話兩個配音員的波形毫無關係，互相關一定失敗 ——
    所以這裡把每一點的吻合度和峰比都報出來，讓人自己判斷。

    兩個門檻的角色不一樣，這點很重要：
      峰比   主要依據。「主峰比其他地方高多少」，不受整體相似度高低影響。
      吻合度 次要。配音不同時，對上的點本來就只有 0.2~0.5，
             拿它當主要門檻會把真的比對砍掉 —— 實際踩過：
             0.246/0.231/0.249 但峰比 2.3~4.3 的四個點全被誤殺，
             結果「可信範圍」看起來只有前四分半，後面被當成沒量到。

    兩階段：
      1. 全域搜尋，挑出高峰比的點，歸納出候選位移。
      2. 拿候選位移回去驗證第一階段沒過的點（只在預期位置附近找）。
         這一步才問得出「後半段到底是位移 0、還是整段不見了」。

    回傳 (probes, groups)：
      probes  [(基準秒, 對方秒, 吻合度, 峰比, 通過與否, 來源)]
      groups  [(起, 迄, 位移, 幾個點)] —— 位移一致的連續區段
    """
    rd, od = duration(ref), duration(other)
    log("基準: %s" % ref)
    log("      長度 %s" % P_fmt(rd))
    log("對方: %s" % other)
    log("      長度 %s（差 %+.3f 秒）" % (P_fmt(od), od - rd))
    log("")
    log("載入對方的能量包絡…")
    oenv = P.envelope(P.load_mono(other), hop=HOP)

    w = min(win, max(5.0, rd / (n_probes + 1)))
    lo, hi = 0.0, max(0.0, rd - w)
    step = (hi - lo) / max(1, n_probes - 1) if n_probes > 1 else 0.0

    log("")
    log("【第一階段】全域搜尋 —— 在基準檔取 %d 個 %.0f 秒的視窗，到對方檔裡找:"
        % (n_probes, w))
    log("  %-12s %-12s %-10s %8s %8s" % ("基準", "對方", "位移", "吻合度", "峰比"))
    probes = []
    for i in range(n_probes):
        t = lo + step * i
        r = locate(other, oenv, ref, t, w)
        if r is None:
            log("  %-12s （這一段取不到音訊）" % P_fmt(t))
            continue
        u, pk, ratio = r
        good = (pk >= min_ncc and ratio >= min_ratio)
        probes.append([t, u, pk, ratio, good, "全域"])
        log("  %-12s %-12s %+10.3f %8.3f %8s%s"
            % (P_fmt(t), P_fmt(u), u - t, pk, fmt_ratio(ratio),
               "" if good else "   ← 不可信"))

    good = [p for p in probes if p[4]]
    log("")
    log("通過門檻（峰比 ≥ %.1f 且吻合度 ≥ %.2f）的有 %d / %d 點"
        % (min_ratio, min_ncc, len(good), len(probes)))
    if not good:
        log("")
        log("  ✗ 對不起來。兩個版本之間沒有足夠的共同成分 ——")
        log("    如果配音以外的底（音效 / 音樂）也是各做各的，波形就真的無關，")
        log("    這種情況只能手動量幾個對應點。")
        return probes, []

    # ---- 第二階段：拿候選位移回頭驗沒過的點 ----
    cands = []
    for p in good:
        o = p[1] - p[0]
        if not any(abs(o - c) <= tol for c in cands):
            cands.append(round(o, 3))
    if not any(abs((od - rd) - c) <= tol for c in cands):
        cands.append(round(od - rd, 3))     # 「對方整段變短」也是一個天然的候選

    todo = [p for p in probes if not p[4]]
    if todo:
        log("")
        log("【第二階段】候選位移 %s —— 只在預期位置 ±3 秒內驗證第一階段沒過的點:"
            % "、".join("%+.3f" % c for c in cands))
        log("  %-12s %-12s %-10s %8s %8s  %s"
            % ("基準", "對方", "位移", "吻合度", "峰比", "說明"))
    for p in todo:
        t = p[0]
        best, far, tried = None, None, 0
        for c in cands:
            if t + c < -1.0 or t + c + w > od + 3.0:
                continue                      # 這個位移下對方根本沒有那段內容
            tried += 1
            r = locate_near(other, od, ref, t, w, t + c)
            if r is None:
                continue
            # 第二階段只能「確認候選位移」，不能自己發明新的位移。
            # 找到的位置離候選太遠就是沒確認到 —— 那多半是搜尋窗裡的巧合峰。
            # 不擋的話會憑空生出剪輯點：實測過一個 ±3 秒窗裡的弱峰（吻合度 0.22）
            # 偏了 1.17 秒還被收下，報告就多出一處根本不存在的「剪輯差異」。
            if abs((r[0] - t) - c) > near_tol:
                if far is None or r[1] > far[1]:
                    far = r
                continue
            if best is None or r[1] > best[1]:
                best = r
        if best is None:
            # 這三種情況要分清楚，不然會把「驗過但不夠強」誤報成「根本沒得比」
            if tried == 0:
                why = "每個候選位移下，對方都沒有這一段的內容"
                p[5] = "超出對方長度"
            elif far is not None:
                why = "找到的位置偏離候選位移 %.3f 秒，不是確認，不採信" % (far[0] - t)
                p[5] = "偏離候選"
            else:
                why = "候選位移附近沒有可用的峰"
                p[5] = "驗不過"
            log("  %-12s %-12s %10s %8s %8s  %s"
                % (P_fmt(t), "—", "—", "—", "—", why))
            continue
        u, pk, ratio = best
        if pk >= near_ncc:
            p[0], p[1], p[2], p[3], p[4], p[5] = t, u, pk, ratio, True, "近距離"
            log("  %-12s %-12s %+10.3f %8.3f %8s  收下"
                % (P_fmt(t), P_fmt(u), u - t, pk, fmt_ratio(ratio)))
        else:
            log("  %-12s %-12s %+10.3f %8.3f %8s  還是太弱，不收"
                % (P_fmt(t), P_fmt(u), u - t, pk, fmt_ratio(ratio)))

    good = [p for p in probes if p[4]]
    good.sort(key=lambda p: p[0])
    strong = [p for p in good if p[5] == "全域"]
    weak = [p for p in good if p[5] != "全域"]

    # ---- 位移一致的連續區段 ----
    #
    # 區段只由「第一階段就過關的高可信點」決定。
    #
    # 血淚：第一版讓所有通過的點一起分組、容差 0.05 秒。第二階段收下的點
    # 訊號本來就弱，位移量測帶著 ±0.2 秒的雜訊 —— 於是一份位移其實完全一致的
    # 音檔被切成 7 段，報告憑空生出 6 個「剪輯差異」，每個都只有 0.1~0.4 秒，
    # 看起來煞有介事。弱點只能「附議」高可信點的結論，不能自己另立一段。
    base_pts = strong if strong else good
    groups = []
    cur = [base_pts[0]]
    for p in base_pts[1:]:
        med = sorted(x[1] - x[0] for x in cur)[len(cur) // 2]
        if abs((p[1] - p[0]) - med) <= tol:
            cur.append(p)
        else:
            groups.append(cur)
            cur = [p]
    groups.append(cur)

    offs = [sorted(x[1] - x[0] for x in g)[len(g) // 2] for g in groups]
    odd = []
    if strong:
        for p in weak:
            o = p[1] - p[0]
            j = min(range(len(offs)), key=lambda k: abs(o - offs[k]))
            if abs(o - offs[j]) <= weak_tol:
                groups[j].append(p)          # 只延伸涵蓋範圍，不影響位移的值
            else:
                odd.append((p[0], o))
        for g in groups:
            g.sort(key=lambda x: x[0])

    out = []
    log("")
    log("位移一致的區段（位移不變 = 這一段兩邊是同一個剪輯）:")
    prev_off, prev_end = None, None
    for g, off in zip(groups, offs):
        a, b = g[0][0], g[-1][0]
        ns = sum(1 for x in g if x[5] == "全域")
        if prev_off is not None:
            # 剪輯點只能定位到「上一個好點」和「這個好點」之間 ——
            # 探測是離散的，中間沒量過。要更準就把 --probes 加大。
            log("    ↕ 剪輯差異 %+.3f 秒，發生在 %s ~ %s 之間（這個區間內沒有量測點）"
                % (off - prev_off, P_fmt(prev_end), P_fmt(a)))
        log("  %s ~ %s   位移 %+.3f 秒   %d 個點（其中 %d 個高可信）"
            % (P_fmt(a), P_fmt(b), off, len(g), ns))
        out.append((a, b, off, len(g)))
        prev_off, prev_end = off, b

    if odd:
        log("")
        log("對不上任何區段的弱點（沒有採用，列出來讓你自己判斷）:")
        for t, o in odd:
            log("  %-12s 位移 %+.3f 秒" % (P_fmt(t), o))
        log("  這些如果集中在同一個位移、而且數量不少，可能是真的剪輯差異；")
        log("  零星散落的話就是弱訊號的量測雜訊。")

    # ---- 涵蓋範圍：沒量到的地方就是沒量到，不要往外推 ----
    covered_to = out[-1][1]
    covered_from = out[0][0]
    log("")
    log("涵蓋範圍：基準檔的 %s ~ %s（全長 %s）"
        % (P_fmt(covered_from), P_fmt(covered_to), P_fmt(rd)))
    gap_tail = rd - covered_to
    if covered_from > w:
        log("  ! 開頭 %s 之前沒有任何可信的量測點。" % P_fmt(covered_from))
    if gap_tail > w:
        log("  ! %s 之後（還有 %.1f 秒）沒有任何可信的量測點 ——"
            % (P_fmt(covered_to), gap_tail))
        log("    **不能**假設那一段也是同一個位移。對方比基準短 %.3f 秒，"
            % (od - rd))
        log("    那 %.1f 秒可能是整段不見、也可能是中間某處被剪掉。" % abs(od - rd))
        log("    要嘛把 --probes 加大再跑一次，要嘛那一段自己聽著量。")
    if len(out) == 1 and gap_tail <= w and covered_from <= w:
        log("  ★ 整支只有一個位移（%+.3f 秒），而且量測點涵蓋全長 —— "
            "對照表兩個點就夠。" % out[0][2])
    elif len(out) > 1:
        log("  兩個版本有 %d 處剪輯差異，對照表要逐段給。" % (len(out) - 1))
    return probes, out


def map_anchors(groups, ref_dur):
    """把區段整理成 [[基準秒, 對方秒], …] 的折線對照表。"""
    pts = []
    for a, b, off, _ in groups:
        pts.append([round(a, 3), round(a + off, 3)])
        pts.append([round(b, 3), round(b + off, 3)])
    # 去掉時間重複的點（折線內插不吃重複的 x）
    out = []
    for p in pts:
        if out and abs(p[0] - out[-1][0]) < 1e-6:
            out[-1] = p
        else:
            out.append(p)
    return out


def cmd_map(argv):
    ap = argparse.ArgumentParser(
        prog="kkaudioalign.py map",
        description="量兩個「完整版本」之間的時間對照（其中一個沒做最後剪輯時用）")
    ap.add_argument("--ref", required=True, help="基準音檔（已經對好對應點的那一個）")
    ap.add_argument("--other", required=True, help="要對照的另一個版本")
    ap.add_argument("--probes", type=int, default=24, help="取幾個視窗（預設 24）")
    ap.add_argument("--win", type=float, default=20.0, help="每個視窗幾秒（預設 20）")
    ap.add_argument("-o", "--out", help="把對照表寫成一個 json 片段")
    a = ap.parse_args(argv)

    _, groups = probe_map(a.ref, a.other, n_probes=a.probes, win=a.win)
    if not groups:
        return 1
    anchors = map_anchors(groups, duration(a.ref))
    print()
    print("對照表（基準秒 → 這個版本的秒）:")
    for x, y in anchors:
        print("  %-12s → %-12s" % (P_fmt(x), P_fmt(y)))
    if a.out:
        with open(a.out, "w", encoding="utf-8") as f:
            f.write(json.dumps(anchors, ensure_ascii=False))
            f.write("\n")
        print("\n已寫出: %s" % a.out)
    return 0


# ---------------------------------------------------------------- CLI

def main(argv=None):
    ap = argparse.ArgumentParser(
        description="從切好的配音片段反推它在原始音檔裡的位置，直接產生 pairs.txt")
    ap.add_argument("--orig", required=True, help="原始（沒切過的）音檔或影片")
    ap.add_argument("--part", action="append", default=[],
                    help="切好的片段，依場景順序給；可以重複這個選項")
    ap.add_argument("--parts-dir", help="改成指定一個資料夾，自動照 PT 編號排序")
    ap.add_argument("--match", default="",
                    help="配合 --parts-dir：只收檔名含這個字串的（例如 [JPN]）")
    ap.add_argument("--card", help="場景卡；用來取得每一段的時間軸起訖")
    ap.add_argument("--segments", nargs="*",
                    help="不給卡片時手動指定，格式 0,47.5 47.6,120.0 …")
    ap.add_argument("--window", type=float, default=90.0,
                    help="頭尾各拿幾秒去比對（預設 90）")
    ap.add_argument("-o", "--out", help="寫出的 pairs.txt")
    a = ap.parse_args(argv)

    parts = list(a.part)
    if a.parts_dir:
        parts += collect_parts(a.parts_dir, a.match)
    if not parts:
        raise SystemExit("沒有片段可以對。用 --part 或 --parts-dir 指定。")

    segs = None
    if a.card or a.segments:
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        import kkcutscene as K

        class _A:
            pass
        shim = _A()
        shim.segments = a.segments
        shim.card = a.card
        segs = K.load_segments(shim)
        print("場景段落（來自%s）:" % ("卡片" if a.card else "--segments"))
        for s in segs:
            print("  %d  %-24s %s ~ %s"
                  % (s["index"], s["name"][:24], P_fmt(s["start"]), P_fmt(s["end"])))
        print()

    print("片段（依順序對到場景 1、2、3…）:")
    for i, p in enumerate(parts, 1):
        print("  %d  %s" % (i, os.path.basename(p)))
    print()

    results, pairs, problems, summary = run_align(a.orig, parts, segs, win=a.window)

    print()
    if pairs:
        print("對應點:")
        print("  %-17s %-17s %s" % ("timeline", "原檔", "說明"))
        for t, v, n in pairs:
            print("  %-17s %-17s %s" % (P_fmt(t), P_fmt(v), n))
    print()
    if problems:
        print("檢查結果:")
        for p in problems:
            print("  ! %s" % p)
    else:
        print("檢查結果：沒發現問題")

    if a.out and pairs:
        write_pairs(a.out, pairs)
        print("\n已寫出: %s（%d 個對應點）" % (a.out, len(pairs)))
    elif a.out:
        print("\n沒有對應點可寫（要有 --card 或 --segments 才算得出來）")
    return 0 if not problems else 0        # 有提醒也不當成失敗，交給人判斷


if __name__ == "__main__":
    # 第一個參數是 map 的話走「兩個完整版本互相對照」那一套，
    # 其餘維持原本的用法（不帶子命令），舊的指令列不用改。
    if len(sys.argv) > 1 and sys.argv[1] == "map":
        sys.exit(cmd_map(sys.argv[2:]))
    sys.exit(main())
