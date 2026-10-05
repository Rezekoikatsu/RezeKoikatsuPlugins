# -*- coding: utf-8 -*-
"""kkcutscene_check —— .cutscene.json 的體檢工具。

為什麼要有這支
==============
產生設定檔的流程有一個致命的性質：**錯誤是無聲的**。
anchors 算不出來就寫空陣列、對應點倒退就默默留著、音檔比 anchors 短就播到一半沒聲音 ——
這些全部都要等到戴上耳機聽到「第二段怪怪的」才會發現，而那時候你已經不記得
是哪一步出的問題了。

所以把「什麼叫做一份好的設定檔」寫成可執行的規則，產生完馬上跑一次。
壞掉的檔案在三秒內被指名，而不是三天後被耳朵抓到。

用法
====
    python kkcutscene_check.py <檔案或資料夾> [...]
    python kkcutscene_check.py D:\\Koikatu\\UserData\\cutscene

    --fix-from-pairs   anchors 空的段落，用 pairs 的頭尾補成直線（會改檔案）
    --quiet            只印有問題的檔案
    --slope LO HI      可接受的局部斜率範圍（預設 0.02 50）

ERROR 會印成紅色、WARN 黃色，每一項下面接一行「→ 解法」。
輸出被導到檔案、或設了環境變數 NO_COLOR 時自動退回純文字。

回傳碼：有 ERROR 回 1，只有 WARN 回 0。方便接在產生流程後面自動擋下來。

沒有第三方相依。wav 自己讀檔頭；其他格式有 ffprobe 就用，沒有就跳過那一項。
"""

import json
import os
import re
import struct
import subprocess
import sys
import wave

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import kkffmpeg                                             # noqa: E402
kkffmpeg.install()

ERR, WARN, OK = "ERROR", "WARN", "ok"


# ----------------------------------------------------------------- 顏色
#
# 體檢的輸出常常有十幾行，ERROR 夾在 WARN 和 ok 中間很容易被滑過去。
# 錯誤和警告的差別是「會不會聽得出來」，這個差別值得用顏色講清楚。
#
# Windows 的主控台從 10 開始支援 ANSI，但預設是關的，要自己打開
# ENABLE_VIRTUAL_TERMINAL_PROCESSING。打不開（舊系統、輸出被導到檔案、
# 在 IDE 的輸出視窗裡）就整個退回純文字 —— 退回去的版本靠行首的符號
# 和 [ERROR] 標籤一樣讀得懂，不會變成一堆 \033[91m 的垃圾。

_VT = 0x0004


def _enable_vt():
    if os.name != "nt":
        return True
    try:
        import ctypes
        k = ctypes.windll.kernel32
        ok = False
        for handle in (-11, -12):          # STD_OUTPUT, STD_ERROR
            h = k.GetStdHandle(handle)
            mode = ctypes.c_uint32()
            if k.GetConsoleMode(h, ctypes.byref(mode)):
                k.SetConsoleMode(h, mode.value | _VT)
                ok = True
        return ok
    except Exception:
        return False


def _color_available():
    # NO_COLOR 是跨工具的慣例，照做
    if os.environ.get("NO_COLOR") or os.environ.get("KK_NO_COLOR"):
        return False
    try:
        if not sys.stdout.isatty():
            return False
    except Exception:
        return False
    return _enable_vt()


COLOR = _color_available()

_RED = "\033[1;91m"     # 亮紅＋粗體，ERROR 專用
_YEL = "\033[93m"       # WARN
_DIM = "\033[90m"       # ok（只是說明，不該搶眼）
_CYA = "\033[96m"       # 解法
_GRN = "\033[92m"       # 沒問題
_RST = "\033[0m"


def paint(s, code):
    return (code + s + _RST) if COLOR else s


def red(s):
    """給別的模組用的捷徑 —— 不用去碰底線開頭的常數。"""
    return paint(s, _RED)


#: 行首符號 —— 沒有顏色的時候，這個就是唯一的視覺區分
MARK = {ERR: "✗", WARN: "!", OK: "·"}
TINT = {ERR: _RED, WARN: _YEL, OK: _DIM}
LABEL = {ERR: "ERROR", WARN: "WARN ", OK: "ok   "}


# ----------------------------------------------------------------- 小工具

def load_json(path):
    """容忍 // 註解、/* */ 註解和結尾多餘逗號 —— 這些檔案是會手改的。"""
    raw = open(path, encoding="utf-8-sig").read()
    raw = re.sub(r"/\*.*?\*/", "", raw, flags=re.S)
    raw = re.sub(r"//[^\n]*", "", raw)
    raw = re.sub(r",(\s*[}\]])", r"\1", raw)
    return json.loads(raw)


