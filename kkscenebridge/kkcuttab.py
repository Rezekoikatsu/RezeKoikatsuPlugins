#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""kkcuttab — kkscenebridge 的「過場」分頁

合併場景之後的那一段流水線：

    ① 合併場景  →  ② 選影片／配音  →  ③ 抓對應點  →  ④ 產生 cutscene.json

抓對應點是整條流水線唯一花人工的地方，所以這一頁的主角是內建播放器：
左邊拖曳 mp4 找到畫面，按「擷取」把影片秒數填進去；timeline 秒數從遊戲
面板抄過來。跑完 plan 之後，報告會直接告訴你下一個該去量哪裡，按「跳到
預測位置」播放器就會停在那附近。

實作上真正的工作都在 kkcutscene.py，這裡只是介面。
"""
from __future__ import annotations
from kksblang import T

import io
import json
import os
import re
import sys
import traceback
from contextlib import redirect_stdout, redirect_stderr
from pathlib import Path

from PyQt6.QtCore import (QMetaObject, QObject, Qt, QThread, QUrl,
                          pyqtSignal, pyqtSlot)
from PyQt6.QtGui import QColor, QFont
from PyQt6.QtWidgets import (
    QAbstractItemView, QCheckBox, QComboBox, QFileDialog, QGroupBox,
    QHBoxLayout, QHeaderView, QLabel, QLineEdit, QListWidget, QMessageBox,
    QDialog, QPlainTextEdit, QPushButton, QSlider, QSplitter,
    QTableWidget, QTableWidgetItem, QVBoxLayout, QWidget,
)

HAVE_MEDIA = True
try:
    from PyQt6.QtMultimedia import QAudioOutput, QMediaPlayer
    from PyQt6.QtMultimediaWidgets import QVideoWidget
except Exception:                                    # 沒裝 PyQt6-Qt6 的多媒體模組
    HAVE_MEDIA = False

RED = "#c0392b"
GREY = "#7f8c8d"

# 音量對齊的目標（EBU R128）。
#
# 為什麼預設是 −23 而不是串流那種 −16：
# 這批素材本身就落在 −21 ~ −23 LUFS（作者那邊的混音），而且真峰值已經在
# 0 dBTP 以上 —— 峰值對響度的比值二十幾 dB，是很動態的電影式混音。
# 訂 −16 的話等於要硬推 6~7 dB，峰值那邊得壓掉將近 8 dB 才塞得回去，
# 動態會被壓扁。訂在 −23 的話每個檔案只要動 0~2 dB，限幅幾乎不用作用。
#
# 用**絕對目標**（而不是每一批自己算一個共同值）的好處是所有卡片都對到同一個
# 數字：不只同一張卡的各版配音齊，換一張卡也齊。
#
# 真峰值留 −1.0 dBTP：夠擋住重取樣和有損轉碼產生的過衝，又不會為了留餘裕
# 把響度往下拖太多。
NORM_I = -23.0
NORM_TP = -1.0
NORM_CHOICES = (
    (T("-23 LUFS　廣播標準 EBU R128（建議：這批素材本來就在這附近）"), -23.0),
    (T("-20 LUFS　響一點"), -20.0),
    ("-18 LUFS", -18.0),
    (T("-16 LUFS　串流常見（要推比較多，限幅會作用）"), -16.0),
    (T("-14 LUFS　YouTube 播放響度（這種素材會被壓得很扁）"), -14.0),
)


class ClickSlider(QSlider):
    """點哪跳哪，而且可以接著拖。

    Qt 預設點軌道只會前進一頁，抓點時很難用。第一版直接在 mousePressEvent
    裡跳完就 accept()，結果把拖曳也一起吃掉了 —— 因為沒有進入 Qt 的拖曳狀態。
    正解是自己接管三個事件：按下時跳過去並進入拖曳狀態，移動時跟著更新，
    放開時結束。兩種操作就能共存。
    """
    def _value_at(self, x):
        span = self.maximum() - self.minimum()
        if span <= 0:
            return self.minimum()
        frac = min(max(x / max(1.0, float(self.width())), 0.0), 1.0)
        return int(self.minimum() + round(frac * span))

    def _apply(self, e):
        v = self._value_at(e.position().x())
        self.setValue(v)
        self.sliderMoved.emit(v)

    def mousePressEvent(self, e):
        if e.button() == Qt.MouseButton.LeftButton and self.maximum() > self.minimum():
            self.setSliderDown(True)          # 進入拖曳狀態，之後的 move 才算數
            self._apply(e)
            e.accept()
            return
        super().mousePressEvent(e)

    def mouseMoveEvent(self, e):
        if self.isSliderDown():
            self._apply(e)
            e.accept()
            return
        super().mouseMoveEvent(e)

    def mouseReleaseEvent(self, e):
        if self.isSliderDown() and e.button() == Qt.MouseButton.LeftButton:
            self._apply(e)
            self.setSliderDown(False)
            e.accept()
            return
        super().mouseReleaseEvent(e)


# ------------------------------------------------------------------ 時間格式

def fmt_time(v, ms=True):
    v = max(0.0, float(v))
    m, s = divmod(v, 60.0)
    h, m = divmod(int(m), 60)
    if h:
        return "%d:%02d:%06.3f" % (h, m, s) if ms else "%d:%02d:%02d" % (h, m, s)
    return "%02d:%06.3f" % (m, s) if ms else "%02d:%02d" % (m, s)


def parse_time(txt):
    """接受 82.933 / 01:22.933 / 1:02:03.300。"""
    t = str(txt).strip().replace("：", ":")
    if not t:
        raise ValueError(T("空白"))
    parts = t.split(":")
    if len(parts) > 3:
        raise ValueError(T("看不懂的時間：%s") % txt)
    total = 0.0
    for p in parts:
        if p.strip() == "":
            raise ValueError(T("看不懂的時間：%s") % txt)
        total = total * 60.0 + float(p)
    return total


# ------------------------------------------------------------------ 背景執行

class PlanWorker(QObject):
    """在背景跑 kkcutscene 的 plan / verify，把主控台輸出原封不動送回來。

    kkcutscene 是 CLI 工具，所有結果都用 print 出來。與其把它拆成函式庫，
    不如直接接管 stdout —— 報告的排版本來就是設計給人看的，搬到介面上一樣好讀。
    """
    done = pyqtSignal(bool, str, str)      # ok, 輸出全文, 產生的 json 路徑

    def __init__(self):
        super().__init__()
        self._argv = None
        self._out = ""

    def submit(self, argv, out_path):
        self._argv = list(argv)
        self._out = out_path

    @pyqtSlot()
    def run(self):
        buf = io.StringIO()
        ok = True
        try:
            here = os.path.dirname(os.path.abspath(__file__))
            if here not in sys.path:
                sys.path.insert(0, here)
            import kkcutscene
            try:
                reload_ = __import__("importlib").reload
                kkcutscene = reload_(kkcutscene)      # 改過 py 不用重開程式
            except Exception:
                pass
            with redirect_stdout(buf), redirect_stderr(buf):
                kkcutscene.main(self._argv)
        except SystemExit as e:
            if e.code not in (0, None):
                ok = False
                buf.write(T("\n[結束碼 %s]\n") % e.code)
        except Exception:
            ok = False
            buf.write("\n" + traceback.format_exc())
        self.done.emit(ok, buf.getvalue(), self._out)


class WavWorker(QObject):
    """從影片抽 wav。參數跟 kkcutscene_prep extract 一模一樣，
    這樣不管走哪條路產生的 wav 都保證跟 mp4 是同一個剪輯。"""
    done = pyqtSignal(bool, str)
    progress = pyqtSignal(str)

    def __init__(self):
        super().__init__()
        self._files = []
        self._ar = 48000
        self._force = False
        self._proc = None
        self._cancel = False

    def submit(self, files, ar=48000, force=False):
        self._files, self._ar, self._force = list(files), ar, force
        self._cancel = False

    def cancel(self):
        """關程式時用 —— 理由跟 MasterWorker.cancel 一樣。"""
        self._cancel = True
        p = self._proc
        if p is not None and p.poll() is None:
            try:
                p.kill()
            except Exception:
                pass

    @pyqtSlot()
    def run(self):
        import subprocess
        lines, n, fail = [], 0, 0
        for src in self._files:
            if self._cancel:
                break
            dst = os.path.splitext(src)[0] + ".wav"
            if os.path.exists(dst) and not self._force:
                lines.append(T("  跳過（已存在）: %s") % os.path.basename(dst))
                continue
            lines.append(T("  抽音訊: %s") % os.path.basename(src))
            self.progress.emit(T("抽 wav %d/%d　%s")
                               % (n + fail + 1, len(self._files), os.path.basename(src)))
            try:
                # 用 Popen 而不是 run()，是為了留一個把手給 cancel() 殺
                self._proc = subprocess.Popen(
                    ["ffmpeg", "-v", "error", "-y", "-i", src, "-vn",
                     "-c:a", "pcm_s16le", "-ar", str(self._ar), dst],
                    stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            except FileNotFoundError:
                self.done.emit(False, T("找不到 ffmpeg。把 ffmpeg 資料夾放在 kkscenebridge 旁邊（或裝好並加進 PATH）再試一次。"))
                return
            _, err = self._proc.communicate()
            rc = self._proc.returncode
            self._proc = None
            if self._cancel:
                break
            if rc != 0:
                fail += 1
                lines.append(T("    失敗: ") + err.decode("utf-8", "replace")[-200:])
            else:
                n += 1
        lines.append(T("完成 %d 個%s") % (n, (T("，失敗 %d 個") % fail) if fail else ""))
        self.done.emit(fail == 0, "\n".join(lines))


class LoudnessWorker(QObject):
    """把幾個音檔的響度對齊到同一個目標（EBU R128）。

    為什麼需要
    ----------
    從 mp4 抽出來的 wav 是**原樣搬過來**的（-c:a pcm_s16le，沒有任何處理），
    所以每一版配音有多大聲完全看作者當初怎麼混。同一個場景的三版配音
    常常差好幾 dB —— 在 Studio 裡按一下切換配音，音量就跳一階。

    做法：純增益，不做動態
    ----------------------
    兩趟：第一趟只量（loudnorm 的 print_format=json），第二趟套一個固定增益
    （volume=XdB）。**刻意不用 loudnorm 的第二趟去重新渲染**：
    那條路會帶進限幅器和動態處理，音色會變，而且不同檔案變的程度不一樣 ——
    對「同一句話的三個版本」來說那反而更不像。

    固定增益是線性運算：波形只是整體放大縮小，長度、相位、對應點通通不動。
    這對這個工具是硬性要求 —— 對應點是拿秒數釘住的，音訊時間被動到就全毀了。

    真峰值上限要**整批一起讓**，不能各夾各的
    -------------------------------------------
    第一版是「每個檔案各自算增益，超過真峰值上限就各自夾住」。
    實測直接失敗：Charcard 那三版的真峰值本來就在 0 dBTP 以上（作者壓到頂了），
    三個全部撞上限，套進去的增益變成「把峰值壓下來」而不是「把響度拉齊」——
    差距從 2.3 dB 變成 2.2 dB，等於白做。

    正解是先量完全部，算出每個檔案「在不爆峰的前提下最多能到多響」，
    取其中**最低**的那一個當共同目標，所有檔案都對到那個數字。
    這樣一定齊，代價是整體會比理想目標安靜一些 —— 那是必然的代價，
    被最沒有餘裕的那個檔案決定。

    想換回音量就開限幅模式：先推到目標響度，再用 alimiter 把峰值壓回上限。
    那會動到峰值的形狀（本來就已經削過頂的素材，影響其實很小），
    但至少各版之間還是齊的。
    """
    line = pyqtSignal(str)
    done = pyqtSignal(bool, str)
    progress = pyqtSignal(str)

    def __init__(self):
        super().__init__()
        self._files = []
        self._I = -16.0
        self._TP = -1.5
        self._limit = False
        self._proc = None
        self._cancel = False

    def submit(self, files, target_i=-16.0, target_tp=-1.5, limit=False):
        self._files = list(files)
        self._I, self._TP = float(target_i), float(target_tp)
        self._limit = bool(limit)
        self._cancel = False

    def cancel(self):
        self._cancel = True
        p = self._proc
        if p is not None and p.poll() is None:
            try:
                p.kill()
            except Exception:
                pass

    def _run(self, cmd):
        import subprocess
        self._proc = subprocess.Popen(cmd, stdout=subprocess.PIPE,
                                      stderr=subprocess.PIPE)
        out, err = self._proc.communicate()
        rc = self._proc.returncode
        self._proc = None
        return rc, out.decode("utf-8", "replace"), err.decode("utf-8", "replace")

    def measure(self, path):
        """回傳 (整體響度 LUFS, 真峰值 dBTP)，量不到回 (None, None)。"""
        rc, _o, err = self._run(
            ["ffmpeg", "-hide_banner", "-nostats", "-i", path,
             "-af", "loudnorm=I=%g:TP=%g:LRA=11:print_format=json" % (self._I, self._TP),
             "-f", "null", "-"])
        if rc != 0 and "input_i" not in err:
            return None, None
        # loudnorm 把 json 印在 stderr 的最後面，前面還有一堆別的輸出
        i = err.rfind("{")
        j = err.rfind("}")
        if i < 0 or j < i:
            return None, None
        try:
            d = json.loads(err[i:j + 1])
            return float(d["input_i"]), float(d["input_tp"])
        except Exception:                                   # noqa: BLE001
            return None, None

    @pyqtSlot()
    def run(self):
        # ---- 第一趟：全部量完再決定要套多少 ----
        # 一定要先量完全部。逐檔「量完就套」沒辦法知道「大家最多能一起到多響」，
        # 而那正是對齊的關鍵 —— 這是第一版失敗的地方。
        meas = []
        for n, src in enumerate(self._files, 1):
            if self._cancel:
                return
            name = os.path.basename(src)
            self.progress.emit(T("量響度 %d/%d　%s") % (n, len(self._files), name))
            try:
                cur_i, cur_tp = self.measure(src)
            except FileNotFoundError:
                self.done.emit(False, T("找不到 ffmpeg。把 ffmpeg 資料夾放在 kkscenebridge 旁邊（或裝好並加進 PATH）再試一次。"))
                return
            if cur_i is None:
                self.line.emit(T("  %-46s 量不到響度，跳過") % name[:46])
                continue
            meas.append((src, cur_i, cur_tp))

        if not meas:
            self.done.emit(False, T("一個檔案都量不到響度 —— 確認 ffmpeg 讀得動這些檔。"))
            return

        # ---- 決定共同目標 ----
        # 每個檔案「不爆峰時最多能到的響度」= 目前響度 + (峰值上限 - 目前峰值)
        headroom = [(i_ + (self._TP - tp)) for _p, i_, tp in meas]
        if self._limit:
            target = self._I                      # 限幅模式：直接推到目標，峰值交給 alimiter
        else:
            target = min([self._I] + headroom)    # 純增益：被最沒餘裕的那個檔案決定
        before = [i_ for _p, i_, _tp in meas]
        self.line.emit("")
        self.line.emit(T("  量測結果（%d 個檔案，原本響度差距 %.1f dB）")
                       % (len(meas), max(before) - min(before)))
        for p, i_, tp in meas:
            self.line.emit("    %-46s %6.1f LUFS / %5.1f dBTP"
                           % (os.path.basename(p)[:46], i_, tp))
        if self._limit:
            self.line.emit(T("  共同目標 %.1f LUFS（限幅模式：峰值由 alimiter 壓回 %.1f dBTP）")
                           % (target, self._TP))
        else:
            self.line.emit(T("  共同目標 %.1f LUFS —— 被「%s」的峰值餘裕決定")
                           % (target,
                              os.path.basename(meas[headroom.index(min(headroom))][0])))
            if target < self._I - 0.2:
                self.line.emit(T("    （比選定的 %.1f LUFS 低 %.1f dB。這些素材本來就壓到頂，純增益推不上去 —— 把「不限幅」取消勾選再跑一次就到得了，代價是最頂端那幾個瞬間會被限幅動到。）")
                               % (self._I, self._I - target))
        self.line.emit("")

        # ---- 第二趟：套增益 ----
        ok = fail = skip = 0
        for n, (src, cur_i, cur_tp) in enumerate(meas, 1):
            if self._cancel:
                break
            name = os.path.basename(src)
            gain = target - cur_i
            # 死區給 0.2 dB（聽不出來），不是 0.01 —— loudnorm 單趟量測本身就有
            # 0.1 dB 上下的誤差，門檻太小的話同一批檔案重跑一次又會全部重新編碼，
            # 每編一次就多一輪 16 bit 量化雜訊。留死區才能「重跑等於沒事」。
            if abs(gain) < 0.2:
                skip += 1
                self.line.emit(T("  %-46s %+6.2f dB   已經在目標上，不動") % (name[:46], gain))
                continue

            af = "volume=%.4fdB" % gain
            if self._limit and cur_tp + gain > self._TP:
                # alimiter 是取樣峰值，真峰值會稍微高一點，所以多留 0.3 dB
                lim = 10.0 ** ((self._TP - 0.3) / 20.0)
                af += ",alimiter=limit=%.6f:level=disabled:attack=5:release=50" % lim

            self.progress.emit(T("套增益 %d/%d　%s（%+.2f dB）") % (n, len(meas), name, gain))
            tmp = src + ".norm.tmp.wav"
            # 先寫暫存再 os.replace：中途被殺掉或轉檔失敗時，原檔完好無損。
            # 這些 wav 是 cutscene.json 直接指名的路徑，半個檔就是整張卡沒聲音。
            rc, _o, err = self._run(
                ["ffmpeg", "-v", "error", "-y", "-i", src,
                 "-af", af, "-c:a", "pcm_s16le", tmp])
            if rc != 0 or not os.path.isfile(tmp):
                fail += 1
                self.line.emit(T("  %-46s 失敗：%s") % (name[:46], err.strip()[-160:]))
                if os.path.isfile(tmp):
                    try:
                        os.remove(tmp)
                    except OSError:
                        pass
                continue
            try:
                os.replace(tmp, src)
            except OSError as e:
                fail += 1
                self.line.emit(T("  %-46s 換不掉原檔：%s") % (name[:46], e))
                continue
            ok += 1
            self.line.emit("  %-46s %6.1f  ->  %6.1f LUFS   （%+.2f dB%s）"
                           % (name[:46], cur_i, target, gain,
                              T("，有限幅") if "alimiter" in af else ""))

        msg = (T("音量對齊完成：全部對到 %.1f LUFS —— 改了 %d 個，本來就在目標上 %d 個%s")
               % (target, ok, skip, (T("，失敗 %d 個") % fail) if fail else ""))
        if not fail:
            msg += T("　（原本差距 %.1f dB → 現在 0.0 dB）") % (max(before) - min(before))
        self.done.emit(fail == 0, msg)


class ProbeWorker(QObject):
    """新增檔案時順手驗一下「是不是同一個剪輯」。

    最便宜又最有效的指標是**總長度**：作者放出的各配音版本是同一套剪輯重新上音，
    長度會一致到毫秒級；長度對不上就一定不是同一個剪輯，對應點全部作廢。
    （更嚴格的畫面簽章比對留在 kkcutscene_prep 的 matrix / parent。）
    """
    # 第三個參數是這次提交的序號 —— 呼叫端用它丟掉過期的結果。
    # 沒有序號的話：載入 A 卡（20 個檔案，ffprobe 跑好幾秒）之後馬上載 B 卡，
    # A 的結果會晚一步打進 B 的畫面，紅字說「不同剪輯」，講的卻是已經不在的那張卡。
    done = pyqtSignal(bool, str, int)

    def __init__(self):
        super().__init__()
        self._files = []
        self._seq = 0

    def submit(self, files, seq=0):
        self._files = list(files)
        self._seq = seq

    @pyqtSlot()
    def run(self):
        import subprocess
        seq = self._seq
        rows = []
        for p in self._files:
            try:
                r = subprocess.run(
                    ["ffprobe", "-v", "error", "-show_entries",
                     "format=duration", "-of", "default=nw=1:nk=1", p],
                    stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            except FileNotFoundError:
                self.done.emit(True, T("（沒有 ffprobe，跳過同剪輯檢查）"), seq)
                return
            try:
                rows.append((os.path.basename(p), float(r.stdout.decode().strip())))
            except ValueError:
                rows.append((os.path.basename(p), None))
        good = [d for _, d in rows if d]
        if not good:
            self.done.emit(True, "", seq)
            return
        base = max(set(good), key=good.count)       # 最多人共用的那個長度當基準
        lines = [T("同剪輯檢查（比總長度）:")]
        bad = False
        for nm, d in rows:
            if d is None:
                lines.append(T("  %-44s 讀不到長度") % nm[:44])
                continue
            diff = d - base
            if abs(diff) <= 0.05:
                lines.append(T("  %-44s %9.3f s   同剪輯") % (nm[:44], d))
            else:
                bad = True
                lines.append(T("  %-44s %9.3f s   差 %+.3f s   << 不同剪輯")
                             % (nm[:44], d, diff))
        if bad:
            lines.append(T("!! 長度對不上的那些不是同一個剪輯 —— 對應點不能共用，拿去當配音版本會整段歪掉。"))
        self.done.emit(not bad, "\n".join(lines), seq)


class MasterWorker(QObject):
    """轉一支「無聲、關鍵影格很密」的工作用影片。

    參數跟 kkcutscene_prep master 一模一樣。兩個重點：
      -an              去掉音軌。音訊另外走 wav，影片只負責畫面，
                       插件播過場時才不會有兩路聲音打架。
      -g 30 -keyint_min 30 -sc_threshold 0
                       每 30 幀一個關鍵影格 → seek 的定位誤差上限約 0.5 秒。
                       原始檔的關鍵影格常常隔好幾秒，拖時間軸會歪得很明顯。

    2GB 的片子要好幾分鐘，所以用 -progress 讀 ffmpeg 自己回報的進度，
    換算成百分比即時顯示 —— 不然使用者只會看到視窗發呆。
    """
    done = pyqtSignal(bool, str, object)   # ok, 訊息, 成功的輸出檔清單
    line = pyqtSignal(str)
    progress = pyqtSignal(str)

    def __init__(self):
        super().__init__()
        self._a = None
        self._proc = None
        self._cancel = False

    def submit(self, jobs, height, gop=30, crf=20, preset="medium"):
        """jobs 是 [(來源, 輸出)] —— 一次可以排好幾支，睡前丟一批下去就好。"""
        self._a = (list(jobs), height, gop, crf, preset)
        self._cancel = False

    def cancel(self):
        """關程式時用：把 ffmpeg 殺掉，run() 才會從讀管線那裡回來。

        QThread.quit() 只是請事件迴圈收工，對「正在執行中的 slot」完全沒有作用 ——
        2GB 的片子轉起來要好幾分鐘，這期間關掉視窗的話 wait() 會逾時，
        然後 QThread 帶著還在跑的執行緒被解構，Qt 直接 qFatal 讓整個程式當掉。
        """
        self._cancel = True
        p = self._proc
        if p is not None and p.poll() is None:
            try:
                p.kill()
            except Exception:
                pass

    @staticmethod
    def _duration(path):
        import subprocess
        try:
            r = subprocess.run(
                ["ffprobe", "-v", "error", "-show_entries", "format=duration",
                 "-of", "default=nw=1:nk=1", path],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            return float(r.stdout.decode().strip())
        except Exception:
            return 0.0

    @pyqtSlot()
    def run(self):
        jobs, height, gop, crf, preset = self._a
        made, fails = [], []
        for i, (src, out) in enumerate(jobs, 1):
            if self._cancel:
                break
            self.line.emit("[%d/%d] %s → %s"
                           % (i, len(jobs), os.path.basename(src), os.path.basename(out)))
            ok, msg = self._one(src, out, height, gop, crf, preset, i, len(jobs))
            if ok is None:
                self.done.emit(False, msg, made)      # 連 ffmpeg 都沒有，不用再試了
                return
            if ok:
                made.append(out)
                self.line.emit("    " + msg)
            else:
                fails.append(os.path.basename(src))
                self.line.emit(T("    失敗：") + msg)
        tail = (T("，失敗 %d 支（%s）") % (len(fails), "、".join(fails[:4]))) if fails else ""
        self.done.emit(not fails, T("無聲工作影片：完成 %d 支%s") % (len(made), tail), made)

    def _one(self, src, out, height, gop, crf, preset, idx, n):
        """轉一支。回傳 (True/False/None, 訊息)；None 代表連 ffmpeg 都找不到。"""
        import subprocess
        self.progress.emit(T("讀取來源長度…（%d/%d）") % (idx, n))
        total = self._duration(src)

        # -progress / -nostats 放在最前面（全域選項），有些 ffmpeg 版本放後面會被忽略。
        cmd = ["ffmpeg", "-y", "-hide_banner", "-loglevel", "error",
               "-progress", "pipe:1", "-nostats", "-i", src]
        if height:
            cmd += ["-vf", "scale=-2:%d" % height]
        cmd += ["-c:v", "libx264", "-preset", preset, "-crf", str(crf),
                "-pix_fmt", "yuv420p",
                "-g", str(gop), "-keyint_min", str(gop), "-sc_threshold", "0",
                "-movflags", "+faststart", "-an", out]
        try:
            # stderr 併進 stdout。分開接的話，stderr 的管線塞滿之後 ffmpeg 會卡住
            # 等人讀走，而我們正忙著讀 stdout —— 互相等，整個轉檔就停在那裡不動。
            p = subprocess.Popen(cmd, stdout=subprocess.PIPE,
                                 stderr=subprocess.STDOUT,
                                 universal_newlines=True, bufsize=1)
        except FileNotFoundError:
            return None, T("找不到 ffmpeg。把 ffmpeg 資料夾放在 kkscenebridge 旁邊（或裝好並加進 PATH）再試一次。")
        self._proc = p              # cancel() 要靠它把 ffmpeg 殺掉

        self.progress.emit(T("轉檔中…（%d/%d，ffmpeg 已啟動）") % (idx, n))
        tail = []
        for line in p.stdout:
            line = line.strip()
            if not line:
                continue
            if line.startswith("out_time="):
                try:
                    hh, mm, ss = line.split("=", 1)[1].split(":")
                    done_s = int(hh) * 3600 + int(mm) * 60 + float(ss)
                except Exception:
                    continue
                if total > 0:
                    self.progress.emit(T("轉檔中 %d/%d　%.0f%%　（%s / %s）")
                                       % (idx, n, min(100.0, done_s / total * 100.0),
                                          fmt_time(done_s, ms=False),
                                          fmt_time(total, ms=False)))
                else:
                    self.progress.emit(T("轉檔中 %d/%d　已處理 %s")
                                       % (idx, n, fmt_time(done_s, ms=False)))
            elif "=" not in line:
                tail.append(line)             # 不是 key=value 的就是錯誤訊息
                del tail[:-20]
        p.wait()
        self._proc = None

        if p.returncode != 0:
            return False, ("\n".join(tail) or (T("ffmpeg 結束碼 %s") % p.returncode))
        mb = os.path.getsize(out) / 1e6 if os.path.isfile(out) else 0
        return True, (T("%s（%.1f MB，關鍵影格每 %d 幀，seek 誤差上限約 %.2f 秒）")
                      % (os.path.basename(out), mb, gop, gop / 60.0))


# ------------------------------------------------------------------ 播放器

class VideoPane(QWidget):
    """影片預覽 + 逐格前後移動。抓對應點全靠這個。"""
    captured = pyqtSignal(float)          # 按下「擷取」時送出目前秒數

    def __init__(self):
        super().__init__()
        lay = QVBoxLayout(self)
        lay.setContentsMargins(0, 0, 0, 0)

        self.player = None
        if HAVE_MEDIA:
            self.view = QVideoWidget()
            self.view.setMinimumHeight(240)
            self.player = QMediaPlayer(self)
            self.audio = QAudioOutput(self)
            self.player.setAudioOutput(self.audio)
            self.audio.setVolume(0.7)
            self.player.setVideoOutput(self.view)
            self.player.positionChanged.connect(self._on_pos)
            self.player.durationChanged.connect(self._on_dur)
            self.player.errorOccurred.connect(self._on_err)
            lay.addWidget(self.view, 1)
        else:
            tip = QLabel(T("這台電腦的 PyQt6 沒有多媒體模組，播放器停用。\n裝上之後重開就會出現：pip install PyQt6-Qt6 PyQt6\n（沒有播放器也能用，秒數自己從外部播放器抄進右邊表格）"))
            tip.setStyleSheet("color:%s" % GREY)
            tip.setWordWrap(True)
            lay.addWidget(tip, 1)

        self.slider = ClickSlider(Qt.Orientation.Horizontal)
        self.slider.setRange(0, 0)
        self.slider.sliderMoved.connect(self._seek_ms)
        self.slider.setEnabled(HAVE_MEDIA)
        lay.addWidget(self.slider)

        row = QHBoxLayout()
        self.btn_play = QPushButton(T("播放"))
        self.btn_play.clicked.connect(self.toggle)
        self.btn_play.setEnabled(HAVE_MEDIA)
        row.addWidget(self.btn_play)
        for label, delta in (("−5s", -5.0), ("−1s", -1.0), (T("−1格"), -1 / 30.0),
                             (T("+1格"), 1 / 30.0), ("+1s", 1.0), ("+5s", 5.0)):
            b = QPushButton(label)
            b.setFixedWidth(52)
            b.clicked.connect(lambda _, d=delta: self.nudge(d))
            b.setEnabled(HAVE_MEDIA)
            row.addWidget(b)
        row.addWidget(QLabel(T("速度")))
        self.cmb_rate = QComboBox()
        for r in ("0.25", "0.5", "1.0", "2.0"):
            self.cmb_rate.addItem(r + "×", float(r))
        self.cmb_rate.setCurrentIndex(2)
        self.cmb_rate.currentIndexChanged.connect(
            lambda: self.player and self.player.setPlaybackRate(self.cmb_rate.currentData()))
        self.cmb_rate.setEnabled(HAVE_MEDIA)
        row.addWidget(self.cmb_rate)

        row.addSpacing(12)
        row.addWidget(QLabel(T("音量")))
        self.sl_vol = ClickSlider(Qt.Orientation.Horizontal)
        self.sl_vol.setRange(0, 100)
        self.sl_vol.setValue(70)
        self.sl_vol.setFixedWidth(110)
        self.sl_vol.valueChanged.connect(self._set_volume)
        self.sl_vol.setEnabled(HAVE_MEDIA)
        row.addWidget(self.sl_vol)
        row.addStretch(1)

        self.lbl_pos = QLabel("00:00.000")
        f = QFont("Consolas")
        f.setStyleHint(QFont.StyleHint.Monospace)
        f.setPointSize(11)
        self.lbl_pos.setFont(f)
        row.addWidget(self.lbl_pos)

        self.btn_cap = QPushButton(T("擷取這一秒 →"))
        self.btn_cap.setToolTip(T("把目前的影片秒數填進右邊那一列的「影片」欄"))
        self.btn_cap.clicked.connect(lambda: self.captured.emit(self.position()))
        self.btn_cap.setEnabled(HAVE_MEDIA)
        row.addWidget(self.btn_cap)
        lay.addLayout(row)

        self._dur = 0.0
        self._path = ""

    # ---- 對外 ----
    def load(self, path):
        if not (self.player and path and os.path.isfile(path)):
            return
        self._path = path
        self._dur = 0.0          # 新片子的長度要等 durationChanged，別讓舊值留著被誤用
        self.player.setSource(QUrl.fromLocalFile(path))
        self.player.pause()

    def clear(self):
        """把片子退掉（換一張卡的時候用）。

        一定要連 _path 和 _dur 一起歸零：外面用 source_path() 去確認
        「duration() 講的是不是同一支」，只 setSource 而不清路徑的話，
        畫面上黑掉了、問它是哪一支卻還答得出上一張卡的影片，
        於是「預測的影片位置」拿舊長度去算，看起來完全合理但整個是錯的。
        """
        self._path = ""
        self._dur = 0.0
        if self.player:
            try:
                self.player.stop()
                self.player.setSource(QUrl())
            except Exception:
                pass
        try:
            self.slider.setRange(0, 0)
            self.slider.setValue(0)
            self.lbl_pos.setText(fmt_time(0.0))
            self.btn_play.setText(T("播放"))
        except Exception:
            pass

    def source_path(self):
        """播放器目前載的是哪一支 —— 用來確認 duration() 講的是不是同一個檔案。"""
        return getattr(self, "_path", "")

    def duration(self):
        return self._dur

    def position(self):
        return (self.player.position() / 1000.0) if self.player else 0.0

    def seek(self, sec):
        if self.player:
            self.player.setPosition(int(max(0.0, sec) * 1000))

    def nudge(self, d):
        self.seek(self.position() + d)

    def toggle(self):
        if not self.player:
            return
        if self.player.playbackState() == QMediaPlayer.PlaybackState.PlayingState:
            self.player.pause()
            self.btn_play.setText(T("播放"))
        else:
            self.player.play()
            self.btn_play.setText(T("暫停"))

    def _set_volume(self, v):
        if self.player is not None:
            self.audio.setVolume(v / 100.0)

    # ---- 內部 ----
    def _seek_ms(self, ms):
        if self.player:
            self.player.setPosition(ms)

    def _on_pos(self, ms):
        if not self.slider.isSliderDown():
            self.slider.setValue(ms)
        self.lbl_pos.setText(fmt_time(ms / 1000.0))

    def _on_dur(self, ms):
        self._dur = ms / 1000.0
        self.slider.setRange(0, ms)

    def _on_err(self, *_):
        if self.player:
            self.lbl_pos.setText(T("讀不到影片"))


AUDIO_EXT = (".wav", ".ogg", ".mp3", ".m4a", ".flac")
VIDEO_EXT = (".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v", ".wmv")


def variant_name(path):
    """從檔名猜一個短名字。面板按鈕只有 96px，太長會被切掉。
    跟 kkcutscene.variant_name 同一套規則，這樣介面上看到的跟寫進 json 的一致。

    名字裡的 = 一律換掉：傳給 kkcutscene 的格式是 "名稱=路徑"，那邊用第一個 =
    切開，名字裡自己有 = 的話路徑會被切斷，json 就寫進一個不是路徑的字串。
    """
    stem = os.path.splitext(os.path.basename(path))[0]
    nm = ""
    if "@" in stem:
        tail = stem.split("@")[-1]
        for sep in ("_cast", " ", "　"):
            tail = tail.split(sep)[0]
        nm = tail
    if not nm:
        m = re.search(r"(Version[_ ]?[A-Za-z0-9]+)", stem)
        if m:
            nm = m.group(1)
    if not nm:
        nm = stem[-20:]
    return nm[:20].replace("=", "-")


class AutoPairWorker(QObject):
    """音檔長度跟場景卡大致一樣長的時候，直接算出對應點（kkcutscene.pairs_by_length）。

    要讀整張卡才知道每一段的起訖和時間流速，大卡要好幾秒，所以放背景。
    """
    done = pyqtSignal(int, str, object, str)       # 序號, 卡片, (pairs, info), 錯誤訊息

    def __init__(self):
        super().__init__()
        self._a = None

    def submit(self, card, files, seq, any_length=False):
        self._a = (card, list(files), seq, any_length)

    @pyqtSlot()
    def run(self):
        card, files, seq, any_length = self._a
        try:
            import kkcutscene as K
            res = K.auto_pairs_for_card(card, files, force=any_length)
            self.done.emit(seq, card, res, "")
        except BaseException as e:                 # load_segments 會丟 SystemExit
            self.done.emit(seq, card, None, str(e) or e.__class__.__name__)


class FileList(QTableWidget):
    """影片和音訊都放這裡。勾起來的音訊當配音版本，勾起來的影片可以批次抽 wav。"""
    COLS = (T("用"), T("名稱"), T("類型"), T("路徑"))
    changed = pyqtSignal()

    def __init__(self):
        super().__init__(0, 4)
        self.setHorizontalHeaderLabels(self.COLS)
        self.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
        self.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        h = self.horizontalHeader()
        h.setSectionResizeMode(0, QHeaderView.ResizeMode.ResizeToContents)
        h.setSectionResizeMode(1, QHeaderView.ResizeMode.ResizeToContents)
        h.setSectionResizeMode(2, QHeaderView.ResizeMode.ResizeToContents)
        h.setSectionResizeMode(3, QHeaderView.ResizeMode.Stretch)
        self.setMaximumHeight(150)
        self.setSortingEnabled(True)          # 點標題排序
        self.source = ""                 # 哪一支是「來源影片」（播放器和插件都用它）
        self.itemChanged.connect(lambda *_: self.changed.emit())

    def set_source(self, path):
        self.source = os.path.abspath(path) if path else ""
        # 改的是「類型」那一欄（"影片" ↔ "影片 ★來源"）。如果使用者點過類型標題排序，
        # 排序開著的時候改值會讓那一列當場跳位，for 迴圈的 r 就對不上了 ——
        # 有的列被跳過、舊的 ★ 留著沒清掉，畫面上同時兩列標 ★，粗體還跑到第三列去。
        was = self.isSortingEnabled()
        self.setSortingEnabled(False)
        try:
            for r in range(self.rowCount()):
                if self.kind(r).startswith(T("影片")):
                    same = self.path(r) == self.source
                    self.item(r, 2).setText(T("影片 ★來源") if same else T("影片"))
                    f = self.item(r, 1).font()
                    f.setBold(same)
                    for c in range(4):
                        if self.item(r, c):
                            self.item(r, c).setFont(f)
        finally:
            self.setSortingEnabled(was)

    def source_path(self):
        """沒指定的話就用清單裡第一支影片。"""
        if self.source and os.path.isfile(self.source):
            return self.source
        for r in range(self.rowCount()):
            if self.kind(r).startswith(T("影片")):
                return self.path(r)
        return ""

    def add(self, paths):
        have = {self.path(r) for r in range(self.rowCount())}
        added = 0
        was = self.isSortingEnabled()
        self.setSortingEnabled(False)         # 插入途中排序會把列打亂
        for p in paths:
            p = os.path.abspath(p)
            if p in have or not os.path.isfile(p):
                continue
            ext = os.path.splitext(p)[1].lower()
            if ext in AUDIO_EXT:
                kind = T("音訊")
            elif ext in VIDEO_EXT:
                kind = T("影片")
            else:
                continue
            r = self.rowCount()
            self.insertRow(r)
            chk = QTableWidgetItem("")
            chk.setFlags(Qt.ItemFlag.ItemIsEnabled | Qt.ItemFlag.ItemIsUserCheckable
                         | Qt.ItemFlag.ItemIsSelectable)
            chk.setCheckState(Qt.CheckState.Checked)
            self.setItem(r, 0, chk)
            self.setItem(r, 1, QTableWidgetItem(variant_name(p) if kind == T("音訊")
                                                else os.path.basename(p)))
            self.setItem(r, 2, QTableWidgetItem(kind))
            self.setItem(r, 3, QTableWidgetItem(p))
            have.add(p)
            added += 1
        self.setSortingEnabled(was)
        if added:
            self.changed.emit()
        return added

    def path(self, r):
        it = self.item(r, 3)
        return it.text() if it else ""

    def kind(self, r):
        it = self.item(r, 2)
        return it.text() if it else ""

    def checked(self, kind=None):
        out = []
        for r in range(self.rowCount()):
            it = self.item(r, 0)
            if it and it.checkState() == Qt.CheckState.Checked:
                if kind is None or self.kind(r).startswith(kind):
                    out.append((self.item(r, 1).text(), self.path(r)))
        return out

    def set_all(self, on):
        st = Qt.CheckState.Checked if on else Qt.CheckState.Unchecked
        for r in range(self.rowCount()):
            if self.item(r, 0):
                self.item(r, 0).setCheckState(st)

    def remove_selected(self):
        for r in sorted({i.row() for i in self.selectedItems()}, reverse=True):
            self.removeRow(r)
        # ★來源被刪掉的話要跟著清掉。source_path() 只看路徑存不存在、不看還在不在清單，
        # 不清的話：畫面上沒有任何一列標 ★（看起來像沒選），json 裡卻還是寫那支被刪掉的影片。
        if self.source and self.source not in {self.path(r) for r in range(self.rowCount())}:
            self.source = ""
        self.changed.emit()

    def clear_all(self):
        """整張清單清掉（換一張卡的時候用）。"""
        was = self.isSortingEnabled()
        self.setSortingEnabled(False)
        self.setRowCount(0)
        self.setSortingEnabled(was)
        self.source = ""
        self.changed.emit()


class VideoWindow(QDialog):
    """把播放器整塊搬進來的獨立視窗。關掉時原封不動搬回去 ——
    同一個 QMediaPlayer，播放位置和所有按鍵都跟著走，不用重新載入。

    這裡**不覆寫 closeEvent**：QDialog 按 Esc 走的是 reject() → done() → hide()，
    那條路徑根本不發 QCloseEvent。第一版靠 closeEvent 把播放器交還，結果
    彈出後按一下 Esc，視窗藏起來、播放器還留在裡面，這一頁唯一要動手的工作
    （抓對應點）就整個沒了，而且沒有任何方法收回來，只能重開程式。
    改用 finished 訊號 —— 關視窗、Esc、程式呼叫 done() 三條路都會發。
    """
    def __init__(self, parent, pane):
        super().__init__(parent)
        self.setWindowTitle(T("影片預覽"))
        # QDialog 預設沒有最大化 / 最小化鈕，要自己掛上去
        self.setWindowFlags(Qt.WindowType.Window
                            | Qt.WindowType.WindowMinimizeButtonHint
                            | Qt.WindowType.WindowMaximizeButtonHint
                            | Qt.WindowType.WindowCloseButtonHint)
        self.resize(1280, 720)
        self.setSizeGripEnabled(True)
        lay = QVBoxLayout(self)
        lay.setContentsMargins(4, 4, 4, 4)
        lay.addWidget(pane, 1)


# ------------------------------------------------------------------ 對應點表

COLS = ("timeline", T("影片"), T("說明"))


class PairTable(QTableWidget):
    def __init__(self):
        super().__init__(0, len(COLS))
        self.setHorizontalHeaderLabels(COLS)
        self.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        h = self.horizontalHeader()
        h.setSectionResizeMode(0, QHeaderView.ResizeMode.ResizeToContents)
        h.setSectionResizeMode(1, QHeaderView.ResizeMode.ResizeToContents)
        h.setSectionResizeMode(2, QHeaderView.ResizeMode.Stretch)
        f = QFont("Consolas")
        f.setStyleHint(QFont.StyleHint.Monospace)
        self.setFont(f)
        self._loading = False
        self.itemChanged.connect(self._on_edit)

    def _on_edit(self, item):
        if self._loading or item is None or item.column() > 1:
            return
        self._loading = True
        try:
            self.note_edit(item.row())
        finally:
            self._loading = False

    def add_row(self, t="", v="", note="", select=True):
        r = self.rowCount()
        self._loading = True
        try:
            self.insertRow(r)
            for c, val in enumerate((t, v, note)):
                self.setItem(r, c, QTableWidgetItem(str(val)))
        finally:
            self._loading = False
        if select:
            self.selectRow(r)
        return r

    def set_video(self, sec):
        r = self.currentRow()
        if r < 0:
            r = self.add_row()
        self.setItem(r, 1, QTableWidgetItem(fmt_time(sec)))

    def rows(self):
        """回傳 [(timeline秒, 影片秒, 說明)]，看不懂的行拋 ValueError。"""
        out = []
        for r in range(self.rowCount()):
            a = (self.item(r, 0).text() if self.item(r, 0) else "").strip()
            b = (self.item(r, 1).text() if self.item(r, 1) else "").strip()
            n = (self.item(r, 2).text() if self.item(r, 2) else "").strip()
            if not a and not b:
                continue
            if not a or not b:
                raise ValueError(T("第 %d 列只填了一半") % (r + 1))
            try:
                out.append((parse_time(a), parse_time(b), n))
            except ValueError as e:
                raise ValueError(T("第 %d 列：%s") % (r + 1, e))
        out.sort()
        return out

    def load_rows(self, rows):
        self._loading = True
        try:
            self.setRowCount(0)
            for t, v, n in rows:
                self.add_row(fmt_time(t), fmt_time(v), n, select=False)
        finally:
            self._loading = False

    AUTO_TAG = "（自動對齊）"
    EDITED_TAG = "（自動對齊，已手動改過）"

    def note_edit(self, r):
        """某一列的秒數被手動改過之後，把說明裡的「自動對齊」改掉。

        為什麼一定要做這件事：自動對齊產生的說明會留在那一列，
        但人常常只改數字（例如把段尾改成自己量到的一個好認的瞬間）。
        改完之後那一列的說明就在說謊 —— 它不再是自動對齊的值了。
        後面「這一列是不是別張卡的舊點」的檢查就是靠這個說明判斷的，
        不改的話會把正當的手動點誤報成舊點（實際踩過一次）。
        """
        it = self.item(r, 2)
        note = it.text() if it else ""
        if self.AUTO_TAG not in note or self.EDITED_TAG in note:
            return
        new = note.replace(self.AUTO_TAG, self.EDITED_TAG)
        if it is None:
            self.setItem(r, 2, QTableWidgetItem(new))
        else:
            it.setText(new)

    def flag_row(self, r, why):
        """把一列標成「有問題」，滑鼠移上去說明為什麼。

        用在「這一列是別張卡算出來的舊點」—— 那種東西看起來完全正常
        （格式對、數字合理），但會把那一段的時基整個釘歪，
        而且產生出來的 json 也不會報錯。不標紅就只能靠人自己想起來。
        """
        for c in range(self.columnCount()):
            it = self.item(r, c)
            if it is None:
                it = QTableWidgetItem("")
                self.setItem(r, c, it)
            it.setBackground(QColor(255, 226, 226))
            it.setToolTip(why)

    def clear_flags(self):
        for r in range(self.rowCount()):
            for c in range(self.columnCount()):
                it = self.item(r, c)
                if it is not None:
                    it.setBackground(QColor(0, 0, 0, 0))
                    it.setToolTip("")


def read_pairs_file(path):
    """讀 pairs.txt，連 # 後面的說明一起留著。"""
    rows = []
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
                rows.append((parse_time(toks[0]), parse_time(toks[1]), note.strip()))
            except ValueError:
                continue
    rows.sort()
    return rows


