#!/usr/bin/env python3
"""kkbridge — 恋活合卡橋接工具

讀卡 → 勾選要處理的服裝槽 → 執行。也可以掛著監看工單資料夾。
"""
from __future__ import annotations

import json
import queue
import sys
import time
import traceback
from datetime import datetime
from pathlib import Path

from PyQt6.QtCore import QObject, Qt, QThread, QUrl, pyqtSignal
from PyQt6.QtGui import QDesktopServices, QFont, QPixmap
from PyQt6.QtWidgets import (
    QAbstractItemView, QApplication, QCheckBox, QComboBox, QFileDialog,
    QGridLayout, QGroupBox, QHBoxLayout, QHeaderView, QLabel, QLineEdit,
    QMainWindow, QPlainTextEdit, QPushButton, QSpinBox, QSplitter,
    QTableWidget, QTableWidgetItem, QTabWidget, QVBoxLayout, QWidget,
)

import kkmerge
from kklang import T, NAMES, set_lang

APP_NAME = "kkbridge"
VERSION = "1.1.0"
POLL_SECONDS = 0.7
JOB_SUFFIX = ".job.json"
DONE_SUFFIX = ".done.json"
RED = "#c0392b"


def app_dir() -> Path:
    if getattr(sys, "frozen", False):
        return Path(sys.executable).parent
    return Path(__file__).resolve().parent


SETTINGS_PATH = app_dir() / "kkbridge_settings.json"
DEFAULTS = {
    "game_root": "", "chara_dir": "", "coord_dir": "",
    "watch_dir": "", "output_dir": "", "pushup": "follow_outfit",
    "skin_overlay": "keep_target", "clean": True, "autostart": True,
    "lang": 0,                     # 0=繁體中文 1=English 2=日本語
}
CHARA_SUB = "UserData/chara/female"          # 讀角色卡預設開這裡
COORD_SUB = "UserData/coordinate"            # 讀服裝卡預設開這裡
OUTPUT_SUB = "UserData/chara/female/Temp"    # 產出與工單都在這裡


def guess_game_root() -> str:
    """猜遊戲根目錄：先看自己所在的資料夾（發佈的 zip 是解壓在遊戲根目錄），
    再看常見安裝位置；找得到 UserData 就算數。"""
    here = app_dir()
    if (here / "UserData").is_dir():
        return str(here)
    for d in ("C:/Koikatu", "D:/Koikatu", "E:/Koikatu", "F:/Koikatu",
              "C:/Illusion/Koikatu", "D:/Illusion/Koikatu"):
        if (Path(d) / "UserData").is_dir():
            return d
    return ""


def derive_dirs(s: dict) -> dict:
    """根目錄 -> 各預設資料夾。使用者自己填過的優先，不覆蓋。"""
    root = s.get("game_root") or ""
    out = dict(s)
    if root:
        for key, sub in (("chara_dir", CHARA_SUB), ("coord_dir", COORD_SUB),
                         ("output_dir", OUTPUT_SUB), ("watch_dir", OUTPUT_SUB)):
            if not out.get(key):
                out[key] = str(Path(root) / sub)
    return out


def load_settings() -> dict:
    s = dict(DEFAULTS)
    try:
        s.update(json.loads(SETTINGS_PATH.read_text("utf-8")))
    except Exception:  # noqa: BLE001 - 設定壞掉不該擋住啟動
        pass
    if not s.get("game_root"):
        s["game_root"] = guess_game_root()
    return derive_dirs(s)


def save_settings(s: dict) -> None:
    try:
        SETTINGS_PATH.write_text(json.dumps(s, ensure_ascii=False, indent=2), "utf-8")
    except Exception:  # noqa: BLE001
        pass


def card_thumbnail(path):
    """只取卡片前面那段 PNG，不要為了縮圖把 70 MB 整個丟給 Qt。"""
    try:
        data = Path(path).read_bytes()[:2_000_000]
        n = kkmerge.png_len(data, 0)
        pm = QPixmap()
        return pm if pm.loadFromData(data[:n], "PNG") else None
    except Exception:  # noqa: BLE001
        return None


def temp_dir() -> Path:
    d = app_dir() / "_kkbridge_tmp"
    d.mkdir(parents=True, exist_ok=True)
    return d


# --------------------------------------------------------------- 工單執行
def _step(job: dict, settings: dict, out: str, log):
    op = job["op"]
    if op == "append":
        return kkmerge.merge(job["chara"], job["coord"], int(job.get("outfit", 0)),
                             out, clean=job.get("clean", settings["clean"]), log=log)
    if op == "transplant":
        return kkmerge.transplant(
            job["src"], int(job.get("src_outfit", 0)),
            job["dst"], int(job.get("dst_outfit", 0)), out,
            pushup=job.get("pushup", settings["pushup"]),
            skin_overlay=job.get("skin_overlay", settings["skin_overlay"]),
            clean=job.get("clean", settings["clean"]), log=log)
    if op == "clean":
        card = kkmerge.CharaCard(job["chara"])
        rm = kkmerge.clean_orphans(card, log)
        card.save(out)
        return {"ok": True, "out": out, "orphans_removed": rm,
                "size_bytes": Path(out).stat().st_size}
    raise ValueError(T("不認得的 op：{0}").format(repr(op)))