def audio_seconds(path):
    """音檔長度（秒）。問不到就回 None —— 問不到不是錯誤，只是少檢查一項。"""
    if not path or not os.path.isfile(path):
        return None
    ext = os.path.splitext(path)[1].lower()
    if ext == ".wav":
        try:
            with wave.open(path, "rb") as w:
                return w.getnframes() / float(w.getframerate())
        except Exception:
            pass
    try:
        out = subprocess.run(
            ["ffprobe", "-v", "error", "-show_entries", "format=duration",
             "-of", "default=noprint_wrappers=1:nokey=1", path],
            capture_output=True, text=True, timeout=20)
        return float(out.stdout.strip())
    except Exception:
        return None


def resolve(root_dir, base, p):
    """設定檔裡的路徑可能是絕對的，也可能相對於 audioRoot / videoRoot。"""
    if not p:
        return None
    if os.path.isabs(p):
        return p
    for b in (base, root_dir):
        if not b:
            continue
        cand = os.path.join(b, p)
        if os.path.exists(cand):
            return cand
    return os.path.join(base or root_dir or ".", p)


# ----------------------------------------------------------------- 檢查

class Report:
    """一個檔案的體檢結果。

    每一項除了「哪裡不對」還帶一句「怎麼修」。
    這兩件事分開存而不是寫在同一個字串裡，是因為它們要印成不同顏色，
    而且重複出現的解法可以摺疊 —— 五個斜率錯誤不該把同一段解法印五次。
    """

    def __init__(self, path):
        self.path = path
        self.items = []          # (level, message, fix)

    def add(self, level, msg, fix=None):
        self.items.append((level, msg, fix or ""))

    @property
    def notes(self):
        return [m for lv, m, _ in self.items if lv == OK]

    @property
    def errors(self):
        return [m for lv, m, _ in self.items if lv == ERR]

    @property
    def warns(self):
        return [m for lv, m, _ in self.items if lv == WARN]


# ----------------------------------------------------------------- 輸出

def print_report(rep, indent="    ", show_ok=True, prn=None):
    """把一份報告印出來：ERROR 紅字、WARN 黃字、解法青字接在下面。

    同一句解法重複出現時只印第一次 —— 後面的用「（同上）」帶過，
    不然一連串同類錯誤會被自己的解法淹掉。
    """
    p = prn or (lambda s="": print(s))
    seen = set()
    for lv, m, fix in rep.items:
        if lv == OK and not show_ok:
            continue
        head = "%s%s [%s] " % (indent, MARK.get(lv, "·"), LABEL.get(lv, lv))
        p(paint(head + m, TINT.get(lv, "")))
        if fix:
            if fix in seen:
                p(paint("%s      → 解法：（同上）" % indent, _CYA))
            else:
                seen.add(fix)
                p(paint("%s      → 解法：%s" % (indent, fix), _CYA))


def summary_line(rep):
    """一行總結，有錯的話整行紅字。"""
    ne, nw = len(rep.errors), len(rep.warns)
    if ne:
        return paint("體檢結果：%d 個錯誤、%d 個警告 —— 錯誤會在遊戲裡聽得出來，"
                     "請照下面的解法處理完再用。" % (ne, nw), _RED)
    if nw:
        return paint("體檢結果：沒有錯誤，%d 個提醒。" % nw, _YEL)
    return paint("體檢結果：沒有發現問題。", _GRN)