# ------------------------------------------------------------ 從切好的音頻反推
class AlignWorker(QObject):
    """在背景跑 kkaudioalign 和卡片的段落解析。

    兩件事都不能放在 UI 執行緒：半小時的原檔比對要十幾秒，
    大張場景卡光是解析就要好幾秒，卡住的話視窗會變成白的、看起來像當掉。
    """
    line = pyqtSignal(str)
    done = pyqtSignal(bool, object, object)        # ok, pairs, problems
    segs_ready = pyqtSignal(bool, object, str)     # ok, segs, 錯誤訊息

    def __init__(self):
        super().__init__()
        self._a = None
        self._card = ""

    def submit_segs(self, card):
        self._card = card

    @pyqtSlot()
    def run_segs(self):
        try:
            segs = load_card_segments(self._card)
            self.segs_ready.emit(True, segs, "")
        except BaseException as e:                 # load_segments 會丟 SystemExit
            self.segs_ready.emit(False, [], str(e) or e.__class__.__name__)

    def submit(self, orig, parts, assign, win=90.0):
        self._a = (orig, parts, assign, win)

    @pyqtSlot()
    def run(self):
        orig, parts, assign, win = self._a
        try:
            import kkaudioalign as A
            _, pairs, problems, summary = A.run_align(
                orig, parts, segs=None, assign=assign, win=win,
                log=lambda s: self.line.emit(str(s)))
            self.done.emit(True, pairs, dict(problems=problems, summary=summary))
        except BaseException as e:                 # SystemExit 也要接住
            self.line.emit(T("失敗：%s") % e)
            self.line.emit(traceback.format_exc())
            self.done.emit(False, [], dict(problems=[str(e) or e.__class__.__name__],
                                           summary=None))


