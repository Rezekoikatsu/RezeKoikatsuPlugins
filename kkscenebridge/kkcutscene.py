#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
kkcutscene.py — 產生 StudioCutScene 的設定檔（.cutscene.json）

跟 kkvnsound.py 平行的一支：kkvnsound 把音頻寫成 VNGE VNSound，
這支把同一批段落資訊寫成 StudioCutScene 插件吃的格式。
兩者可以並存，合併分頁選哪一種就寫哪一種。

--------------------------------------------------------------------------
核心：timeline 秒 ↔ 影片秒
--------------------------------------------------------------------------
場景卡的 timeline 時間和作者成品影片的時間不是 1:1，因為

  1. 影片有開頭動畫、過場，場景卡裡沒有
  2. 場景有「時間流速」(timeScale) 軌道 —— timeScale 0.82 時，
     真實過 1 秒、timeline 只走 0.82 秒，而影片是真實時間

所以每一段場景各給兩個對應點 [timeline秒, 影片秒]，中間線性內插。
兩點的斜率就吸收掉該段的 timeScale；段與段之間的跳躍量就是被剪掉的過場。

    每段兩個點，一個靠開頭、一個靠結尾，兩點拉越遠越準。
    N 段場景 = 2N 個點。開場/過場/片尾的長度是外推算出來的，不用另外量。

--------------------------------------------------------------------------
指令
--------------------------------------------------------------------------
  points   <卡片.png|--segments …>              列出每段建議抓哪兩個時間點
  plan     <卡片.png> --video src.mp4            產生 .cutscene.json
  verify   <設定檔.json> [--video-duration 秒]   檢查對應關係合不合理

給了卡片路徑時，這幾個預設就跟卡片放在一起，不用每次打：

    --pairs   →  <卡片同名>.pairs.txt，找不到再找同資料夾的 pairs.txt
    -o        →  <卡片同名>.cutscene.json

  --variant-dir <資料夾>  把裡面的 wav / ogg 全部當成配音版本，名稱自動從檔名取
                          （有 @ 就取 @ 後面那段，否則取檔名尾巴）

pairs.txt 每行一組，時間可以寫秒數或 mm:ss.sss / h:mm:ss.sss：

    # timeline        影片
    00:22.017         00:00:42.783
    00:52.317         00:01:14.433
    00:58.517         00:01:22.350