def check_anchors(rep, i, tr, lo, hi):
    """一段音軌的對應點本身合不合理。"""
    an = tr.get("anchors") or []
    frm = tr.get("from")
    to = tr.get("to")
    off = tr.get("offset")

    if len(an) < 2:
        if off in (None, 0, 0.0):
            # 這正是「第二段從 0 秒開始播」的成因。沒有對應點又沒有 offset，
            # 播放端只能退回 offset = 0，整段音檔從頭開始。
            rep.add(ERR, f"音軌 {i}（{frm}–{to}）沒有對應點，offset 也沒有／是 0 "
                         f"→ 播放時會從音檔 0 秒開始，整段對不上",
                    "這一段的 timeScale 中途掉到 0（通常是段落中間有過場），舊版算不出曲線就寫了空陣列。"
                    "用新版 kkcutscene plan 重跑一次即可（新版會夾住 timeScale 下限，真的不行才退成直線）；"
                    "不想重跑的話執行 kkcutscene_check.py <這個檔> --fix-from-pairs，"
                    "會用 pairs 裡落在這一段的點把 anchors 補回來（原檔會備份成 .bak）")
        else:
            rep.add(WARN, f"音軌 {i}（{frm}–{to}）沒有對應點，只靠 offset={off} 平移。"
                          f"這一段內的速度變化不會被補償",
                    "這一段如果全程等速就沒問題。中間有加速／慢動作的話，"
                    "在 .pairs.txt 的這一段裡補 2–3 個手量的對應點再重跑 plan")
        return

    # 單調性。時間軸往前走、音訊卻往回，物理上不可能 —— 一定是產生端算錯。
    for k in range(len(an) - 1):
        dt = an[k + 1][0] - an[k][0]
        da = an[k + 1][1] - an[k][1]
        if dt <= 0:
            rep.add(ERR, f"音軌 {i} 第 {k}→{k+1} 個對應點的時間軸沒有前進"
                         f"（{an[k][0]} → {an[k+1][0]}）",
                    f"anchors 的第一個數字必須遞增。打開 .pairs.txt 看 t={an[k][0]} 附近，"
                    f"通常是同一個時間點被量了兩次或秒數打錯；刪掉重複的那一行再重跑 plan")
            return
        if da < 0:
            rep.add(ERR, f"音軌 {i} 第 {k}→{k+1} 個對應點的音訊往回走"
                         f"（{an[k][1]} → {an[k+1][1]}）—— 播放時會在這裡來回硬 seek",
                    f"音訊時間不可能倒退，所以這兩個點裡一定有一個量錯。"
                    f"回 .pairs.txt 把 t={an[k][0]} 和 t={an[k+1][0]} 這兩個時間點的音訊秒數重新量一次"
                    f"（常見是分秒寫反，或是兩段音軌的秒數混在一起）")
            return

    slopes = [(an[k + 1][1] - an[k][1]) / (an[k + 1][0] - an[k][0])
              for k in range(len(an) - 1)]

    for k, s in enumerate(slopes):
        if s < lo or s > hi:
            rep.add(ERR, f"音軌 {i} 第 {k}→{k+1} 段的斜率 {s:.2f} 超出 {lo}–{hi}"
                         f"（t {an[k][0]}→{an[k+1][0]}）",
                    "斜率＝音訊走的秒數 ÷ 時間軸走的秒數，正常在 1 附近，"
                    "慢動作會變大、快轉會變小，但不會誇張到這個程度。"
                    "檢查這個區間兩端的 pin，最常見的是其中一個的音訊時間差了一個位數或一整分鐘。"
                    "如果這一段本來就是極端慢動作，加 --slope <下限> <上限> 放寬範圍")

    # 相鄰區間的斜率突變。真實的 timeScale 變化是連續的，
    # 突然快三倍再突然變回來，幾乎都是某個點量錯了。
    # 斜率突變只做摘要。
    #
    # 一開始這裡是每一處都報 WARN，結果 Charcard 那張卡（有真正的慢動作，
    # timeScale 掉到 0.07，斜率自然跳到 14）被塞了一長串警告。
    # 真慢動作和量錯在這個層次上長得一模一樣，分不出來 —— 分不出來就不該用
    # 嚇人的語氣講。真正能判斷對錯的是下面 pairs 的交叉驗證：
    # 只要累積的對應關係跟量到的點吻合，中間怎麼起伏都不重要。
    jumps = [(an[k + 1][0], slopes[k], slopes[k + 1])
             for k in range(len(slopes) - 1)
             if slopes[k] > 1e-6 and slopes[k + 1] > 1e-6
             and max(slopes[k] / slopes[k + 1], slopes[k + 1] / slopes[k]) > 3.0]
    if jumps:
        mx = max(max(a / b, b / a) for _, a, b in jumps)
        rep.add(OK, f"音軌 {i} 有 {len(jumps)} 處斜率突變（最大 {mx:.1f} 倍，"
                    f"最早在 t={jumps[0][0]}）—— 如果那裡本來就是慢動作就正常")

    # 頭尾有沒有蓋滿整段。沒蓋滿的話兩端是外推的，外推的誤差會比內插大得多。
    if abs(an[0][0] - frm) > 0.5:
        rep.add(WARN, f"音軌 {i} 第一個對應點在 t={an[0][0]}，但這一段從 {frm} 開始"
                      f"（前 {an[0][0]-frm:.2f} s 是外推的）",
                "外推的誤差比內插大。在這一段開頭附近補一個量到的 pin，整段就穩了")
    if to is not None and abs(an[-1][0] - to) > 0.5:
        rep.add(WARN, f"音軌 {i} 最後一個對應點在 t={an[-1][0]}，但這一段到 {to}"
                      f"（後 {to-an[-1][0]:.2f} s 是外推的）",
                "同上，在這一段結尾附近補一個量到的 pin")