def load_card_segments(card):
    """從合併好的場景卡讀出每一段的時間軸起訖。"""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import kkcutscene as K

    class _Args:
        pass
    shim = _Args()
    shim.segments = None
    shim.card = card
    return K.load_segments(shim)


class AlignDialog(QDialog):
    """以前用 VNGE 做的卡片，音檔是一段一段切好的。新插件不切割 ——
    它播原始長音檔，靠對應點把時間軸對上去。這個視窗就是把前者換算成後者。

    需要三樣東西：
      合併好的場景卡  提供每一段的時間軸起訖（也是之後 --auto-slope 讀 timeScale 的來源）
      原始音檔        沒切過的那一支
      切好的片段      每一個都要指定它對應卡片上的哪一段

    最後那件事是這一版才補上的。原本是「照 PT 編號順序配」，
    但 PT 編號跟合併後的段落順序不見得一致（合併時換過順序、
    一張卡拆成兩段、中間插了別的卡…），猜錯了產生的對應點會整段歪掉
    而且完全看不出來。所以改成每一列自己選，預設照順序填好讓人確認。
    """

    def __init__(self, parent, card, apply_cb):
        super().__init__(parent)
        self.setWindowTitle(T("從切好的音頻反推對應點"))
        self.setWindowFlags(Qt.WindowType.Window
                            | Qt.WindowType.WindowMinimizeButtonHint
                            | Qt.WindowType.WindowMaximizeButtonHint
                            | Qt.WindowType.WindowCloseButtonHint)
        self.resize(980, 700)
        self._apply_cb = apply_cb
        self._pairs = []
        self._segs = []

        lay = QVBoxLayout(self)
        lay.addWidget(QLabel(
            T("把切好的片段拿去跟原始音檔比對，量出每一段的頭尾落在原檔的第幾秒，\n配上卡片的段落時間軸，就是新插件要的對應點。\n檔案可以直接拖進這個視窗：.png 當場景卡，音訊進下面的清單。")))

        r0 = QHBoxLayout()
        lb = QLabel(T("合併好的場景卡"))
        lb.setFixedWidth(104)
        r0.addWidget(lb)
        self.ed_card = QLineEdit(card or "")
        self.ed_card.setPlaceholderText(T("要有它才知道每一段的時間軸是幾秒到幾秒"))
        self.ed_card.editingFinished.connect(self._reload_segs)
        r0.addWidget(self.ed_card, 1)
        b = QPushButton(T("瀏覽…"))
        b.clicked.connect(self._pick_card)
        r0.addWidget(b)
        lay.addLayout(r0)

        r1 = QHBoxLayout()
        lb = QLabel(T("原始音檔"))
        lb.setFixedWidth(104)
        r1.addWidget(lb)
        self.ed_orig = QLineEdit()
        self.ed_orig.setPlaceholderText(T("沒切過的那一支（bak 資料夾裡的原音頻）"))
        r1.addWidget(self.ed_orig, 1)
        b = QPushButton(T("瀏覽…"))
        b.clicked.connect(self._pick_orig)
        r1.addWidget(b)
        lay.addLayout(r1)

        self.lbl_segs = QLabel("")
        self.lbl_segs.setTextFormat(Qt.TextFormat.RichText)
        lay.addWidget(self.lbl_segs)

        lay.addWidget(QLabel(T("切好的片段 —— 右邊選它對應卡片上的哪一段")))
        self.tbl = QTableWidget(0, 2)
        self.tbl.setHorizontalHeaderLabels((T("切好的音頻"), T("對應卡片的哪一段")))
        self.tbl.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.tbl.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        h = self.tbl.horizontalHeader()
        h.setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        h.setSectionResizeMode(1, QHeaderView.ResizeMode.ResizeToContents)
        lay.addWidget(self.tbl, 1)

        r2 = QHBoxLayout()
        for txt, fn in ((T("加入檔案…"), self._add_files),
                        (T("加入資料夾…"), self._add_folder),
                        (T("移除選取"), self._remove),
                        (T("↑ 設為原始音檔"), self._to_orig),
                        (T("依順序重新配段"), self._repair_assign)):
            bb = QPushButton(txt)
            bb.clicked.connect(fn)
            r2.addWidget(bb)
        r2.addStretch(1)
        lay.addLayout(r2)

        r3 = QHBoxLayout()
        self.btn_run = QPushButton(T("開始比對"))
        self.btn_run.clicked.connect(self._run)
        r3.addWidget(self.btn_run)
        self.btn_apply = QPushButton(T("套用到對應點表格"))
        self.btn_apply.setEnabled(False)
        self.btn_apply.clicked.connect(self._apply)
        r3.addWidget(self.btn_apply)
        r3.addStretch(1)
        lay.addLayout(r3)

        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        f = QFont("Consolas")
        f.setStyleHint(QFont.StyleHint.Monospace)
        self.log.setFont(f)
        lay.addWidget(self.log, 2)

        self.worker = AlignWorker()
        self.thread = QThread(self)
        self.worker.moveToThread(self.thread)
        self.worker.line.connect(self._line)
        self.worker.done.connect(self._done)
        self.worker.segs_ready.connect(self._segs_done)
        self.thread.start()
        # 收執行緒掛在 finished 上，不要掛 closeEvent —— 說明見 _cleanup
        self.finished.connect(self._cleanup)

        self.setAcceptDrops(True)
        if card:
            self._reload_segs()
        else:
            self._show_segs_state()

    # ------------------------------------------------------------ 防呆

    def _guard(self, fn, *a):
        """PyQt6 的 slot 裡漏接例外會直接 abort 整個行程 ——
        使用者看到的就是「拖進去就閃退」，連訊息都沒有。
        所有從介面進來的動作都包一層，把錯誤變成看得懂的對話框。
        """
        try:
            return fn(*a)
        except Exception as e:
            self._line(T("【錯誤】%s") % e)
            self._line(traceback.format_exc())
            QMessageBox.warning(self, T("出錯了（視窗沒關）"),
                                T("%s\n\n完整內容看下面的紀錄。") % e)
            return None

    # ------------------------------------------------------------ 拖放

    def dragEnterEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()

    def dragMoveEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()

    def dropEvent(self, e):
        self._guard(self._drop, e)

    def _drop(self, e):
        audio, took = [], False
        for url in e.mimeData().urls():
            p = url.toLocalFile()
            if not p:
                continue
            if os.path.isdir(p):
                audio += [os.path.join(p, f) for f in sorted(os.listdir(p))]
            elif p.lower().endswith(".png"):
                self.ed_card.setText(p)
                self._reload_segs()
                took = True
            else:
                audio.append(p)
        if audio and self._add(audio):
            took = True
        if took:
            e.acceptProposedAction()

    # ------------------------------------------------------------ 段落

    def _pick_card(self):
        p, _ = QFileDialog.getOpenFileName(self, T("選合併好的場景卡"),
                                           self.ed_card.text(), T("場景卡 (*.png)"))
        if p:
            self.ed_card.setText(p)
            self._reload_segs()

    def _reload_segs(self):
        card = self.ed_card.text().strip()
        if not card or not os.path.isfile(card):
            self._segs = []
            self._show_segs_state()
            return
        self.lbl_segs.setText(T("讀取卡片的段落中…"))
        self.worker.submit_segs(card)
        QMetaObject.invokeMethod(self.worker, "run_segs",
                                 Qt.ConnectionType.QueuedConnection)

    def _segs_done(self, ok, segs, err):
        if not ok:
            self._segs = []
            self.lbl_segs.setText(
                T("<span style='color:%s'>卡片讀不出段落：%s</span>") % (RED, err))
            self._line(T("卡片讀不出段落：%s") % err)
            return
        self._segs = list(segs)
        self._show_segs_state()
        self._refill_combos()
        self._repair_assign()

    def _seg_label(self, s):
        import kkaudioalign as A
        return T("第%d段  %s  (%s ~ %s)") % (s["index"], s["name"][:18],
                                          A.P_fmt(s["start"]), A.P_fmt(s["end"]))

    def _show_segs_state(self):
        if self._segs:
            self.lbl_segs.setText(T("卡片有 <b>%d</b> 段：%s")
                                  % (len(self._segs),
                                     "、".join(T("第%d段 %s") % (s["index"], s["name"][:14])
                                               for s in self._segs)))
        else:
            self.lbl_segs.setText(
                T("<span style='color:%s'>還沒有段落資料 —— 指定合併好的場景卡才算得出對應點</span>") % RED)

    # ------------------------------------------------------------ 片段清單

    def _add(self, paths):
        import kkaudioalign as A
        have = {self._path(r) for r in range(self.tbl.rowCount())}
        got = [p for p in paths
               if p.lower().endswith(A.AUDIO_EXT)
               and os.path.isfile(p) and p not in have]
        if not got:
            return 0
        got.sort(key=A.part_key)
        for p in got:
            r = self.tbl.rowCount()
            self.tbl.insertRow(r)
            it = QTableWidgetItem(os.path.basename(p))
            it.setToolTip(p)
            it.setData(Qt.ItemDataRole.UserRole, p)     # 存字串，不存 dict
            self.tbl.setItem(r, 0, it)
            cb = QComboBox()
            self._fill_combo(cb)
            self.tbl.setCellWidget(r, 1, cb)
        self._repair_assign()
        return len(got)

    def _path(self, r):
        it = self.tbl.item(r, 0)
        return it.data(Qt.ItemDataRole.UserRole) if it else ""

    def _fill_combo(self, cb):
        cur = cb.currentData()
        cb.clear()
        cb.addItem(T("（不用這個片段）"), -1)
        for s in self._segs:
            cb.addItem(self._seg_label(s), s["index"])
        if cur is not None:
            i = cb.findData(cur)
            if i >= 0:
                cb.setCurrentIndex(i)

    def _refill_combos(self):
        for r in range(self.tbl.rowCount()):
            cb = self.tbl.cellWidget(r, 1)
            if cb is not None:
                self._fill_combo(cb)

    def _repair_assign(self):
        """照清單順序配段：第一列配第一段、第二列配第二段…

        只是個起點，配不對的自己用下拉改。多出來的列會留在「不用這個片段」。
        """
        for r in range(self.tbl.rowCount()):
            cb = self.tbl.cellWidget(r, 1)
            if cb is None:
                continue
            want = self._segs[r]["index"] if r < len(self._segs) else -1
            i = cb.findData(want)
            cb.setCurrentIndex(max(0, i))

    def _add_files(self):
        ps, _ = QFileDialog.getOpenFileNames(
            self, T("選切好的片段"), "",
            T("音訊 (*.wav *.flac *.ogg *.m4a *.mp3);;全部 (*.*)"))
        self._guard(self._add, ps)

    def _add_folder(self):
        d = QFileDialog.getExistingDirectory(self, T("選一個資料夾"))
        if d:
            self._guard(self._add,
                        [os.path.join(d, f) for f in sorted(os.listdir(d))])

    def _remove(self):
        for r in sorted({i.row() for i in self.tbl.selectedItems()}, reverse=True):
            self.tbl.removeRow(r)

    def _to_orig(self):
        r = self.tbl.currentRow()
        if r < 0:
            return
        self.ed_orig.setText(self._path(r))
        self.tbl.removeRow(r)

    def _pick_orig(self):
        p, _ = QFileDialog.getOpenFileName(
            self, T("選原始（沒切過的）音檔"), self.ed_orig.text(),
            T("音訊/影片 (*.wav *.flac *.ogg *.m4a *.mp3 *.mp4 *.mkv);;全部 (*.*)"))
        if p:
            self.ed_orig.setText(p)

    # ------------------------------------------------------------ 執行

    def _run(self):
        self._guard(self._run_inner)

    def _run_inner(self):
        orig = self.ed_orig.text().strip()
        if not orig or not os.path.isfile(orig):
            QMessageBox.warning(self, T("還不能跑"), T("先指定原始音檔"))
            return
        if not self._segs:
            QMessageBox.warning(self, T("還不能跑"),
                                T("先指定合併好的場景卡 —— 沒有段落的時間軸就算不出對應點"))
            return

        by_index = {s["index"]: s for s in self._segs}
        parts, assign = [], []
        for r in range(self.tbl.rowCount()):
            p = self._path(r)
            cb = self.tbl.cellWidget(r, 1)
            idx = cb.currentData() if cb is not None else -1
            if not p or idx is None or idx < 0:
                continue
            parts.append(p)
            assign.append(by_index.get(idx))
        if not parts:
            QMessageBox.warning(self, T("還不能跑"),
                                T("沒有任何片段指定了對應的段落"))
            return
        if orig in parts:
            QMessageBox.warning(self, T("重複了"), T("原始音檔也出現在片段清單裡，移掉它"))
            return

        self.btn_run.setEnabled(False)
        self.btn_apply.setEnabled(False)
        self.log.setPlainText("")
        for p, s in zip(parts, assign):
            self._line(T("%-40s → 第%d段 %s")
                       % (os.path.basename(p)[:40], s["index"], s["name"][:20]))
        self.worker.submit(orig, parts, assign)
        QMetaObject.invokeMethod(self.worker, "run", Qt.ConnectionType.QueuedConnection)

    def _line(self, s):
        self.log.appendPlainText(s)
        self.log.verticalScrollBar().setValue(self.log.verticalScrollBar().maximum())

    def _done(self, ok, pairs, extra):
        self.btn_run.setEnabled(True)
        self._pairs = list(pairs or [])
        extra = extra or {}
        problems = extra.get("problems") or []
        self._summary = extra.get("summary")

        if problems:
            self.log.appendHtml(
                "<br><span style='color:%s'><b>%s</b></span>"
                % (RED, "<br>".join(("！ " + str(p)).replace(" ", "&nbsp;")
                                    for p in problems)))
        # 片段完整蓋滿原檔 ＝ 沒有開場/過場/片尾 ＝ 這張卡根本不用影片。
        # 這件事講不清楚的話，使用者會繼續去找一支根本不需要的影片。
        if self._summary and self._summary.get("full"):
            self.log.appendHtml(
                T("<br><span style='color:#1a7f37'><b>★&nbsp;不需要影片：片段完整蓋滿原檔，沒有開場動畫、過場或片尾。<br>&nbsp;&nbsp;&nbsp;產生&nbsp;cutscene.json&nbsp;時不用指定&nbsp;★來源影片，把配音勾起來就好。</b></span>"))
        if ok and self._pairs:
            self.log.appendPlainText(
                T("\n量出 %d 個對應點，按「套用到對應點表格」帶進去。") % len(self._pairs))
            self.btn_apply.setEnabled(True)
        self.log.verticalScrollBar().setValue(self.log.verticalScrollBar().maximum())

    def _apply(self):
        if not self._pairs:
            return
        self._apply_cb(self._pairs, getattr(self, "_summary", None))
        self.accept()

    def _cleanup(self, *_):
        """視窗結束就把背景執行緒收掉 —— 按套用、按取消、按右上角 X 都算。

        血淚：原本只寫在 closeEvent 裡。QDialog 的 accept() 走的是
        done() → hide()，**不發 QCloseEvent** —— 所以按「套用」時執行緒沒被收掉，
        exec() 回來、dialog 被 Python 回收，QThread 帶著還在跑的事件迴圈被解構，
        Qt 直接 abort。使用者看到的就是「按套用就閃退」。
        （跟 VideoWindow 按 Esc 那次是同一個坑：QDialog 結束不等於 close。）
        finished 訊號三條路都會發，所以掛在它上面。
        """
        th, self.thread = getattr(self, "thread", None), None
        if th is None:
            return
        th.quit()
        if not th.wait(3000):
            th.terminate()
            th.wait(1000)