def run_job(job: dict, settings: dict, log) -> dict:
    """單一動作，或 batch：把上一步的產出餵給下一步，最後才落到 out。"""
    out = job.get("out")
    if not out:
        odir = Path(job.get("output_dir") or settings.get("output_dir") or app_dir())
        odir.mkdir(parents=True, exist_ok=True)
        first = (job.get("batch") or [{}])[0]
        stem = Path(job.get("chara") or job.get("dst")
                    or first.get("chara") or first.get("dst") or "card").stem
        out = str(odir / f"{stem}_{datetime.now():%H%M%S}.png")

    pre = job.get("pre")
    pre_out = None
    if pre:
        log(T("── 前置：{0} 步").format(len(pre)))
        cur = None
        for i, st in enumerate(pre):
            st = dict(st)
            if cur is not None:
                st["chara"] = cur
            dest = str(temp_dir() / f"pre_{i}_{datetime.now():%H%M%S%f}.png")
            _step(st, settings, dest, log)
            if cur is not None:
                try:
                    Path(cur).unlink()
                except Exception:  # noqa: BLE001
                    pass
            cur = dest
        pre_out = cur

    steps = job.get("batch")
    if steps and pre_out:
        steps = [{k: (pre_out if v == "<PRE>" else v) for k, v in st.items()}
                 for st in steps]
    if not steps:
        return _step(job, settings, out, log)

    warnings, orphans, tmps = [], {}, []
    cur = None
    for i, st in enumerate(steps):
        st = dict(st)
        if cur is not None:                       # 接上一步的產出
            st["chara" if st["op"] in ("append", "clean") else "dst"] = cur
        last = i == len(steps) - 1
        dest = out if last else str(
            temp_dir() / f"chain_{i}_{datetime.now():%H%M%S%f}.png")
        log(T("── 第 {0}/{1} 步：{2}").format(i + 1, len(steps), st['op']))
        res = _step(st, settings, dest, log)
        warnings += res.get("warnings") or []
        for k, v in (res.get("orphans_removed") or {}).items():
            orphans[k] = orphans.get(k, 0) + v
        if cur is not None:
            tmps.append(cur)
        cur = dest
    if pre_out:
        tmps.append(pre_out)
    for t in tmps:
        try:
            Path(t).unlink()
        except Exception:  # noqa: BLE001
            pass
    return {"ok": True, "out": out, "steps": len(steps),
            "warnings": sorted(set(warnings)), "orphans_removed": orphans,
            "size_bytes": Path(out).stat().st_size}


class Worker(QObject):
    """單一背景執行緒，工單一件一件做，不會同時改兩張卡。"""

    log_line = pyqtSignal(str)
    job_started = pyqtSignal(str, str)
    job_finished = pyqtSignal(str, bool, str)

    def __init__(self, settings: dict):
        super().__init__()
        self.q = queue.Queue()
        self.settings = settings
        self._stop = False

    def submit(self, name: str, job: dict, result_path):
        self.q.put((name, job, result_path))

    def stop(self):
        self._stop = True
        self.q.put(None)

    def loop(self):
        while not self._stop:
            item = self.q.get()
            if item is None:
                break
            name, job, result_path = item
            label = job.get("op") or ("batch×%d" % len(job.get("batch", [])))
            self.job_started.emit(name, label)
            t0 = time.time()
            try:
                res = run_job(job, self.settings,
                              lambda *a: self.log_line.emit(
                                  " ".join(str(x) for x in a)))
                res["seconds"] = round(time.time() - t0, 1)
                self.job_finished.emit(
                    name, True, T("{0}（{1} 秒）").format(Path(res['out']).name, res['seconds']))
                for w in res.get("warnings") or []:
                    self.log_line.emit(T("[注意] {0}").format(w))
            except Exception as e:  # noqa: BLE001 - 單件失敗不該讓佇列停擺
                res = {"ok": False, "error": f"{type(e).__name__}: {e}"}
                self.log_line.emit(traceback.format_exc().strip())
                self.job_finished.emit(name, False, res["error"])
            if result_path is not None:
                try:
                    Path(result_path).write_text(
                        json.dumps(res, ensure_ascii=False, indent=2), "utf-8")
                except Exception:  # noqa: BLE001
                    self.log_line.emit(T("寫不出結果檔 {0}").format(result_path))


class Watcher(QObject):
    """輪詢監看資料夾。用輪詢而不是檔案事件，避免插件還在寫就被撿走。"""

    found = pyqtSignal(str, dict, object)
    log_line = pyqtSignal(str)

    def __init__(self):
        super().__init__()
        self.dir = None
        self.running = False
        self.seen = set()

    def loop(self):
        while True:
            time.sleep(POLL_SECONDS)
            if not self.running or self.dir is None:
                continue
            try:
                files = sorted(Path(self.dir).glob("*" + JOB_SUFFIX))
            except Exception:  # noqa: BLE001
                continue
            for f in files:
                if str(f) in self.seen:
                    continue
                size = f.stat().st_size
                time.sleep(0.2)
                if f.stat().st_size != size:
                    continue                      # 還在寫，下一輪再撿
                self.seen.add(str(f))
                try:
                    job = json.loads(f.read_text("utf-8"))
                except Exception as e:  # noqa: BLE001
                    self.log_line.emit(T("工單解析失敗 {0}：{1}").format(f.name, e))
                    continue
                self.found.emit(f.name, job,
                                f.with_name(f.name[:-len(JOB_SUFFIX)] + DONE_SUFFIX))