def check_against_pairs(rep, d, tol=0.35):
    """用 pairs 交叉驗證 anchors。

    pairs 是量出來的頭尾對應點，anchors 是算出來的曲線。兩者應該吻合 ——
    對不上就代表校正那一步出了問題，而那種錯誤從 anchors 本身完全看不出來
    （它自己仍然是一條漂亮的單調曲線，只是整條平移或縮放錯了）。

    **段落交界要特別處理。** 交界上過場會吃掉一段來源時間：時間軸凍住，
    影片從 videoStart 播到 videoEnd。所以「上一段結尾的音訊位置」和
    「下一段開頭的音訊位置」本來就會差一個過場的長度，那不是錯誤。
    第一版沒考慮這個，把所有交界都報成錯 —— 規則必須認得這個結構，
    否則真正的錯誤會被淹沒在誤報裡。
    """
    pairs = d.get("pairs") or []
    tracks = d.get("tracks") or []
    cuts = d.get("cuts") or []
    if not pairs:
        rep.add(WARN, "設定檔裡沒有 pairs —— 無法交叉驗證，"
                      "而且萬一某一段的 anchors 是空的，播放端也沒有東西可以補",
                "用新版 kkcutscene plan 重跑一次，它會把量到的對應點一起寫進 json 的 pairs，"
                "插件才有 fallback 可用（舊檔不重跑也能用，只是少一層保險）")
        return

    def map_at(an, t):
        if not an:
            return None
        if t <= an[0][0]:
            return an[0][1]
        for k in range(len(an) - 1):
            if t <= an[k + 1][0]:
                dt = an[k + 1][0] - an[k][0]
                if dt <= 0:
                    return an[k][1]
                f = (t - an[k][0]) / dt
                return an[k][1] + (an[k + 1][1] - an[k][1]) * f
        return an[-1][1]

    def explained_by_cut(lo, hi):
        """這段落差是不是剛好等於某個過場吃掉的來源時間。"""
        for c in cuts:
            vs, ve = c.get("videoStart"), c.get("videoEnd")
            if vs is None or ve is None:
                continue
            if abs(vs - lo) <= tol and abs(ve - hi) <= tol:
                return c
        return None

    checked = inner = 0
    for pr in pairs:
        if not isinstance(pr, list) or len(pr) < 2:
            continue
        t, a = float(pr[0]), float(pr[1])
        label = pr[2] if len(pr) > 2 else ""
        # 交界上的點（上一段的尾、下一段的頭只差 0.01 秒）兩段都對得到。
        # 多張卡接起來、音檔不合併的設定檔，兩段是不同的音檔、秒數完全不相干，
        # 所以要挑「曲線跟它最接近的那一段」來比，不能看到第一段就比。
        cands = []
        for i, tr in enumerate(tracks):
            frm, to = tr.get("from"), tr.get("to")
            if frm is None or to is None:
                continue
            if not (frm - 0.05 <= t <= to + 0.05):
                continue
            an = tr.get("anchors") or []
            if len(an) < 2:
                continue
            got = map_at(an, t)
            if got is None:
                continue
            cands.append((abs(a - got), i, tr, got))
        cands.sort(key=lambda x: x[0])
        for _d, i, tr, got in cands[:1]:
            frm, to = tr.get("from"), tr.get("to")
            checked += 1
            at_edge = abs(t - frm) <= 0.2 or abs(t - to) <= 0.2
            if not at_edge:
                inner += 1
            diff = a - got
            if abs(diff) <= tol:
                break
            if at_edge and explained_by_cut(min(got, a), max(got, a)) is not None:
                # 交界上的落差剛好等於一個過場的來源區間 —— 這是對的，不是錯的
                break
            lvl = ERR if (not at_edge or abs(diff) > 1.0) else WARN
            if at_edge:
                fix = (f"落差 {diff:+.3f} s 出現在段落交界，代表這裡的過場沒有被算進去。"
                       f"檢查 cuts 裡對應這個交界的那一筆：videoStart／videoEnd 是不是漏了、"
                       f"或區間長度跟實際過場對不上。確認過場正確的話，就是這個 pin 量錯了")
            else:
                fix = (f"這是段落**內部**的誤差，曲線整條偏掉了 {diff:+.3f} s。"
                       f"先確認 t={t} 這個 pin 本身量得對（用 kkaudioalign 或直接聽）；"
                       f"pin 沒問題的話，在它前後各補 1–2 個手量的對應點再重跑 plan，"
                       f"讓分段仿射有依據 —— 只有頭尾兩個點的話中間只能用猜的")
            rep.add(lvl, f"音軌 {i} 的曲線跟量到的點對不上：t={t} 量到音訊 {a}，"
                         f"曲線算出來是 {got:.3f}（差 {diff:+.3f} s）"
                         + ("　※ 在段落交界上，但找不到吃掉這段時間的過場"
                            if at_edge else "")
                         + (f"　{label}" if label else ""),
                    fix)
            break

    if checked and inner == 0:
        rep.add(WARN, f"pairs 全部都在段落交界上（{checked} 個），"
                      f"段落**內部**沒有任何量到的點可以驗證 —— "
                      f"中間對不對只能靠耳朵",
                "頭尾對得上不代表中間對得上：只要有一個 pin 標成（自動對齊），"
                "那個點就是從音檔長度推回去的，不是量到的。"
                "在每一段中間挑 2–3 個有明顯聲音起點的地方手量對應點，加進 .pairs.txt 再重跑 plan")