# ------------------------------------------------------------ 配音對照

class VariantScanWorker(QObject):
    """互相關掃描要讀整支音檔、做好幾十次 FFT，放在 UI 執行緒會整個凍住。"""
    line = pyqtSignal(str)
    one = pyqtSignal(str, object, str)      # 名稱, 點, 失敗原因
    done = pyqtSignal()

    def __init__(self):
        super().__init__()
        self._jobs = None

    def submit(self, ref_file, jobs, probes=24, win=20.0):
        self._jobs = (ref_file, list(jobs), probes, win)

    @pyqtSlot()
    def run(self):
        ref_file, jobs, probes, win = self._jobs
        try:
            import kkvariantmap as VM
        except Exception as e:                              # noqa: BLE001
            self.line.emit(T("載入不了 kkvariantmap：%s") % e)
            self.done.emit()
            return
        for name, path in jobs:
            self.line.emit("")
            self.line.emit("=" * 56)
            self.line.emit(T("掃描配音「%s」") % name)
            try:
                pts, why = VM.scan(ref_file, path, probes=probes, win=win,
                                   log=lambda s: self.line.emit(str(s)))
            except BaseException as e:                      # noqa: BLE001
                pts, why = [], "%s" % e
                self.line.emit(traceback.format_exc())
            self.one.emit(name, pts, why)
        self.done.emit()


class VariantMapDialog(QDialog):
    """管理各配音版本之間的時間對照。

    什麼時候需要它：作者放出的各配音版本通常是同一套剪輯重新上音，
    每一版的秒數完全一樣，一份 anchors 通吃 —— 那就完全不用開這個視窗。

    例外是其中一版沒跟著做最後的剪輯（少了開場、中間多剪掉一塊）。
    那一版的秒數跟主配音對不上，而且不是固定偏移，差距會一路變。
    這裡就是量那條差距。

    量法兩種，可以混著用：
      自動  互相關掃全曲。兩版底下的音效／音樂是同一條時有效
      手動  在 Studio 裡找一個好認的瞬間，兩版各記一次秒數

    存在卡片旁邊的 <卡片>.variants.json，產生 cutscene.json 時自動帶入。
    """

    def __init__(self, parent, card, variants, ref_guess="", extra_dirs=()):
        super().__init__(parent)
        self.setWindowTitle(T("配音對照 —— 剪輯不一樣的版本"))
        self.setWindowFlags(Qt.WindowType.Window
                            | Qt.WindowType.WindowMinimizeButtonHint
                            | Qt.WindowType.WindowMaximizeButtonHint
                            | Qt.WindowType.WindowCloseButtonHint)
        self.resize(940, 680)
        self._card = card
        self._variants = list(variants)            # [(名稱, 路徑)]
        self._durs = {}
        self._cur = ""
        self._rep_cache = None

        import kkvariantmap as VM
        self._VM = VM
        self._extra = list(extra_dirs or ())
        ref, maps, self._src = VM.load_maps(card, self._extra)
        self._maps = maps
        if not ref:
            ref = ref_guess or (self._variants[0][0] if self._variants else "")

        lay = QVBoxLayout(self)
        lay.addWidget(QLabel(
            T("各版配音如果是同一套剪輯重新上音（多數情況），這裡什麼都不用做。\n只有「某一版少了開場 / 中間剪掉一塊」時才要量對照點 —— 對照表是「主配音的第幾秒 → 這一版的第幾秒」。")))

        r0 = QHBoxLayout()
        r0.addWidget(QLabel(T("主配音")))
        self.cmb_ref = QComboBox()
        for n, _p in self._variants:
            self.cmb_ref.addItem(n)
        i = self.cmb_ref.findText(ref)
        if i >= 0:
            self.cmb_ref.setCurrentIndex(i)
        self.cmb_ref.currentTextChanged.connect(lambda _t: self._refresh())
        self.cmb_ref.setToolTip(T("對應點表格（timeline → 影片）量的是哪一版的秒數。\n通常是跟來源影片一樣長的那一版。"))
        r0.addWidget(self.cmb_ref)
        self.lbl_len = QLabel("")
        r0.addWidget(self.lbl_len)
        r0.addStretch(1)
        lay.addLayout(r0)

        split = QSplitter(Qt.Orientation.Horizontal)

        left = QWidget()
        ll = QVBoxLayout(left)
        ll.setContentsMargins(0, 0, 0, 0)
        ll.addWidget(QLabel(T("配音版本")))
        self.tbl_v = QTableWidget(0, 4)
        self.tbl_v.setHorizontalHeaderLabels((T("配音"), T("長度"), T("對照點"), T("狀態")))
        self.tbl_v.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.tbl_v.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.tbl_v.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.tbl_v.itemSelectionChanged.connect(self._pick)
        hh = self.tbl_v.horizontalHeader()
        hh.setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        for c in (1, 2, 3):
            hh.setSectionResizeMode(c, QHeaderView.ResizeMode.ResizeToContents)
        ll.addWidget(self.tbl_v, 1)
        b = QPushButton(T("自動掃所有需要的版本"))
        b.setToolTip(T("長度跟主配音不一樣、而且還沒有對照表的版本，全部掃一次。\n要讀整支音檔，一個版本大概十幾秒。"))
        b.clicked.connect(lambda: self._scan(all_missing=True))
        ll.addWidget(b)
        b = QPushButton(T("只掃選取的這一個"))
        b.clicked.connect(lambda: self._scan(all_missing=False))
        ll.addWidget(b)
        split.addWidget(left)

        right = QWidget()
        rl = QVBoxLayout(right)
        rl.setContentsMargins(0, 0, 0, 0)
        self.lbl_pts = QLabel(T("對照點"))
        rl.addWidget(self.lbl_pts)
        self.tbl_p = QTableWidget(0, 2)
        self.tbl_p.setHorizontalHeaderLabels((T("主配音秒數"), T("這一版的秒數")))
        self.tbl_p.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        hp = self.tbl_p.horizontalHeader()
        hp.setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        hp.setSectionResizeMode(1, QHeaderView.ResizeMode.Stretch)
        f = QFont("Consolas")
        f.setStyleHint(QFont.StyleHint.Monospace)
        self.tbl_p.setFont(f)
        self.tbl_p.itemChanged.connect(self._pts_edited)
        rl.addWidget(self.tbl_p, 1)

        rr = QHBoxLayout()
        for txt, fn in ((T("新增一列"), self._add_pt),
                        (T("刪除這列"), self._del_pt),
                        (T("全部清掉"), self._clear_pts)):
            bb = QPushButton(txt)
            bb.clicked.connect(fn)
            rr.addWidget(bb)
        rr.addStretch(1)
        rl.addLayout(rr)

        rl.addWidget(QLabel(T("檢查與建議（存檔時會重算）")))
        self.lst = QListWidget()
        self.lst.setMaximumHeight(120)
        rl.addWidget(self.lst)
        split.addWidget(right)
        split.setSizes([380, 540])
        lay.addWidget(split, 1)

        r3 = QHBoxLayout()
        self.btn_save = QPushButton(T("存檔"))
        self.btn_save.setToolTip(T("寫回這張卡的 cutscene.json（還沒產生的話先暫存，產生時自動併入）。"))
        self.btn_save.clicked.connect(self._save)
        r3.addWidget(self.btn_save)
        b = QPushButton(T("關閉"))
        b.clicked.connect(self.reject)
        r3.addWidget(b)
        r3.addStretch(1)
        lay.addLayout(r3)

        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        self.log.setFont(f)
        self.log.setMaximumHeight(150)
        lay.addWidget(self.log)

        self.worker = VariantScanWorker()
        self.thread = QThread(self)
        self.worker.moveToThread(self.thread)
        self.worker.line.connect(self._line)
        self.worker.one.connect(self._scanned)
        self.worker.done.connect(self._scan_done)
        self.thread.start()
        self.finished.connect(self._cleanup)

        self._measure()
        self._refresh()

    # ------------------------------------------------------------ 資料

    def _path_of(self, name):
        for n, p in self._variants:
            if n == name:
                return p
        return ""

    def _measure(self):
        """量一次各配音的長度。長度是「要不要對照表」最便宜的指標。"""
        for n, p in self._variants:
            d = self._VM.file_duration(p)
            if d:
                self._durs[n] = d

    def _refresh(self):
        ref = self.cmb_ref.currentText()
        rd = self._durs.get(ref)
        self.lbl_len.setText((T("長 %.2f 秒") % rd) if rd else T("（量不到長度）"))
        self.tbl_v.blockSignals(True)
        self.tbl_v.setRowCount(0)
        for n, _p in self._variants:
            r = self.tbl_v.rowCount()
            self.tbl_v.insertRow(r)
            d = self._durs.get(n)
            pts = self._maps.get(n) or []
            if n == ref:
                state = T("主配音")
            elif pts:
                state = T("⇄ 有對照表")
            elif rd and d and abs(d - rd) > 0.5:
                state = T("≈ 長度不同，要量！")
            else:
                state = T("≈ 當成同步")
            for c, v in enumerate((n, ("%.2f" % d) if d else "?",
                                   str(len(pts)) if pts else "-", state)):
                self.tbl_v.setItem(r, c, QTableWidgetItem(v))
        self.tbl_v.blockSignals(False)
        if self._cur:
            for r in range(self.tbl_v.rowCount()):
                if self.tbl_v.item(r, 0).text() == self._cur:
                    self.tbl_v.selectRow(r)
                    break
        self._show_pts()

    def _pick(self):
        r = self.tbl_v.currentRow()
        self._cur = self.tbl_v.item(r, 0).text() if r >= 0 else ""
        self._show_pts()

    def _show_pts(self):
        name = self._cur
        ref = self.cmb_ref.currentText()
        self.tbl_p.blockSignals(True)
        self.tbl_p.setRowCount(0)
        if name and name != ref:
            for a, b in (self._maps.get(name) or []):
                r = self.tbl_p.rowCount()
                self.tbl_p.insertRow(r)
                self.tbl_p.setItem(r, 0, QTableWidgetItem(fmt_time(a)))
                self.tbl_p.setItem(r, 1, QTableWidgetItem(fmt_time(b)))
        self.tbl_p.blockSignals(False)
        if not name:
            self.lbl_pts.setText(T("對照點"))
        elif name == ref:
            self.lbl_pts.setText(T("「%s」是主配音，不需要對照表") % name)
        else:
            self.lbl_pts.setText(T("「%s」的對照點 —— 左欄是主配音「%s」的秒數")
                                 % (name, ref))
        self._advise()

    def _read_pts(self):
        out = []
        for r in range(self.tbl_p.rowCount()):
            a = (self.tbl_p.item(r, 0).text() if self.tbl_p.item(r, 0) else "").strip()
            b = (self.tbl_p.item(r, 1).text() if self.tbl_p.item(r, 1) else "").strip()
            if not a and not b:
                continue
            try:
                out.append([parse_time(a), parse_time(b)])
            except ValueError:
                raise ValueError(T("第 %d 列看不懂（格式像 01:12.550 或 72.55）") % (r + 1))
        return out

    def _pts_edited(self, _item):
        if not self._cur or self._cur == self.cmb_ref.currentText():
            return
        try:
            self._maps[self._cur] = self._VM.clean_points(self._read_pts())
        except ValueError as e:
            self._line(T("【格式】%s") % e)
            return
        if not self._maps[self._cur]:
            self._maps.pop(self._cur, None)
        self._advise()

    def _add_pt(self):
        if not self._cur or self._cur == self.cmb_ref.currentText():
            QMessageBox.information(self, T("先選一個版本"),
                                    T("左邊選一個「不是主配音」的版本再加點。"))
            return
        r = self.tbl_p.rowCount()
        self.tbl_p.insertRow(r)
        self.tbl_p.setItem(r, 0, QTableWidgetItem(""))
        self.tbl_p.setItem(r, 1, QTableWidgetItem(""))
        self.tbl_p.selectRow(r)

    def _del_pt(self):
        r = self.tbl_p.currentRow()
        if r >= 0:
            self.tbl_p.removeRow(r)
            self._pts_edited(None)

    def _clear_pts(self):
        if not self._cur:
            return
        self._maps.pop(self._cur, None)
        self._show_pts()
        self._refresh()

    # ------------------------------------------------------------ 檢查

    def _advise(self):
        self.lst.clear()
        name = self._cur
        ref = self.cmb_ref.currentText()
        if not name or name == ref:
            return
        rep = self._report()
        if rep is None:
            self.lst.addItem(T("讀不到卡片的段落 —— 只能檢查對照點本身，沒辦法告訴你哪一段沒被蓋到"))
            rep = []
        p, sug = self._VM.check(rep, name, self._maps.get(name) or [],
                                self._durs.get(name), self._durs.get(ref))
        for m in p:
            self.lst.addItem("! " + m)
        for idx, x, y, why in sug:
            self.lst.addItem(T("建議：段 %d   主配音 %s  →  預估這一版 %s   （%s）")
                             % (idx, fmt_time(x), fmt_time(y), why))
        if not p and not sug:
            self.lst.addItem(T("沒發現問題。"))

    def _report(self):
        """拿卡片的段落 + 對應點算出每一段落在主配音的哪個區間。

        這是「哪一段沒有對照點」唯一的判斷依據 —— 沒有它就只能看點的清單，
        看不出覆蓋。讀不到就回 None，讓上層講清楚少了什麼。
        """
        if getattr(self, "_rep_cache", None) is not None:
            return self._rep_cache
        try:
            import kkcutscene as K
            segs = load_card_segments(self._card)
            pf = os.path.splitext(self._card)[0] + ".pairs.txt"
            pairs = K.read_pairs(pf) if os.path.isfile(pf) else []
            byseg, _o = K.assign_pairs(segs, pairs)
            _t, rep = K.build_tracks(segs, byseg)
            self._rep_cache = rep
            return rep
        except BaseException as e:                          # noqa: BLE001
            self._line(T("讀不到卡片段落：%s") % e)
            return None

    # ------------------------------------------------------------ 掃描

    def _scan(self, all_missing):
        ref = self.cmb_ref.currentText()
        rf = self._path_of(ref)
        if not rf or not os.path.isfile(rf):
            QMessageBox.warning(self, T("主配音檔案不見了"),
                                T("找不到「%s」的音檔，沒辦法拿它當基準。") % ref)
            return
        jobs = []
        if all_missing:
            rd = self._durs.get(ref)
            for n, p in self._variants:
                if n == ref or self._maps.get(n):
                    continue
                d = self._durs.get(n)
                if rd and d and abs(d - rd) <= 0.5:
                    continue                # 長度一樣，幾乎一定是同一套剪輯
                jobs.append((n, p))
            if not jobs:
                QMessageBox.information(
                    self, T("沒有需要掃的"),
                    T("其他版本要嘛長度跟主配音一樣（同一套剪輯，不用對照表），要嘛已經有對照表了。\n\n想重掃某一個，選它再按「只掃選取的這一個」。"))
                return
        else:
            if not self._cur or self._cur == ref:
                QMessageBox.information(self, T("先選一個版本"),
                                        T("左邊選一個「不是主配音」的版本。"))
                return
            jobs = [(self._cur, self._path_of(self._cur))]

        self.btn_save.setEnabled(False)
        self._line("=" * 56)
        self._line(T("基準（主配音）：%s") % ref)
        self.worker.submit(rf, jobs)
        QMetaObject.invokeMethod(self.worker, "run",
                                 Qt.ConnectionType.QueuedConnection)

    def _scanned(self, name, pts, why):
        if not pts:
            self._line(T("配音「%s」掃不出來：%s") % (name, why))
            return
        self._maps[name] = pts
        self._line(T("配音「%s」得到 %d 個對照點") % (name, len(pts)))

    def _scan_done(self):
        self.btn_save.setEnabled(True)
        self._refresh()

    # ------------------------------------------------------------ 雜項

    def _line(self, s):
        self.log.appendPlainText(str(s))
        self.log.verticalScrollBar().setValue(
            self.log.verticalScrollBar().maximum())

    def _save(self):
        try:
            if self._cur and self._cur != self.cmb_ref.currentText():
                self._maps[self._cur] = self._VM.clean_points(self._read_pts())
                if not self._maps[self._cur]:
                    self._maps.pop(self._cur, None)
        except ValueError as e:
            QMessageBox.warning(self, T("對照點格式不對"), str(e))
            return
        ref = self.cmb_ref.currentText()
        self._maps.pop(ref, None)          # 主配音對自己的對照表是恆等
        try:
            p, kind = self._VM.save_maps(self._card, ref, self._maps, self._extra)
        except Exception as e:                              # noqa: BLE001
            QMessageBox.warning(self, T("存不起來"), str(e))
            return
        self._line(T("已存 %s（%s）") % (p, kind))
        if kind == "json":
            msg = (T("已寫回：\n%s\n\n插件下次載入這張卡就讀得到了，不用重新產生。\n（重新產生也不會弄丟 —— 產生流程會從這份 json 把對照表讀回去）") % p)
        else:
            msg = (T("這張卡還沒有 cutscene.json，先暫存在：\n%s\n\n回主畫面按「產生 cutscene.json」就會併進去，然後這個暫存檔會被收掉。") % p)
        QMessageBox.information(self, T("存好了"), msg)
        self.accept()

    def _cleanup(self, *_a):
        th = getattr(self, "thread", None)
        if th is None:
            return
        th.quit()
        if not th.wait(3000):
            th.terminate()
            th.wait(1000)