# --------------------------------------------------------------- 讀卡面板
class CardPanel(QGroupBox):
    """可以拖進來、也可以按按鈕選檔。讀完會把卡片資訊發出去。"""

    loaded = pyqtSignal(str, dict)

    def __init__(self, title: str, want: str = "character", start_dir=None):
        super().__init__(title)
        self.want = want
        self.start_dir = start_dir          # callable，回傳預設開啟的資料夾
        self.path = ""
        self.info = {}
        self.setAcceptDrops(True)

        self.thumb = QLabel(T("把卡片\n拖到這裡"))
        self.thumb.setFixedSize(96, 108)
        self.thumb.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.thumb.setStyleSheet(
            "border:2px dashed #999; border-radius:6px; color:#777;")
        self.name = QLabel(T("（尚未讀卡）"))
        self.name.setWordWrap(True)
        self.detail = QLabel("")
        self.detail.setStyleSheet("color:#666;")
        btn = QPushButton(T("讀卡…"))
        btn.clicked.connect(self.pick)

        right = QVBoxLayout()
        right.addWidget(self.name)
        right.addWidget(self.detail)
        right.addWidget(btn, alignment=Qt.AlignmentFlag.AlignLeft)
        right.addStretch(1)

        lay = QHBoxLayout(self)
        lay.addWidget(self.thumb)
        lay.addLayout(right, 1)

    def dragEnterEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()

    def dropEvent(self, e):
        for url in e.mimeData().urls():
            p = url.toLocalFile()
            if p.lower().endswith(".png"):
                self.load(p)
                break

    def pick(self):
        start = self.path or ""
        if not start and self.start_dir:
            d = self.start_dir() or ""
            if d and Path(d).is_dir():
                start = d
        p, _ = QFileDialog.getOpenFileName(self, T("選擇卡片"), start, T("卡片 (*.png)"))
        if p:
            self.load(p)

    def load(self, path: str):
        try:
            info = kkmerge.info(path)
        except Exception as e:  # noqa: BLE001
            self.name.setText(T("<span style='color:{0}'>讀不出來：{1}</span>").format(RED, e))
            return
        if self.want and info.get("type") != self.want:
            got = T("服裝卡") if info.get("type") == "coordinate" else T("角色卡")
            need = T("服裝卡") if self.want == "coordinate" else T("角色卡")
            self.name.setText(
                T("<span style='color:{0}'>這是{1}，這裡要的是{2}</span>").format(RED, got, need))
            return

        self.path, self.info = path, info
        pm = card_thumbnail(path)
        if pm:
            self.thumb.setPixmap(pm.scaled(
                96, 108, Qt.AspectRatioMode.KeepAspectRatio,
                Qt.TransformationMode.SmoothTransformation))
            self.thumb.setStyleSheet("border:1px solid #bbb; border-radius:6px;")
        self.name.setText("<b>" + Path(path).name + "</b>")
        mb = Path(path).stat().st_size / 1e6
        if info["type"] == "character":
            self.detail.setText(
                T("{0} 套換裝　{1} 個外掛　{2:.0f} MB").format(info['outfits'], len(info['plugins']), mb))
        else:
            self.detail.setText(
                T("服裝卡「{0}」　{1} 個飾品　{2} 個欄位").format(info.get('name', ''), info.get('accessories', 0), info.get('slots', 0)))
        self.loaded.emit(path, info)


class OutfitTable(QTableWidget):
    """列出卡片的服裝槽，可以勾選；移植模式多一欄「輸出到第幾套」。"""

    def __init__(self, with_target=False):
        self.with_target = with_target
        cols = ["", T("編號"), T("名稱"), T("飾品"), T("欄位")]
        if with_target:
            cols.append(T("輸出到第幾套"))
        super().__init__(0, len(cols))
        self.setHorizontalHeaderLabels(cols)
        self.verticalHeader().setVisible(False)
        self.setSelectionMode(QAbstractItemView.SelectionMode.NoSelection)
        self.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.horizontalHeader().setSectionResizeMode(
            2, QHeaderView.ResizeMode.Stretch)
        self.setColumnWidth(0, 34)
        self.setColumnWidth(1, 48)
        self.boxes = []
        self.spins = []

    def fill(self, info: dict):
        self.setRowCount(0)
        self.boxes, self.spins = [], []
        for o in info.get("outfit_detail", []):
            r = self.rowCount()
            self.insertRow(r)
            cb = QCheckBox()
            cb.stateChanged.connect(lambda _s, row=r: self._paint(row))
            holder = QWidget()
            hl = QHBoxLayout(holder)
            hl.setContentsMargins(0, 0, 0, 0)
            hl.addWidget(cb, alignment=Qt.AlignmentFlag.AlignCenter)
            self.setCellWidget(r, 0, holder)
            self.boxes.append(cb)

            vals = [str(o["index"]), o["name"], str(o["accessories"]), str(o["slots"])]
            for c, v in enumerate(vals, start=1):
                self.setItem(r, c, QTableWidgetItem(v))
            if self.with_target:
                sp = QSpinBox()
                sp.setRange(0, 99)
                sp.setValue(o["index"])
                self.setCellWidget(r, 5, sp)
                self.spins.append(sp)
        self.setColumnWidth(0, 34)
        self.setColumnWidth(1, 48)
        self.horizontalHeader().setSectionResizeMode(
            2, QHeaderView.ResizeMode.Stretch)

    def set_target_limit(self, n: int):
        for sp in self.spins:
            sp.setMaximum(max(0, n - 1))

    def _paint(self, row: int):
        on = self.boxes[row].isChecked()
        for c in range(1, 5):
            it = self.item(row, c)
            if it is None:
                continue
            f = it.font()
            f.setBold(on)
            it.setFont(f)
            it.setForeground(Qt.GlobalColor.red if on else Qt.GlobalColor.black)
        if self.with_target and row < len(self.spins):
            self.spins[row].setStyleSheet(
                "color:%s; font-weight:bold;" % RED if on else "")

    def selection(self):
        """回傳 [(來源索引, 目標索引)]；非移植模式目標等於來源。"""
        out = []
        for r, cb in enumerate(self.boxes):
            if not cb.isChecked():
                continue
            src = int(self.item(r, 1).text())
            dst = self.spins[r].value() if self.with_target else src
            out.append((src, dst))
        return out

    def check_all(self, on: bool):
        for cb in self.boxes:
            cb.setChecked(on)