def check_layout(rep, d):
    """段落有沒有把時間軸鋪滿、有沒有重疊。"""
    tracks = d.get("tracks") or []
    if not tracks:
        rep.add(ERR, "沒有任何音軌",
                "tracks 是空的，這個設定檔在遊戲裡等於不存在。"
                "確認 .pairs.txt 有內容、而且 plan 有抓到 variantFiles 指的音檔；"
                "整個流程重跑一次看 plan 的輸出有沒有印出段落")
        return
    if abs(tracks[0].get("from", 0) or 0) > 0.05:
        rep.add(WARN, f"第一段從 {tracks[0].get('from')} 開始，不是 0 —— "
                      f"開頭那一段沒有音訊是故意的嗎？",
                "如果開頭本來就是無聲的片頭就不用管；"
                "不是的話，第一個 pin 的時間軸座標可能量錯了")
    for i in range(len(tracks) - 1):
        gap = (tracks[i + 1].get("from") or 0) - (tracks[i].get("to") or 0)
        if gap < -0.05:
            rep.add(ERR, f"第 {i} 段（到 {tracks[i].get('to')}）和第 {i+1} 段"
                         f"（從 {tracks[i+1].get('from')}）重疊了 {-gap:.2f} s",
                    f"重疊的那 {-gap:.2f} 秒會有兩段音軌搶著播。"
                    f"改 tracks 的 from／to，讓第 {i} 段的 to 不大於第 {i+1} 段的 from —— "
                    f"通常把交界對齊到過場所在的時間點就對了")
        elif gap > 0.5:
            rep.add(WARN, f"第 {i} 段和第 {i+1} 段之間有 {gap:.2f} s 沒有音訊",
                    "中間這段會是靜音。如果那裡是過場就正常；"
                    "不是的話把前一段的 to 或後一段的 from 拉過來補滿")


def _map_at(pts, x):
    """折線內插（含兩端外推），跟插件的 VariantSec / kkvariantmap.map_at 同一套。"""
    n = len(pts or [])
    if n == 0:
        return float(x)
    if n == 1:
        return pts[0][1] + (x - pts[0][0])

    def sl(p, q):
        dx = q[0] - p[0]
        return 1.0 if abs(dx) < 1e-6 else (q[1] - p[1]) / dx

    if x <= pts[0][0]:
        return pts[0][1] + (x - pts[0][0]) * sl(pts[0], pts[1])
    for i in range(n - 1):
        if x <= pts[i + 1][0]:
            return pts[i][1] + (x - pts[i][0]) * sl(pts[i], pts[i + 1])
    return pts[-1][1] + (x - pts[-1][0]) * sl(pts[-2], pts[-1])


def check_variant_maps(rep, d):
    """配音對照表本身的合理性。"""
    names = d.get("variantNames") or []
    vmaps = d.get("variantMaps") or {}
    ref = d.get("refVariant") or ""
    if ref and names and ref not in names:
        rep.add(ERR, f"refVariant「{ref}」不在 variantNames 裡",
                "主配音必須是配音清單裡的一個。改成清單裡的名字，"
                "否則插件會拿第一個當主配音，其他版的對照表全部錯位")
    _check_maps(rep, vmaps, names, "")
    # 多張卡接起來、音檔不合併的設定檔：每條音軌自己帶對照表
    for i, tr in enumerate(d.get("tracks") or []):
        if isinstance(tr.get("maps"), dict):
            _check_maps(rep, tr["maps"], list((tr.get("files") or {}).keys()), f"音軌 {i} 的")


def _check_maps(rep, vmaps, names, where):
    for n, pts in (vmaps or {}).items():
        if names and n not in names:
            rep.add(WARN, f"{where}配音對照裡的「{n}」不在配音清單裡 —— 這張對照表用不到",
                    "名字打錯，或那個配音被拿掉了。改名字或把這一筆刪掉")
            continue
        if not isinstance(pts, list) or not pts:
            rep.add(WARN, f"{where}配音「{n}」的對照表是空的", "刪掉這一筆，或補上對照點")
            continue
        prev = None
        for p in pts:
            if not (isinstance(p, list) and len(p) >= 2):
                rep.add(ERR, f"{where}配音「{n}」的對照表裡有一筆不是 [主配音秒, 該版秒]",
                        "每一個點都要是兩個數字的陣列")
                break
            if prev is not None and (p[0] <= prev[0] or p[1] < prev[1]):
                rep.add(ERR, f"{where}配音「{n}」的對照點在 {p[0]} 秒倒退了",
                        "兩邊的秒數都只能往前走。倒退的點一定是量錯或抄錯，"
                        "插件會丟掉它，但那一帶的對照就沒人管了 —— 回去重量")
                break
            prev = p
        if len(pts) == 1:
            rep.add(WARN, f"{where}配音「{n}」只有 1 個對照點 —— 只能平移，修不了尺度",
                    "在場景的另一端再量一個點。兩版如果是不同的剪輯，"
                    "離那個點越遠偏得越多")