# ------------------------------------------------------------------ 設定檔

def _escape_loose_backslash(txt):
    """把 JSON 裡沒跳脫的單反斜線補成 \\\\，合法的跳脫序列原樣保留。

    Windows 路徑手打進 json 就會長這樣（"D:\\Koikatu\\..."），嚴格的 json.loads
    會直接拒收。實際踩過：一份被改壞的設定檔連讀都讀不進來，連救都沒得救。
    """
    out, i, n = [], 0, len(txt)
    while i < n:
        c = txt[i]
        if c != "\\":
            out.append(c)
            i += 1
            continue
        nxt = txt[i + 1] if i + 1 < n else ""
        if nxt in '"\\/bfnrt' or (nxt == "u"
                                  and re.match(r"[0-9a-fA-F]{4}", txt[i + 2:i + 6] or "")):
            out.append(txt[i:i + 2])
            i += 2
        else:
            out.append("\\\\")
            i += 1
    return "".join(out)


def read_cutscene_json(path):
    """讀 cutscene.json，刻意讀得很寬鬆。

    容忍：// 註解、結尾多餘的逗號、沒跳脫的單反斜線、字串裡混進來的控制字元。
    手改過或被別的工具改寫過的設定檔都還救得回來 —— 嚴格解析在這裡沒有好處。
    """
    with open(path, encoding="utf-8-sig") as f:
        txt = f.read()
    txt = re.sub(r"//[^\n]*", "", txt)
    txt = re.sub(r",(\s*[}\]])", r"\1", txt)
    try:
        return json.loads(txt, strict=False)
    except Exception:
        return json.loads(_escape_loose_backslash(txt), strict=False)


def looks_like_view_json(cfg, path):
    """這份 json 是 F7 插件存的視角檔（<卡名>.view.json），不是 cutscene 設定檔。

    為什麼需要分辨
    --------------
    F7 現在會把每張卡的 VR 視角存成 <卡名>.view.json，跟 <卡名>.cutscene.json
    同名、同資料夾、同副檔名 —— 在開檔視窗裡兩份長得幾乎一樣，很容易點錯。

    點錯的後果很難看懂：視角檔裡只有座標，沒有 tracks／pairs／sourceCard，
    所以載入會先把整頁清空，然後報「找不到場景卡」「沒有對應點」。
    看起來像工具壞了，其實只是讀錯檔案。

    認檔名也認內容：使用者可能把檔案改過名字，但裡面就是只有 views。
    """
    if os.path.basename(path).lower().endswith(".view.json"):
        return True
    if not isinstance(cfg, dict):
        return False
    return "views" in cfg and not any(
        k in cfg for k in ("tracks", "pairs", "sourceCard", "variantFiles"))


def sibling_cutscene_json(path):
    """<卡名>.view.json → 同資料夾的 <卡名>.cutscene.json；沒有就回空字串。"""
    base = os.path.basename(path)
    low = base.lower()
    if low.endswith(".view.json"):
        stem = base[:-len(".view.json")]
    else:
        stem = os.path.splitext(base)[0]
    cand = os.path.join(os.path.dirname(os.path.abspath(path)), stem + ".cutscene.json")
    return cand if os.path.isfile(cand) else ""


def mapping_from_json(path):
    """從產生的 cutscene.json 讀出 timeline → 影片 的對應，用來「跳到預測位置」。"""
    try:
        tracks = read_cutscene_json(path).get("tracks") or []
    except Exception:
        return None
    segs = [(tr.get("from", 0.0), tr.get("to", 0.0), tr.get("anchors") or [])
            for tr in tracks if tr.get("anchors")]
    if not segs:
        return None

    def f(t):
        A = segs[0][2]
        best_d = None
        for a, b, anchors in segs:
            if a <= t <= b:
                A = anchors
                break
            d = min(abs(t - a), abs(t - b))
            if best_d is None or d < best_d:
                best_d, A = d, anchors
        if len(A) < 2:
            return None
        if t <= A[0][0]:
            sl = (A[1][1] - A[0][1]) / (A[1][0] - A[0][0])
            return A[0][1] + (t - A[0][0]) * sl
        for i in range(len(A) - 1):
            if t <= A[i + 1][0]:
                sl = (A[i + 1][1] - A[i][1]) / (A[i + 1][0] - A[i][0])
                return A[i][1] + (t - A[i][0]) * sl
        sl = (A[-1][1] - A[-2][1]) / (A[-1][0] - A[-2][0])
        return A[-1][1] + (t - A[-1][0]) * sl
    return f


# ------------------------------------------------------------------ 分頁