class PathPicker(QWidget):
    def __init__(self, caption: str, is_dir=False, save=False, reveal=False):
        super().__init__()
        self.caption, self.is_dir, self.save = caption, is_dir, save
        self.edit = QLineEdit()
        b = QPushButton(T("瀏覽…"))
        b.setFixedWidth(70)
        b.clicked.connect(self.pick)
        lay = QHBoxLayout(self)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.addWidget(self.edit)
        lay.addWidget(b)
        if reveal:
            o = QPushButton(T("開啟"))
            o.setFixedWidth(56)
            o.setToolTip(T("在檔案總管開啟這個資料夾"))
            o.clicked.connect(self.reveal)
            lay.addWidget(o)

    def reveal(self):
        d = self.text()
        if not d:
            return
        p = Path(d)
        if not p.is_dir():
            p = p.parent
        if not p.is_dir():
            try:
                p.mkdir(parents=True, exist_ok=True)
            except Exception:  # noqa: BLE001
                return
        QDesktopServices.openUrl(QUrl.fromLocalFile(str(p)))

    def pick(self):
        if self.is_dir:
            p = QFileDialog.getExistingDirectory(self, self.caption, self.edit.text())
        elif self.save:
            p, _ = QFileDialog.getSaveFileName(self, self.caption, self.edit.text(),
                                               T("角色卡 (*.png)"))
        else:
            p, _ = QFileDialog.getOpenFileName(self, self.caption, self.edit.text(),
                                               T("卡片 (*.png)"))
        if p:
            self.edit.setText(p)

    def text(self):
        return self.edit.text().strip()

    def set(self, v):
        self.edit.setText(v or "")


def _row(*widgets):
    w = QWidget()
    lay = QHBoxLayout(w)
    lay.setContentsMargins(0, 0, 0, 0)
    for x in widgets:
        lay.addWidget(x)
    return w


def _pair(a, b):
    w = QWidget()
    lay = QHBoxLayout(w)
    lay.setContentsMargins(0, 0, 0, 0)
    lay.addWidget(a, 1)
    lay.addWidget(b, 1)
    return w


def _buttons(table, run):
    on = QPushButton(T("全選"))
    on.clicked.connect(lambda: table.check_all(True))
    off = QPushButton(T("全不選"))
    off.clicked.connect(lambda: table.check_all(False))
    go = QPushButton(T("執行"))
    go.setMinimumHeight(34)
    go.clicked.connect(run)
    return _row(on, off, go)