def check_files(rep, path, d):
    """檔案在不在、夠不夠長。"""
    root = os.path.dirname(os.path.abspath(path))
    game = None
    # audioRoot / videoRoot 可能是相對於遊戲根目錄的
    for key in ("audioRoot", "videoRoot"):
        v = d.get(key)
        if v and os.path.isabs(v):
            game = os.path.dirname(os.path.dirname(os.path.dirname(v)))
            break

    names = d.get("variantNames") or []
    files = d.get("variantFiles") or []
    if len(names) != len(files):
        rep.add(ERR, f"variantNames 有 {len(names)} 個、variantFiles 有 {len(files)} 個，對不起來",
                "這兩個陣列是一一對應的（第 n 個名字配第 n 個檔案）。"
                "補上缺的那一個，或把多出來的刪掉 —— 數量不符的話插件會抓到錯的配音")

    # 每一段音軌需要的最大音訊時間。
    # 音軌自己帶音檔的（多張卡接起來、音檔不合併）另外算：每個檔只管它自己那幾段。
    need = 0.0
    own = {}             # 檔案 → [需要到第幾秒, 說明]
    uses_global = False
    for i, tr in enumerate(d.get("tracks") or []):
        an = tr.get("anchors") or []
        nd_tr = an[-1][1] if an else 0.0
        tfiles = tr.get("files") if isinstance(tr.get("files"), dict) else None
        au = str(tr.get("audio", "@"))
        if tfiles and au.startswith("@"):
            tmaps = tr.get("maps") or {}
            for n, f in tfiles.items():
                nd = nd_tr
                if tmaps.get(n):
                    try:
                        nd = _map_at(tmaps[n], nd_tr)
                    except Exception:                       # noqa: BLE001
                        pass
                cur = own.get(f)
                if cur is None or nd > cur[0]:
                    own[f] = [nd, f"音軌 {i} 的配音「{n}」"]
            continue
        if au.startswith("@"):
            uses_global = True
        need = max(need, nd_tr)

    for f, (nd, what) in own.items():
        full = resolve(game, d.get("audioRoot"), f)
        if not full or not os.path.isfile(full):
            rep.add(ERR, f"{what}的檔案不存在：{f}",
                    "修正這條音軌 files 裡的路徑，或把音檔放回那個位置。"
                    "這份設定檔是從幾張卡的設定接起來的，音檔還是各張卡原本的那幾個 —— "
                    "原本的音檔搬走的話，改好各張卡的設定再回「合併場景」重新接一次")
            continue
        dur = audio_seconds(full)
        if dur is not None and nd > 0 and dur < nd - 0.5:
            rep.add(ERR, f"{what}長度只有 {dur:.2f} s，但對應點最遠需要到 {nd:.2f} s"
                         f"（差 {nd-dur:.2f} s）—— 後面那段會沒有聲音",
                    "音檔被換成比較短的版本，或原本那張卡的設定就有這個問題。"
                    "先單獨檢查那張卡自己的 cutscene.json")

    vmaps = d.get("variantMaps") or {}
    ref = d.get("refVariant") or (names[0] if names else "")

    def need_for(n):
        """這個配音版本實際需要的最遠秒數。

        anchors 的秒數是**主配音**的。剪輯不同的版本有自己的對照表，
        直接拿主配音的 need 去量它的長度會誤報 —— Charcard 的 VoiceB
        比主配音短 24 秒，那不是「尾巴被裁掉」，是本來就是另一套剪輯。
        """
        pts = vmaps.get(n)
        if not pts or n == ref:
            return need
        try:
            return _map_at(pts, need)
        except Exception:                                   # noqa: BLE001
            return need

    for n, f in zip(names, files):
        if own and not uses_global:
            break                # 每條音軌都自己帶音檔，最上層的 variantFiles 沒人用
        full = resolve(game, d.get("audioRoot"), f)
        if not full or not os.path.isfile(full):
            rep.add(ERR, f"配音「{n}」的檔案不存在：{f}",
                    "修正 variantFiles 裡的這一筆路徑，或把音檔放回那個位置。"
                    "相對路徑是相對於 audioRoot（"
                    + (d.get("audioRoot") or "沒設定") + "）")
            continue
        dur = audio_seconds(full)
        if dur is None:
            continue
        nd = need_for(n)
        if nd > 0 and dur < nd - 0.5:
            rep.add(ERR, f"配音「{n}」長度只有 {dur:.2f} s，但對應點最遠需要到 {nd:.2f} s"
                         f"（差 {nd-dur:.2f} s）—— 後面那段會沒有聲音",
                    f"三個可能：音檔在切的時候尾巴被裁掉了（重切，或換成完整的那一份）；"
                    f"音檔就是這麼長，那就是最後一個 pin 的音訊時間多打了 {nd-dur:.2f} 秒，"
                    f"回 .pairs.txt 重量最後那個點；"
                    f"或這一版跟主配音是不同的剪輯，那要用 kkvariantmap 量一張配音對照表"
                    + ("" if n in vmaps else "（它現在沒有）"))

    vf = d.get("videoFile")
    if vf:
        full = resolve(game, d.get("videoRoot"), vf)
        if not full or not os.path.isfile(full):
            rep.add(ERR, f"共用來源影片不存在：{vf}",
                    "修正 videoFile 的路徑，或把影片放回那個位置。"
                    "相對路徑是相對於 videoRoot（"
                    + (d.get("videoRoot") or "沒設定") + "）")
        else:
            dur = audio_seconds(full)
            if dur is not None:
                for i, c in enumerate(d.get("cuts") or []):
                    if c.get("video") or c.get("source"):
                        continue         # 自己帶影片／來源片的過場不看這一支
                    ve = c.get("videoEnd")
                    if ve is not None and ve > dur + 0.5:
                        rep.add(ERR, f"過場 {i} 的 videoEnd={ve} 超過影片長度 {dur:.2f} s",
                                f"這個過場的結束時間落在影片外面，播到底就停了。"
                                f"重新量這個過場在來源影片裡的區間；"
                                f"也有可能是 videoFile 指到了錯的（比較短的）那一份影片")