"""

import argparse
import json
import math
import os
import re
import sys

DEFAULTS = dict(
    colorMode=3, pathMode="abs",
    audioRoot="UserData\\audio", videoRoot="UserData\\cutscene",
    tracksEnabled=True, trackVolume=1.0,
    jumpThreshold=0.35, panicSeek=1.5, hardSeek=0.35,
    slewDeadzone=0.03, slewMax=0.02, slewGain=0.15,
    fadeIn=0.25, fadeOut=0.35, preloadLead=3.0, window=0.5,
    freezeTimeScale=True, videoWidth=1920, videoHeight=1080, maxDeltaTime=0,
    useOpening=True, useTransitions=True, useEnding=True,
    # Timeline 本身是循環播放的；關掉的話插件會在繞回 0 時停下來等人按播放
    autoReplay=False,
    # VR：IMGUI 畫的東西進不了頭顯，所以 VR 時改用相機前面的一塊板子。
    # vrMode 0=自動偵測 1=一律使用 2=一律不用；距離和寬度都是公尺。
    vrMode=0, vrDistance=2.0, vrWidth=3.0,
    vrFollow=False, vrFlipY=False, vrKeepDesktop=True,
)


# ---------------------------------------------------------------- 時間

def parse_time(s):
    """吃 12.345 / 01:02.345 / 1:02:03.456，回傳秒。"""
    s = str(s).strip()
    if not s:
        raise ValueError("空的時間")
    if ":" not in s:
        return float(s)
    parts = s.split(":")
    if len(parts) > 3:
        raise ValueError("看不懂的時間: %s" % s)
    total = 0.0
    for p in parts:
        total = total * 60.0 + float(p)
    return total


def fmt_time(v):
    v = float(v)
    m, s = divmod(v, 60.0)
    h, m = divmod(int(m), 60)
    return ("%d:%02d:%06.3f" % (h, m, s)) if h else ("%02d:%06.3f" % (m, s))


def read_pairs(path):
    out = []
    with open(path, encoding="utf-8-sig") as f:
        for ln, line in enumerate(f, 1):
            line = line.split("#")[0].strip()
            if not line:
                continue
            toks = re.split(r"[\s,\t]+", line)
            if len(toks) < 2:
                raise ValueError("第 %d 行只有一個值，需要「timeline 影片」兩欄" % ln)
            try:
                out.append((parse_time(toks[0]), parse_time(toks[1])))
            except ValueError as e:
                raise ValueError("第 %d 行: %s" % (ln, e))
    out.sort()
    return out


def read_pairs_notes(path):
    """跟 read_pairs 一樣，但把 # 後面的說明留著，而且壞掉的行直接跳過不報錯。

    這份是給「把 json 載回工具」用的：它的目的是盡量還原當初的對應點表格，
    所以寧可少一行也不要整個讀不進來 —— 真正要驗格式的是 read_pairs。
    """
    out = []
    try:
        with open(path, encoding="utf-8-sig") as f:
            for line in f:
                body, _, note = line.partition("#")
                body = body.strip()
                if not body:
                    continue
                toks = re.split(r"[\s,\t]+", body)
                if len(toks) < 2:
                    continue
                try:
                    out.append((parse_time(toks[0]), parse_time(toks[1]), note.strip()))
                except ValueError:
                    continue
    except Exception:
        return []
    out.sort()
    return out


# ---------------------------------------------------------------- 段落

def load_segments(args, want_scene=False):
    """回傳 [{'index','name','start','end'}]（want_scene 時多回傳 scene）。"""
    if args.segments:
        segs = []
        for i, tok in enumerate(args.segments, 1):
            a, _, b = tok.partition(",")
            segs.append(dict(index=i, name="場景%d" % i,
                             start=parse_time(a), end=parse_time(b)))
        return (segs, None) if want_scene else segs

    if not args.card:
        raise SystemExit("要嘛給卡片路徑，要嘛用 --segments 0,58 58.01,68.51 …")

    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import kkscene2 as S                # 跟 kkvnsound.py 同一套
    import kkvnsound as VS

    scene = S.Scene(args.card)          # 注意：是建構子，不是 Scene.load()
    warn = []
    slots = VS.scene_slots(scene, warn)
    for w in warn:
        print("  [注意] %s" % w)
    segs = []
    for s in slots:
        if s.get("start") is None or s.get("end") is None:
            raise SystemExit("卡片裡第 %d 段沒有起訖時間，"
                             "請改用 --segments 手動指定" % s["index"])
        segs.append(dict(index=s["index"], name=s["name"],
                         start=float(s["start"]), end=float(s["end"])))
    return (segs, scene) if want_scene else segs


# 段落邊界的容差（秒）。相鄰兩段之間只隔 0.01 秒，所以要小於一半。
EDGE_TOL = 0.004


def assign_pairs(segs, pairs):
    """把對應點分配到各段。回傳 {段index: [(t, v), …]}，以及落在段外的點。

    **頭尾兩端都算在段內**，而且留一點容差。
    以前是 start <= t < end：量在「段落結尾」那一格的點（01:37.000 = 第 1 段的 97.00）
    因為 t < end 不成立而被丟掉；量在「段落開頭」的點（01:37.010）也可能因為卡片裡
    存的起點是 97.0100002 這種浮點數而差一點點進不去。兩個都變成「段外的點」，
    交界兩側都沒有點，過場就算不出來 —— 明明量了頭尾，報告卻說「第 2 段沒有對應點」。
    """
    byseg, orphan = {s["index"]: [] for s in segs}, []
    for t, v in pairs:
        hit = None
        for s in segs:
            if s["start"] - EDGE_TOL <= t <= s["end"] + EDGE_TOL:
                hit = s["index"]; break
        if hit is None:
            orphan.append((t, v))
        else:
            byseg[hit].append((t, v))
    return byseg, orphan


# ---------------------------------------------------------------- timeScale 積分

RE_INTERP = re.compile(r'<interpolable\b[^>]*?/>'
                       r'|<interpolable\b[^>]*?>(?:(?!</?interpolable\b).)*?</interpolable>', re.S)
RE_HEAD = re.compile(r'<interpolable\b[^>]*?>')
RE_KF = re.compile(r'<keyframe\b([^>]*)>(.*?)</keyframe>|<keyframe\b([^>]*)/>', re.S)
RE_ATTR = re.compile(r'(\w+)="([^"]*)"')


def read_timescale(scene):
    """從 timeline XML 撈出全域的 timeScale 軌道。回傳 [(time, value), …]。

    判斷方式跟 kkscene2.is_global_track 一樣：owner="Timeline" 且沒有 objectIndex。
    """
    xml = scene.timeline_xml()
    if not xml:
        return []
    for m in RE_INTERP.finditer(xml):
        blk = m.group(0)
        hm = RE_HEAD.match(blk)
        head = hm.group(0) if hm else blk
        if 'owner="Timeline"' not in head or 'objectIndex="' in head:
            continue
        a = dict(RE_ATTR.findall(head))
        if a.get("id") != "timeScale":
            continue
        inner = blk[len(head):-len("</interpolable>")] if blk.endswith("</interpolable>") else ""
        kfs = []
        for k in RE_KF.finditer(inner):
            ka = dict(RE_ATTR.findall(k.group(1) or k.group(3) or ""))
            if "time" not in ka:
                continue
            for key in ("value", "valueX", "valueF"):
                if key in ka:
                    kfs.append((float(ka["time"]), float(ka[key])))
                    break
        kfs.sort()
        return kfs
    return []


def ts_value(kfs, p):
    if not kfs:
        return 1.0
    if p <= kfs[0][0]:
        return kfs[0][1]
    if p >= kfs[-1][0]:
        return kfs[-1][1]
    for i in range(len(kfs) - 1):
        if p <= kfs[i + 1][0]:
            t0, v0 = kfs[i]
            t1, v1 = kfs[i + 1]
            if t1 - t0 < 1e-9:
                return v0
            return v0 + (v1 - v0) * (p - t0) / (t1 - t0)
    return kfs[-1][1]


# timeScale 夾住的下限。0.01 等於「最慢是百分之一速」——
# 比卡片裡實際用過的最慢值（0.03）還低，所以不會影響正常的慢動作，
# 只在真的掉到 0 的時候接住。
TS_FLOOR = 0.01


def tau(kfs, a, b, step=None, floor=0.0):
    """∫ dp / timeScale(p)，a→b。解析解，沒有數值誤差。

    dump 出來的 curveKeyframe 是 (0,0,in=0,out=1) 和 (1,1,in=1,out=0)。
    Hermite 代進去化簡後是 h(s) = s —— 也就是關鍵影格之間**就是線性內插**，
    不是曲線。所以 timeScale 在每一段裡是一次函數，1/timeScale 可以直接積：

        v 線性從 v0 到 v1：  ∫dp/v = (t1-t0)/(v1-v0) · ln(v1/v0)
        v 是定值        ：  ∫dp/v = (t1-t0)/v0

    這很重要：原本用 Simpson 步長 0.25 秒硬算，但這張卡的凹槽
    （例如 23.758 的 1.0 → 23.922 的 0.4 → 24.137 的 1.0）只有 0.16 秒寬，
    比步長還窄，整個被跨過去了。凹槽底部 1/v = 2.5，漏掉的都是低估，
    兩百個凹槽累積起來就是場景 1 那 4.5% 的偏差。
    改成逐段解析積分之後，不管凹槽多窄都是精確值。
    """
    if b <= a or not kfs:
        return None if not kfs else (b - a)
    total = 0.0
    p = a

    # 第一格**之前**的那一段。
    #
    # Timeline 在第一格之前是維持第一格的值（ts_value 上面就是這樣寫的），
    # 但這個迴圈只走「相鄰兩格之間」，所以 a 早於第一格時那一截完全沒被算進去，
    # 等於當成「時間軸在那裡不走」——積出來的 τ 憑空少掉一大塊。
    #
    # 實測踩到：Scenecard 的 timeScale 第一格在 5.841 秒
    #（作者就是從那裡開始放的，值全程都是 1.0）。段落從 0 秒開始，
    # 於是 0~5.841 這 5.8 秒被當成 0，校正倍率被推成 1.6149，
    # 往回外推出來的段落起點變成影片 3.693 秒 —— 實際量到的是 5.148。
    # 開場過場的長度就是這樣被寫錯 1.455 秒的。
    #
    # 尾巴那一截本來就有處理（迴圈後面的 `if p < b`），只有頭沒有。
    t_first = kfs[0][0]
    if a < t_first:
        hi = min(b, t_first)
        v = kfs[0][1]
        if floor > 0.0:
            v = max(v, floor)
        if v <= 1e-6:
            return None
        total += (hi - a) / v
        p = hi
        if p >= b - 1e-9:
            return total

    for i in range(len(kfs) - 1):
        t0, v0 = kfs[i]
        t1, v1 = kfs[i + 1]
        if t1 <= p:
            continue
        if t0 >= b:
            break
        lo = max(p, t0)
        hi = min(b, t1)
        if hi <= lo:
            continue
        span = t1 - t0
        if span < 1e-9:
            continue
        # 這一小段兩端的值（可能只取中間一截）
        a0 = v0 + (v1 - v0) * (lo - t0) / span
        a1 = v0 + (v1 - v0) * (hi - t0) / span
        if floor > 0.0:
            # 夾住下限：timeScale 掉到 0 的那一瞬間，∫dp/v 在數學上是發散的，
            # 但那個發散只發生在「無限薄的一個點」上 —— 實際播放時時間軸在那裡
            # 停住，對「時間軸秒 → 音訊秒」這個對應關係其實沒有貢獻。
            #
            # 夾一個下限之後積分變成有限值，形狀大致還在，而後面的 _calibrate
            # 會拿量到的點把尺度整條校正回來 —— 積分本來就只負責形狀，不負責尺度。
            # 所以夾住的誤差大部分會被校正吸收掉。
            a0 = max(a0, floor)
            a1 = max(a1, floor)
        if a0 <= 1e-6 or a1 <= 1e-6:
            return None                      # timeScale 掉到 0 或負的，積分發散
        if abs(a1 - a0) < 1e-9:
            total += (hi - lo) / a0
        else:
            total += (hi - lo) / (a1 - a0) * math.log(a1 / a0)
        p = hi
        if p >= b - 1e-9:
            return total
    # 超出最後一個關鍵影格的部分，值是定值
    if p < b:
        vlast = kfs[-1][1] if p >= kfs[-1][0] else ts_value(kfs, p)
        if floor > 0.0:
            vlast = max(vlast, floor)
        if vlast <= 1e-6:
            return None
        total += (b - p) / vlast
    return total


def sample_times(kfs, a, b, max_gap=5.0):
    """段落內的採樣時間點：所有關鍵影格 + 補到間隔不超過 max_gap。"""
    pts = {round(a, 4), round(b, 4)}
    for t, _ in kfs:
        if a < t < b:
            pts.add(round(t, 4))
    xs = sorted(pts)
    out = []
    for i, x in enumerate(xs):
        out.append(x)
        if i + 1 < len(xs):
            gap = xs[i + 1] - x
            if gap > max_gap:
                k = int(math.ceil(gap / max_gap))
                for j in range(1, k):
                    out.append(round(x + gap * j / k, 4))
    return sorted(set(out))


def _grid(kfs, a, b, max_gap=5.0):
    """段落內的採樣點：**每一個關鍵影格都要留**，再補到間隔不超過 max_gap。

    關鍵影格不能丟。timeScale 的凹槽底部就落在關鍵影格上
    （1.0 → 0.4 → 1.0 只有 0.16 秒寬），丟掉一個底部就等於把那個凹槽填平，
    折線內插會直接少算那一塊影片時間。
    """
    pts = {round(a, 4), round(b, 4)}
    for t, _ in kfs:
        if a < t < b:
            pts.add(round(t, 4))
    xs = sorted(pts)
    out = []
    for i, x in enumerate(xs):
        out.append(x)
        if i + 1 < len(xs):
            gap = xs[i + 1] - x
            if gap > max_gap:
                k = int(math.ceil(gap / max_gap))
                for j in range(1, k):
                    out.append(round(x + gap * j / k, 4))
    return sorted(set(out))


def merge_close_pins(pins, min_span=10.0):
    """把靠得太近的對應點合併成一個。

    兩個點相隔 2 秒時，它們算出來的局部倍率是 (量測誤差 ±0.1 秒) / 2 秒 ≈ ±5%，
    幾乎全是雜訊。這種點放在一起當兩個校正區間，只會在那 2 秒裡製造一個
    假的速率突變。實務上這種成對的點是「互相驗證」用的，不是兩個獨立的錨點，
    合併成平均值才是它們真正的用途。
    """
    ps = sorted(pins)
    out, grp = [], [ps[0]]
    for p in ps[1:]:
        if p[0] - grp[0][0] <= min_span:
            grp.append(p)
        else:
            out.append(grp); grp = [p]
    out.append(grp)
    merged = []
    for g in out:
        if len(g) == 1:
            merged.append(g[0])
        else:
            merged.append((sum(x[0] for x in g) / len(g),
                           sum(x[1] for x in g) / len(g)))
    return merged


def _calibrate(tau_at, pins):
    """把積分曲線校正成「形狀照積分、尺度照量測」。

    為什麼需要：積分只描述卡片裡的 timeScale 軌道，但作者匯出影片之後
    通常還在剪輯軟體裡動過速度／停格／重複段，那些時間卡片裡根本沒有。
    實測 Charcard 場景 1 因此差了 4.5%（到 timeline 211 秒累積 8.3 秒），
    場景 2 則幾乎沒差 —— 差多少完全看作者後製了多少，沒有模型推得出來。

    所以：一個點只能平移（尺度無從得知），兩個點以上就逐區間做仿射修正 ——
    每個區間維持積分算出來的形狀（慢動作尖峰都保留），只把它拉伸平移到
    剛好通過兩端量到的點。首尾區間之外沿用最靠近的倍率外推。
    """
    ps = merge_close_pins(pins)
    if len(ps) == 1:
        t0, v0 = ps[0]
        off = v0 - tau_at(t0)
        g = lambda t: tau_at(t) + off
        g.factors = []
        return g

    segs = []
    for (t0, v0), (t1, v1) in zip(ps, ps[1:]):
        i0, i1 = tau_at(t0), tau_at(t1)
        di = i1 - i0
        b = (v1 - v0) / di if abs(di) > 1e-9 else 1.0
        segs.append((t0, t1, v0, i0, b))

    def f(t):
        for t0, t1, v0, i0, b in segs:
            if t <= t1:
                return v0 + b * (tau_at(t) - i0)
        t0, t1, v0, i0, b = segs[-1]
        return v0 + b * (tau_at(t) - i0)
    f.factors = [(t0, t1, b) for t0, t1, _, _, b in segs]
    return f


def auto_anchors(kfs, seg, pins, max_gap=5.0, floor=0.0):
    """用積分算出整段的對應曲線形狀，再用量到的點校正尺度，採樣成密集 anchors。

    回傳 (anchors, 留一驗證報告)。

    ※ 積分一定要「逐段累加」，不能每個點都從段首重積一次。
      每次重積用的是各自的網格，誤差彼此獨立，做出來的折線會倒退，
      執行時 TrackPlayer 就會在那裡左右亂跳（實測 ±1.5 秒）。
      逐段累加的話 τ 是 1/timeScale(>0) 的累積和，天生單調。
    """
    a, b = seg["start"], seg["end"]

    xs = _grid(kfs, a, b, max_gap)
    cum = [0.0]                                  # cum[i] = τ(a → xs[i])
    for x0, x1 in zip(xs, xs[1:]):
        d = tau(kfs, x0, x1, floor=floor)
        if d is None or d <= 0.0:
            return None, [], []
        cum.append(cum[-1] + d)

    def tau_at(t):
        """用同一條累積曲線內插，不另外重積，才不會跟 anchors 打架。"""
        if t <= xs[0]:
            return cum[0]
        for i in range(len(xs) - 1):
            if t <= xs[i + 1]:
                f = (t - xs[i]) / (xs[i + 1] - xs[i])
                return cum[i] + (cum[i + 1] - cum[i]) * f
        return cum[-1]

    ps = sorted(pins)
    if not ps:
        return None, [], []

    cal = _calibrate(tau_at, ps)
    anchors = sanitize_anchors([[round(x, 3), round(cal(x), 3)] for x in xs])
    factors = list(getattr(cal, "factors", []))

    # 留一驗證：拿掉一個點、用其餘的點校正，再看那個點預測得準不準。
    # 校正後的曲線必然精準通過每個點，直接看殘差是零，量不出東西；
    # 留一才是真的在問「這條曲線對沒看過的地方準不準」。
    loo = []
    for i, (t, v) in enumerate(ps):
        rest = ps[:i] + ps[i + 1:]
        if not rest:
            loo.append((t, v, None))
            continue
        loo.append((t, v, _calibrate(tau_at, rest)(t) - v))
    return anchors, loo, factors


def sanitize_anchors(anchors, hi=50.0):
    """把折線壓成嚴格遞增。

    對應關係是「時間軸秒 → 音訊秒」，倒退在物理上不可能。
    但**陡**是可能的：斜率 = 該處的 1/timeScale，卡片把時間流速調到 0.03
    （近乎定格）時斜率就是 33，那是真的，不能砍 —— 砍掉等於把那段
    影片時間憑空抹掉，後面全部會提早，聽起來就是「語音落後畫面」。
    所以這裡只擋倒退和重複點，不擋陡。
    """
    out = []
    for t, v in anchors:
        if not out:
            out.append([t, v]); continue
        dt = t - out[-1][0]
        if dt <= 1e-6:
            continue
        sl = (v - out[-1][1]) / dt
        if sl < 0.0 or sl > hi:
            continue
        out.append([t, v])
    return out


# ---------------------------------------------------------------- 對應

def slope(a, b):
    dt = b[0] - a[0]
    return 1.0 if abs(dt) < 1e-9 else (b[1] - a[1]) / dt


def map_at(anchors, t):
    """折線內插 / 外推。anchors = [(timeline, video), …]，已排序。"""
    n = len(anchors)
    if n == 0:
        return None
    if n == 1:
        return anchors[0][1] + (t - anchors[0][0])
    if t <= anchors[0][0]:
        return anchors[0][1] + (t - anchors[0][0]) * slope(anchors[0], anchors[1])
    for i in range(n - 1):
        if t <= anchors[i + 1][0]:
            return anchors[i][1] + (t - anchors[i][0]) * slope(anchors[i], anchors[i + 1])
    return anchors[-1][1] + (t - anchors[-1][0]) * slope(anchors[-2], anchors[-1])


def residual(anchors):
    """三個點以上才有意義：中間各點離「頭尾連線」多遠。
    兩個點的話直線本來就穿過它們，殘差恆為 0，量不出東西。"""
    if len(anchors) < 3:
        return None
    a0, a1 = anchors[0], anchors[-1]
    sp = slope(a0, a1)
    worst = 0.0
    for t, v in anchors[1:-1]:
        worst = max(worst, abs(v - (a0[1] + (t - a0[0]) * sp)))
    return worst


def build_tracks(segs, byseg, audio="@"):
    tracks, report = [], []
    for s in segs:
        raw = sorted(byseg.get(s["index"], []))
        # sanitize_anchors 會默默丟掉「往回走」或斜率大到離譜的點（多半是量錯 ——
        # 秒數抄錯、或對到重複鏡頭的另一次出現）。
        #
        # 原本報告是拿沒過濾的 raw 去算 n / v0 / v1 / slope / residual，
        # 寫進 json 的卻是過濾後的 an。兩個點的段落被丟掉一個之後，
        # json 裡只剩一個錨點（插件會當成等速 1.0），報告卻還顯示「對應點 2」、
        # 一個看起來合理的速率，而且「只有 1 點」的警告也不會跳 ——
        # 最後印出「沒發現問題」，實際上差好幾秒。報告必須看過濾後的結果。
        an = sanitize_anchors([[round(a, 3), round(b, 3)] for a, b in raw])
        tr = dict(**{"from": round(s["start"], 3), "to": round(s["end"], 3)},
                  audio=audio, volume=1.0, mute=False, anchors=an)
        tracks.append(tr)
        v0 = map_at(an, s["start"]) if an else None
        v1 = map_at(an, s["end"]) if an else None
        sp = (v1 - v0) / (s["end"] - s["start"]) if (v0 is not None and s["end"] > s["start"]) else None
        report.append(dict(seg=s, n=len(an), v0=v0, v1=v1, slope=sp,
                           res=residual(an), dropped=len(raw) - len(an)))
    return tracks, report


def build_tracks_auto(segs, byseg, kfs, max_gap=5.0, audio="@"):
    """曲線由 timeScale 積分決定，量到的點只負責釘住位移。"""
    tracks, report = [], []
    for s in segs:
        pins = sorted(byseg.get(s["index"], []))
        an, spread, factors = auto_anchors(kfs, s, pins, max_gap)
        note = ""
        if an is None:
            # 積分發散了 —— 這一段裡 timeScale 掉到 0（多半是中間插了過場，
            # 時間軸被凍住）。**不能就這樣寫 anchors=[] 然後 continue**：
            # 播放端沒有對應點又沒有 offset 時會退回 offset = 0，
            # 整段音檔從 0 秒開始播。前後段都正常、只有中間對不上，
            # 那種症狀看起來完全不像設定檔的問題，極難追。
            #
            # 退而求其次，照可信度由高到低試：
            #   1. 夾住 timeScale 下限再積一次 —— 形狀大致保住，尺度由校正修正
            #   2. 還是不行就用量到的點拉直線 —— 至少頭尾是對的
            an, spread, factors = auto_anchors(kfs, s, pins, max_gap, floor=TS_FLOOR)
            if an is not None:
                note = f"timeScale 有凍結，已夾住下限 {TS_FLOOR} 重算"
                print(f"  [注意] 第 {s['index']} 段：{note}。"
                      f"形狀是近似的，建議在這一段多補幾個對應點驗證。")
            else:
                fb = sanitize_anchors([[round(t, 3), round(v, 3)] for t, v in sorted(pins)])
                if len(fb) < 2:
                    print(f"  [警告] 第 {s['index']} 段算不出對應曲線，而且量到的點只有 "
                          f"{len(fb)} 個 —— 這一段的音訊會對不上，請補量對應點。")
                else:
                    print(f"  [警告] 第 {s['index']} 段算不出對應曲線，"
                          f"改用量到的 {len(fb)} 個點拉直線。準確度會比其他段差。")
                tracks.append(dict(**{"from": round(s["start"], 3), "to": round(s["end"], 3)},
                                   audio=audio, volume=1.0, mute=False, anchors=fb))
                report.append(dict(seg=s, n=len(fb), v0=None, v1=None, slope=None,
                                   res=None, pins=len(pins), spread=None, factors=[],
                                   note="直線退路"))
                continue
        tracks.append(dict(**{"from": round(s["start"], 3), "to": round(s["end"], 3)},
                           audio=audio, volume=1.0, mute=False, anchors=an))
        v0, v1 = an[0][1], an[-1][1]
        sp = (v1 - v0) / (s["end"] - s["start"]) if s["end"] > s["start"] else None
        worst = max((abs(d) for _, _, d in spread if d is not None), default=0.0)
        report.append(dict(seg=s, n=len(an), v0=v0, v1=v1, slope=sp,
                           res=None, pins=len(pins), spread=spread, worst=worst,
                           factors=factors))
    return tracks, report


def derive_cuts(report, video_duration=None):
    """由相鄰段落往邊界外推，算出開場 / 過場 / 片尾的影片區間。"""
    cuts, prev_end, prev_i = [], None, None
    for i, r in enumerate(report):
        if r["v0"] is None:
            # 這一段沒有任何對應點，整段跳過。
            #
            # 原本這裡寫的是「prev_end = r['v1']; continue」，但 v0 是 None 的時候
            # v1 一定也是 None，等於把 prev_end 清成 None —— 下一個有對應點的段落
            # 就會被當成開場，多生出一段 t=0 的 opening 疊在真正的開場上面。
            # 保持 prev_end 和 prev_i 不動，讓它跟前一個有對應點的段落接成過場才對。
            continue
        if prev_end is None:                       # 開場
            if r["v0"] > 0.05:
                cuts.append(dict(t=0.0, kind="opening",
                                 videoStart=0.0, videoEnd=round(r["v0"], 3),
                                 audioStart=0.0))
        else:                                       # 段與段之間
            jump = r["v0"] - prev_end                       # 影片／音訊吃掉多少
            # 用 prev_i 不用 i-1：中間可能夾著沒有對應點、被跳過的段落
            gap = r["seg"]["start"] - report[prev_i]["seg"]["end"]   # 時間軸走了多少
            if jump > 0.05:
                # 過場的定義是「時間軸停住、影片自己播」。所以影片吃掉的時間
                # 必須遠多於時間軸走掉的時間 —— 真正的過場長這樣：
                # 時間軸 0.01 s、影片 5.46 s，比值幾百倍。
                #
                # 中間夾著一段沒算出對應點的段落時，這裡看到的是「音訊跳了 82 s」，
                # 但時間軸同時也走了 82 s。那不是過場，那是那一段沒被算進來。
                # 照樣生過場的話，會在段落正中間插一段 80 秒的影片
                # （Charcard 的 t=149.01 就是這樣來的），比沒有還糟。
                if gap > 1.0 and jump <= gap * 3.0:
                    print("  [不生過場] 時間軸 %.2f–%.2f：影片跳了 %.2f s，"
                          "但時間軸也走了 %.2f s —— 這是中間有段落沒算出對應點，"
                          "不是過場"
                          % (report[prev_i]["seg"]["end"], r["seg"]["start"], jump, gap))
                else:
                    bnd = (report[prev_i]["seg"]["end"] + r["seg"]["start"]) / 2.0
                    cuts.append(dict(t=round(bnd, 3), kind="transition",
                                     videoStart=round(prev_end, 3),
                                     videoEnd=round(r["v0"], 3), audioStart=-1.0))
        prev_end = r["v1"]
        prev_i = i

    if video_duration and prev_end is not None and video_duration - prev_end > 0.05:
        cuts.append(dict(t=round(report[prev_i]["seg"]["end"], 3), kind="ending",
                         videoStart=round(prev_end, 3),
                         videoEnd=round(float(video_duration), 3), audioStart=-1.0))

    for c in cuts:
        c.update(video="", audio="", useTrackAudio=True,
                 skip=False, maxSeconds=0, fadeIn=0.0, fadeOut=0.0)
    return cuts


def build_config(segs, pairs, video_file="", video_duration=None,
                 variants=None, active=None, extra=None,
                 ts_kfs=None, max_gap=5.0, ref_variant=None, variant_maps=None):
    byseg, orphan = assign_pairs(segs, pairs)
    if ts_kfs:
        tracks, report = build_tracks_auto(segs, byseg, ts_kfs, max_gap)
    else:
        tracks, report = build_tracks(segs, byseg)
    cuts = derive_cuts(report, video_duration)

    names, files = [], []
    for v in (variants or []):
        nm, _, fp = v.partition("=")
        if not fp:
            fp, nm = nm, os.path.splitext(os.path.basename(nm))[0]
        names.append(nm); files.append(fp.replace("\\", "/"))

    cfg = dict(version=1, enabled=True)
    cfg.update(DEFAULTS)
    cfg["videoFile"] = (video_file or "").replace("\\", "/")
    cfg["variantNames"] = names
    cfg["variantFiles"] = files
    # activeVariant 指到一個不在清單裡的名字時，插件面板上沒有對應的按鈕，
    # 結果就是「有設定、但沒有聲音」，而且什麼都不會報。最常見的成因是
    # 某個 wav 被搬走、沒有進到這次的 --variant 裡。
    if active and names and active not in names:
        print("  [注意] activeVariant「%s」不在配音清單裡，改用「%s」" % (active, names[0]))
        active = names[0]
    cfg["activeVariant"] = active or (names[0] if names else "")

    # --- 配音對照 ---
    # anchors 和 cuts 的秒數是以「主配音」為準的。其他版本剪輯一樣的話
    # 什麼都不用做（這是常態）；剪輯不同的才需要一張對照表。
    # 沒有對照表時插件當成同步，也就是這個欄位出現之前的行為，舊卡不受影響。
    ref = ref_variant if (ref_variant and ref_variant in names) else (names[0] if names else "")
    if ref_variant and names and ref_variant not in names:
        print("  [注意] 主配音「%s」不在配音清單裡，改用「%s」" % (ref_variant, names[0]))
    cfg["refVariant"] = ref
    vmaps = {}
    for k, v in (variant_maps or {}).items():
        if names and k not in names:
            print("  [注意] 配音對照裡的「%s」不在配音清單裡，這張對照表沒有寫進去" % k)
            continue
        if k == ref:
            continue                     # 主配音對自己的對照表是恆等，不用寫
        pts = [[round(float(x), 3), round(float(y), 3)] for x, y in (v or [])]
        if pts:
            vmaps[k] = pts
    cfg["variantMaps"] = vmaps

    cfg["tracks"] = tracks
    cfg["cuts"] = cuts
    if extra:
        cfg.update(extra)
    return cfg, report, orphan


# ---------------------------------------------------------------- 長度一致就自動設定對應點

# 「大致一致」的容許範圍：差在總長的 5% 以內，最少給 1.5 秒、最多 6 秒。
# 實際的卡：60 秒的場景配 61.78 秒的音檔（+3.0%）、58.17 秒配 56.30 秒（-3.2%）都算一致；
# 430 秒的場景配 474 秒的音檔（多了開場和過場）不算，那種要量對應點。
AUTO_TOL_REL = 0.05
AUTO_TOL_MIN = 1.5
AUTO_TOL_MAX = 6.0


def auto_tol(total):
    return min(AUTO_TOL_MAX, max(AUTO_TOL_MIN, float(total) * AUTO_TOL_REL))


def real_seconds(kfs, t):
    """timeline 的第 t 秒，實際播到那裡要花幾秒（有時間流速軌道時兩者不一樣）。"""
    if not kfs or t <= 0:
        return max(0.0, float(t))
    v = tau(kfs, 0.0, float(t))
    if v is None:
        v = tau(kfs, 0.0, float(t), floor=TS_FLOOR)
    return float(t) if v is None else float(v)


def pairs_by_length(segs, kfs, media, force=False):
    """音檔（或影片）的長度跟場景卡大致一樣長的時候，直接算出對應點：頭對頭、尾對尾。

    這種音檔是照著場景從頭播到尾的（沒有開場動畫、沒有過場）。整個音檔對到整段場景，
    差的那一點點平均攤在整段上（跟「從切好的音頻反推」量出來的結果是同一種形狀）。
    有時間流速軌道的卡先把 timeline 秒數換成實際播放的秒數再對。每一段給頭尾兩個點。

    media＝[(路徑, 長度秒 或 None)]。force＝不管差多少，拿最接近的那個檔硬套。
    回傳 (pairs, info)：pairs＝[(timeline, 音訊秒, 說明)]，對不上時是 []；
    info：total_tl / total_real / file / dur / diff / tol / mode / media，給介面說明用。
    """
    info = {"total_tl": None, "total_real": None, "file": "", "dur": None,
            "diff": None, "tol": None, "mode": "", "media": list(media)}
    if not segs:
        return [], info
    total_tl = max(float(s["end"]) for s in segs)
    total_real = real_seconds(kfs, total_tl)
    info["total_tl"], info["total_real"] = total_tl, total_real
    info["tol"] = auto_tol(total_real)
    modes = [("real", total_real)]
    if abs(total_real - total_tl) > 0.05:
        modes.append(("timeline", total_tl))
    best = None
    for mode, total in modes:
        if total <= 0:
            continue
        for path, d in media:
            if not d:
                continue
            diff = float(d) - total
            if best is None or abs(diff) < abs(best[2]):
                best = (path, float(d), diff, auto_tol(total), mode, total)
    if not best:
        return [], info
    path, d, diff, tol, mode, total = best
    info.update(file=path, dur=d, diff=diff, tol=tol, mode=mode)
    if abs(diff) > tol and not force:
        return [], info
    k = d / total
    conv = (lambda t: real_seconds(kfs, t) * k) if mode == "real" else (lambda t: float(t) * k)
    tag = "長度一致，自動設定" if abs(diff) <= tol else "頭尾硬套"
    pairs = []
    for s in sorted(segs, key=lambda x: x["start"]):
        t0, t1 = float(s["start"]), float(s["end"])
        a, b = conv(t0), min(conv(t1), d)
        if t1 - t0 < 0.05 or b - a < 0.05:
            continue
        nm = s.get("name") or ("場景%d" % s.get("index", 0))
        pairs.append((round(t0, 3), round(a, 3), "%s 頭（%s）" % (nm, tag)))
        pairs.append((round(t1, 3), round(b, 3), "%s 尾（%s）" % (nm, tag)))
    return pairs, info


def media_duration(path):
    """影片或音訊的長度（秒）。讀不到回傳 None。"""
    try:
        import subprocess
        r = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration",
                            "-of", "default=nw=1:nk=1", path],
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        return float(r.stdout.decode().strip())
    except Exception:                                       # noqa: BLE001
        pass
    try:
        import wave
        with wave.open(path, "rb") as w:
            return w.getnframes() / float(w.getframerate())
    except Exception:                                       # noqa: BLE001
        return None


_AUTO_CACHE = {}


def auto_pairs_for_card(card, files, force=False):
    """讀卡片的段落和時間流速，量每個檔案的長度，回傳 pairs_by_length 的結果。

    卡片的段落會記住（同一張卡、檔案沒變就不重讀）——大卡讀一次要好幾秒。
    """
    key = os.path.normcase(os.path.abspath(card))
    try:
        stamp = (os.path.getmtime(card), os.path.getsize(card))
    except OSError:
        stamp = None
    hit = _AUTO_CACHE.get(key)
    if hit and hit[0] == stamp:
        segs, kfs = hit[1], hit[2]
    else:
        class _A:
            pass
        shim = _A()
        shim.segments, shim.card = None, card
        segs, scene = load_segments(shim, want_scene=True)
        kfs = read_timescale(scene) if scene is not None else []
        del scene
        _AUTO_CACHE.clear()
        _AUTO_CACHE[key] = (stamp, segs, kfs)
    media = [(f, media_duration(f)) for f in files]
    return pairs_by_length(segs, kfs, media, force=force)


# ---------------------------------------------------------------- 輸出

def print_report(segs, report, cuts, orphan, video_duration=None):
    print("%-4s %-22s %-19s %-21s %8s %6s  %s" %
          ("段", "名稱", "timeline", "→ 影片", "速率", "殘差", "對應點"))
    print("-" * 104)
    problems = []
    suggestions = []
    for r in report:
        s = r["seg"]
        rng = "%.2f–%.2f" % (s["start"], s["end"])
        if r["v0"] is None:
            print("%-4d %-22s %-19s %-21s %8s %6s  %s" %
                  (s["index"], s["name"][:22], rng, "（沒有對應點）", "-", "-", 0))
            problems.append("第 %d 段沒有任何對應點" % s["index"])
            continue
        res = ("%6.3f" % r["res"]) if r["res"] is not None else "   -  "
        note = "" if r["n"] >= 2 else "  ← 只有 1 點，速率被當成 1.0"
        print("%-4d %-22s %-19s %-21s %8.5f %s  %d%s" %
              (s["index"], s["name"][:22], rng,
               "%.3f–%.3f" % (r["v0"], r["v1"]), r["slope"] or 0, res, r["n"], note))
        if r["slope"] and not (0.5 <= r["slope"] <= 2.0):
            problems.append("第 %d 段速率 %.4f 不合理（正常在 0.8~1.3），八成有一個點抓錯段"
                            % (s["index"], r["slope"]))
        if r["res"] is not None and r["res"] > 0.5:
            problems.append("第 %d 段中間的對應點偏離頭尾連線 %.2f 秒，"
                            "代表那段速率不是定值（timeScale 有起伏），可以多補幾個點"
                            % (s["index"], r["res"]))
        if r.get("dropped"):
            problems.append("第 %d 段有 %d 個對應點被過濾掉了（時間往回走、或斜率離譜）——"
                            "幾乎都是秒數抄錯、或對到重複鏡頭的另一次出現。"
                            "剩下 %d 個點，%s"
                            % (s["index"], r["dropped"], r["n"],
                               "整段會被當成等速 1.0" if r["n"] < 2 else "回頭檢查那幾個點"))
        if r.get("pins") == 1:
            problems.append("第 %d 段只有 1 個對應點 —— 只能平移、無法校正尺度。"
                            "積分形狀對不對完全沒人驗證，實測有過 8 秒等級的偏差，"
                            "務必補第二個點（抓在這一段的另一端）" % (s["index"]))

    for r in report:
        sp = r.get("spread")
        if not sp:
            continue
        w = r.get("worst", 0.0)
        # 只有兩個點的話，拿掉一個就只剩一個點 → 只能平移、沒有尺度可言，
        # 於是「誤差」必然等於整段尺度修正量的一半，跟準不準無關。
        # 這種情況要講清楚，不然會把「倍率 0.995 的乾淨段落」誤報成有問題。
        degenerate = len(sp) < 3
        tag = ("（只有兩個點，這個數字沒有判斷力）" if degenerate
               else ("準" if w <= 0.30 else "**不夠準**"))
        print("\n  段 %d 留一驗證（拿掉該點、用其餘的點校正，再預測它）%s:"
              % (r["seg"]["index"], tag))
        for t, v, d in sp:
            if d is None:
                print("     timeline %9.3f → 影片 %9.3f   （只有這一個點，無法驗證）" % (t, v))
            else:
                print("     timeline %9.3f → 影片 %9.3f   預測誤差 %+7.3f 秒" % (t, v, d))
        if degenerate:
            print("     （兩個點時，拿掉一個就只剩平移，誤差必然等於整段尺度修正量的一半，"
                  "不代表量得不準。要真的驗證請補第三個點。）")
        elif w > 0.30:
            problems.append("段 %d 的留一誤差達 %.2f 秒 —— 不是量錯就是那一帶的速率變化沒被"
                            "涵蓋，在誤差最大的點附近再補一個對應點"
                            % (r["seg"]["index"], w))

        fa = r.get("factors") or []
        if fa:
            print("\n  段 %d 各區間的修正倍率（積分 → 實際影片）:" % r["seg"]["index"])
            for t0, t1, b in fa:
                mark = ""
                if b > 1.25 or b < 0.80:
                    mark = "   ← 偏離 1 很多"
                print("     %s ~ %s   長 %6.1f s   倍率 %7.4f%s"
                      % (fmt_time(t0), fmt_time(t1), t1 - t0, b, mark))

            # 重複鏡頭抓錯的特徵：一個遠大於 1 的倍率，緊接著一個遠小於 1 的
            for i in range(len(fa) - 1):
                b0, b1 = fa[i][2], fa[i + 1][2]
                if (b0 > 1.25 and b1 < 0.90) or (b0 < 0.80 and b1 > 1.15):
                    problems.append(
                        "段 %d：timeline %s 前後的倍率一高一低（%.3f → %.3f）。"
                        "這是「那個點對到重複鏡頭／重複台詞的另一次出現」的典型特徵 —— "
                        "作者這種片子常把同一句話或同一個鏡頭放兩次。"
                        "在 %s 和 %s 之間再量一個點就能判定：真的有慢動作的話，"
                        "新點會落在同一條線上；對錯出現的話，新點會跟它打架"
                        % (r["seg"]["index"], fmt_time(fa[i][1]), b0, b1,
                           fmt_time(fa[i][0]), fmt_time(fa[i][1])))

            # 下一個該去量哪裡：區間越長、且倍率跟鄰居差越多，風險越高。
            # （試過用 timeScale 關鍵影格密度當指標，實測不準 —— Charcard 出問題的
            #   那一段密度反而比正常的那一段低，所以不要用密度。）
            risk = []
            for i, (t0, t1, b) in enumerate(fa):
                nb = [fa[j][2] for j in (i - 1, i + 1) if 0 <= j < len(fa)]
                jump = max([abs(b - x) for x in nb] or [0.0])
                risk.append((( t1 - t0) * (1.0 + 8.0 * jump), t0, t1, b, jump))
            risk.sort(reverse=True)
            for sc, t0, t1, b, jump in risk:
                if t1 - t0 > 20.0:
                    suggestions.append((sc, r["seg"]["index"], t0, t1, b, jump))

    if orphan:
        print("\n落在所有段落之外的對應點（已忽略）:")
        for t, v in orphan:
            print("  timeline %s  →  影片 %s" % (fmt_time(t), fmt_time(v)))
        problems.append("有 %d 個對應點落在段落之外，檢查是不是抄錯 timeline 秒數" % len(orphan))

    print("\n影片中「場景卡沒有」的片段:")
    tot = 0.0
    for c in cuts:
        d = c["videoEnd"] - c["videoStart"]
        tot += d
        print("  %-11s 影片 %8.3f – %-8.3f  長 %6.3fs   觸發於 timeline %.3f"
              % (c["kind"], c["videoStart"], c["videoEnd"], d, c["t"]))
    if not cuts:
        print("  （沒有）")

    # --- 合理性檢查 ---
    # 注意：不能用「影片總長 − 場景總長」當誤差，那暗設速率 = 1。
    # 速率一偏離 1，那個差值就完全沒有意義。
    prev = None
    for r in report:
        if r["v0"] is None:
            continue
        if prev is not None and r["v0"] < prev - 0.001:
            problems.append("第 %d 段的起點(%.3f)比前一段的終點(%.3f)還早 —— "
                            "影片時間倒退了，一定有點抓錯"
                            % (r["seg"]["index"], r["v0"], prev))
        prev = r["v1"]

    first = next((r for r in report if r["v0"] is not None), None)
    last = next((r for r in reversed(report) if r["v1"] is not None), None)
    if first and first["v0"] < -0.001:
        problems.append("第一段對應到影片 %.3f 秒（負的）—— 開頭那個點抓錯了" % first["v0"])
    if video_duration and last and last["v1"] > float(video_duration) + 0.001:
        problems.append("最後一段對應到影片 %.3f 秒，超過影片總長 %.3f"
                        % (last["v1"], float(video_duration)))

    if video_duration and first and last:
        cover = sum((r["v1"] - r["v0"]) for r in report if r["v0"] is not None)
        print("\n  場景佔掉影片 %.2fs，過場佔 %.2fs，合計 %.2fs / 影片總長 %.2fs"
              % (cover, tot, cover + tot, float(video_duration)))

    print()
    if problems:
        print("檢查結果：")
        for m in problems:
            print("  ! " + m)
    else:
        print("檢查結果：沒發現問題。")
        if all((r["res"] is None) for r in report):
            print("  （每段只有兩個點時，直線必然穿過那兩點，沒有獨立的準確度可以量。")
            print("    想要真的驗證，在某一段中間多抓第三個點，殘差就會出現在上表。）")

    # 建議放到最後 —— 這是唯一需要「你現在去做」的東西，前面都是結果報告。
    # 介面會把這一段標紅。
    if suggestions:
        suggestions.sort(reverse=True)
        print("\n>>> 若不同步，建議測量位置如下（風險高的排前面）:")
        for sc, seg, t0, t1, b, jump in suggestions[:5]:
            print(">>>   段 %d  %s   （夾在 %s ~ %s，長 %.0f s，倍率 %.4f，跟鄰居差 %.3f）"
                  % (seg, fmt_time((t0 + t1) / 2.0), fmt_time(t0), fmt_time(t1),
                     t1 - t0, b, jump))


def suggest_points(span):
    """段落越長，timeScale 起伏造成的中段誤差越大，要多給幾個點。
    兩個點只能畫直線，頭尾一定準、中間看運氣。"""
    n = int(round(span / 90.0)) + 1
    return max(2, min(6, n))


def cmd_points(args):
    segs = load_segments(args)
    print("在遊戲裡用面板的「複製目前時間」抄 timeline 秒數，影片那邊看播放器時間碼。")
    print("點數依段長自動建議：兩個點只能畫一條直線，長段的中間會偏，要多給幾個。\n")
    total = 0
    lines = []
    for s in segs:
        span = s["end"] - s["start"]
        n = suggest_points(span)
        total += n
        pad = max(1.0, span * 0.10)
        inner = span - 2 * pad
        pts = [s["start"] + pad + inner * i / (n - 1) for i in range(n)]
        lines.append((s, n, pts))
        print("段 %d  %s   %.1f 秒   建議 %d 個點"
              % (s["index"], s["name"][:26], span, n))
        print("      " + "  ".join(fmt_time(p) for p in pts))
    print("\n共 %d 段 → %d 個點。" % (len(segs), total))
    print("不用精確踩在這些秒數上，找附近好認的瞬間（換鏡頭、某句台詞開頭）即可。")
    print("避開場景交界前後幾秒，容易抓錯段。")
    print("頭尾那兩個點決定整段的斜率，拉越遠越準；中間的點負責把彎曲的部分拉平。")

    print("\npairs.txt（放在卡片旁邊，命名 <卡片同名>.pairs.txt 或 pairs.txt）:")
    print("# timeline        影片")
    for s, n, pts in lines:
        for p in pts:
            print("%-17s 00:00:00.000      # 段%d" % (fmt_time(p), s["index"]))


def guess_pairs(args):
    if args.pairs:
        return args.pairs
    if not args.card:
        raise SystemExit("沒給 --pairs，而且也沒給卡片路徑可以推斷")
    d = os.path.dirname(os.path.abspath(args.card))
    stem = os.path.splitext(os.path.basename(args.card))[0]
    named = os.path.join(d, stem + ".pairs.txt")
    if os.path.exists(named):
        return named
    # 共用的 pairs.txt 只是為了讓舊資料還能用。同一個資料夾放兩張卡時，
    # 第二張會默默吃到第一張的對應點 —— 不會報錯，只會整部不同步，
    # 所以這裡一定要吵一下。
    plain = os.path.join(d, "pairs.txt")
    if os.path.exists(plain):
        print("  [注意] 用的是共用的 %s —— 同資料夾的每張卡都會讀到它。" % plain)
        print("         建議改名成 %s，避免多張卡互相蓋掉。" % os.path.basename(named))
        return plain
    raise SystemExit("找不到對應點檔案。放一份在:\n  %s\n或用 --pairs 指定"
                     % os.path.join(d, stem + ".pairs.txt"))


def variant_name(path):
    """從檔名猜一個短名字。面板按鈕只有 96px，太長會被切掉。

    @ 後面那段最能代表配音者；沒有 @ 就退回 Version_xxx；再不行取尾巴。
    """
    stem = os.path.splitext(os.path.basename(path))[0]
    nm = ""
    if "@" in stem:
        nm = stem.rsplit("@", 1)[1]
        nm = re.sub(r"[_\s]*Version[_\s]*[IVXLC0-9]+\s*$", "", nm, flags=re.I)
        nm = re.sub(r"[_\s]*cast\s*$", "", nm, flags=re.I)
    if not nm:
        m = re.search(r"Version[_\s]*([IVXLC0-9]+)", stem, re.I)
        if m:
            nm = "Version_" + m.group(1)
    if not nm:
        nm = re.split(r"[_\s]+", stem)[-1]
    nm = nm.strip(" _-")
    nm = nm[:18] if nm else stem[:18]
    # "名稱=路徑" 是 --variant 的格式，用第一個 = 切開。
    # 名字裡自己帶 = 的話路徑會被切斷，json 就寫進一個不是路徑的字串，而且不會報錯。
    return nm.replace("=", "-")


def collect_variants(args):
    out = list(args.variant or [])
    if args.variant_dir:
        found = []
        for f in sorted(os.listdir(args.variant_dir)):
            if f.lower().endswith((".wav", ".ogg")):
                found.append(os.path.join(args.variant_dir, f))
        seen = {}
        for fp in found:
            nm = variant_name(fp)
            n, i = nm, 2
            while n in seen:
                n = "%s_%d" % (nm, i); i += 1
            seen[n] = fp
            out.append("%s=%s" % (n, fp))
        print("配音版本（來自 %s）:" % args.variant_dir)
        for n, fp in seen.items():
            print("  %-26s %s" % (n, os.path.basename(fp)))
        print()
    return out


def load_variant_maps(args):
    """取得 (主配音名稱, {配音名稱: 對照點})。

    來源有兩個，--variant-map 蓋過側檔：
      <卡片>.variants.json   平常用的，介面和 kkvariantmap 都寫這裡
      --variant-map 名稱=檔  一次性的覆寫
    兩個都沒有就回空的 —— 那代表「所有配音剪輯一樣」，也就是絕大多數的卡。
    """
    ref, maps = "", {}
    if not getattr(args, "no_variant_map", False) and getattr(args, "card", ""):
        try:
            import kkvariantmap as VM
            # 輸出資料夾也找一下 —— 對照表平常就住在那裡的 cutscene.json 裡
            extra = [os.path.dirname(os.path.abspath(args.out))] if getattr(args, "out", None) else []
            ref, maps, src = VM.load_maps(args.card, extra)
            if maps:
                print("配音對照: %s（%d 個版本）" % (src, len(maps)))
        except Exception as e:                              # noqa: BLE001
            print("  [注意] 讀不到配音對照側檔：%s" % e)
    for tok in (getattr(args, "variant_map", None) or []):
        name, _, path = str(tok).partition("=")
        name, path = name.strip(), path.strip()
        if not name or not path:
            print("  [注意] --variant-map 要寫成 名稱=檔案：%s" % tok)
            continue
        try:
            with open(path, encoding="utf-8-sig") as f:
                pts = json.load(f)
            maps[name] = [[float(x), float(y)] for x, y in pts]
            print("配音對照: %s ← %s（%d 點）" % (name, path, len(maps[name])))
        except Exception as e:                              # noqa: BLE001
            print("  [注意] 讀不到 %s：%s" % (path, e))
    if getattr(args, "ref_variant", None):
        ref = args.ref_variant
    return ref, maps


def cmd_plan(args):
    segs, scene = load_segments(args, want_scene=True)
    pairs_path = guess_pairs(args)
    print("對應點: %s\n" % pairs_path)
    pairs = read_pairs(pairs_path)
    variants = collect_variants(args)
    dur = args.video_duration
    if dur is None and args.video and os.path.exists(args.video):
        try:
            sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
            import kkcutscene_prep as P
            i = P.info(args.video)
            if i:
                dur = i["dur"]
        except Exception:
            pass

    ts_kfs = []
    if args.auto_slope:
        if scene is None:
            raise SystemExit("--auto-slope 需要卡片路徑（要從 timeline 讀 timeScale）")
        ts_kfs = read_timescale(scene)
        if not ts_kfs:
            print("!! 這張卡沒有 timeScale 軌道 —— 等速，--auto-slope 沒有作用\n")
            args.auto_slope = False
        else:
            vs = [v for _, v in ts_kfs]
            print("timeScale 軌道: %d 個關鍵影格，值 %.3f ~ %.3f\n"
                  % (len(ts_kfs), min(vs), max(vs)))

    ref_variant, variant_maps = load_variant_maps(args)

    cfg, report, orphan = build_config(
        segs, pairs, video_file=args.video or "", video_duration=dur,
        variants=variants, active=args.active,
        ts_kfs=ts_kfs if args.auto_slope else None, max_gap=args.max_gap,
        ref_variant=ref_variant, variant_maps=variant_maps)

    # 回填「這份設定是怎麼做出來的」。插件不看這幾個欄位（不認得的鍵會被忽略），
    # 它們是給工具的「載入既有 json」用的 —— 有了這些，之後想多加一個配音版本
    # 或多量一個對應點，就能把整份設定讀回介面上改，不用從頭再走一次流程。
    cfg["sourceCard"] = os.path.abspath(args.card) if args.card else ""
    cfg["pairsFile"] = os.path.abspath(pairs_path) if pairs_path else ""
    cfg["pairs"] = [[round(t, 3), round(v, 3), n]
                    for t, v, n in read_pairs_notes(pairs_path)]

    print_report(segs, report, cfg["cuts"], orphan, dur)

    # --- 配音對照的檢查 ---
    # 就算一張對照表都沒有也要跑：各版長度不一樣卻沒有對照表，
    # 正是「換了配音就整段對不上」的成因，而且從產生流程完全看不出來。
    try:
        import kkvariantmap as VM
        durs = {}
        for nm, fp in zip(cfg["variantNames"], cfg["variantFiles"]):
            d = VM.file_duration(fp)
            if d:
                durs[nm] = d
        vp = VM.print_report(report, cfg.get("refVariant", ""),
                             cfg.get("variantMaps") or {}, durs)
        rd = durs.get(cfg.get("refVariant", ""))
        for nm in cfg["variantNames"]:
            if nm == cfg.get("refVariant") or nm in (cfg.get("variantMaps") or {}):
                continue
            d = durs.get(nm)
            if rd and d and abs(d - rd) > 0.5:
                vp.append("配音「%s」長 %.2f 秒、主配音「%s」長 %.2f 秒（差 %+.2f）"
                          "卻沒有對照表 —— 這一版會整段偏掉。"
                          "跑 kkvariantmap.py 量幾個點再產生一次。"
                          % (nm, d, cfg.get("refVariant"), rd, d - rd))
        if vp:
            print()
            print("配音對照檢查：")
            for m in vp:
                print("  ! " + m)
    except Exception as e:                                  # noqa: BLE001
        print("\n  [注意] 配音對照檢查跳過了：%s" % e)

    out = args.out
    if not out and args.card:
        d = os.path.dirname(os.path.abspath(args.card))
        stem = os.path.splitext(os.path.basename(args.card))[0]
        out = os.path.join(d, stem + ".cutscene.json")
    if out:
        with open(out, "w", encoding="utf-8") as f:
            f.write(json.dumps(cfg, ensure_ascii=False, indent=2) + "\n")
        print("\n已寫出: %s\n        （%d 段音軌 / %d 段過場）"
              % (out, len(cfg["tracks"]), len(cfg["cuts"])))

        # ---- 寫完馬上體檢 ----
        #
        # 這一步是刻意放在這裡的。產生流程的失敗是無聲的：
        # anchors 空陣列、對應點倒退、音檔比曲線短 —— 產生的當下全都不會噴錯，
        # 要等到戴上耳機聽到「第二段怪怪的」才會發現，而那時候已經隔了好幾天。
        #
        # 在這裡跑一次，壞掉的檔案在寫出去的下一秒就被指名。
        # 檢查器失敗不該讓產生流程整個掛掉（它只是體檢，不是產線），所以包起來。
        if not getattr(args, "no_check", False):
            run_check(out)


def run_check(path):
    """跑 kkcutscene_check 體檢剛寫出來的設定檔。

    用 import 而不是開子行程：打包成 exe 之後子行程那條路很容易失效
    （PyInstaller 的暫存目錄、python 解譯器不在 PATH…），而 import 一定會成功。
    檢查器不在的話就只是少了這一步，不會影響產生。
    """
    print()
    try:
        import kkcutscene_check as chk
    except Exception as e:
        print("（跳過體檢：載入 kkcutscene_check 失敗 —— %s）" % e)
        return
    try:
        rep = chk.check_file(path, 0.02, 50.0, False)
    except Exception as e:
        print("（體檢本身出錯，不影響已寫出的檔案：%s）" % e)
        return

    # 印出來的格式跟單獨跑 kkcutscene_check 完全一樣：紅字 ERROR、黃字 WARN、
    # 每一項下面接一行解法。共用同一個函式，不要在這裡重寫一份 ——
    # 兩份輸出長得不一樣的話，看的人會以為是兩種不同的檢查。
    print(chk.summary_line(rep))
    if not rep.items:
        return
    chk.print_report(rep, indent="  ")
    if rep.errors:
        print()
        print(chk.red("  ※ 上面紅色的項目在遊戲裡會聽得出來，建議照解法處理完再用。"
                      "設定檔已經寫出來了，修好再重跑一次 plan 即可。"))


def cmd_verify(args):
    cfg = json.load(open(args.config, encoding="utf-8-sig"))
    segs, report = [], []
    for i, tr in enumerate(cfg.get("tracks") or [], 1):
        s = dict(index=i, name="track%d" % i, start=tr["from"], end=tr["to"])
        an = [(a[0], a[1]) for a in (tr.get("anchors") or [])]
        v0 = map_at(an, s["start"]) if an else None
        v1 = map_at(an, s["end"]) if an else None
        sp = (v1 - v0) / (s["end"] - s["start"]) if (v0 is not None and s["end"] > s["start"]) else None
        segs.append(s)
        report.append(dict(seg=s, n=len(an), v0=v0, v1=v1, slope=sp, res=residual(an)))
    print_report(segs, report, cfg.get("cuts") or [], [], args.video_duration)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd")

    def common(p):
        p.add_argument("card", nargs="?")
        p.add_argument("--segments", nargs="+",
                       help='手動指定段落，例如 --segments 0,58 58.01,68.51')

    p = sub.add_parser("points"); common(p); p.set_defaults(f=cmd_points)

    p = sub.add_parser("plan"); common(p)
    p.add_argument("--no-check", action="store_true",
                   help="寫出之後不要自動體檢（預設會檢查）")
    p.add_argument("--pairs")
    p.add_argument("--video", default="")
    p.add_argument("--video-duration", type=float, default=None)
    p.add_argument("--variant", action="append",
                   help='可重複。"名稱=路徑" 或直接給路徑（用檔名當名稱）')
    p.add_argument("--variant-dir", help="資料夾裡的 wav/ogg 全部當成配音版本")
    p.add_argument("--auto-slope", action="store_true",
                   help="曲線形狀由卡片的 timeScale 軌道積分決定，量到的點負責校正尺度。每段至少要兩個點：只給一個時尺度無人驗證，實測有過 8 秒等級的偏差")
    p.add_argument("--max-gap", type=float, default=5.0,
                   help="--auto-slope 採樣密度（秒），預設 5")
    p.add_argument("--active")
    p.add_argument("--ref-variant", default=None,
                   help="主配音（anchors 的秒數以哪一版為準）。預設是清單第一個，"
                        "或 <卡片>.variants.json 裡寫的那個")
    p.add_argument("--variant-map", action="append", default=[],
                   help='可重複。"名稱=對照表.json"，內容是 [[主配音秒, 該版秒], …]。'
                        "不給的話自動讀 <卡片>.variants.json")
    p.add_argument("--no-variant-map", action="store_true",
                   help="忽略 <卡片>.variants.json，所有配音一律當成跟主配音同步")
    p.add_argument("-o", "--out")
    p.set_defaults(f=cmd_plan)

    p = sub.add_parser("verify"); p.add_argument("config")
    p.add_argument("--video-duration", type=float, default=None)
    p.set_defaults(f=cmd_verify)

    a = ap.parse_args(argv)
    if not a.cmd:
        ap.print_help(); sys.exit(1)
    a.f(a)


if __name__ == "__main__":
    main()