class CutTab(QWidget):
    log_line = pyqtSignal(str)
    status = pyqtSignal(str)

    def __init__(self, settings=None):
        super().__init__()
        self.settings = settings if settings is not None else {}
        self._pairs_path = ""
        self._json_path = ""
        self._map = None
        # set_card ↔ load_json 會互相呼叫（一個要自動帶設定檔，一個要把
        # 設定檔裡記的卡片帶回來），靠這個旗標擋住第二層
        self._auto_loading = False

        root = QVBoxLayout(self)

        # --- 流程提示 ---
        self.lbl_step = QLabel()
        self.lbl_step.setStyleSheet("color:%s" % GREY)
        self._set_step(1)
        root.addWidget(self.lbl_step)

        # --- 檔案 ---
        box = QGroupBox(T("檔案（卡片 / 影片 / 音訊 / 對應點 / 資料夾 都可以直接拖進來）"))
        g = QVBoxLayout(box)
        self.ed_card = self._picker(g, T("合併好的場景卡"), T("場景卡 (*.png)"),
                                    on_pick=self.set_card)
        self.ed_card.setToolTip(T("挑好卡片之後，會自動帶進同名的 <卡名>.pairs.txt，\n以及輸出資料夾裡同名的 <卡名>.cutscene.json（有的話）。"))
        # 直接把路徑打／貼進去的人也該有一樣的待遇。editingFinished 只在
        # 打完（按 Enter 或移開焦點）才發一次，不像 textChanged 每打一個字就跑。
        self.ed_card.editingFinished.connect(self._card_typed)
        self.ed_out = self._picker(g, T("設定檔輸出到"), is_dir=True)
        self.ed_out.setToolTip(T("產生的 <卡名>.cutscene.json 會寫到這裡。\n插件預設會搜尋 UserData/cutscene 和場景卡旁邊。\n挑卡片時也是到這裡找同名的設定檔。"))

        r0 = QHBoxLayout()
        b_load = QPushButton(T("載入既有的 cutscene.json…"))
        b_load.setToolTip(T("把做好的設定整份讀回介面（卡片、影片、配音、對應點），\n改完按「產生」蓋回去。也可以直接把 json 拖進視窗。"))
        b_load.clicked.connect(lambda: self.load_json())
        r0.addWidget(b_load)
        self.lbl_loaded = QLabel("")
        self.lbl_loaded.setTextFormat(Qt.TextFormat.RichText)
        r0.addWidget(self.lbl_loaded, 1)
        g.addLayout(r0)

        g.addWidget(QLabel(T("影片與配音 —— 打勾的音訊成為配音版本；標 ★來源 的影片就是寫進 json 當 videoFile 的那一支")))
        self.files = FileList()
        self.files.changed.connect(self._refresh_variants)
        g.addWidget(self.files)
        self.lbl_edit = QLabel("")
        g.addWidget(self.lbl_edit)

        r2 = QHBoxLayout()
        for txt, fn in ((T("加入檔案…"), self._add_files),
                        (T("加入資料夾…"), self._add_folder),
                        (T("移除選取"), self.files.remove_selected),
                        (T("全選"), lambda: self.files.set_all(True)),
                        (T("全不選"), lambda: self.files.set_all(False)),
                        (T("把勾選的影片抽成 wav"), self._extract_wav),
                        (T("抽 wav（另外挑檔案…）"), self._extract_wav_pick),
                        (T("對齊音量…"), self._normalize_pick)):
            b = QPushButton(txt)
            b.clicked.connect(fn)
            if fn == self._extract_wav:
                b.setToolTip(T("每支影片抽一個同名 wav（48kHz / pcm_s16le），\n抽完自動加進清單。需要 ffmpeg。"))
                self.btn_wav = b
            if fn == self._extract_wav_pick:
                b.setToolTip(T("開檔案總管多選影片，不必先加進上面的清單。\n抽出來的 wav 放在各自影片旁邊，抽完自動加進清單。"))
            if fn == self._normalize_pick:
                b.setToolTip(
                    T("多選音檔，把響度對齊到同一個目標（EBU R128）。只套固定增益，長度不變。\n同一場景的各版配音一起選，切換時才不會忽大忽小。會直接換掉原檔。"))
                self.btn_norm = b
            r2.addWidget(b)
        self.chk_force = QCheckBox(T("已存在也重抽"))
        r2.addWidget(self.chk_force)
        self.chk_norm = QCheckBox(T("抽完順便對齊音量"))
        self.chk_norm.setToolTip(T("抽出來的 wav 是原樣搬過來的，各版配音本來多大聲就多大聲。\n勾起來的話抽完直接對齊到同一個響度。"))
        r2.addWidget(self.chk_norm)
        self.cmb_lufs = QComboBox()
        for label, v in NORM_CHOICES:
            self.cmb_lufs.addItem(label, v)
        self.cmb_lufs.setToolTip(
            T("所有檔案都對到這個響度。越靠近 0 越大聲，峰值塞不下的部分會被限幅。"))
        r2.addWidget(self.cmb_lufs)
        self.chk_nolimit = QCheckBox(T("不限幅"))
        self.chk_nolimit.setToolTip(
            T("不勾：推到目標，峰值超出 %g dBTP 的部分用限幅壓回。\n勾起來：只套固定增益，不動波形，但可能比目標安靜。")
            % NORM_TP)
        r2.addWidget(self.chk_nolimit)
        r2.addStretch(1)
        g.addLayout(r2)

        r3 = QHBoxLayout()
        b_src = QPushButton(T("設為 ★來源影片"))
        b_src.clicked.connect(self._set_as_source)
        r3.addWidget(b_src)
        b_master = QPushButton(T("★來源轉成無聲工作影片"))
        b_master.setToolTip(T("拿目前的 ★來源影片去轉：去掉音軌、加密關鍵影格。\n音訊另外走 wav，影片只負責畫面；關鍵影格夠密，拖時間軸才定得準。\n轉好的檔會自動接手成新的 ★來源。"))
        b_master.clicked.connect(self._make_master)
        self.btn_master = b_master
        r3.addWidget(b_master)
        b_mb = QPushButton(T("批次轉無聲（選檔案…）"))
        b_mb.setToolTip(T("多選影片排隊轉成 <原名>_src.mp4（放在原片旁邊），\n不會自動設成 ★來源。"))
        b_mb.clicked.connect(self._make_master_pick)
        self.btn_master_batch = b_mb
        r3.addWidget(b_mb)
        self.cmb_h = QComboBox()
        self.cmb_h.addItem(T("壓成 1080p"), 1080)
        self.cmb_h.addItem(T("壓成 1440p（VR 留餘裕）"), 1440)
        self.cmb_h.addItem(T("壓成 720p"), 720)
        self.cmb_h.addItem(T("不壓，保持原本（4K 就是 4K）"), 0)
        self.cmb_h.setToolTip(
            T("長寬比保持原樣。VR 通常 1080p 就夠，4K 容易讓 Studio 掉幀。"))
        r3.addWidget(self.cmb_h)
        r3.addSpacing(20)
        r3.addWidget(QLabel(T("主要配音")))
        self.cmb_active = QComboBox()
        self.cmb_active.setEditable(True)
        self.cmb_active.setMinimumWidth(200)
        r3.addWidget(self.cmb_active)
        self.chk_vnge = QCheckBox(T("改輸出 VNGE 格式（預設是新的插件格式）"))
        r3.addWidget(self.chk_vnge)
        r3.addStretch(1)
        g.addLayout(r3)

        # 轉檔要好幾分鐘，沒有狀態的話視窗看起來就像當掉了
        self.lbl_busy = QLabel("")
        self.lbl_busy.setTextFormat(Qt.TextFormat.RichText)
        g.addWidget(self.lbl_busy)
        root.addWidget(box)

        # --- 中間：播放器 ｜ 對應點 ---
        split = QSplitter(Qt.Orientation.Horizontal)

        self.video = VideoPane()
        self.video.captured.connect(self._capture)
        left = QWidget()
        ll = QVBoxLayout(left)
        ll.setContentsMargins(0, 0, 0, 0)
        hdr = QHBoxLayout()
        hdr.addWidget(QLabel(T("影片預覽 —— 找到畫面後按「擷取這一秒」")))
        hdr.addStretch(1)
        self.btn_pop = QPushButton(T("放大成獨立視窗 ⬈"))
        self.btn_pop.setToolTip(T("整塊搬到一個可自由縮放的視窗，按鍵一起帶過去。\n關掉那個視窗就搬回來，播放位置不會重來。"))
        self.btn_pop.clicked.connect(self._popout)
        hdr.addWidget(self.btn_pop)
        ll.addLayout(hdr)
        self._video_holder = ll
        ll.addWidget(self.video, 1)
        self._popup = None
        split.addWidget(left)

        right = QWidget()
        rl = QVBoxLayout(right)
        rl.setContentsMargins(0, 0, 0, 0)
        self.lbl_pairs = QLabel(T("對應點（timeline 秒數從遊戲面板抄過來）"))
        rl.addWidget(self.lbl_pairs)
        self.table = PairTable()
        # 雙擊「影片」那一欄也直接跳過去，回頭檢查某個點時最順手
        self.table.cellDoubleClicked.connect(
            lambda r, c: self._jump_row() if c == 1 else None)
        rl.addWidget(self.table, 1)

        rr = QHBoxLayout()
        for txt, fn in ((T("新增一列"), lambda: self.table.add_row()),
                        (T("刪除這列"), self._del_row),
                        (T("跳到這列的影片秒數"), self._jump_row),
                        (T("讀 pairs.txt"), self._load_pairs),
                        (T("存 pairs.txt"), self._save_pairs),
                        (T("從切好的音頻反推…"), self._align_dialog),
                        (T("依長度自動設定"), self._auto_pairs_clicked),
                        (T("配音對照…"), self._variant_dialog)):
            b = QPushButton(txt)
            if fn == self._auto_pairs_clicked:
                b.setToolTip(T("清單裡有音檔（或影片）跟場景卡大致一樣長的時候用：\n這種音檔是照著場景從頭播到尾的，每一段的頭尾直接對上，不用自己量。\n把音檔拖進來時，對應點表格是空的就會自動做一次。"))
            if fn == self._align_dialog:
                b.setToolTip(T("以前用 VNGE 做的卡片，音檔是一段一段切好的。\n這個功能把切好的片段拿去跟原始音檔比對，\n自動量出每一段落在原檔的第幾秒，直接變成對應點。"))
            if fn == self._variant_dialog:
                b.setToolTip(T("某一版配音的剪輯跟其他版不同（少了開場、中間剪掉）時用：\n量「主配音的第幾秒 → 這一版的第幾秒」。各版長度一樣就不用開。"))
            b.clicked.connect(fn)
            rr.addWidget(b)
        rr.addStretch(1)
        rl.addLayout(rr)

        jr = QHBoxLayout()
        jr.addWidget(QLabel(T("跳到 timeline")))
        self.ed_jump = QLineEdit()
        self.ed_jump.setPlaceholderText("01:22.933")
        self.ed_jump.setFixedWidth(110)
        self.ed_jump.returnPressed.connect(self._jump)
        jr.addWidget(self.ed_jump)
        b = QPushButton(T("→ 預測的影片位置"))
        b.setToolTip(T("用目前這份設定推算該 timeline 秒數對應到影片的哪裡，\n播放器跳過去，你再確認實際是第幾秒"))
        b.clicked.connect(self._jump)
        jr.addWidget(b)
        jr.addStretch(1)
        rl.addLayout(jr)

        rl.addWidget(QLabel(T("建議下一個去量的位置（跑完才會有，雙擊直接跳過去）")))
        self.lst_suggest = QListWidget()
        self.lst_suggest.setMaximumHeight(76)
        self.lst_suggest.itemDoubleClicked.connect(self._suggest_clicked)
        rl.addWidget(self.lst_suggest)
        split.addWidget(right)
        split.setSizes([620, 460])
        root.addWidget(split, 1)

        # --- 執行 ---
        br = QHBoxLayout()
        self.btn_run = QPushButton(T("產生 cutscene.json"))
        self.btn_run.clicked.connect(self.run)
        br.addWidget(self.btn_run)
        self.btn_reveal = QPushButton(T("開啟輸出資料夾"))
        self.btn_reveal.clicked.connect(self._reveal)
        br.addWidget(self.btn_reveal)
        br.addStretch(1)
        self.lbl_result = QLabel("")
        br.addWidget(self.lbl_result)
        root.addLayout(br)

        root.addWidget(QLabel(T("報告")))
        self.report = QPlainTextEdit()
        self.report.setReadOnly(True)
        f = QFont("Consolas")
        f.setStyleHint(QFont.StyleHint.Monospace)
        self.report.setFont(f)
        self.report.setMinimumHeight(150)
        root.addWidget(self.report, 1)

        # --- 背景執行緒 ---
        self.worker = PlanWorker()
        self.thread = QThread(self)
        self.worker.moveToThread(self.thread)
        self.worker.done.connect(self._finished)
        self.thread.start()

        self.wav_worker = WavWorker()
        self.wav_thread = QThread(self)
        self.wav_worker.moveToThread(self.wav_thread)
        self.wav_worker.done.connect(self._wav_done)
        self.wav_worker.progress.connect(self._set_busy)
        self.wav_thread.start()

        self.probe_worker = ProbeWorker()
        self.probe_thread = QThread(self)
        self.probe_worker.moveToThread(self.probe_thread)
        self.probe_worker.done.connect(self._probe_done)
        self.probe_thread.start()

        self.autopair_worker = AutoPairWorker()
        self.autopair_thread = QThread(self)
        self.autopair_worker.moveToThread(self.autopair_thread)
        self.autopair_worker.done.connect(self._auto_pairs_done)
        self.autopair_thread.start()
        self._autopair_seq = 0
        self._autopair_busy = False
        self._autopair_force = False

        self.master_worker = MasterWorker()
        self.master_thread = QThread(self)
        self.master_worker.moveToThread(self.master_thread)
        self.master_worker.done.connect(self._master_done)
        self.master_worker.line.connect(self.log_line)
        self.master_worker.progress.connect(self._set_busy)
        self.master_thread.start()

        self.norm_worker = LoudnessWorker()
        self.norm_thread = QThread(self)
        self.norm_worker.moveToThread(self.norm_thread)
        self.norm_worker.done.connect(self._norm_done)
        self.norm_worker.line.connect(self.log_line)
        self.norm_worker.progress.connect(self._set_busy)
        self.norm_thread.start()

        # 輸出資料夾：預設 <遊戲根目錄>/UserData/cutscene —— 插件就是只找這裡
        out = self.settings.get("cut_out_dir", "")
        if not out:
            gr = self.settings.get("game_root", "")
            out = os.path.join(gr, "UserData", "cutscene") if gr else ""
        self.ed_out.setText(out)

        # 還原上次「儲存設定」時的狀態
        self.files.add(self.settings.get("cut_files") or [])
        for i, p in enumerate(self.settings.get("cut_files") or []):
            if p in (self.settings.get("cut_unchecked") or []):
                for r in range(self.files.rowCount()):
                    if self.files.path(r) == p and self.files.item(r, 0):
                        self.files.item(r, 0).setCheckState(Qt.CheckState.Unchecked)
        self._set_source(self.settings.get("cut_video", "") or self.files.source_path())
        card = self.settings.get("cut_card", "")
        if card and os.path.isfile(card):
            # 開程式還原狀態時「認」設定檔就好，不要整份套上來 ——
            # 上面才剛把「儲存設定」存下來的檔案清單還原回去，
            # 自動載入會連那些一起洗掉（詳見 _bind_json_quiet）
            self.set_card(card, auto_json=False)
            self._bind_json_quiet()
        act = self.settings.get("cut_active", "")
        if act:
            self.cmb_active.setCurrentText(act)
        self.chk_vnge.setChecked(bool(self.settings.get("cut_vnge", False)))
        self.chk_force.setChecked(bool(self.settings.get("cut_force", False)))
        h = self.settings.get("cut_height", 1080)
        for i in range(self.cmb_h.count()):
            if self.cmb_h.itemData(i) == h:
                self.cmb_h.setCurrentIndex(i)
                break

        self.setAcceptDrops(True)

    # ---- 拖放 ----
    # 副檔名決定它是什麼，一次丟一堆也行：
    #   .png → 場景卡   影片/音訊 → 清單   .txt → 對應點   .json → 既有設定
    #   資料夾 → 把裡面的影片和音訊全部加進清單

    def dragEnterEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()

    def dragMoveEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()

    def dropEvent(self, e):
        took = []
        media = []
        for url in e.mimeData().urls():
            p = url.toLocalFile()
            if not p:
                continue
            ext = os.path.splitext(p)[1].lower()
            if os.path.isdir(p):
                media += [os.path.join(p, f) for f in sorted(os.listdir(p))]
            elif ext in VIDEO_EXT or ext in AUDIO_EXT:
                media.append(p)
            elif ext == ".png":
                self.set_card(p)
                took.append(T("場景卡"))
            elif ext == ".txt":
                self._load_pairs(p)
                took.append(T("對應點"))
            elif ext == ".json":
                self.load_json(p)
                took.append(T("既有設定（整份帶入）"))
        if media:
            n = self.files.add(media)
            if n:
                took.append(T("%d 個影片/音訊") % n)
                self._probe_added()
            # 還沒指定原始影片的話，拿第一支影片當它
            if not self.files.source_path() or not self.files.source:
                for p in media:
                    if os.path.splitext(p)[1].lower() in VIDEO_EXT:
                        self._set_source(p)
                        took.append(T("（順便設成 ★來源）"))
                        break
        if took:
            e.acceptProposedAction()
            self.status.emit(T("已收下：") + "、".join(took))
            # 丟進來的音檔跟場景卡一樣長的話，對應點直接設好（表格是空的才做）
            self._maybe_auto_pairs()

    # ---- 小工具 ----
    def _picker(self, lay, caption, filt="", is_dir=False, on_change=None,
                on_pick=None):
        row = QHBoxLayout()
        lbl = QLabel(caption)
        lbl.setFixedWidth(120)
        row.addWidget(lbl)
        ed = QLineEdit()
        row.addWidget(ed, 1)
        b = QPushButton(T("瀏覽…"))
        b.setFixedWidth(70)

        def pick():
            if is_dir:
                p = QFileDialog.getExistingDirectory(self, caption, ed.text())
            else:
                p, _ = QFileDialog.getOpenFileName(self, caption, ed.text(), filt)
            if p:
                ed.setText(p)
                # on_change 掛的是 textChanged，打字的時候每按一個鍵都會跑，
                # 所以「挑完一個檔案」這件事要另外給一個鉤子 ——
                # 自動載入那種會清空介面的動作只能掛在這裡。
                if on_pick:
                    on_pick(p)
        b.clicked.connect(pick)
        row.addWidget(b)
        lay.addLayout(row)
        if on_change:
            ed.textChanged.connect(on_change)
        return ed

    def _set_step(self, n):
        steps = [T("① 合併場景"), T("② 選影片／配音"), T("③ 抓對應點"), T("④ 產生設定")]
        self.lbl_step.setText("　→　".join(
            ("<b>%s</b>" % s) if i == n - 1 else s for i, s in enumerate(steps)))
        self.lbl_step.setTextFormat(Qt.TextFormat.RichText)

    def _set_source(self, path):
        """指定來源影片：載進播放器、在清單上標 ★、記進設定。"""
        path = (path or "").strip()
        self.files.set_source(path)
        self.settings["cut_video"] = path
        if path and os.path.isfile(path):
            self.video.load(path)
            self._set_step(3)

    def _set_busy(self, msg):
        """msg 空字串 = 收工。轉檔期間把兩個會互相打架的按鈕鎖起來。"""
        busy = bool(msg)
        self.lbl_busy.setText(
            ("<b style='color:#2c7'>⏳ " + msg + "</b>") if busy else "")
        if busy:
            self.status.emit(msg)
        for b in (getattr(self, "btn_wav", None), getattr(self, "btn_master", None),
                  getattr(self, "btn_master_batch", None),
                  getattr(self, "btn_norm", None)):
            if b is not None:
                b.setEnabled(not busy)

    def _probe_added(self):
        """清單裡的影片和音訊一起驗長度。只要有兩個以上才有比的意義。"""
        files = [self.files.path(r) for r in range(self.files.rowCount())]
        if len(files) < 2:
            return
        self._probe_seq = getattr(self, "_probe_seq", 0) + 1
        self.probe_worker.submit(files, self._probe_seq)
        QMetaObject.invokeMethod(self.probe_worker, "run",
                                 Qt.ConnectionType.QueuedConnection)

    def _probe_done(self, ok, text, seq=0):
        # 過期的結果直接丟掉（連續載兩份 json 時會發生，說明見 ProbeWorker）
        if seq and seq != getattr(self, "_probe_seq", 0):
            return
        if not text:
            return
        for ln in text.splitlines():
            self.log_line.emit(ln)
        if ok:
            self.lbl_edit.setText("")
        else:
            self.lbl_edit.setText(
                T("<span style='color:%s'><b>有檔案的長度對不上，不是同一個剪輯 —— 詳見下方紀錄</b></span>") % RED)
            self.lbl_edit.setTextFormat(Qt.TextFormat.RichText)

    def _add_files(self):
        ps, _ = QFileDialog.getOpenFileNames(
            self, T("加入影片或音訊"), self.files.source_path(),
            T("影片與音訊 (*.mp4 *.mkv *.mov *.webm *.avi *.m4v *.wmv *.wav *.ogg *.mp3 *.m4a *.flac);;全部 (*.*)"))
        if ps:
            self.log_line.emit(T("加入 %d 個檔案") % self.files.add(ps))
            self._probe_added()
            self._maybe_auto_pairs()

    def _add_folder(self):
        d = QFileDialog.getExistingDirectory(self, T("加入整個資料夾裡的影片與音訊"))
        if not d:
            return
        ps = [os.path.join(d, f) for f in sorted(os.listdir(d))]
        self.log_line.emit(T("加入 %d 個檔案") % self.files.add(ps))
        self._probe_added()
        self._maybe_auto_pairs()

    # ---- 長度一致就自動設定對應點 ----
    def _table_empty(self):
        for r in range(self.table.rowCount()):
            for c in (0, 1):
                it = self.table.item(r, c)
                if it and it.text().strip():
                    return False
        return True

    def _auto_pairs_clicked(self):
        card = self.ed_card.text().strip()
        if not card or not os.path.isfile(card):
            QMessageBox.warning(self, T("還不能執行"), T("先指定合併好的場景卡"))
            return
        if not self.files.rowCount():
            QMessageBox.warning(self, T("還不能執行"), T("先把音檔（或影片）加進清單"))
            return
        if not self._table_empty():
            if QMessageBox.question(
                    self, T("依長度自動設定"),
                    T("對應點表格裡已經有東西了，長度對得上的話會整個換掉。要繼續嗎？")
            ) != QMessageBox.StandardButton.Yes:
                return
        self._maybe_auto_pairs(force=True)

    def _maybe_auto_pairs(self, force=False, any_length=False):
        """清單裡的檔案跟場景卡一樣長就把對應點設好。

        自動觸發（把檔案拖進來、加進清單）只在表格是空的時候做 ——
        已經量好或載回來的對應點不能被悄悄蓋掉。按鈕（force）才會問過之後覆蓋。
        """
        card = self.ed_card.text().strip()
        if not card or not os.path.isfile(card):
            return
        # 打勾的排前面：兩個檔案都對得上的時候，會挑差最少的那個
        files = [self.files.path(r) for r in range(self.files.rowCount())]
        if not files:
            return
        if not force and not self._table_empty():
            return
        if self._autopair_busy:
            self._autopair_again = force or getattr(self, "_autopair_again", False)
            self._autopair_pending = True
            return
        self._autopair_busy = True
        self._autopair_pending = False
        self._autopair_force = force
        self._autopair_seq += 1
        self.status.emit(T("比對音檔長度和場景卡…（大卡要等一下）"))
        self.autopair_worker.submit(card, files, self._autopair_seq, any_length)
        QMetaObject.invokeMethod(self.autopair_worker, "run",
                                 Qt.ConnectionType.QueuedConnection)

    def _auto_pairs_done(self, seq, card, res, err):
        self._autopair_busy = False
        force = self._autopair_force
        if getattr(self, "_autopair_pending", False):
            # 等的時候清單又變了（或換了卡片）→ 用最新的狀態再跑一次
            again = getattr(self, "_autopair_again", False)
            self._autopair_again = False
            self._maybe_auto_pairs(force=again)
            return
        now = self.ed_card.text().strip()
        if (seq != self._autopair_seq
                or os.path.normcase(os.path.abspath(now or "?")) != os.path.normcase(os.path.abspath(card))):
            return
        if err or res is None:
            self.log_line.emit(T("依長度自動設定：讀不了這張卡（%s）") % err)
            if force:
                QMessageBox.warning(self, T("依長度自動設定"), T("讀不了這張卡：\n%s") % err)
            return
        pairs, info = res
        tl, real = info.get("total_tl") or 0.0, info.get("total_real") or 0.0
        scene_txt = fmt_time(real) + ((T("（timeline %s，有時間流速軌道）") % fmt_time(tl))
                                      if abs(real - tl) > 0.05 else "")
        if not pairs:
            lens = "、".join("%s %s" % (os.path.basename(p), fmt_time(d) if d else T("讀不到"))
                             for p, d in (info.get("media") or [])[:8])
            msg = (T("沒有一個檔案的長度跟場景卡對得上（場景 %s，容許差 %.1f 秒）：%s")
                   % (scene_txt, info.get("tol") or 0.0, lens))
            self.log_line.emit(T("依長度自動設定：") + msg)
            if force and info.get("file"):
                # 按鈕按的：差太多也可以硬套，但要先講清楚會發生什麼事
                if QMessageBox.question(
                        self, T("依長度自動設定"),
                        msg + "\n\n" + T("最接近的是 %s（差 %+.2f 秒）。要不管長度，直接頭對頭、尾對尾套上去嗎？\n音檔裡有開場動畫或過場的話，這樣會整段對不上。")
                        % (os.path.basename(info["file"]), info.get("diff") or 0.0),
                        QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                        QMessageBox.StandardButton.No) == QMessageBox.StandardButton.Yes:
                    self._maybe_auto_pairs(force=True, any_length=True)
            elif force:
                QMessageBox.information(self, T("依長度自動設定"), msg)
            else:
                self.status.emit(T("音檔長度跟場景卡不一樣，對應點要自己量"))
            return
        if not force and not self._table_empty():
            return                          # 等結果的時候使用者自己填了東西
        self.table.load_rows(pairs)
        self._pairs_path = ""
        self._show_pairs_path()
        fn = os.path.basename(info.get("file") or "")
        self.log_line.emit(
            T("依長度自動設定：%s 長 %s，場景 %s（差 %+.2f 秒）→ 頭對頭、尾對尾，帶入 %d 個對應點（尚未存檔）")
            % (fn, fmt_time(info.get("dur") or 0.0), scene_txt, info.get("diff") or 0.0, len(pairs)))
        self.lbl_edit.setText(
            T("<span style='color:#1a7f37'><b>音檔長度跟場景卡一致（%s）—— 對應點已經自動設好，確認配音有打勾就可以直接按「產生 cutscene.json」。</b></span>") % fn)
        self.lbl_edit.setTextFormat(Qt.TextFormat.RichText)
        self.status.emit(T("已自動帶入 %d 個對應點（音檔長度跟場景卡一致）") % len(pairs))

    def _set_as_source(self):
        for r in sorted({i.row() for i in self.files.selectedItems()}):
            if self.files.kind(r).startswith(T("影片")):
                self._set_source(self.files.path(r))
                self.status.emit(T("★來源改成 ") + os.path.basename(self.files.path(r)))
                return
        self.status.emit(T("選一列影片再按這個"))

    @staticmethod
    def _master_out(src):
        out = os.path.splitext(src)[0] + "_src.mp4"
        if os.path.abspath(src) == os.path.abspath(out):
            out = os.path.splitext(src)[0] + "_work.mp4"
        return out

    def _make_master(self):
        src = self.files.source_path()
        if not src or not os.path.isfile(src):
            QMessageBox.warning(self, T("沒有來源影片"),
                                T("先在清單裡選一列影片按「設為 ★來源影片」。"))
            return
        out = self._master_out(src)
        if os.path.isfile(out) and QMessageBox.question(
                self, T("已經有這個檔"),
                T("%s 已存在，要覆蓋嗎？") % os.path.basename(out)
                ) != QMessageBox.StandardButton.Yes:
            return
        self._start_master([(src, out)], adopt=True)

    def _make_master_pick(self):
        """開檔案總管多選影片批次轉。

        跟上面那顆的差別只有「要轉哪些」—— 上面那顆是 ★來源一支，
        這顆是自己挑一批。睡前丟一批下去的用法，所以已經存在的直接跳過，
        不要跑到一半彈一個「要覆蓋嗎」擋在那裡沒人按。
        """
        start = os.path.dirname(self.files.source_path() or "") or ""
        files, _ = QFileDialog.getOpenFileNames(
            self, T("選要轉成無聲工作影片的影片（可多選）"), start,
            T("影片 (*.mp4 *.mkv *.mov *.avi *.wmv *.m4v *.webm);;所有檔案 (*.*)"))
        if not files:
            return
        jobs, skipped = [], []
        for p in files:
            out = self._master_out(p)
            if os.path.isfile(out):
                skipped.append(os.path.basename(out))
                continue
            jobs.append((p, out))
        if skipped:
            self.log_line.emit(T("跳過已經存在的 %d 支：%s")
                               % (len(skipped), "、".join(skipped[:6])
                                  + ("…" if len(skipped) > 6 else "")))
        if not jobs:
            QMessageBox.information(self, T("沒有要轉的"),
                                    T("選到的影片都已經有對應的 _src.mp4 了。\n要重轉的話先把那些檔刪掉或改名。"))
            return
        self._start_master(jobs, adopt=False)

    def _start_master(self, jobs, adopt):
        height = self.cmb_h.currentData()
        gop = 30
        self._master_adopt = adopt
        self.log_line.emit("=" * 60)
        self.log_line.emit(T("轉無聲工作影片：%d 支（%s，關鍵影格每 %d 幀，seek 誤差上限約 %.2f 秒）")
                           % (len(jobs), ("%dp" % height) if height else T("原解析度"),
                              gop, gop / 60.0))
        self._set_busy(T("準備轉檔…"))
        self.status.emit(T("轉檔中…（%d 支，跑在背景）") % len(jobs))
        self.master_worker.submit(jobs, height, gop)
        QMetaObject.invokeMethod(self.master_worker, "run",
                                 Qt.ConnectionType.QueuedConnection)

    def _master_done(self, ok, msg, made):
        self._set_busy("")
        self.log_line.emit(msg)
        self.status.emit(msg.splitlines()[0] if msg else T("完成"))
        made = list(made or [])
        if made:
            self.files.add(made)
        if not ok:
            QMessageBox.warning(self, T("轉檔沒全部成功"), msg.strip().splitlines()[-1])
        if made and getattr(self, "_master_adopt", False) and len(made) == 1:
            self._set_source(made[0])
            self.log_line.emit(T("已加進清單並設成 ★來源 —— 產生 json 時 videoFile 就會是這一支。音訊記得用同一支**原片**抽的 wav，剪輯才會對得上。"))
        elif made:
            self.log_line.emit(T("已加進清單（批次轉的不會自動變成 ★來源）—— 要用哪一支就選它按「設為 ★來源影片」。"))

    def _refresh_variants(self):
        """清單一變就重算「主要配音」的選項，盡量保留原本選的那個。"""
        cur = self.cmb_active.currentText().strip()
        names = [n for n, _ in self.files.checked(T("音訊"))]
        self.cmb_active.blockSignals(True)
        self.cmb_active.clear()
        self.cmb_active.addItems(names)
        if cur in names:
            self.cmb_active.setCurrentText(cur)
        elif names:
            self.cmb_active.setCurrentIndex(0)
        self.cmb_active.blockSignals(False)

    def _popout(self):
        if self._popup is not None:
            self._popup.raise_()
            self._popup.activateWindow()
            return
        self._video_holder.removeWidget(self.video)
        self._popup = VideoWindow(self, self.video)
        self._popup.finished.connect(lambda _=0: self._popin())
        self._popup.show()
        self.btn_pop.setText(T("已彈出（關掉視窗收回）"))
        self.btn_pop.setEnabled(False)

    def _popin(self):
        if self._popup is None:
            return
        popup = self._popup
        self._popup = None          # 先清掉，finished 再打進來時才不會遞迴
        popup.layout().removeWidget(self.video)
        self.video.setParent(None)
        self._video_holder.addWidget(self.video, 1)
        self.video.show()
        popup.deleteLater()         # 不刪的話每彈出一次就留一個空視窗物件
        self.btn_pop.setText(T("放大成獨立視窗 ⬈"))
        self.btn_pop.setEnabled(True)

    def _extract_wav_pick(self):
        """開檔案總管多選影片抽 wav —— 不必先加進清單。"""
        start = os.path.dirname(self.files.source_path() or "") or ""
        files, _ = QFileDialog.getOpenFileNames(
            self, T("選要抽 wav 的影片（可多選）"), start,
            T("影片 (*.mp4 *.mkv *.mov *.avi *.wmv *.m4v *.webm);;所有檔案 (*.*)"))
        if files:
            self._extract_wav(files)

    def _extract_wav(self, files=None):
        # clicked 訊號會塞一個 bool 進來（checked），要擋掉，不然會被當成檔案清單
        if not isinstance(files, (list, tuple)):
            files = None
        files = list(files) if files else [p for _, p in self.files.checked(T("影片"))]
        if not files:
            QMessageBox.warning(self, T("沒有勾選影片"),
                                T("在上面的清單裡勾選要抽 wav 的影片，或用「抽 wav（另外挑檔案…）」自己挑。"))
            return
        self.log_line.emit("=" * 60)
        self.log_line.emit(T("抽 wav（%d 支影片）") % len(files))
        self._set_busy(T("準備抽 wav…"))
        self.status.emit(T("抽音訊中…（2GB 的檔案要一兩分鐘）"))
        # 記下這一批是哪些檔 —— 抽的過程中勾選還是可以改，
        # 完成後再去讀勾選狀態的話，加進清單的會是「現在勾的」而不是「剛剛抽的」
        self._wav_batch = list(files)
        self.wav_worker.submit(files, force=self.chk_force.isChecked())
        QMetaObject.invokeMethod(self.wav_worker, "run",
                                 Qt.ConnectionType.QueuedConnection)

    def _wav_done(self, ok, text):
        self._set_busy("")
        for ln in text.splitlines():
            self.log_line.emit(ln)
        # 抽出來的 wav 直接加進清單，不用再手動找一次
        made = [os.path.splitext(p)[0] + ".wav"
                for p in (getattr(self, "_wav_batch", None) or [])]
        made = [p for p in made if os.path.isfile(p)]
        n = self.files.add(made)
        if n:
            self.log_line.emit(T("已把 %d 個新的 wav 加進清單") % n)
        self.status.emit(text.splitlines()[-1] if text else T("完成"))
        if not ok:
            QMessageBox.warning(self, T("抽 wav 沒全部成功"), text.strip().splitlines()[-1])
            return
        # 抽出來的是原樣的音量。勾了就順手對齊，省得再跑一次「對齊音量…」
        if made and self.chk_norm.isChecked():
            self._start_normalize(made)

    # ---- 音量對齊 ----

    def _normalize_pick(self):
        start = ""
        for _n, p in self.files.checked(T("音訊")):
            start = os.path.dirname(p)
            break
        files, _ = QFileDialog.getOpenFileNames(
            self, T("選要對齊音量的音檔（同一個場景的各版配音一起選）"), start,
            T("音訊 (*.wav *.mp3 *.ogg *.flac *.m4a);;所有檔案 (*.*)"))
        if files:
            self._start_normalize(files)

    def _start_normalize(self, files):
        self.log_line.emit("=" * 60)
        lim = not self.chk_nolimit.isChecked()
        tgt = self.cmb_lufs.currentData()
        if tgt is None:
            tgt = NORM_I
        self.log_line.emit(T("音量對齊：目標 %g LUFS / 真峰值上限 %g dBTP（%s）")
                           % (tgt, NORM_TP,
                              T("峰值超出的部分限幅") if lim else T("純增益，不動波形形狀")))
        self.log_line.emit(T("  %d 個檔案，會直接換掉原檔。") % len(files))
        self._set_busy(T("量響度…"))
        self.status.emit(T("音量對齊中…（要整支讀過一遍，大檔要一點時間）"))
        self.norm_worker.submit(files, tgt, NORM_TP, lim)
        QMetaObject.invokeMethod(self.norm_worker, "run",
                                 Qt.ConnectionType.QueuedConnection)

    def _norm_done(self, ok, msg):
        self._set_busy("")
        self.log_line.emit(msg)
        self.status.emit(msg)
        if not ok:
            QMessageBox.warning(self, T("音量對齊沒全部成功"), msg)

    def _capture(self, sec):
        self.table.set_video(sec)

    def _jump_row(self):
        """把播放器跳到選取那一列已經填好的「影片」秒數 —— 回頭核對舊的點用。"""
        r = self.table.currentRow()
        if r < 0:
            self.status.emit(T("先選一列"))
            return
        it = self.table.item(r, 1)
        txt = it.text().strip() if it else ""
        if not txt:
            self.status.emit(T("這一列還沒有影片秒數"))
            return
        try:
            v = parse_time(txt)
        except ValueError as e:
            self.status.emit(str(e))
            return
        self.video.seek(v)
        self.status.emit(T("跳到影片 ") + fmt_time(v))

    def _del_row(self):
        r = self.table.currentRow()
        if r >= 0:
            self.table.removeRow(r)

    def _pairs_named(self):
        """這張卡專屬的對應點檔：<卡片同名>.pairs.txt

        一定要用卡片同名，不能用共用的 pairs.txt ——
        同一個資料夾放兩張卡時，共用檔會讓第二張卡默默吃到第一張卡的對應點，
        而且不會報錯，只會整部不同步。存檔永遠寫這個名字。
        """
        card = self.ed_card.text().strip()
        if not card:
            return ""
        # normpath：卡片路徑可能是從 json 帶進來的正斜線寫法，
        # 不整理的話拼出來會變成 "D:/a/b\c.pairs.txt" 這種混合寫法，看了很難受
        return os.path.normpath(
            os.path.join(os.path.dirname(card),
                         os.path.splitext(os.path.basename(card))[0] + ".pairs.txt"))

    def _pairs_default(self):
        """要讀的檔：先找卡片專屬的，找不到才回頭看舊的共用 pairs.txt。

        回退只是為了讓舊資料還能用 —— 讀到共用檔時會提醒，
        而且一按「存 pairs.txt」就會寫成卡片專屬的名字，等於自動搬家。
        """
        named = self._pairs_named()
        if not named:
            return ""
        if os.path.isfile(named):
            return named
        plain = os.path.join(os.path.dirname(named), "pairs.txt")
        if os.path.isfile(plain):
            return plain
        return named

    def _show_pairs_path(self):
        p = self._pairs_path
        if not p:
            self.lbl_pairs.setText(T("對應點（timeline 秒數從遊戲面板抄過來）"))
            return
        name = os.path.basename(p)
        warn = name.lower() == "pairs.txt"
        self.lbl_pairs.setText(
            T("對應點 —— <b>%s</b>%s") % (name,
             (T("<span style='color:%s'>（共用檔，存檔後會改成卡片專屬名）</span>") % RED)
             if warn else ""))
        self.lbl_pairs.setTextFormat(Qt.TextFormat.RichText)

    def _card_json_default(self):
        """這張卡對應的設定檔：<輸出資料夾>\\<卡名>.cutscene.json。

        跟 pairs.txt 同一個道理 —— 檔名是從卡片名推出來的，工具自己找得到，
        沒理由每次都要人去檔案視窗裡翻。差別只在它住在輸出資料夾
        （插件只讀 UserData/cutscene），不是卡片旁邊；輸出資料夾沒設或
        那裡沒有的話，再回頭找卡片旁邊。

        只認 .cutscene.json。F7 存的 <卡名>.view.json 同名同副檔名，
        但那是視角檔，不能當設定檔讀（見 looks_like_view_json）。
        """
        card = self.ed_card.text().strip()
        if not card:
            return ""
        stem = os.path.splitext(os.path.basename(card))[0]
        for d in (self.ed_out.text().strip(), os.path.dirname(card)):
            if not d:
                continue
            p = os.path.normpath(os.path.join(d, stem + ".cutscene.json"))
            if os.path.isfile(p):
                return p
        return ""

    def _card_typed(self):
        """自己把卡片路徑打／貼進去的時候，比照挑檔案處理。"""
        p = self.ed_card.text().strip()
        if p and os.path.isfile(p):
            self.set_card(p)

    def _clear_card_state(self):
        """把「屬於上一張卡」的東西整組清掉。

        為什麼要連影片清單和預覽一起：換到一張還沒做過的卡時，原本只解除
        cutscene.json 和 pairs 的綁定，影片與配音那張表卻整份留著 ——
        於是新卡一開就帶著前一張的主配音和三個配音版本，按「產生」直接把
        別人的聲音寫進這張卡的設定檔。預覽播放器同理：畫面上還是上一支影片，
        「跳到預測的影片位置」還會拿上一張的長度去算，看起來完全合理。

        「設定檔輸出到」那一格不清 —— 那是資料夾，本來就該跨卡沿用。
        對應點（pairs）也不在這裡動：set_card 已經處理過了，而且它可能剛剛
        才把這張卡自己的 pairs.txt 載進來，在這裡清會把它清掉。
        """
        self._json_path = ""
        self._map = None
        try:
            self.files.clear_all()
        except Exception:
            pass
        try:
            self.video.clear()
        except Exception:
            pass
        for w, setter in ((getattr(self, "lbl_loaded", None), "setText"),
                          (getattr(self, "lbl_edit", None), "setText"),
                          (getattr(self, "lbl_result", None), "setText")):
            if w is not None:
                getattr(w, setter)("")
        if getattr(self, "report", None) is not None:
            self.report.setPlainText("")
        if getattr(self, "lst_suggest", None) is not None:
            self.lst_suggest.clear()

    def _auto_load_json(self, clear_if_missing=False):
        """卡片換好之後，順手把這張卡的設定檔整份帶進來。

        這個流程九成的使用情境是「回頭改一張已經做好的卡」—— 加一個配音版本、
        補一個對應點。原本每次都得再按一次「載入既有的 cutscene.json」
        並在檔案視窗裡找到那一份。

        兩個一定要擋的地方：
        1. 重入。load_json 內部會回頭呼叫 set_card（它要把 json 裡記的卡片
           帶回來），set_card 又會走到這裡 —— 不擋就是無窮遞迴。
        2. 同一份重載。load_json 是「整頁換成這份設定」，會清空清單和表格；
           已經是這一份的話重載等於把人家改到一半的東西洗掉。
        """
        if self._auto_loading:
            return
        p = self._card_json_default()
        if not p:
            # 這張卡還沒有設定檔。上一張的綁定一定要斷掉 ——
            # _json_path 是「產生」時要蓋回去的那個檔，留著就會把 A 的設定檔
            # 用 B 的內容覆寫掉；_map 是「跳到預測的影片位置」用的，
            # 留著會拿 A 的曲線去推 B 的秒數，而且看起來完全合理。
            if clear_if_missing:
                old = os.path.basename(self._json_path or "")
                had = bool(self._json_path or self._map)
                self._clear_card_state()
                if had:
                    self.log_line.emit(T("這張卡還沒有 cutscene.json —— 已解除跟 %s 的綁定，並清掉上一張卡的影片與配音")
                                       % (old or T("上一張卡的設定檔")))
                else:
                    self.log_line.emit(T("換了卡片 —— 已清掉上一張卡的影片與配音"))
            return
        if self._json_path and os.path.normcase(os.path.abspath(self._json_path)) \
                == os.path.normcase(os.path.abspath(p)):
            return
        # 先確認讀得動再載。自動觸發的動作不該因為檔案壞掉就彈一個視窗出來
        # 擋在挑卡片的路上 —— 記一行就好，要載的人自己按按鈕會看到完整錯誤。
        try:
            read_cutscene_json(p)
        except Exception as e:
            self.log_line.emit(T("[自動載入] %s 讀不了，跳過：%s") % (os.path.basename(p), e))
            return
        self._auto_loading = True
        try:
            self.log_line.emit(T("（自動帶入這張卡的設定檔：%s）") % os.path.basename(p))
            self.load_json(p)
        finally:
            self._auto_loading = False

    def _bind_json_quiet(self):
        """只把設定檔「認起來」，不動介面。開程式還原狀態時用。

        還原時介面上已經是上次按「儲存設定」存下來的樣子 —— 可能是加了檔案
        還沒產生的半成品。這時候硬套 json 會把那些洗掉，所以只接上
        _json_path 和 _map：一個是「產生」時蓋回同一個檔要用的，
        一個是「跳到預測的影片位置」要用的，兩個都是唯讀，沒有副作用。
        """
        p = self._card_json_default()
        if not p:
            return
        self._json_path = p
        self._map = mapping_from_json(p)
        self.log_line.emit(T("（這張卡已經有設定檔：%s —— 要整份帶回介面的話按「載入既有的 cutscene.json」，或重新挑一次卡片）")
                           % os.path.basename(p))

    # ---- 對外：合併分頁做完就把卡片帶過來 ----
    def set_card(self, path, auto_json=True):
        """換卡片。

        重點是「換到一張還沒做過的卡」時要把上一張的東西清掉。
        原本只有「找得到就載入」，找不到就什麼都不做 —— 於是上一張卡的對應點
        還留在表格裡、上一張的 json 還綁在 _json_path 上，按下「產生」就會拿
        A 的對應點去寫 B 的設定檔，而且畫面上完全看不出來。
        """
        old = self.ed_card.text().strip()
        changed = (os.path.normcase(os.path.abspath(old or "?"))
                   != os.path.normcase(os.path.abspath(path or "?")))
        self.ed_card.setText(path)
        self._set_step(2)
        p = self._pairs_default()
        if os.path.isfile(p):
            self._load_pairs(p)
        elif changed and self.table.rowCount():
            n = self.table.rowCount()
            self.table.load_rows([])
            self._pairs_path = ""
            self._show_pairs_path()
            self.log_line.emit(T("這張卡還沒有 pairs.txt —— 已清掉上一張卡留下的 %d 個對應點") % n)
            self.status.emit(T("換了卡片，對應點已清空（這張卡還沒有 pairs.txt）"))
        if auto_json:
            self._auto_load_json(clear_if_missing=changed)

    # ---- 從切好的音頻反推對應點 ----

    def _align_dialog(self):
        # 先確認相依套件在不在。這個功能比別的多需要 numpy（FFT 互相關），
        # 而整個專案裡其他用到 numpy 的地方都包在 try/except 裡靜靜地退場，
        # 所以少了它平常完全不會發現 —— 到這裡才炸，而且 PyQt6 的 slot
        # 漏接例外是直接 abort 整個行程，使用者看到的就是「拖進去就閃退」。
        try:
            import kkaudioalign          # noqa: F401
        except Exception as e:
            # 重點是「裝到哪個 Python」。電腦上常常有好幾個 Python
            # （系統的、conda 的、PyCharm 的 venv、打包用的），
            # 在命令提示字元敲 pip install numpy 裝到的是 PATH 上那一個，
            # 跟這個程式正在跑的那一個不見得是同一個 ——
            # 所以這裡直接把自己的 exe 路徑印出來，連指令一起給。
            cmd = '"%s" -m pip install numpy' % sys.executable
            box = QMessageBox(self)
            box.setIcon(QMessageBox.Icon.Warning)
            box.setWindowTitle(T("這個 Python 沒有 numpy"))
            box.setText(
                T("「從切好的音頻反推」要用 FFT 互相關，需要 numpy。\n\n目前這個工具跑在：\n    %s\n    (%s)\n\n用這一行裝到**這個** Python：\n    %s\n\n如果你剛才已經 pip install numpy 卻還是看到這個視窗，就是裝到別的 Python 去了。\n\n錯誤訊息：%s")
                % (sys.executable, sys.version.split()[0], cmd, e))
            btn = box.addButton(T("複製指令"), QMessageBox.ButtonRole.ActionRole)
            box.addButton(QMessageBox.StandardButton.Ok)
            box.exec()
            if box.clickedButton() is btn:
                try:
                    from PyQt6.QtWidgets import QApplication
                    QApplication.clipboard().setText(cmd)
                    self.status.emit(T("安裝指令已複製到剪貼簿"))
                except Exception:
                    pass
            self.log_line.emit(T("[從切好的音頻反推] 載入失敗：%s") % e)
            self.log_line.emit(T("  目前的 Python：%s") % sys.executable)
            self.log_line.emit(T("  安裝指令：%s") % cmd)
            return
        try:
            card = self.ed_card.text().strip()
            if card and not os.path.isfile(card):
                card = ""
            dlg = AlignDialog(self, card, self._align_apply)
            dlg.exec()
        except Exception as e:
            self.log_line.emit(traceback.format_exc())
            QMessageBox.warning(self, T("開不起來"), T("%s\n\n完整內容看下面的紀錄。") % e)

    def _variant_dialog(self):
        """開「配音對照」視窗。

        要有卡片：哪一段落在主配音的哪個區間，只能從卡片的段落＋對應點算出來，
        而那正是「哪一段沒有對照點」唯一的判斷依據。
        """
        card = self.ed_card.text().strip()
        if not card or not os.path.isfile(card):
            QMessageBox.warning(self, T("先指定場景卡"),
                                T("配音對照要對照卡片的段落，才知道哪一段缺點。"))
            return
        variants = list(self.files.checked(T("音訊")))
        if len(variants) < 2:
            QMessageBox.information(
                self, T("至少要兩個配音"),
                T("配音對照是「這一版相對主配音差多少」，只有一個版本沒有對照的對象。\n\n上面的清單把要用的音訊都勾起來再開。"))
            return
        try:
            dlg = VariantMapDialog(self, card, variants,
                                   ref_guess=self.cmb_active.currentText().strip(),
                                   extra_dirs=[self.ed_out.text().strip()])
            dlg.exec()
        except Exception as e:                              # noqa: BLE001
            self.log_line.emit(traceback.format_exc())
            QMessageBox.warning(self, T("開不起來"), T("%s\n\n完整內容看下面的紀錄。") % e)
            return
        try:
            import kkvariantmap as VM
            ref, maps, src = VM.load_maps(card, [self.ed_out.text().strip()])
            if maps:
                self.log_line.emit(T("配音對照：主配音 %s，%d 個版本有對照表（%s）← %s")
                                   % (ref, len(maps), "、".join(sorted(maps)), src))
            else:
                self.log_line.emit(T("配音對照：目前沒有任何對照表，各版當成同步"))
        except Exception:                                   # noqa: BLE001
            pass

    def _align_apply(self, pairs, summary=None):
        """量出來的對應點蓋掉表格。

        是「蓋掉」不是「附加」—— 自動量出來的是完整的一套（每一段的頭和尾），
        跟手動量的混在一起只會讓同一段出現兩組來源不明的點。
        要保留舊的就先存成別的名字。
        """
        self.table.load_rows([(t, v, n) for t, v, n in pairs])
        self.log_line.emit(T("從切好的音頻反推：帶入 %d 個對應點（尚未存檔）") % len(pairs))
        self.status.emit(T("已帶入 %d 個對應點，確認後按「存 pairs.txt」") % len(pairs))

        if summary and summary.get("full"):
            # 片段把原檔蓋滿 → 沒有開場/過場/片尾 → 這張卡不需要影片。
            # 在主畫面也講一次，因為使用者按完「套用」之後視窗就關了，
            # 對話框裡那段字看不到了。
            self.lbl_edit.setText(
                T("<span style='color:#1a7f37'><b>不需要影片 —— 配音完整蓋滿原始音檔，這張卡沒有開場動畫、過場或片尾。把配音勾起來直接按「產生 cutscene.json」就好，★來源影片可以留空。</b></span>"))
            self.lbl_edit.setTextFormat(Qt.TextFormat.RichText)
            self.log_line.emit(T("不需要影片：配音完整蓋滿原檔（沒有開場 / 過場 / 片尾）"))

    # ---- 載入既有設定 ----

    def _guess_card(self, json_path):
        """json 的檔名就是 <卡名>.cutscene.json，拿卡名去幾個常見的地方找那張 png。

        只有舊的設定檔才會走到這裡 —— 新產生的 json 裡面有 sourceCard，
        直接照著開就好，不用猜。
        """
        stem = os.path.basename(json_path)
        for suf in (".cutscene.json", ".view.json", ".json"):
            if stem.lower().endswith(suf):
                stem = stem[:-len(suf)]
                break
        dirs = [os.path.dirname(os.path.abspath(json_path))]
        old = self.ed_card.text().strip() or self.settings.get("cut_card", "")
        if old:
            dirs.append(os.path.dirname(old))
        for key in ("output_dir", "scene_dir"):
            d = self.settings.get(key, "")
            if d:
                dirs.append(d)
        for d in dirs:
            p = os.path.join(d, stem + ".png")
            if os.path.isfile(p):
                return p
        return ""

    def load_json(self, path=None):
        """把一份做好的 cutscene.json 整個讀回介面上。

        為什麼要有這個：加一個配音版本、或多量一個對應點，這種事會一直發生，
        但原本只能從頭把整條流水線再走一次（重選卡、重加檔案、重打對應點）。
        現在載進來 → 改 → 再按「產生 cutscene.json」蓋回同一個檔就好。

        帶不回來的東西會在下面的「報告」講清楚，不會默默跳過 ——
        最常見的是檔案被搬走了，那種情況如果不講，產出來的 json 會少掉一個配音。
        """
        if not isinstance(path, str) or not path:
            start = self.ed_out.text().strip() or os.path.dirname(self._json_path or "")
            # 預設只列 *.cutscene.json。同資料夾裡還有 F7 存的 <卡名>.view.json，
            # 兩份同名只差中間那一段，用 *.json 當預設等於在請人點錯。
            path, _ = QFileDialog.getOpenFileName(
                self, T("載入既有的 cutscene.json"), start,
                T("cutscene 設定檔 (*.cutscene.json);;所有 json (*.json)"))
        if not path:
            return
        try:
            cfg = read_cutscene_json(path)
        except Exception as e:
            QMessageBox.warning(self, T("讀不了這份設定"), "%s\n\n%s" % (path, e))
            return

        # 點到視角檔（也可能是拖進來的）：旁邊有真的設定檔就自動改讀，
        # 沒有就直接擋下。這個判斷一定要在下面清空介面之前 ——
        # 先清再發現讀錯，使用者的卡片、清單和對應點就白白沒了。
        if looks_like_view_json(cfg, path):
            alt = sibling_cutscene_json(path)
            if not alt:
                QMessageBox.information(
                    self, T("這不是 cutscene 設定檔"),
                    T("%s\n\n這是 F7 插件存 VR 視角用的 <卡名>.view.json，裡面只有視角座標，沒有音軌也沒有對應點。\n同資料夾裡也找不到對應的 <卡名>.cutscene.json。\n\n介面沒有動，重選一次檔案就好。") % path)
                self.status.emit(T("選到的是視角檔，沒有載入"))
                return
            self.log_line.emit(T("[注意] %s 是 F7 的視角檔，已自動改讀 %s")
                               % (os.path.basename(path), os.path.basename(alt)))
            path = alt
            try:
                cfg = read_cutscene_json(path)
            except Exception as e:
                QMessageBox.warning(self, T("讀不了這份設定"), "%s\n\n%s" % (path, e))
                return

        # 載入＝「這一頁換成這份設定」，不是疊上去。
        # 血淚：第一版沒清，連續載兩份之後清單裡同時有兩張卡的檔案 ——
        # 同剪輯檢查拿 A 的影片去比 B 的音訊，滿江紅說「不同剪輯」；
        # 更糟的是直接按「產生」的話，A 的配音會被寫進 B 的 json。
        self.files.clear_all()
        self.table.setRowCount(0)
        self.lst_suggest.clear()
        self.lbl_edit.setText("")
        self.lbl_result.setText("")
        self._pairs_path = ""

        self._json_path = path
        self._map = mapping_from_json(path)
        self.ed_out.setText(os.path.dirname(os.path.abspath(path)))

        notes = []          # 沒帶回來的東西
        lines = [T("載入：") + path]

        # --- 場景卡 ---
        card = cfg.get("sourceCard") or ""
        card = os.path.normpath(card) if card else ""
        if not card or not os.path.isfile(card):
            card = self._guess_card(path)
        if card:
            # 注意順序：set_card 會順手去讀卡片專屬的 pairs.txt，
            # 底下的對應點再覆蓋掉它，所以以 json 裡記的那一份為準。
            self.set_card(card)
            lines.append(T("場景卡：") + card)
        else:
            notes.append(T("找不到對應的場景卡，要自己指定（舊的設定檔沒記這個）"))

        # --- 影片與配音 ---
        vid = os.path.normpath(cfg.get("videoFile") or "") if cfg.get("videoFile") else ""
        names = [str(n) for n in (cfg.get("variantNames") or [])]
        files = [os.path.normpath(p) for p in (cfg.get("variantFiles") or [])]

        add, missing = [], []
        if vid:
            (add if os.path.isfile(vid) else missing).append(vid)
        for p in files:
            (add if os.path.isfile(p) else missing).append(p)
        if self.files.add(add):
            # 順手驗一次長度：載回來的檔案如果被換成別的剪輯，對應點整組都會歪掉
            self._probe_added()

        if len(names) != len(files):
            notes.append(T("json 裡的配音名稱有 %d 個、檔案有 %d 個，數量對不上；對不到名字的用檔名重新猜，產生出來的按鈕名稱會跟原本不同")
                         % (len(names), len(files)))
            names += [""] * max(0, len(files) - len(names))

        # 名稱照 json 裡寫的，不要讓工具自己重猜 ——
        # 重猜出來的名字可能跟插件面板上的按鈕對不起來。
        # 改「名稱」欄同樣會在排序開著時讓列跳位，所以先把排序關掉（見 set_source）。
        hit, got = 0, []
        was = self.files.isSortingEnabled()
        self.files.setSortingEnabled(False)
        try:
            for nm, p in zip(names, files):
                if not nm or not os.path.isfile(p):
                    continue
                key = os.path.normcase(os.path.abspath(p))
                for r in range(self.files.rowCount()):
                    if os.path.normcase(self.files.path(r)) == key:
                        self.files.item(r, 1).setText(nm)
                        self.files.item(r, 0).setCheckState(Qt.CheckState.Checked)
                        hit += 1
                        got.append(nm)
                        break
        finally:
            self.files.setSortingEnabled(was)
        if vid and os.path.isfile(vid):
            self._set_source(vid)
            lines.append(T("來源影片：") + os.path.basename(vid))
        if hit:
            # 報「真的帶回來的」，不是 json 上寫的 —— 檔案被搬走的那些不該算進去
            lines.append(T("配音 %d 個：%s") % (hit, "、".join(got[:8])))
        if missing:
            notes.append(T("這幾個檔案不在原來的位置了，沒有帶進清單（產生前要補回來，不然新的 json 會少掉它們）：\n    ")
                         + "\n    ".join(missing))

        act = str(cfg.get("activeVariant") or "")
        if act and act not in got:
            # 「主要配音」指到一個沒帶回來的版本（通常是那個 wav 被搬走了）。
            # cmb_active 是可編輯的，setCurrentText 會照樣把不存在的名字填進去，
            # 然後 run() 原封不動送出去 —— 產生的 json 就有一個指不到任何按鈕的 activeVariant。
            notes.append(T("主要配音「%s」不在帶回來的配音裡，已改用 %s")
                         % (act, got[0] if got else T("（沒有可用的配音）")))
            act = got[0] if got else ""
        if act:
            self.cmb_active.setCurrentText(act)
            lines.append(T("主要配音：") + act)

        # --- 對應點 ---
        rows, src = [], ""
        pf = cfg.get("pairsFile") or ""
        if pf and os.path.isfile(pf):
            try:
                rows, src = read_pairs_file(pf), pf
                self._pairs_path = pf
            except Exception:
                rows = []
        if not rows and cfg.get("pairs"):
            try:
                rows = [(float(r[0]), float(r[1]),
                         str(r[2]) if len(r) > 2 else "") for r in cfg["pairs"]]
                src = T("設定檔內建的備份")
            except Exception:
                rows = []
        if rows:
            self.table.load_rows(rows)
            self._show_pairs_path()
            lines.append(T("對應點 %d 個（來源：%s）") % (len(rows), src))
            self._check_stale_pairs()
        elif self.table.rowCount() == 0:
            notes.append(T("這份設定裡沒有對應點，pairs.txt 也找不到 —— 要重新量"))

        if cfg.get("tracks") and card:
            self._set_step(4)

        self.lbl_loaded.setText(
            T("已載入 <b>%s</b>%s") % (os.path.basename(path),
                                    (T("　<span style='color:%s'>有 %d 項要處理</span>")
                                     % (RED, len(notes))) if notes else ""))
        self.report.setPlainText("\n".join(lines))
        if notes:
            self.report.appendHtml(
                "<br><span style='color:%s'><b>%s</b></span>"
                % (RED, "<br>".join(("！ " + n).replace("\n", "<br>").replace(" ", "&nbsp;")
                                    for n in notes)))
        for ln in lines:
            self.log_line.emit(ln)
        self.status.emit(T("已載入 ") + os.path.basename(path))

    def save_state(self):
        """把這一頁的狀態寫進共用的 settings —— 主視窗按「儲存設定」時會叫這個。"""
        paths = [self.files.path(r) for r in range(self.files.rowCount())]
        unchecked = [self.files.path(r) for r in range(self.files.rowCount())
                     if self.files.item(r, 0)
                     and self.files.item(r, 0).checkState() != Qt.CheckState.Checked]
        self.settings.update({
            "cut_card": self.ed_card.text().strip(),
            "cut_files": paths,
            "cut_unchecked": unchecked,
            "cut_video": self.files.source_path(),
            "cut_active": self.cmb_active.currentText().strip(),
            "cut_vnge": self.chk_vnge.isChecked(),
            "cut_force": self.chk_force.isChecked(),
            "cut_height": self.cmb_h.currentData(),
            "cut_out_dir": self.ed_out.text().strip(),
        })

    def shutdown(self):
        # 先把外部程式殺掉再 quit()。quit() 只是請事件迴圈收工，
        # 對「卡在 ffmpeg 管線上的那個 slot」毫無作用 —— 轉檔轉到一半關視窗，
        # wait() 逾時後 QThread 會帶著還在跑的執行緒被解構，Qt 直接讓程式當掉。
        for w in (getattr(self, "master_worker", None),
                  getattr(self, "wav_worker", None),
                  getattr(self, "norm_worker", None)):
            if w is not None and hasattr(w, "cancel"):
                try:
                    w.cancel()
                except Exception:
                    pass
        for th in (self.thread, getattr(self, "wav_thread", None),
                   getattr(self, "master_thread", None),
                   getattr(self, "norm_thread", None),
                   getattr(self, "probe_thread", None),
                   getattr(self, "autopair_thread", None)):
            if th is not None:
                th.quit()
                if not th.wait(5000):
                    th.terminate()      # 最後手段，總比帶著執行中的執行緒解構好
                    th.wait(2000)

    # ---- pairs.txt ----
    def _load_pairs(self, path=None):
        if not isinstance(path, str) or not path:
            path = self._pairs_default()
        if not path or not os.path.isfile(path):
            self.status.emit(T("找不到 pairs.txt，直接在表格裡新增就好"))
            return
        try:
            rows = read_pairs_file(path)
        except Exception as e:
            QMessageBox.warning(self, T("讀不了"), str(e))
            return
        self.table.load_rows(rows)
        self._pairs_path = path
        self._show_pairs_path()
        self.log_line.emit(T("讀入 %d 個對應點：%s") % (len(rows), path))
        self._check_stale_pairs()
        if os.path.basename(path).lower() == "pairs.txt":
            self.log_line.emit(T("[注意] 這是共用的 pairs.txt，同資料夾的其他卡片也會讀到它。按一下「存 pairs.txt」就會改存成這張卡專屬的名字。"))

    def _check_stale_pairs(self):
        """揪出「別張卡算出來的自動對齊點」。

        為什麼要查：「從切好的音頻反推」產生的點，timeline 那一欄**必然**
        等於某一段的起點或終點（程式就是拿 seg['start'] / seg['end'] 去填的）。
        落在別的地方就代表這一列跟它自己的說明對不上。

        但那不一定是壞事 —— 人常常只改數字、不改說明（把段尾換成自己量到的
        一個好認的瞬間，就會這樣）。所以改完數字的那一刻說明就會被改成
        「已手動改過」（見 PairTable.note_edit），這裡只查還標著純「自動對齊」的列。
        真的對不上時也不下定論，兩種可能都講出來讓人自己認。
        """
        self.table.clear_flags()
        card = self.ed_card.text().strip()
        if not card or not os.path.isfile(card):
            return
        try:
            segs = load_card_segments(card)
        except BaseException:                               # noqa: BLE001
            return                       # 讀不到段落就不查，不要拿警告嚇人
        edges = []
        for s in segs:
            edges.append((s["start"], s["index"], T("起點")))
            edges.append((s["end"], s["index"], T("終點")))
        bad = 0
        for r in range(self.table.rowCount()):
            note = (self.table.item(r, 2).text() if self.table.item(r, 2) else "")
            if PairTable.AUTO_TAG not in note or PairTable.EDITED_TAG in note:
                continue
            try:
                t = parse_time((self.table.item(r, 0).text()
                                if self.table.item(r, 0) else "").strip())
            except ValueError:
                continue
            if any(abs(t - e) < 0.002 for e, _i, _k in edges):
                continue
            near = min(edges, key=lambda e: abs(e[0] - t))
            why = (T("標著「自動對齊」的點應該落在段落的起點或終點上，但 %s 不是（最近：第 %d 段的%s %s，差 %.3f 秒）。\n手動改過的話把說明清掉即可；如果是別張卡算出來的舊點，請刪掉或重跑反推。")
                   % (fmt_time(t), near[1], near[2], fmt_time(near[0]),
                      abs(near[0] - t)))
            self.table.flag_row(r, why)
            bad += 1
            self.log_line.emit(T("[確認一下] 第 %d 列 timeline %s 標著「自動對齊」，卻不在任何段落的起訖上（最近：第 %d 段%s %s）—— 手動改過的話沒事，說明清掉即可")
                               % (r + 1, fmt_time(t), near[1], near[2],
                                  fmt_time(near[0])))
        if bad:
            self.status.emit(T("有 %d 列標著「自動對齊」但對不上段落起訖 —— 標紅了，滑鼠移上去看說明") % bad)

    def _save_pairs(self):
        """存檔成功回傳 True。

        回傳值是給 run() 用的：存不起來（唯讀資料夾、卡片在拔掉的隨身碟上、
        檔案被別的程式鎖住）卻照樣往下跑的話，plan 會拿 self._pairs_path
        —— 也就是「上一次」的對應點檔 —— 去產生 json，甚至因為沒帶 --pairs
        而回頭吃到同資料夾那份共用的 pairs.txt，等於用另一張卡的量測結果。
        報告會驗那一份、然後說一切正常。這種錯太難發現，必須擋在這裡。
        """
        path = self._pairs_named()          # 永遠存成卡片專屬名
        if not path:
            QMessageBox.warning(self, T("還不能存"), T("先指定場景卡，才知道要存在哪裡"))
            return False
        try:
            rows = self.table.rows()
        except ValueError as e:
            QMessageBox.warning(self, T("表格有問題"), str(e))
            return False
        try:
            with open(path, "w", encoding="utf-8") as f:
                f.write("# timeline        影片              說明\n")
                for t, v, n in rows:
                    f.write("%-17s %-17s %s\n"
                            % (fmt_time(t), fmt_time(v), ("# " + n) if n else ""))
        except Exception as e:
            QMessageBox.warning(self, T("存不了"), str(e))
            return False
        moved = self._pairs_path and os.path.abspath(self._pairs_path) != os.path.abspath(path)
        self._pairs_path = path
        self._show_pairs_path()
        self.log_line.emit(T("寫出 %d 個對應點：%s") % (len(rows), path))
        if moved:
            self.log_line.emit(T("（原本讀的是共用的 pairs.txt，已改存成這張卡專屬的名字）"))
        self.status.emit(T("對應點已存檔"))
        return True

    # ---- 跳轉 ----
    def _jump(self):
        if self._map is None:
            self.status.emit(T("還沒有對應曲線，先按「產生 cutscene.json」"))
            return
        try:
            t = parse_time(self.ed_jump.text())
        except ValueError as e:
            self.status.emit(str(e))
            return
        v = self._map(t)
        if v is None:
            self.status.emit(T("這個 timeline 秒數不在任何段落裡"))
            return
        self.video.seek(v)
        self.status.emit(T("timeline %s → 預測影片 %s") % (fmt_time(t), fmt_time(v)))

    def _suggest_clicked(self, item):
        self.ed_jump.setText(item.text().split("　")[0].strip())
        self._jump()

    def _reveal(self):
        target = self._json_path or self.ed_card.text().strip()
        d = os.path.dirname(target)
        if d and os.path.isdir(d):
            try:
                os.startfile(d)                       # Windows
            except Exception:
                from PyQt6.QtGui import QDesktopServices
                QDesktopServices.openUrl(QUrl.fromLocalFile(d))

    # ---- 執行 ----
    def run(self):
        card = self.ed_card.text().strip()
        if not card or not os.path.isfile(card):
            QMessageBox.warning(self, T("還不能執行"), T("先指定合併好的場景卡"))
            return
        try:
            rows = self.table.rows()
        except ValueError as e:
            QMessageBox.warning(self, T("表格有問題"), str(e))
            return
        if len(rows) < 2:
            QMessageBox.warning(self, T("對應點太少"),
                                T("每一段至少要兩個點。只有一個點的話尺度無從驗證，實測出過 8 秒等級的偏差。"))
            return
        # 用「合併場景」接出來的設定檔不能在這裡重新產生：它的後半段是別張卡的
        # 影片和音檔接上去的，這一頁只認得一支來源影片，重算會把後面幾段的過場弄壞。
        stem0 = os.path.splitext(os.path.basename(card))[0]
        outdir0 = self.ed_out.text().strip() or os.path.dirname(card)
        prev = os.path.join(outdir0, stem0 + ".cutscene.json")
        try:
            merged = bool(os.path.isfile(prev) and (read_cutscene_json(prev).get("mergedFrom") or [])
                          and len(read_cutscene_json(prev).get("mergedFrom")) > 1)
        except Exception:
            merged = False
        if merged:
            if QMessageBox.question(
                    self, T("這份設定是接出來的"),
                    T("這張卡現有的 cutscene.json 是「合併場景」時從各張卡的設定接起來的。\n在這裡重新產生會蓋掉它，而且後面幾張卡的過場和音訊位置會算錯。\n\n要改的話，建議改原本各張卡的設定，再回「合併場景」重新接一次。\n\n還是要在這裡重新產生嗎？"),
                    QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                    QMessageBox.StandardButton.No) != QMessageBox.StandardButton.Yes:
                return
        # 對應點存不起來就整個停下來 —— 理由見 _save_pairs 的說明
        if not self._save_pairs():
            return

        argv = ["plan", card, "--auto-slope"]
        if self._pairs_path:
            argv += ["--pairs", self._pairs_path]
        vid = self.files.source_path()
        if vid:
            argv += ["--video", vid]
            # 播放器裡的長度只有在「播放器載的就是這一支」時才算數。
            # ★來源是清單算出來的，播放器是上一次 _set_source 載的，兩者可以不一樣
            #（例如載入的 json 裡影片被搬走了，使用者另外手動加了一支）。
            # 拿錯的長度會讓片尾被算到一個不存在的秒數，而且「超過影片總長」的
            # 檢查也是拿同一個錯數字去比，所以永遠不會亮。
            # 不給的話 kkcutscene 會自己用 ffprobe 量，寧可慢一點也不要錯。
            dur = self.video.duration()
            same = (os.path.normcase(os.path.abspath(self.video.source_path() or "x"))
                    == os.path.normcase(os.path.abspath(vid)))
            if dur > 0 and same:
                argv += ["--video-duration", "%.3f" % dur]
            elif dur > 0:
                self.log_line.emit(T("[注意] 播放器裡的不是 ★來源影片，長度改由 ffprobe 量"))
        for name, path in self.files.checked(T("音訊")):
            argv += ["--variant", "%s=%s" % (name, path)]
        self.save_state()
        act = self.cmb_active.currentText().strip()
        if act:
            argv += ["--active", act]

        stem = os.path.splitext(os.path.basename(card))[0]
        outdir = self.ed_out.text().strip() or os.path.dirname(card)
        try:
            if not os.path.isdir(outdir):
                os.makedirs(outdir)
        except Exception as e:
            QMessageBox.warning(self, T("輸出資料夾建立不了"), str(e))
            return
        out = os.path.join(outdir, stem + ".cutscene.json")
        argv += ["-o", out]

        self.btn_run.setEnabled(False)
        self.lbl_result.setText("")
        self.report.setPlainText(T("執行中…"))
        self.status.emit(T("產生中…"))
        self.log_line.emit("=" * 60)
        self.log_line.emit("kkcutscene " + " ".join(argv))
        self.worker.submit(argv, out)
        QMetaObject.invokeMethod(self.worker, "run",
                                 Qt.ConnectionType.QueuedConnection)

    def _finished(self, ok, text, out):
        self.btn_run.setEnabled(True)
        body = [ln for ln in text.splitlines() if not ln.startswith(">>>")]
        tips = [ln[3:].strip() for ln in text.splitlines() if ln.startswith(">>>")]
        self.report.setPlainText("\n".join(body).rstrip())
        if tips:
            self.report.appendHtml(
                "<br><span style='color:%s'><b>%s</b></span>"
                % (RED, "<br>".join(t.replace(" ", "&nbsp;") for t in tips)))
        self.report.verticalScrollBar().setValue(
            self.report.verticalScrollBar().maximum())
        for ln in text.splitlines():
            if ln.strip():
                self.log_line.emit(ln)

        # 建議清單（報告最後那幾行 >>> 開頭的）
        self.lst_suggest.clear()
        for ln in text.splitlines():
            if not ln.startswith(">>>"):
                continue
            m = re.search(r"((?:\d+:)?\d{2}:\d{2}\.\d{3})", ln)
            if m:
                self.lst_suggest.addItem(m.group(1) + "　" + ln[3:].strip())

        if ok and os.path.isfile(out):
            self._json_path = out
            self._map = mapping_from_json(out)
            self._set_step(4)
            bad = "! " in text
            self.lbl_result.setText(
                (T("<b>已寫出</b> ") + os.path.basename(out)) +
                (T("　<span style='color:%s'>報告裡有提醒，往下看</span>") % RED if bad else ""))
            self.lbl_result.setTextFormat(Qt.TextFormat.RichText)
            self.status.emit(T("完成：") + out)
        else:
            self.lbl_result.setText(T("<span style='color:%s'>失敗，看報告</span>") % RED)
            self.lbl_result.setTextFormat(Qt.TextFormat.RichText)
            self.status.emit(T("失敗"))