def check_cut_sources(rep, path, d):
    """過場自己帶的來源片（多張卡接起來的設定檔）：在不在、區間有沒有超出去。"""
    game = None
    for key in ("audioRoot", "videoRoot"):
        v = d.get(key)
        if v and os.path.isabs(v):
            game = os.path.dirname(os.path.dirname(os.path.dirname(v)))
            break
    seen = {}
    ntr = len(d.get("tracks") or [])
    for i, c in enumerate(d.get("cuts") or []):
        tk = c.get("track")
        if tk is not None and not (isinstance(tk, int) and -1 <= tk < ntr):
            rep.add(ERR, f"過場 {i} 的 track={tk} 不是有效的音軌編號（0–{ntr-1}）",
                    "track 是「過場期間沿用哪一條音軌的音檔」。改成那張卡的音軌編號，或刪掉這個欄位")
        src = c.get("source")
        if not src or c.get("video"):
            continue
        if src not in seen:
            full = resolve(game, d.get("videoRoot"), src)
            ok = bool(full) and os.path.isfile(full)
            seen[src] = (ok, audio_seconds(full) if ok else None)
            if not ok:
                rep.add(ERR, f"過場的來源影片不存在：{src}",
                        "修正這段過場 source 的路徑，或把影片放回那個位置（第一次出現在過場 %d）" % i)
        ok, dur = seen[src]
        ve = c.get("videoEnd")
        if ok and dur is not None and ve is not None and ve > dur + 0.5:
            rep.add(ERR, f"過場 {i} 的 videoEnd={ve} 超過它的來源影片長度 {dur:.2f} s",
                    "這個過場的結束時間落在影片外面，播到底就停了。先單獨檢查那張卡自己的 cutscene.json")


def check_cuts(rep, d):
    tracks = d.get("tracks") or []
    end = tracks[-1].get("to") if tracks else None

    # 過場一定坐在段落交界上。
    #
    # 過場的物理意義是「時間軸停住、來源影片自己播完那一段」，所以它只可能
    # 發生在兩段之間。落在段落**正中間**的過場是產生端瞎掰出來的 ——
    # 中間夾了一段沒算出對應點的段落時，舊版會把前後兩段直接接起來，
    # 生出一個橫跨整段、長達好幾十秒的假過場，播放時會在劇情中間插一段影片。
    bounds = []
    for tr in tracks:
        for k in ("from", "to"):
            v = tr.get(k)
            if v is not None:
                bounds.append(float(v))

    for i, c in enumerate(d.get("cuts") or []):
        t = c.get("t")
        vs, ve = c.get("videoStart"), c.get("videoEnd")
        if t is None:
            rep.add(ERR, f"過場 {i} 沒有 t",
                    "每一筆 cut 都要有 t（它在時間軸上的位置），不然插件不知道什麼時候該跳過。"
                    "補上這個數字，或整筆刪掉")
            continue
        if t < -0.05 or (end is not None and t > end + 0.05):
            rep.add(WARN, f"過場 {i} 的 t={t} 在時間軸範圍（0–{end}）之外",
                    "這個過場永遠不會被觸發。確認 t 沒打錯，或這筆是舊版留下來的殘留")

        kind_now = c.get("kind", "")
        if kind_now in ("", "transition") and bounds:
            near = min(abs(t - b) for b in bounds)
            if near > 2.0:
                span = (ve - vs) if (vs is not None and ve is not None) else None
                # 找出它落在哪一段裡面，講得具體一點
                inside = next((j for j, tr in enumerate(tracks)
                               if tr.get("from") is not None and tr.get("to") is not None
                               and tr["from"] < t < tr["to"]), None)
                where = f"音軌 {inside} 的正中間" if inside is not None else "段落交界以外"
                rep.add(ERR,
                        f"過場 {i} 的 t={t} 不在任何段落交界上（最近的交界差 {near:.2f} s），"
                        f"落在{where}"
                        + (f"，而且長達 {span:.2f} s" if span and span > 10 else ""),
                        "過場只會發生在兩段之間（時間軸停住、影片自己播）。"
                        "落在段落中間的過場是舊版產生端瞎掰出來的：中間夾了一段"
                        "沒算出對應點的音軌時，它會把前後兩段硬接起來，"
                        "生出一個橫跨整段的假過場，播放時會在劇情中間插一段影片。"
                        "直接把這一筆 cut 從 cuts 裡刪掉；"
                        "那一段的 anchors 如果也是空的，一併用新版 plan 重跑")
        # 自己帶影片檔的過場（video 有填）是整支播完，videoStart／videoEnd 都是 -1，不算區間反了
        own_clip = bool(c.get("video")) and (vs is None or vs < 0)
        if vs is not None and ve is not None and not own_clip:
            if ve <= vs:
                rep.add(ERR, f"過場 {i} 的 videoEnd({ve}) 沒有大於 videoStart({vs})",
                        "影片區間反了或長度是 0。把 videoStart／videoEnd 對調，"
                        "或重新量這個過場在來源影片裡的起訖時間")
            elif ve - vs > 300:
                rep.add(WARN, f"過場 {i} 長達 {ve-vs:.1f} s —— 確定是過場不是整段內容？",
                        "過場一般是幾秒到幾十秒。這麼長通常是 videoEnd 多打了位數，"
                        "或把整段內容誤標成過場")
        kind = c.get("kind", "")
        if kind not in ("", "opening", "transition", "ending"):
            rep.add(WARN, f"過場 {i} 的 kind=\"{kind}\" 不是 opening / transition / ending",
                    "插件只認得這三種（留空等同 transition）。"
                    "打錯字的話改回來，不然這個過場會被當成一般過場處理")