# --------------------------------------------------------------- 主視窗
class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.settings = load_settings()
        # 語言要在任何介面元件生出來之前就定好 —— Qt 的文字是建構時設進去的，
        # 設定晚了這一輪就還是舊語言
        set_lang(int(self.settings.get("lang", 0) or 0))
        self.resize(1000, 780)

        self._build_ui()

        # 執行緒只建一次。換語言會把介面整個重建，但這兩條不能跟著重建 ——
        # 正在處理的工單會被丟掉，而且舊的 QThread 還連著已經被刪掉的元件。
        self.worker = Worker(self.settings)
        self.wthread = QThread(self)
        self.worker.moveToThread(self.wthread)
        self.wthread.started.connect(self.worker.loop)
        self.worker.log_line.connect(self.append_log)
        self.worker.job_started.connect(self.on_started)
        self.worker.job_finished.connect(self.on_finished)
        self.wthread.start()

        self.watcher = Watcher()
        self.vthread = QThread(self)
        self.watcher.moveToThread(self.vthread)
        self.vthread.started.connect(self.watcher.loop)
        self.watcher.found.connect(self.on_job_found)
        self.watcher.log_line.connect(self.append_log)
        self.vthread.start()

        if self.settings.get("autostart") and self.settings.get("watch_dir"):
            # 預設的工單資料夾第一次用時還沒建：遊戲根目錄認得的話就自己建起來
            wd = Path(self.settings["watch_dir"])
            root = self.settings.get("game_root") or ""
            if (not wd.is_dir() and root and (Path(root) / "UserData").is_dir()
                    and wd == Path(root) / OUTPUT_SUB):
                try:
                    wd.mkdir(parents=True, exist_ok=True)
                except OSError:
                    pass
            if Path(self.settings["watch_dir"]).is_dir():
                self.btn_watch.setChecked(True)
            else:
                self.append_log(T("預設監看資料夾不存在：{0}"
                                  "　→ 到「設定」填遊戲根目錄")
                                .format(self.settings["watch_dir"]))

    # ---- 介面 ----
    def _build_ui(self):
        """把整個介面建起來。換語言時會再跑一次。

        為什麼是整個重建，不是一個個 setText 回去：Qt 的文字是建構元件時
        設進去的，要靠 setText 換語言就得替每一個 QLabel / QPushButton /
        表頭 / 提示文字留一個成員變數再一個個改回來 —— 一百多處，
        漏掉一個就是介面上混著兩種語言，而且漏掉的通常是最少人點到的那個。
        重建一次就不會有漏網的。

        代價是元件會換一批新的，所以使用者填到一半的東西要自己接回去，
        那就是 _capture_state() / _restore_state() 在做的事。
        """
        self.setWindowTitle(T("{0} {1} — 恋活合卡").format(APP_NAME, VERSION))

        # 舊的那一棵要親手拆掉。setCentralWidget 對舊的只會 deleteLater()，
        # 而 deleteLater 要等事件迴圈回到最上層才真的執行 —— 在那之前舊元件
        # 還掛在視窗底下，每換一次語言就多留一整棵。setParent(None) 先把它
        # 從樹上摘掉，deleteLater() 再把記憶體還回去。
        old = self.centralWidget()
        if old is not None:
            old.setParent(None)
            old.deleteLater()

        self.tabs = QTabWidget()
        self.tabs.addTab(self._append_tab(), T("附加飾品"))
        self.tabs.addTab(self._transplant_tab(), T("移植整套換裝"))
        self.tabs.addTab(self._clean_tab(), T("修卡"))
        self.tabs.addTab(self._watch_tab(), T("監看工單"))
        self.tabs.addTab(self._settings_tab(), T("設定"))

        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        self.log.setMaximumBlockCount(4000)
        f = QFont("Consolas")
        f.setStyleHint(QFont.StyleHint.Monospace)
        self.log.setFont(f)

        split = QSplitter(Qt.Orientation.Vertical)
        split.addWidget(self.tabs)
        box = QWidget()
        bl = QVBoxLayout(box)
        bl.setContentsMargins(0, 0, 0, 0)
        bl.addWidget(QLabel(T("紀錄")))
        bl.addWidget(self.log)
        split.addWidget(box)
        split.setSizes([540, 240])
        self.setCentralWidget(split)
        self.status = self.statusBar()
        self.status.showMessage(T("就緒"))

    # ---- 換語言 ----
    def _capture_state(self):
        """重建介面之前，把使用者已經填好的東西記下來。"""
        return {
            "tab": self.tabs.currentIndex(),
            "cards": [p.path for p in (self.a_card, self.a_coord, self.t_src,
                                       self.t_coord, self.t_dst, self.c_card)],
            "outs": [self.a_out.text(), self.t_out.text(), self.c_out.text()],
            "watch_dir": self.w_dir.text(),
            "watching": self.btn_watch.isChecked(),
            "log": self.log.toPlainText(),
        }

    def _restore_state(self, st):
        for panel, path in zip((self.a_card, self.a_coord, self.t_src,
                                self.t_coord, self.t_dst, self.c_card),
                               st["cards"]):
            if path:
                panel.load(path)
        for picker, v in zip((self.a_out, self.t_out, self.c_out), st["outs"]):
            picker.set(v)
        self.w_dir.set(st["watch_dir"])
        if st["log"]:
            self.log.setPlainText(st["log"])
        self.tabs.setCurrentIndex(st["tab"])
        # 監看是背景執行緒在跑，重建的是按鈕而已 —— 把按鈕的狀態接回去，
        # toggled 會再跑一次 toggle_watch 把文字和標籤設成對的語言
        if st["watching"]:
            self.btn_watch.setChecked(True)

    def change_lang(self, index: int):
        if index == int(self.settings.get("lang", 0) or 0):
            return
        st = self._capture_state()
        # 換語言之前先停掉監看，免得重建到一半有工單進來去找已經被刪掉的表格
        was_watching = st["watching"]
        if was_watching:
            self.btn_watch.setChecked(False)
        self.settings["lang"] = index
        save_settings(self.settings)
        set_lang(index)
        self._build_ui()
        self._restore_state(st)

    # ---- 附加飾品 ----
    def _append_tab(self):
        w = QWidget()
        lay = QVBoxLayout(w)
        self.a_card = CardPanel(T("① 目標角色卡"), "character",
                                lambda: self.settings.get("chara_dir"))
        self.a_coord = CardPanel(T("② 來源服裝卡"), "coordinate",
                                 lambda: self.settings.get("coord_dir"))
        lay.addWidget(_pair(self.a_card, self.a_coord))
        lay.addWidget(QLabel(T("③ 勾選要加上飾品的服裝槽（可複選，會依序套用到同一張卡）")))
        self.a_table = OutfitTable(with_target=False)
        lay.addWidget(self.a_table, 1)
        self.a_card.loaded.connect(lambda p, i: self.a_table.fill(i))
        self.a_out = PathPicker(T("輸出到"), save=True, reveal=True)
        lay.addWidget(_row(QLabel(T("輸出檔")), self.a_out))
        lay.addWidget(_buttons(self.a_table, self.run_append))
        return w

    # ---- 移植換裝 ----
    def _transplant_tab(self):
        w = QWidget()
        lay = QVBoxLayout(w)
        self.t_src = CardPanel(T("① 來源角色卡（衣服從這裡拿）"), "character",
                               lambda: self.settings.get("chara_dir"))
        self.t_coord = CardPanel(T("② 附加服裝卡（可留空）"), "coordinate",
                                 lambda: self.settings.get("coord_dir"))
        self.t_dst = CardPanel(T("③ 目標角色卡（人是這個）"), "character",
                               lambda: self.settings.get("chara_dir"))
        trip = QWidget()
        tl = QHBoxLayout(trip)
        tl.setContentsMargins(0, 0, 0, 0)
        for x in (self.t_src, self.t_coord, self.t_dst):
            tl.addWidget(x, 1)
        lay.addWidget(trip)
        clr = QPushButton(T("清掉②的服裝卡"))
        clr.clicked.connect(self.clear_t_coord)
        lay.addWidget(_row(QLabel(
            T("②留空 = 直接把①移植到③；放了服裝卡 = ①先附加它的飾品，再移植到③")), clr))
        lay.addWidget(QLabel(
            T("④ 勾選要搬的服裝槽，右邊那欄改成要放到目標卡的第幾套（預設同號）")))
        self.t_table = OutfitTable(with_target=True)
        lay.addWidget(self.t_table, 1)
        self.t_src.loaded.connect(lambda p, i: self.t_table.fill(i))
        self.t_dst.loaded.connect(
            lambda p, i: self.t_table.set_target_limit(i["outfits"]))
        self.t_out = PathPicker(T("輸出到"), save=True, reveal=True)
        lay.addWidget(_row(QLabel(T("輸出檔")), self.t_out))
        lay.addWidget(_buttons(self.t_table, self.run_transplant))
        return w

    def clear_t_coord(self):
        self.t_coord.path = ""
        self.t_coord.info = {}
        self.t_coord.name.setText(T("（尚未讀卡）"))
        self.t_coord.detail.setText("")
        self.t_coord.thumb.clear()
        self.t_coord.thumb.setText(T("把卡片\n拖到這裡"))
        self.t_coord.thumb.setStyleSheet(
            "border:2px dashed #999; border-radius:6px; color:#777;")
        self.append_log(T("已清掉附加服裝卡，執行時會直接移植"))

    # ---- 修卡 ----
    def _clean_tab(self):
        w = QWidget()
        lay = QVBoxLayout(w)
        self.c_card = CardPanel(T("要清理的角色卡"), "character",
                                lambda: self.settings.get("chara_dir"))
        lay.addWidget(self.c_card)
        lay.addWidget(QLabel(
            T("清掉指向空飾品欄位的殘留擴充資料。飾品拿掉後 MaterialEditor 的材質資料\n會留在卡上，載入時取到 null 物件會讓整個套用流程中止，那一套換裝的貼圖\n就全部不會套用——切到別套再切回來才正常的，就是這個毛病。")))
        self.c_out = PathPicker(T("輸出到"), save=True, reveal=True)
        lay.addWidget(_row(QLabel(T("輸出檔")), self.c_out))
        b = QPushButton(T("執行清理"))
        b.setMinimumHeight(34)
        b.clicked.connect(self.run_clean)
        lay.addWidget(b)
        lay.addStretch(1)
        return w

    def _watch_tab(self):
        w = QWidget()
        lay = QVBoxLayout(w)
        self.w_dir = PathPicker(T("選監看資料夾"), is_dir=True, reveal=True)
        self.w_dir.set(self.settings["watch_dir"])
        lay.addWidget(_row(QLabel(T("監看資料夾")), self.w_dir))
        self.btn_watch = QPushButton(T("開始監看"))
        self.btn_watch.setCheckable(True)
        self.btn_watch.setMinimumHeight(34)
        self.btn_watch.toggled.connect(self.toggle_watch)
        self.lbl_state = QLabel(T("未監看"))
        lay.addWidget(_row(self.btn_watch, self.lbl_state))
        self.table = QTableWidget(0, 4)
        self.table.setHorizontalHeaderLabels([T("時間"), T("工單"), T("動作"), T("狀態")])
        self.table.horizontalHeader().setSectionResizeMode(
            3, QHeaderView.ResizeMode.Stretch)
        lay.addWidget(self.table, 1)
        lay.addWidget(QLabel(
            T("插件把工單寫成 *{0} 丟進這個資料夾，處理完會在旁邊出現同名的 *{1}").format(JOB_SUFFIX, DONE_SUFFIX)))
        return w

    def _settings_tab(self):
        w = QWidget()
        g = QGridLayout(w)
        self.s_root = PathPicker(T("選遊戲根目錄"), is_dir=True, reveal=True)
        self.s_root.set(self.settings.get("game_root", ""))
        self.s_root.edit.textChanged.connect(self.on_root_changed)
        self.s_chara = PathPicker(T("選角色卡資料夾"), is_dir=True, reveal=True)
        self.s_chara.set(self.settings.get("chara_dir", ""))
        self.s_coord = PathPicker(T("選服裝卡資料夾"), is_dir=True, reveal=True)
        self.s_coord.set(self.settings.get("coord_dir", ""))
        self.s_out = PathPicker(T("選輸出資料夾"), is_dir=True, reveal=True)
        self.s_out.set(self.settings["output_dir"])
        self.s_pushup = QComboBox()
        self.s_pushup.addItems([T("跟著服裝走"), T("保留目標人物的")])
        self.s_pushup.setCurrentIndex(
            0 if self.settings["pushup"] == "follow_outfit" else 1)
        self.s_skin = QComboBox()
        self.s_skin.addItems([T("保留目標人物的"), T("跟著服裝走")])
        self.s_skin.setCurrentIndex(
            0 if self.settings["skin_overlay"] == "keep_target" else 1)
        self.s_clean = QCheckBox(T("存檔前自動清除指向空飾品欄位的殘渣（建議開著）"))
        self.s_clean.setChecked(bool(self.settings["clean"]))
        self.s_auto = QCheckBox(T("開啟程式時自動開始監看"))
        self.s_auto.setChecked(bool(self.settings["autostart"]))
        g.addWidget(QLabel(T("遊戲根目錄")), 0, 0)
        g.addWidget(self.s_root, 0, 1)
        g.addWidget(QLabel(T("　讀角色卡預設開這裡")), 1, 0)
        g.addWidget(self.s_chara, 1, 1)
        g.addWidget(QLabel(T("　讀服裝卡預設開這裡")), 2, 0)
        g.addWidget(self.s_coord, 2, 1)
        g.addWidget(QLabel(T("預設輸出資料夾")), 3, 0)
        g.addWidget(self.s_out, 3, 1)
        g.addWidget(QLabel(T("胸托參數（Pushup）")), 4, 0)
        g.addWidget(self.s_pushup, 4, 1)
        g.addWidget(QLabel(T("皮膚／眼睛 overlay（KSOX）")), 5, 0)
        g.addWidget(self.s_skin, 5, 1)
        g.addWidget(self.s_clean, 6, 0, 1, 2)
        g.addWidget(self.s_auto, 7, 0, 1, 2)
        # 一整句當一條，不要拆成四塊 —— 英日文的語序跟中文不一樣，
        # 拆開翻的話翻譯的人沒辦法把路徑搬到該去的位置
        hint = QLabel(T("填了根目錄就自動帶成：讀角色卡 {0}、讀服裝卡 {1}、"
                        "輸出與工單 {2}。想改直接改。")
                      .format(CHARA_SUB, COORD_SUB, OUTPUT_SUB))
        hint.setWordWrap(True)
        hint.setStyleSheet("color:#666;")
        g.addWidget(hint, 8, 0, 1, 2)
        # 語言放最下面，跟三個插件的設定視窗同一個位置，換過去不用重找
        self.s_lang = QComboBox()
        self.s_lang.addItems(list(NAMES))
        self.s_lang.setCurrentIndex(int(self.settings.get("lang", 0) or 0))
        # currentIndexChanged 在 addItems 的時候也會發，所以連線放在設好值之後
        self.s_lang.currentIndexChanged.connect(self.change_lang)
        g.addWidget(QLabel(T("語言 / Language")), 9, 0)
        g.addWidget(self.s_lang, 9, 1)

        b = QPushButton(T("儲存設定"))
        b.clicked.connect(self.save_ui_settings)
        g.addWidget(b, 10, 0)
        g.setRowStretch(11, 1)
        return w

    # ---- 行為 ----
    def append_log(self, text: str):
        for line in str(text).splitlines():
            self.log.appendPlainText(f"{datetime.now():%H:%M:%S}  {line}")

    def on_root_changed(self, text: str):
        """改了根目錄，就把還沒被手動改過的子路徑帶成預設值。"""
        root = text.strip()
        if not root or not Path(root).is_dir():
            return
        for picker, sub in ((self.s_chara, CHARA_SUB), (self.s_coord, COORD_SUB),
                            (self.s_out, OUTPUT_SUB), (self.w_dir, OUTPUT_SUB)):
            cur = picker.text().replace("\\", "/").rstrip("/")
            if not cur or cur.endswith(sub):
                picker.set(str(Path(root) / sub))

    def save_ui_settings(self):
        self.settings.update({
            "game_root": self.s_root.text(),
            "chara_dir": self.s_chara.text(),
            "coord_dir": self.s_coord.text(),
            "watch_dir": self.w_dir.text(),
            "output_dir": self.s_out.text(),
            "pushup": "follow_outfit" if self.s_pushup.currentIndex() == 0 else "keep_target",
            "skin_overlay": "keep_target" if self.s_skin.currentIndex() == 0 else "follow_outfit",
            "clean": self.s_clean.isChecked(),
            "autostart": self.s_auto.isChecked(),
            # 語言在選單一動就已經存過了，這裡再帶一次是為了不要被 update 蓋掉
            "lang": self.s_lang.currentIndex(),
        })
        save_settings(self.settings)
        self.append_log(T("設定已儲存到 {0}").format(SETTINGS_PATH))

    def _default_out(self, base: str, tag: str) -> str:
        odir = Path(self.settings.get("output_dir") or app_dir())
        odir.mkdir(parents=True, exist_ok=True)
        return str(odir / f"{Path(base).stem}_{tag}_{datetime.now():%H%M%S}.png")

    def run_append(self):
        if not self.a_card.path or not self.a_coord.path:
            self.append_log(T("角色卡和服裝卡都要先讀進來"))
            return
        sel = self.a_table.selection()
        if not sel:
            self.append_log(T("至少勾一個服裝槽"))
            return
        steps = [{"op": "append", "chara": self.a_card.path,
                  "coord": self.a_coord.path, "outfit": s} for s, _ in sel]
        out = self.a_out.text() or self._default_out(self.a_card.path, "append")
        self.append_log(T("附加飾品到第 {0} 套")
                        .format("、".join(str(s) for s, _ in sel)))
        self.worker.submit(T("附加飾品"), {"batch": steps, "out": out}, None)

    def run_transplant(self):
        if not self.t_src.path or not self.t_dst.path:
            self.append_log(T("來源卡和目標卡都要先讀進來"))
            return
        sel = self.t_table.selection()
        if not sel:
            self.append_log(T("至少勾一個服裝槽"))
            return
        dsts = [d for _, d in sel]
        if len(set(dsts)) != len(dsts):
            self.append_log(T("輸出的服裝槽有重複，後面那筆會蓋掉前面的——先改掉再執行"))
            return
        job = {"out": self.t_out.text()
               or self._default_out(self.t_dst.path, "transplant")}
        if self.t_coord.path:
            job["pre"] = [{"op": "append", "chara": self.t_src.path,
                           "coord": self.t_coord.path, "outfit": s} for s, _ in sel]
            src_ref = "<PRE>"
            self.append_log(T("先把服裝卡飾品附加到來源第 {0} 套，再移植")
                            .format("、".join(str(s) for s, _ in sel)))
        else:
            src_ref = self.t_src.path
        job["batch"] = [{"op": "transplant", "src": src_ref, "src_outfit": s,
                         "dst": self.t_dst.path, "dst_outfit": d} for s, d in sel]
        self.append_log(T("移植 {0}")
                        .format("、".join("%s→%s" % (s, d) for s, d in sel)))
        self.worker.submit(T("移植換裝"), job, None)

    def run_clean(self):
        if not self.c_card.path:
            self.append_log(T("先讀一張角色卡"))
            return
        out = self.c_out.text() or self._default_out(self.c_card.path, "clean")
        self.worker.submit(
            T("修卡"), {"op": "clean", "chara": self.c_card.path, "out": out}, None)

    def toggle_watch(self, on: bool):
        if on:
            d = Path(self.w_dir.text())
            if not d.is_dir():
                self.append_log(T("監看資料夾不存在，先選一個"))
                self.btn_watch.setChecked(False)
                return
            self.watcher.dir = d
            self.watcher.seen = {
                str(p) for p in d.glob("*" + JOB_SUFFIX)
                if p.with_name(p.name[:-len(JOB_SUFFIX)] + DONE_SUFFIX).exists()}
            self.watcher.running = True
            self.btn_watch.setText(T("停止監看"))
            self.lbl_state.setText(T("監看中：{0}").format(d))
            self.append_log(T("開始監看 {0}").format(d))
            self.settings["watch_dir"] = str(d)
            save_settings(self.settings)
        else:
            self.watcher.running = False
            self.btn_watch.setText(T("開始監看"))
            self.lbl_state.setText(T("未監看"))
            self.append_log(T("停止監看"))

    def on_job_found(self, name, job, done_path):
        self.append_log(T("收到工單 {0}").format(name))
        self.worker.submit(name, job, done_path)

    def on_started(self, name, op):
        r = self.table.rowCount()
        self.table.insertRow(r)
        for c, v in enumerate([f"{datetime.now():%H:%M:%S}", name, op, T("處理中…")]):
            self.table.setItem(r, c, QTableWidgetItem(v))
        self.table.scrollToBottom()
        self.status.showMessage(T("處理中：{0}").format(name))

    def on_finished(self, name, ok, msg):
        for r in range(self.table.rowCount() - 1, -1, -1):
            if self.table.item(r, 1) and self.table.item(r, 1).text() == name:
                self.table.setItem(r, 3, QTableWidgetItem(("✔ " if ok else "✘ ") + msg))
                break
        self.append_log((T("完成：{0} — {1}") if ok else T("失敗：{0} — {1}"))
                        .format(name, msg))
        self.status.showMessage(T("就緒"))

    def closeEvent(self, e):
        self.watcher.running = False
        self.worker.stop()
        self.wthread.quit()
        self.wthread.wait(3000)
        self.vthread.terminate()
        self.vthread.wait(1000)
        e.accept()


def main():
    app = QApplication(sys.argv)
    win = MainWindow()
    win.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