def fix_from_pairs(path, d):
    """anchors 空的段落，用 pairs 裡落在這一段的點補成折線。改檔案。"""
    pairs = d.get("pairs") or []
    if not pairs:
        return 0
    changed = 0
    for i, tr in enumerate(d.get("tracks") or []):
        if len(tr.get("anchors") or []) >= 2:
            continue
        frm, to = tr.get("from"), tr.get("to")
        pts = sorted((float(p[0]), float(p[1])) for p in pairs
                     if isinstance(p, list) and len(p) >= 2
                     and frm - 0.05 <= float(p[0]) <= to + 0.05)
        dedup = []
        for t, a in pts:
            if dedup and t - dedup[-1][0] <= 1e-6:
                continue
            dedup.append((t, a))
        if len(dedup) >= 2:
            tr["anchors"] = [[round(t, 3), round(a, 3)] for t, a in dedup]
            changed += 1
            print(f"    補上音軌 {i}：{len(dedup)} 個點 "
                  f"（{dedup[0][0]}→{dedup[0][1]} ～ {dedup[-1][0]}→{dedup[-1][1]}）")
    if changed:
        bak = path + ".bak"
        if not os.path.exists(bak):
            os.replace(path, bak)
            print(f"    原檔備份成 {os.path.basename(bak)}")
        with open(path, "w", encoding="utf-8") as f:
            json.dump(d, f, ensure_ascii=False, indent=2)
    return changed


def check_file(path, lo, hi, do_fix):
    rep = Report(path)
    try:
        d = load_json(path)
    except Exception as e:
        rep.add(ERR, f"讀不了：{e}",
                "JSON 格式壞了。錯誤訊息裡的 line／column 就是出事的位置，"
                "最常見是少了逗號、多了一個結尾逗號、或引號沒收尾。"
                "如果是手改壞的，旁邊有 .bak 的話可以先還原")
        return rep

    if do_fix:
        n = fix_from_pairs(path, d)
        if n:
            d = load_json(path)

    check_layout(rep, d)
    for i, tr in enumerate(d.get("tracks") or []):
        check_anchors(rep, i, tr, lo, hi)
    check_against_pairs(rep, d)
    check_cuts(rep, d)
    check_variant_maps(rep, d)
    check_files(rep, path, d)
    check_cut_sources(rep, path, d)
    return rep


def main(argv):
    args = [a for a in argv[1:] if not a.startswith("--")]
    do_fix = "--fix-from-pairs" in argv
    quiet = "--quiet" in argv
    lo, hi = 0.02, 50.0
    if "--slope" in argv:
        k = argv.index("--slope")
        lo, hi = float(argv[k + 1]), float(argv[k + 2])

    if not args:
        print(__doc__)
        return 2

    targets = []
    for a in args:
        if os.path.isdir(a):
            for root, _, fs in os.walk(a):
                for f in fs:
                    if f.endswith(".cutscene.json"):
                        targets.append(os.path.join(root, f))
        elif os.path.isfile(a):
            targets.append(a)

    if not targets:
        print("沒有找到任何 .cutscene.json")
        return 2

    bad = 0
    for p in sorted(targets):
        rep = check_file(p, lo, hi, do_fix)
        if quiet and not rep.errors and not rep.warns:
            continue
        if rep.errors:
            print(paint("✗ " + os.path.basename(p), _RED))
        elif rep.warns:
            print(paint("! " + os.path.basename(p), _YEL))
        else:
            print(paint("✓ " + os.path.basename(p), _GRN))
        print_report(rep, show_ok=not quiet)
        if rep.errors:
            bad += 1
        if rep.items:
            print()

    tail = f"—— 看了 {len(targets)} 個檔案，{bad} 個有 ERROR"
    print(paint(tail, _RED) if bad else paint(tail, _GRN))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
