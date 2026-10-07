#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""kkscenebridge — 恋活場景卡合併工具

把多張場景卡按順序接成一張：加卡、排順序、選相機、按執行。
核心邏輯在 kkscenemerge.py，這裡只是介面。
"""
from __future__ import annotations
from kksblang import T
import kksblang as L
import kkffmpeg
kkffmpeg.install()     # ffmpeg / ffprobe 先找 exe 旁邊的 ffmpeg 資料夾，並且不閃主控台視窗

import datetime
import json
import os
import queue
import sys
import traceback
from pathlib import Path

from PyQt6.QtCore import QObject, QProcess, Qt, QThread, QUrl, pyqtSignal
from PyQt6.QtGui import QDesktopServices, QFont, QPixmap
from PyQt6.QtWidgets import (
    QAbstractItemView, QApplication, QCheckBox, QComboBox, QDialog, QDialogButtonBox, QDoubleSpinBox,
    QFileDialog, QGroupBox, QHBoxLayout, QHeaderView, QLabel, QLineEdit,
    QMainWindow, QMessageBox, QPlainTextEdit, QPushButton, QSplitter,
    QTableWidget, QTableWidgetItem, QTabWidget, QVBoxLayout, QWidget,
)

import kkaudiotab
import kkbridgetab
import kkcutmerge as CM
import kkcuttab
import kktreetab
import kkscenemerge as KM
import kkvnsound as V
import kklang

# 「人物卡合卡」分頁（原本的 kkbridge）用自己那張翻譯表，語言跟著這邊走
kklang.set_lang(L.Current)

APP_NAME = "kkscenebridge"
VERSION = "1.1.6"
RED = "#c0392b"


def app_dir() -> Path:
    if getattr(sys, "frozen", False):
        return Path(sys.executable).parent
    return Path(__file__).resolve().parent


SETTINGS_PATH = app_dir() / "kkscenebridge_settings.json"
DEFAULTS = {
    # 這一份是從實際在用的設定檔倒回來的（dist\\kkscenebridge_settings.json，
    # 2026-09-23），不是「出廠值」。設定檔存在時它會蓋掉這裡 ——
    # 這裡只在設定檔不見／被刪／換一台機器第一次跑的時候生效。
    "game_root": "D:/Koikatu",
    "scene_dir": "D:\\Koikatu\\UserData\\Studio\\scene",
    "output_dir": "D:\\Koikatu\\UserData\\Studio\\scene",
    "gap": 0.01,
    "camera_switch": True,
    "group": True,
    "nc_rename": True,
    "subfolders": True,
    "zero_chain": True,
    "auto_camera": True,
    "enable_tracks": True,
    # Timeline 兩個清法現在都是關的 —— 合併不會動任何一條既有軌道。
    #   tl_clean     按 id 整批砍，會連 charClothes 那種「演到一半脫衣服」
    #                的好軌道一起丟，所以關。
    #   tl_mismatch  只砍 objectIndex 指錯的那幾條。也關 ——
    #                代價是遇到真的指錯的軌道時，Timeline 的 Recurse 會從
    #                第 0 秒整棵中斷（整張卡的 timeline 看起來完全沒作用）。
    #                真的碰到再回設定頁把它打開。
    "tl_clean": False,
    "tl_mismatch": False,
    "shader_type": "",      # "" = 保留底卡的
    "park": True,
    "park_lead": 0.0,       # 秒；讓揺れ物在入鏡前先晃完
    "nc_enable_tracks": True,    # 作者自己的 NC 也寫啟用軌道（盲狙收割）
    "clear_frame": True,         # 清掉底卡的外框（StudioImageEmbed FrameData）
    "enable_all_tracks": True,   # 把原本沒打勾的相機／時間流速軌道打開（其他保留作者設定）
    "cam_tracks": True,          # 每段開頭切換相機縮放 / 相機FOV
    "cam_name": "",
    "audio_root": "D:\\Koikatu\\UserData\\audio",
    "audio_rel_prefix": "UserData\\audio",
    "audio_dir": "",
    "audio_mode": "relpath",
    "cut_vnge": False,
    "cut_force": False,
    "cut_height": 1080,
    "cut_out_dir": "D:\\Koikatu\\UserData\\cutscene",
    "tree_save_treestate": True,
    "tree_backup": True,
    "after_merge": "ask",
    # 合併時，各張卡已經做好的 F7 設定（cutscene.json）一起接起來
    "merge_cut": True,
    # 刻意不收進來的：cut_video / cut_card / cut_files / cut_unchecked /
    # cut_active / tree_card。那幾個是「上次開的是哪張卡」，不是設定 ——
    # 寫死在這裡會讓全新的一份一啟動就去載某張 1.3 GB 的卡。
}
SCENE_SUB = "UserData/Studio/scene"

# 這幾個路徑預設是「遊戲根目錄底下的某個位置」。只要欄位裡放的還是舊根目錄
# 算出來的那個值，就代表使用者沒有自己改過 —— 換根目錄時整批跟著換。
# 一旦欄位被改成別的東西，就當作使用者自己指定，之後不再自動覆蓋。
SUB_SCENE = ("UserData", "Studio", "scene")
SUB_AUDIO = ("UserData", "audio")


def under(root, parts):
    """<root>/<parts…>，root 空的就回空字串（用系統原生的分隔符號）。"""
    root = (root or "").strip()
    return str(Path(root).joinpath(*parts)) if root else ""


def guess_game_root() -> str:
    for d in ("C:/Koikatu", "D:/Koikatu", "E:/Koikatu", "F:/Koikatu",
              "C:/Illusion/Koikatu", "D:/Illusion/Koikatu"):
        if (Path(d) / "UserData").is_dir():
            return d
    return ""


def guess_kks_root() -> str:
    for d in ("C:/KoikatsuSunshine", "D:/KoikatsuSunshine", "E:/KoikatsuSunshine",
              "F:/KoikatsuSunshine", "C:/Illusion/KoikatsuSunshine",
              "D:/Illusion/KoikatsuSunshine"):
        if (Path(d) / "UserData").is_dir():
            return d
    return ""


def load_settings() -> dict:
    s = dict(DEFAULTS)
    try:
        s.update(json.loads(SETTINGS_PATH.read_text("utf-8")))
    except Exception:                                   # noqa: BLE001
        pass
    # Koikatsu Sunshine 根目錄：設定檔裡從來沒有這一項才去猜（第一次）；
    # 使用者自己清空的就維持空的，不要每次開程式又幫他填回來。
    if "game_root_kks" not in s:
        s["game_root_kks"] = guess_kks_root()
    if not s.get("game_root"):
        s["game_root"] = guess_game_root()
    root = s.get("game_root") or ""
    if not s.get("scene_dir"):
        s["scene_dir"] = under(root, SUB_SCENE)
    if not s.get("output_dir"):
        s["output_dir"] = s.get("scene_dir", "")
    if not s.get("audio_root"):
        s["audio_root"] = under(root, SUB_AUDIO)
    return s


def save_settings(s: dict) -> None:
    try:
        SETTINGS_PATH.write_text(json.dumps(s, ensure_ascii=False, indent=2), "utf-8")
    except Exception:                                   # noqa: BLE001
        pass


def card_thumbnail(path):
    """只取卡片前面那段 PNG —— 別為了縮圖把 1 GB 丟給 Qt。"""
    try:
        with open(path, "rb") as f:
            data = f.read(4_000_000)
        n = KM.png_len(data, 0)
        pm = QPixmap()
        return pm if pm.loadFromData(data[:n], "PNG") else None
    except Exception:                                   # noqa: BLE001
        return None


def fmt_dur(sec):
    if sec is None:
        return "—"
    m, s = divmod(float(sec), 60)
    return f"{int(m):02d}:{s:05.2f}"


def parse_dur(txt):
    """吃 '15'、'15.5'、'00:15.00'、'1:02.5'，回傳秒數；看不懂就 None。"""
    t = (txt or "").strip().replace("：", ":")
    if not t:
        return None
    try:
        if ":" in t:
            m, sec = t.rsplit(":", 1)
            return int(float(m)) * 60 + float(sec)
        return float(t)
    except ValueError:
        return None


def common_prefix(names):
    """一串包裝資料夾名稱的共同開頭，例如 Scenecard_(1..4) -> Scenecard"""
    names = [n for n in names if n]
    if not names:
        return ""
    pre = names[0]
    for n in names[1:]:
        i = 0
        while i < min(len(pre), len(n)) and pre[i] == n[i]:
            i += 1
        pre = pre[:i]
    return pre.rstrip(" _-|(（[")


def auto_out_name(names):
    """<共同名稱>_merge_<現在時間>.png，時間格式跟戀活存場景卡一樣。

    只有一張卡時沒有「合併」這回事，只是整理成同一種形狀，所以改用
    _prep_ —— 之後在資料夾裡一眼就分得出哪些是接出來的、哪些是整理出來的。
    """
    from datetime import datetime
    t = datetime.now()
    stamp = f"{t:%Y_%m%d_%H%M_%S}_{t.microsecond // 1000:03d}"
    base = common_prefix(names) or ("合併場景" if L.Current == L.ZH else "merged_scene")
    return f"{base}_{'prep' if len(names) == 1 else 'merge'}_{stamp}.png"


def fmt_size(n):
    for u in ("B", "KB", "MB", "GB"):
        if n < 1024 or u == "GB":
            return f"{n:,.0f} {u}" if u == "B" else f"{n:.1f} {u}"
        n /= 1024.0
    return str(n)


# ============================================================ 背景執行緒
class InfoLoader(QObject):
    """一張一張把卡片資訊讀出來（大卡很慢，所以丟到背景）。"""

    done = pyqtSignal(str, dict)
    failed = pyqtSignal(str, str)
    log_line = pyqtSignal(str)

    def __init__(self):
        super().__init__()
        self.q: queue.Queue = queue.Queue()
        self.running = True

    def submit(self, path):
        self.q.put(path)

    def stop(self):
        self.running = False
        self.q.put(None)

    def loop(self):
        while self.running:
            path = self.q.get()
            if path is None:
                break
            try:
                self.log_line.emit(T("讀卡：{0}").format(Path(path).name))
                info = KM.scene_info(path)
                self.done.emit(path, info)
            except Exception as e:                      # noqa: BLE001
                self.failed.emit(path, f"{type(e).__name__}: {e}")
                self.log_line.emit(traceback.format_exc())


class MergeWorker(QObject):
    """跑實際的整理 + 合併。"""

    log_line = pyqtSignal(str)
    started_ = pyqtSignal()
    finished_ = pyqtSignal(bool, str)

    def __init__(self):
        super().__init__()
        self.q: queue.Queue = queue.Queue()
        self.running = True

    def submit(self, jobs, out_path, opts):
        self.q.put((jobs, out_path, opts))

    def stop(self):
        self.running = False
        self.q.put(None)

    def loop(self):
        while self.running:
            item = self.q.get()
            if item is None:
                break
            jobs, out_path, opts = item
            self.started_.emit()
            old_log = KM.log
            KM.log = lambda *a: self.log_line.emit(" ".join(str(x) for x in a))
            try:
                sources = []
                for j in jobs:
                    sources.append(self._producer(j, opts))
                # 有選音頻就寫進去，沒選就是這次不配音 —— 不用額外的開關
                post = None
                if opts.get("audio_root") and opts.get("audio_rows"):
                    def post(scene, _segs, o=opts):
                        self._apply_rows(
                            scene, o["audio_rows"], o["audio_root"],
                            o.get("audio_rel_prefix") or V.DEFAULT_REL_PREFIX,
                            o.get("audio_mode") or V.MODE_REL)
                res = KM.merge_many(
                    sources, out_path,
                    gap=opts["gap"], park=opts.get("park", True),
                    park_lead=float(opts.get("park_lead", 0.0) or 0.0),
                    nc_enable_tracks=opts.get("nc_enable_tracks", False),
                    clear_frame=opts.get("clear_frame", True),
                    enable_all_tracks=opts.get("enable_all_tracks", True),
                    fov_track=opts.get("cam_tracks", True),
                    group=opts["group"],
                    camera_switch=opts["camera_switch"],
                    cam_name=opts["cam_name"] or None,
                    nested_cams=True,
                    enable_tracks=opts.get("enable_tracks", True),
                    tl_clean=opts.get("tl_clean", False),
                    tl_mismatch=opts.get("tl_mismatch", True),
                    shader_type=(int(opts["shader_type"])
                                 if str(opts.get("shader_type", "")).strip()
                                 else None),
                    save_version=(opts.get("save_version") or None),
                    post=post)
                note = ""
                if opts.get("cut"):
                    note = self._merge_cut(jobs, out_path, res, opts["cut"])
                self.finished_.emit(
                    True, T("完成：{0}（{1}，總時長 {2}）").format(out_path, fmt_size(res['size']), fmt_dur(res['duration'])) + note)
            except SystemExit as e:                     # noqa: BLE001
                self.finished_.emit(False, str(e))
            except Exception as e:                      # noqa: BLE001
                self.log_line.emit(traceback.format_exc())
                self.finished_.emit(False, f"{type(e).__name__}: {e}")
            finally:
                KM.log = old_log

    @staticmethod
    def _merge_cut(jobs, out_path, res, cut):
        """卡片接好之後，把各張卡的 cutscene.json 照同樣的順序接成一份。

        這一步失敗不算合併失敗 —— 卡片已經寫出來了，設定檔可以之後再處理。
        回傳要接在完成訊息後面的一句話。
        """
        try:
            parts = CM.make_parts([{"card": j["path"], "json": c.get("json") or ""}
                                   for j, c in zip(jobs, cut["cards"])])
            offs = [c["off"] for c in (res.get("cards") or [])]
            if len(offs) != len(parts):
                raise CM.CutMergeError("合併回報的卡片數量跟清單對不上")
            for p, j, o in zip(parts, jobs, offs):
                p["off"] = o
                p["dur"] = j.get("duration")        # 只有手動改過時長才有值（縮短時要把超出的音軌拿掉）
            stem = Path(out_path).stem
            out_dir = cut.get("out_dir") or str(Path(out_path).parent)
            out_json = os.path.join(out_dir, stem + ".cutscene.json")
            KM.log("")
            KM.log(T("接 F7 設定（cutscene.json）…"))
            r = CM.merge(parts, out_path, out_json, rows=cut.get("rows"), log=KM.log)
            for w in r["warnings"]:
                KM.log("[注意] " + w)
            if not r["json"]:
                return ""
            # 寫完馬上體檢，跟「產生 cutscene.json」同一套
            try:
                import contextlib
                import io
                import kkcutscene as K
                buf = io.StringIO()
                with contextlib.redirect_stdout(buf):
                    K.run_check(r["json"])
                for ln in buf.getvalue().splitlines():
                    if ln.strip():
                        KM.log(ln)
            except Exception:                           # noqa: BLE001
                pass
            return T("；F7 設定已接好：{0}").format(r["json"])
        except Exception as e:                          # noqa: BLE001
            KM.log(traceback.format_exc())
            KM.log(T("[提醒] F7 設定（cutscene.json）沒有接成：{0}").format(e))
            return T("；F7 設定沒有接成（看紀錄）")

    @staticmethod
    def _apply_rows(scene, rows, audio_root, rel_prefix, mode=None):
        """用合併分頁每一列選好的音檔寫進去 —— 照包裝資料夾名稱對回各段。"""
        slots = V.scene_slots(scene)
        by_name = {}
        for r in rows:
            by_name.setdefault(r["name"], r)
        plan, miss = [], []
        for i, sl in enumerate(slots):
            r = by_name.get(sl["name"])
            if r is None and len(slots) == len(rows):
                r = rows[i]                      # 名稱對不上就照順序來
            if not r or not r.get("files"):
                miss.append(sl["name"])
                continue
            plan.append({"slot": sl, "group": sl["name"],
                         "files": r["files"], "primary": r.get("primary")})
        if not plan:
            KM.log(T("  [音頻] 沒有任何一段配到音檔，跳過"))
            return
        warn = []
        res = V.apply_audio(scene, plan, audio_root=audio_root,
                            rel_prefix=rel_prefix, warn=warn,
                            mode=mode or V.MODE_REL)
        for item in plan:
            sl = item["slot"]
            pri = item["primary"] or V.pick_primary(item["files"])
            KM.log(T("  [音頻] [{0}] {1} → 群組「{2}」 trigger={3}：").format(sl['index'], sl['name'], item['group'], sl['sfx']) + ", ".join(
                       ("★" if f == pri else "") + Path(f).name
                       for f in item["files"]))
        for m in miss:
            KM.log(T("  [音頻] {0} 沒有配音檔").format(m))
        for w in warn:
            KM.log(T("  [音頻][注意] ") + w)
        KM.log(T("  [音頻] 共 {0} 個音頻、{1} 個群組（★＝100%，{2}）").format(res['sounds'], res['groups'], T(V.MODE_LABEL.get(res['mode'], res['mode']))))

    @staticmethod
    def _producer(job, opts):
        """每張卡在要用到的時候才讀進來、整理好，用完就丟 —— 省記憶體。"""
        def make():
            if job.get("prepped"):
                sc = KM.S.Scene(job["path"])
                ch = KM.set_scene_duration(sc, job.get("duration"))
                if ch:
                    KM.log(T("  時長 {0:g} -> {1:g} 秒").format(ch[0], ch[1]))
            else:
                sc = KM.prep_scene(
                    job["path"], job["name"], job.get("camera"),
                    subfolders=opts["subfolders"],
                    nc_rename=opts["nc_rename"],
                    zero_chain=opts["zero_chain"],
                    auto_camera=opts.get("auto_camera", True),
                    duration=job.get("duration"),
                    index=job.get("index"))
            sc.label = job["name"]
            return sc
        make.label = job["name"]
        return make


# ============================================================ 小元件
class PathPicker(QWidget):
    def __init__(self, caption: str, is_dir=False, save=False, reveal=False,
                 default=None, default_tip=""):
        """default：回傳「預設值」的函式。給了就多一顆「回預設」按鈕，
        而且只有在目前的值跟預設不一樣的時候才會亮。"""
        super().__init__()
        self.caption, self.is_dir, self.save = caption, is_dir, save
        self.default = default
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
            o.clicked.connect(self.reveal)
            lay.addWidget(o)
        self.btn_reset = None
        if default is not None:
            self.btn_reset = QPushButton(T("回預設"))
            self.btn_reset.setFixedWidth(66)
            self.btn_reset.setToolTip(default_tip or T("換回預設位置"))
            self.btn_reset.clicked.connect(self.reset_default)
            lay.addWidget(self.btn_reset)
            self.edit.textChanged.connect(self.refresh_reset)

    def default_value(self):
        try:
            return (self.default() or "") if self.default else ""
        except Exception:                               # noqa: BLE001
            return ""

    def reset_default(self):
        d = self.default_value()
        if d:
            self.set(d)

    def refresh_reset(self):
        if self.btn_reset is None:
            return
        d = self.default_value()
        self.btn_reset.setEnabled(bool(d) and self.text() != d)
        self.btn_reset.setToolTip(
            (T("已經是預設值：\n") + d) if d and self.text() == d
            else (T("換回預設：\n") + d) if d else T("還沒有預設值（先填遊戲根目錄）"))

    def reveal(self):
        d = self.text()
        if not d:
            return
        p = Path(d)
        if not p.is_dir():
            p = p.parent
        if p.is_dir():
            QDesktopServices.openUrl(QUrl.fromLocalFile(str(p)))

    def pick(self):
        if self.is_dir:
            p = QFileDialog.getExistingDirectory(self, self.caption, self.edit.text())
        elif self.save:
            p, _ = QFileDialog.getSaveFileName(self, self.caption, self.edit.text(),
                                               T("場景卡 (*.png)"))
        else:
            p, _ = QFileDialog.getOpenFileName(self, self.caption, self.edit.text(),
                                               T("場景卡 (*.png)"))
        if p:
            self.edit.setText(p)

    def text(self):
        return self.edit.text().strip()

    def set(self, v):
        self.edit.setText(v or "")


(COL_NO, COL_THUMB, COL_FILE, COL_DUR, COL_NODES, COL_CAM, COL_NAME,
 COL_AUDIO, COL_STATE) = range(9)
HEADERS = [T("順序"), T("縮圖"), T("檔案"), T("時長"), T("節點"), T("相機"), T("包裝資料夾名稱"),
           T("音頻"), T("狀態")]


class CardTable(QTableWidget):
    """場景卡清單。可以拖檔案進來，順序就是播放順序。"""

    changed = pyqtSignal()
    warn = pyqtSignal(str)
    mode_changed = pyqtSignal()

    def __init__(self, settings=None):
        super().__init__(0, len(HEADERS))
        self.rows: list[dict] = []
        self.settings = settings if settings is not None else {}
        self.auto_camera = True          # 沒有相機時自動生一台（由設定頁控制）
        self.setHorizontalHeaderLabels(HEADERS)
        self.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.setAcceptDrops(True)
        self.verticalHeader().setVisible(False)
        self.verticalHeader().setDefaultSectionSize(76)
        h = self.horizontalHeader()
        h.setStretchLastSection(False)
        for c in range(len(HEADERS)):
            h.setSectionResizeMode(c, QHeaderView.ResizeMode.Interactive)
        h.setSectionResizeMode(COL_FILE, QHeaderView.ResizeMode.Stretch)
        self.setColumnWidth(COL_NO, 44)
        self.setColumnWidth(COL_THUMB, 70)
        self.setColumnWidth(COL_DUR, 80)
        self.setColumnWidth(COL_NODES, 60)
        self.setColumnWidth(COL_CAM, 260)
        self.setColumnWidth(COL_NAME, 190)
        self.setColumnWidth(COL_AUDIO, 150)
        self.setColumnWidth(COL_STATE, 120)
        self.itemChanged.connect(self._name_edited)

    # -- 拖放 --
    def dragEnterEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()

    def dragMoveEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()

    def dropEvent(self, e):
        paths = [u.toLocalFile() for u in e.mimeData().urls()]
        paths = [p for p in paths if p.lower().endswith(".png")]
        if paths:
            self.add_paths(paths)
            e.acceptProposedAction()

    # -- 資料 --
    def add_paths(self, paths):
        for p in paths:
            self.rows.append({"path": p, "name": Path(p).stem, "camera": None,
                              "info": None, "state": T("等待讀卡"), "prepped": False,
                              "duration": None,
                              "audio_files": [], "audio_primary": None,
                              # 這張卡已經做好的 F7 設定檔（合併時可以一起接）
                              "cut_json": CM.find_json(p, [self.settings.get("cut_out_dir", "")])})
        self.rebuild()
        self.changed.emit()

    def remove_current(self):
        r = self.currentRow()
        if 0 <= r < len(self.rows):
            self.rows.pop(r)
            self.rebuild()
            self.changed.emit()

    def clear_all(self):
        self.rows.clear()
        self.rebuild()
        self.changed.emit()

    def move(self, delta):
        r = self.currentRow()
        n = r + delta
        if 0 <= r < len(self.rows) and 0 <= n < len(self.rows):
            self.rows[r], self.rows[n] = self.rows[n], self.rows[r]
            self.rebuild()
            self.setCurrentCell(n, COL_FILE)
            self.changed.emit()

    def set_info(self, path, info):
        for row in self.rows:
            if row["path"] == path and row["info"] is None:
                row["info"] = info
                row["prepped"] = bool(info.get("prepped"))
                if info.get("prepped") and info.get("wrapper"):
                    row["name"] = info["wrapper"]
                cams = info.get("cameras") or []
                act = [c for c in cams if c["active"]]
                row["camera"] = (act or cams)[0]["dicKey"] if cams else None
                if info.get("camera_path"):
                    row["camera"] = KM.PATH_CAM
                    row["state"] = T("timeline 相機路徑")
                elif not cams:
                    row["state"] = T("沒有相機")
                elif info.get("prepped"):
                    row["state"] = T("已整理過")
                elif len(cams) > 1:
                    row["state"] = T("{0} 台相機，請選").format(len(cams))
                else:
                    row["state"] = T("就緒")
                break
        self.rebuild()
        self.changed.emit()

    def set_failed(self, path, msg):
        for row in self.rows:
            if row["path"] == path:
                row["state"] = T("讀取失敗")
                row["error"] = msg
        self.rebuild()

    def _name_edited(self, item):
        r = item.row()
        if not (0 <= r < len(self.rows)):
            return
        row = self.rows[r]
        if item.column() == COL_NAME:
            row["name"] = item.text().strip() or Path(row["path"]).stem
            return
        if item.column() != COL_DUR:
            return
        base = (row.get("info") or {}).get("duration")
        if base is None:
            return
        v = parse_dur(item.text())
        if v is None or v <= 0 or abs(v - base) <= 1e-6:
            row["duration"] = None            # 看不懂、或跟原本一樣 -> 當作沒改
        else:
            row["duration"] = v
            if v < base:
                self.warn.emit(
                    T("第 {0} 張縮短成 {1}（原本 {2}）—— 超出新結尾的關鍵影格會被拿掉，動畫等於剪短").format(r + 1, fmt_dur(v), fmt_dur(base)))
        self.rebuild()
        self.changed.emit()

    # -- 畫面 --
    def rebuild(self):
        self.blockSignals(True)
        self.setRowCount(len(self.rows))
        for i, row in enumerate(self.rows):
            info = row.get("info") or {}

            it = QTableWidgetItem(str(i + 1))
            it.setFlags(Qt.ItemFlag.ItemIsEnabled)
            it.setTextAlignment(Qt.AlignmentFlag.AlignCenter)
            self.setItem(i, COL_NO, it)

            lab = QLabel()
            lab.setAlignment(Qt.AlignmentFlag.AlignCenter)
            pm = row.get("_pm")
            if pm is None:
                pm = card_thumbnail(row["path"])
                row["_pm"] = pm or False
            if pm:
                lab.setPixmap(pm.scaled(60, 68, Qt.AspectRatioMode.KeepAspectRatio,
                                        Qt.TransformationMode.SmoothTransformation))
            self.setCellWidget(i, COL_THUMB, lab)

            f = QTableWidgetItem(Path(row["path"]).name)
            f.setToolTip(row["path"] + "\n" + fmt_size(info.get("size", 0)))
            f.setFlags(Qt.ItemFlag.ItemIsEnabled | Qt.ItemFlag.ItemIsSelectable)
            self.setItem(i, COL_FILE, f)

            # ---- 時長 ----
            #
            # 三種狀態，優先序由重到輕：
            #   紅字  目前的時長蓋不住卡片裡真正的內容 —— 合併會砍掉後面的動畫，
            #         而且後面每一段都會提早。這個最嚴重，壓過另外兩種顏色。
            #   藍字  手動加長
            #   橘字  手動縮短
            # 提示全部塞在 tooltip 裡：欄位只有 80 px，寫不下，
            # 但滑鼠移上去就該看到完整的來龍去脈和「該填多少」。
            base = info.get("duration")
            eff = row.get("duration") or base
            end = info.get("content_end")
            d = QTableWidgetItem(fmt_dur(eff))
            d.setTextAlignment(Qt.AlignmentFlag.AlignCenter)

            tips = []
            if base is None:
                d.setFlags(Qt.ItemFlag.ItemIsEnabled)
            else:
                tips.append(
                    T("加長＝最後一幀多停一會；縮短＝超出的關鍵影格會被拿掉。\n原本 {0}，輸入 15 或 00:15.00 都可以。").format(fmt_dur(base)))

            # eff 是「目前生效的時長」，所以手動填對之後紅字就會自己消失
            too_short = (base is not None and end is not None
                         and eff is not None and end > eff + 0.05)
            if row.get("duration") and base is not None:
                diff = eff - base
                tips.insert(0, T("原本 {0}，").format(fmt_dur(base))
                            + (T("多停 {0:.2f} 秒（後面的場景一起延後）").format(diff) if diff > 0
                               else T("剪短 {0:.2f} 秒（超出的關鍵影格會被拿掉，後面的場景一起提前）").format(-diff)))
                if not too_short:
                    d.setForeground(Qt.GlobalColor.blue if diff > 0
                                    else Qt.GlobalColor.darkYellow)
            if too_short:
                d.setForeground(Qt.GlobalColor.red)
                bf = d.font()
                bf.setBold(True)
                d.setFont(bf)
                tips.insert(0,
                    T("⚠ 時長不夠：動畫其實到 {0}，但時長只算到 {1}（短 {2:.2f} 秒）。\n超出的關鍵影格會被刪掉，後面每一段也會提早。\n解法：把這一格改成 {3}。").format(fmt_dur(end), fmt_dur(eff), end - eff, fmt_dur(end)))
            if tips:
                d.setToolTip("\n\n".join(tips))
            self.setItem(i, COL_DUR, d)
            x = QTableWidgetItem(str(info.get("nodes", "—")))
            x.setFlags(Qt.ItemFlag.ItemIsEnabled)
            x.setTextAlignment(Qt.AlignmentFlag.AlignCenter)
            self.setItem(i, COL_NODES, x)

            cams = info.get("cameras") or []
            if cams or info.get("camera_path"):
                cb = QComboBox()
                keys = []
                if info.get("camera_path"):
                    cb.addItem(T("timeline 相機路徑（生一台相機接手）"), KM.PATH_CAM)
                    keys.append(KM.PATH_CAM)
                    cb.setToolTip(T("這張卡用 timeline 的相機軌道運鏡。選這個會生一台相機接手整段路徑；\n選別台相機的話，那兩條軌道會被砍掉。"))
                for c in cams:
                    cb.addItem(f"{c['path']}  (dicKey {c['dicKey']})", c["dicKey"])
                    keys.append(c["dicKey"])
                idx = keys.index(row["camera"]) if row["camera"] in keys else 0
                cb.setCurrentIndex(idx)
                cb.setEnabled(not row["prepped"])
                cb.currentIndexChanged.connect(
                    lambda _, rr=row, c=cb: rr.update(camera=c.currentData()))
                self.setCellWidget(i, COL_CAM, cb)
            elif info.get("auto_camera"):
                v = (info.get("view") or {}).get("pos") or {}
                lab2 = QLabel(T("  自動新增（鎖初始視角）") if self.auto_camera else "  —")
                if v:
                    lab2.setToolTip(
                        T("沒有相機，合併時會自動生一台鎖在這張卡存檔時的視角\n位置 {0:.3f}, {1:.3f}, {2:.3f}").format(v.get('x', 0), v.get('y', 0), v.get('z', 0)))
                self.setCellWidget(i, COL_CAM, lab2)
            else:
                self.setCellWidget(i, COL_CAM, QLabel("  —"))

            nm = QTableWidgetItem(row["name"])
            if row["prepped"]:
                nm.setFlags(Qt.ItemFlag.ItemIsEnabled)
            self.setItem(i, COL_NAME, nm)

            files = row.get("audio_files") or []
            ab = QPushButton()
            if files:
                pri = row.get("audio_primary") or kkaudiotab.V.pick_primary(files)
                tag = kkaudiotab.V.audio_tag(Path(pri).stem) if pri else ""
                ab.setText(T("{0} 個 · {1}").format(len(files), tag or '★'))
                ab.setToolTip("\n".join(
                    ("★ " if f == pri else "   ") + Path(f).name for f in files))
                ab.setStyleSheet("color:#2d7d46;")
            else:
                ab.setText(T("選音頻…"))
                ab.setToolTip(T("為這一段場景挑音檔。\n合併時會自動建群組（名稱＝包裝資料夾名稱）、綁該段的 (SFX) 觸發。"))
            ab.clicked.connect(lambda _c, rr=row: self.pick_audio(rr))
            self.setCellWidget(i, COL_AUDIO, ab)

            txt = row.get("state", "")
            if txt == T("沒有相機"):
                txt = T("沒有相機，自動生一台") if self.auto_camera else T("沒有相機")
            st = QTableWidgetItem(txt)
            st.setFlags(Qt.ItemFlag.ItemIsEnabled)
            if row.get("state") == T("讀取失敗") or (
                    row.get("state") == T("沒有相機") and not self.auto_camera):
                st.setForeground(Qt.GlobalColor.red)
            elif row.get("state") in (T("沒有相機"), T("timeline 相機路徑")):
                st.setForeground(Qt.GlobalColor.darkYellow)
            if row.get("error"):
                st.setToolTip(row["error"])
            elif row.get("cut_json"):
                st.setText(txt + T("｜有 F7 設定"))
                st.setToolTip(T("這張卡已經有 F7 的設定檔，合併時可以一起接：\n{0}").format(row["cut_json"]))
            self.setItem(i, COL_STATE, st)
        self.blockSignals(False)

    # -- 音頻 --
    def pick_audio(self, row):
        before = self.settings.get("audio_mode")
        got = kkaudiotab.pick_audio(self, row["name"], row.get("audio_files"),
                                    row.get("audio_primary"), self.settings)
        if self.settings.get("audio_mode") != before:
            self.mode_changed.emit()
        if got is None:
            return
        row["audio_files"], row["audio_primary"] = got
        self.rebuild()
        self.changed.emit()

    def apply_base_name(self, base):
        """包裝資料夾名稱照順序設成 <base>_(1)、<base>_(2)…

        已經整理過的卡（名稱鎖住、灰字的那種）不改，但照樣佔一個編號，
        這樣編號永遠跟「順序」欄對得起來。回傳改了幾列。
        """
        base = (base or "").strip()
        if not base or not self.rows:
            return 0
        n = 0
        for i, row in enumerate(self.rows, 1):
            if row.get("prepped"):
                continue
            row["name"] = f"{base}_({i})"
            n += 1
        self.rebuild()
        self.changed.emit()
        return n

    def auto_audio_all(self):
        """照每一列的包裝資料夾名稱，一次把音檔全配好。"""
        root = self.settings.get("audio_root", "")
        if not root or not os.path.isdir(root):
            self.warn.emit(T("請先在設定頁指定音頻根目錄"))
            return
        folder = (self.settings.get("audio_dir")
                  or kkaudiotab.V.find_audio_folder(
                      root, [r["name"] for r in self.rows]) or "")
        if not folder or not os.path.isdir(folder):
            self.warn.emit(T("音頻根目錄底下找不到對得上的資料夾，請用每列的按鈕自己選"))
            return
        slots = [{"index": i, "name": r["name"]}
                 for i, r in enumerate(self.rows, 1)]
        assign, left = kkaudiotab.V.auto_match(slots, kkaudiotab.V.scan_audio(folder))
        for i, r in enumerate(self.rows, 1):
            r["audio_files"] = assign.get(i, [])
            r["audio_primary"] = None
        self.rebuild()
        self.changed.emit()
        n = sum(len(r["audio_files"]) for r in self.rows)
        self.warn.emit(T("自動配對：{0} → 共 {1} 個音檔").format(folder, n)
                       + (T("，{0} 個配不出去").format(len(left)) if left else ""))

    def audio_rows(self):
        return [{"name": r["name"], "files": r.get("audio_files") or [],
                 "primary": r.get("audio_primary")}
                for r in self.rows if r.get("audio_files")]

    def cut_cards(self):
        """每張卡的 F7 設定檔（沒有的是空字串），照目前的順序。加卡之後才做好的也找得到。"""
        d = [self.settings.get("cut_out_dir", "")]
        for r in self.rows:
            r["cut_json"] = CM.find_json(r["path"], d)
        return [{"card": r["path"], "json": r.get("cut_json") or ""} for r in self.rows]

    def jobs(self):
        return [{"path": r["path"], "name": r["name"], "camera": r["camera"],
                 "prepped": r["prepped"], "duration": r.get("duration"),
                 "index": i}
                for i, r in enumerate(self.rows, 1)]

    def ready(self):
        # 一張也可以跑 —— 不接卡，但照樣整理成合併後的形狀
        #（主資料夾 + (CAM) + (MAP)(FX)(CHAR)(SFX)），
        # 這樣單場景的片子跟合併過的卡在後面的流程裡長得一樣。
        if len(self.rows) < 1:
            return False, T("至少要一張場景卡")
        for i, r in enumerate(self.rows, 1):
            if r.get("info") is None:
                return False, T("第 {0} 張還在讀卡").format(i)
            if r.get("state") == T("讀取失敗"):
                return False, T("第 {0} 張讀不起來").format(i)
            if (not (r["info"].get("cameras") or [])
                    and not r["info"].get("camera_path") and not self.auto_camera):
                return False, T("第 {0} 張沒有相機（可在設定勾「沒有相機時自動新增」）").format(i)
        # studio 版本不一樣（Koikatsu 1.0.x 和 Sunshine 1.1.x 混在一起）不在這裡擋：
        # 主視窗的「選項」會多出一列要使用者選存成哪個版本，沒選不能執行。
        return True, ""


class CutMergeDialog(QDialog):
    """兩張以上的卡各有自己的配音時，決定合併後的每個配音版本在每張卡用哪一個音檔。

    音檔和影片不會動：設定檔記下「播到這張卡時，這個配音版本用哪個檔」，F7 播放時自己換檔。
    每一列可以自己改配對、改名、增減。
    """

    def __init__(self, parent, parts):
        super().__init__(parent)
        self.setWindowTitle(T("接 F7 設定：配音怎麼配"))
        self.resize(820, 420)
        self.parts = parts
        self.aud = CM.audio_parts(parts)
        lay = QVBoxLayout(self)
        tip = QLabel(T("每一列是合併後的一個配音版本（F7 面板上的一顆按鈕），每張卡挑一個版本。\n音檔和影片不會動：播到哪張卡，F7 就換成那張卡挑的音檔。版本比較少的卡可以重複用同一個。"))
        tip.setWordWrap(True)
        lay.addWidget(tip)
        self.tbl = QTableWidget(0, 1 + len(self.aud))
        self.tbl.setHorizontalHeaderLabels(
            [T("合併後的名稱")] + [T("第 {0} 張　{1}").format(i + 1, Path(parts[i]["card"]).stem[:22])
                                 for i in self.aud])
        h = self.tbl.horizontalHeader()
        h.setSectionResizeMode(0, QHeaderView.ResizeMode.ResizeToContents)
        for c in range(1, 1 + len(self.aud)):
            h.setSectionResizeMode(c, QHeaderView.ResizeMode.Stretch)
        lay.addWidget(self.tbl, 1)
        for r in CM.default_rows(parts):
            self._add_row(r)

        br = QHBoxLayout()
        b = QPushButton(T("加一列"))
        b.clicked.connect(lambda: self._add_row(None))
        br.addWidget(b)
        b = QPushButton(T("刪除這列"))
        b.clicked.connect(self._del_row)
        br.addWidget(b)
        br.addStretch(1)
        lay.addLayout(br)

        no = [str(i + 1) for i, p in enumerate(parts) if not p["cfg"]]
        if no:
            lab = QLabel(T("第 {0} 張沒有 cutscene.json，那一段不會有配音和過場。").format("、".join(no)))
            lab.setStyleSheet("color:%s" % RED)
            lay.addWidget(lab)
        bb = QDialogButtonBox(QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel)
        bb.button(QDialogButtonBox.StandardButton.Ok).setText(T("開始合併"))
        bb.button(QDialogButtonBox.StandardButton.Cancel).setText(T("取消"))
        bb.accepted.connect(self.accept)
        bb.rejected.connect(self.reject)
        lay.addWidget(bb)

    def _add_row(self, row):
        r = self.tbl.rowCount()
        self.tbl.insertRow(r)
        self.tbl.setItem(r, 0, QTableWidgetItem(row["name"] if row else ""))
        for c, i in enumerate(self.aud, 1):
            cb = QComboBox()
            for nm, fp in self.parts[i]["vars"]:
                cb.addItem(nm, nm)
                cb.setItemData(cb.count() - 1, fp, Qt.ItemDataRole.ToolTipRole)
            want = (row or {}).get("pick", {}).get(i)
            k = cb.findData(want) if want else 0
            cb.setCurrentIndex(k if k >= 0 else 0)
            self.tbl.setCellWidget(r, c, cb)

    def _del_row(self):
        r = self.tbl.currentRow()
        if r > 0:
            self.tbl.removeRow(r)

    def rows(self):
        out = []
        for r in range(self.tbl.rowCount()):
            it = self.tbl.item(r, 0)
            pick = {i: self.tbl.cellWidget(r, c).currentData() for c, i in enumerate(self.aud, 1)}
            out.append({"name": (it.text().strip() if it else ""), "pick": pick})
        return CM.normalize_rows(self.parts, out)


# ============================================================ 主視窗
class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.settings = load_settings()
        self.cmb_after = None
        self.setWindowTitle(T("{0} {1} — 恋活場景卡合併").format(APP_NAME, VERSION))
        self.resize(1060, 820)

        self.tabs = QTabWidget()
        self.audio_tab = kkaudiotab.AudioTab(self.settings)
        settings_tab = self._settings_tab()      # 先建，合併分頁的 run() 會用到裡面的勾選
        self.cut_tab = kkcuttab.CutTab(self.settings)
        self.tree_tab = kktreetab.TreeTab(self.settings)
        # 原本獨立的 kkbridge：人物卡附加飾品／移植換裝／修卡，以及 F6 的工單監看
        self.bridge_tab = kkbridgetab.BridgeTab(
            self.settings, save=lambda: save_settings(self.settings), settings_path=SETTINGS_PATH)
        self.tabs.addTab(self._merge_tab(), T("合併場景"))
        self.tabs.addTab(self.cut_tab, T("添加動畫音頻"))
        self.tabs.addTab(self.audio_tab, T("VNGE音頻"))
        self.tabs.addTab(self.tree_tab, T("整理"))
        i = self.tabs.addTab(self.bridge_tab, T("人物卡合卡"))
        self.tabs.setTabToolTip(i, T("原本的 kkbridge：人物卡附加飾品、移植整套換裝、修卡。\nF6（StudioCharTools）的「添加飾品」「保持服裝換人」要這個程式開著、而且這一頁的「監看工單」在監看中。"))
        self.tabs.addTab(settings_tab, T("設定"))

        self.log = QPlainTextEdit()
        self.log.setReadOnly(True)
        self.log.setMaximumBlockCount(6000)
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
        split.setSizes([560, 240])
        self.setCentralWidget(split)
        self.status = self.statusBar()
        self.status.showMessage(T("就緒"))

        self.loader = InfoLoader()
        self.lthread = QThread(self)
        self.loader.moveToThread(self.lthread)
        self.lthread.started.connect(self.loader.loop)
        self.loader.done.connect(self.on_info)
        self.loader.failed.connect(self.on_info_failed)
        self.loader.log_line.connect(self.append_log)
        self.lthread.start()

        self.worker = MergeWorker()
        self.wthread = QThread(self)
        self.worker.moveToThread(self.wthread)
        self.wthread.started.connect(self.worker.loop)
        self.worker.log_line.connect(self.append_log)
        self.worker.started_.connect(self.on_started)
        self.worker.finished_.connect(self.on_finished)
        self.wthread.start()

        self._last_root = self.p_root.text()
        self._last_out_default = self.p_out.text()
        for w in (self.p_scene, self.p_out, self.p_audio, self.out):
            w.refresh_reset()

        self.audio_tab.log_line.connect(self.append_log)
        self.audio_tab.status.connect(lambda m: self.status.showMessage(m, 8000))
        self.cut_tab.log_line.connect(self.append_log)
        self.cut_tab.status.connect(lambda m: self.status.showMessage(m, 8000))
        self.tree_tab.log_line.connect(self.append_log)
        self.tree_tab.status.connect(lambda m: self.status.showMessage(m, 8000))
        self.bridge_tab.log_line.connect(self.append_log)
        self.bridge_tab.status.connect(lambda m: self.status.showMessage(m, 8000))
        self.audio_tab.edit_root.setText(self.settings.get("audio_root", ""))
        # 音頻分頁改路徑時即時同步回 settings —— 合併分頁每列的「選音頻…」要用
        for key, w in (("audio_root", self.audio_tab.edit_root),
                       ("audio_rel_prefix", self.audio_tab.edit_prefix),
                       ("audio_dir", self.audio_tab.edit_dir)):
            w.textChanged.connect(
                lambda t, k=key: self.settings.__setitem__(k, t.strip()))
        self.table.mode_changed.connect(self.audio_tab.sync_mode)
        # 紀錄欄和訊號都接好了才開始監看工單
        self.bridge_tab.autostart()

    def _apply_base_name(self):
        base = self.edit_base.text().strip()
        if not base:
            self.status.showMessage(T("先在「資料夾名稱」輸入共同名稱"), 4000)
            return
        if not self.table.rows:
            self.status.showMessage(T("清單是空的，先加卡片"), 4000)
            return
        n = self.table.apply_base_name(base)
        total = len(self.table.rows)
        msg = T("包裝資料夾名稱：{0}_(1) ~ {1}_({2})").format(base, base, total)
        if n < total:
            msg += T("（{0} 張已整理過、名稱鎖住，沒改）").format(total - n)
        self.append_log(msg)
        self.status.showMessage(msg, 5000)

    # ---- 合併分頁 ----
    def _merge_tab(self):
        w = QWidget()
        lay = QVBoxLayout(w)

        self.table = CardTable(self.settings)
        self.table.auto_camera = bool(self.settings.get("auto_camera", True))
        self.table.changed.connect(self.refresh_out)
        self.table.warn.connect(lambda m: (self.status.showMessage(m, 5000),
                                           self.append_log(T("[提醒] ") + m)))

        btns = QVBoxLayout()
        for text, slot in ((T("新增卡片…"), self.add_cards),
                           (T("移除"), self.table.remove_current),
                           (T("上移 ↑"), lambda: self.table.move(-1)),
                           (T("下移 ↓"), lambda: self.table.move(1)),
                           (T("清空"), self.table.clear_all),
                           (T("全部配音頻"), self.table.auto_audio_all)):
            b = QPushButton(text)
            b.clicked.connect(slot)
            btns.addWidget(b)

        # 一次設好全部的包裝資料夾名稱：輸入 Scenecard
        # -> Scenecard_(1)、_(2)、_(3)，照目前的順序
        btns.addSpacing(10)
        btns.addWidget(QLabel(T("資料夾名稱")))
        self.edit_base = QLineEdit()
        self.edit_base.setPlaceholderText(T("例：Scenecard"))
        self.edit_base.setToolTip(
            T("輸入共同名稱後按 Enter，依目前順序設成 名稱_(1)、名稱_(2)…\n已整理過（名稱鎖住）的卡不會改名。"))
        self.edit_base.returnPressed.connect(self._apply_base_name)
        btns.addWidget(self.edit_base)
        b = QPushButton(T("套用 _(1)~"))
        b.clicked.connect(self._apply_base_name)
        btns.addWidget(b)

        btns.addStretch(1)
        hint = QLabel(T("順序＝播放順序\n也可以直接把卡片\n拖到左邊清單"))
        hint.setStyleSheet("color:#777;")
        btns.addWidget(hint)

        top = QHBoxLayout()
        top.addWidget(self.table, 1)
        top.addLayout(btns)
        lay.addLayout(top, 1)

        # 只留這一個 —— 其餘全部搬到設定頁的「詳細設定」，預設值就是常用值
        g = QGroupBox(T("選項"))
        gv = QVBoxLayout(g)

        # 卡片的 studio 版本不一樣時才出現：一定要選一個版本來存
        self.ver_row = QWidget()
        vl = QHBoxLayout(self.ver_row)
        vl.setContentsMargins(0, 0, 0, 0)
        self.lbl_ver = QLabel()
        self.lbl_ver.setStyleSheet("color:#c0392b; font-weight:bold;")
        vl.addWidget(self.lbl_ver)
        self.cmb_ver = QComboBox()
        self.cmb_ver.setToolTip(
            T("合併卡只能用一個版本存。\n"
              "・存成較新的：舊卡的內容都留得住；要用開得了新版本原卡的遊戲／外掛來開。\n"
              "・存成較舊的：新版本才有的欄位不會存進去（物件的動畫樣式、天空設定、著色類型）。\n"
              "不管選哪個，另一個版本那幾段的角色、物件遊戲認不認得都要進遊戲確認。"))
        self.cmb_ver.currentIndexChanged.connect(lambda _i: self.refresh_out())
        vl.addWidget(self.cmb_ver)
        self.lbl_ver_hint = QLabel(T("混用不同版本的卡可能會出問題，合併後請進遊戲確認"))
        self.lbl_ver_hint.setStyleSheet("color:#777;")
        vl.addWidget(self.lbl_ver_hint, 1)
        self.ver_row.setVisible(False)
        self._ver_kinds = []
        gv.addWidget(self.ver_row)

        gl = QHBoxLayout()
        gv.addLayout(gl)
        gl.addWidget(QLabel(T("新相機名稱")))
        self.edit_cam = QLineEdit(self.settings.get("cam_name", ""))
        self.edit_cam.setPlaceholderText(T("留空＝取各場景相機的共同開頭"))
        gl.addWidget(self.edit_cam, 1)
        # 合併後那張卡的檔名。不存進設定檔 —— 每次合併的名字都不一樣。
        gl.addWidget(QLabel(T("合併後的場景名稱")))
        self.edit_outname = QLineEdit()
        self.edit_outname.setPlaceholderText(T("留空＝自動命名（共同名稱_merge_時間）"))
        self.edit_outname.setToolTip(
            T("合併出來那張場景卡的檔名（不用打 .png）。\n"
              "留空就照以前的方式自動取名：<各卡的共同名稱>_merge_<時間>.png。\n"
              "F7 的設定檔會跟著用同一個名字。"))
        gl.addWidget(self.edit_outname, 1)
        self.chk_cut = QCheckBox(T("F7 設定（cutscene.json）一起接"))
        self.chk_cut.setChecked(bool(self.settings.get("merge_cut", True)))
        self.chk_cut.setToolTip(
            T("卡片各自已經做好 F7 的設定檔（狀態欄有「有 F7 設定」）時，合併完順便把設定也接成一份，\n不用重新量對應點。音檔和影片不會動：設定檔記下每張卡各用哪個檔，F7 播到哪張卡就換哪個檔\n（需要 F7 1.14.0 以上）。"))
        gl.addWidget(self.chk_cut)
        lay.addWidget(g)

        # 輸出
        og = QGroupBox(T("輸出資料夾"))
        ov = QVBoxLayout(og)
        ol = QHBoxLayout()
        self.out = PathPicker(
            T("輸出資料夾"), is_dir=True, reveal=True,
            default=lambda: self.p_out.text(),
            default_tip=T("換回設定頁的「輸出預設資料夾」"))
        ol.addWidget(self.out, 1)
        self.btn_run = QPushButton(T("執行合併"))
        self.btn_run.setToolTip(
            T("兩張以上：照順序接成一張。\n只有一張：不接卡，只整理成合併後的形狀（檔名 _prep_）。"))
        self.btn_run.setMinimumHeight(36)
        self.btn_run.setMinimumWidth(140)
        self.btn_run.clicked.connect(self.run)
        ol.addWidget(self.btn_run)
        ov.addLayout(ol)
        lay.addWidget(og)
        return w

    # ---- 設定分頁 ----
    def _settings_tab(self):
        w = QWidget()
        lay = QVBoxLayout(w)
        g = QGroupBox(T("資料夾"))
        gl = QVBoxLayout(g)

        gl.addWidget(QLabel(T("遊戲根目錄")))
        hint = QLabel(T("改了這個，底下三個路徑與主頁面的輸出資料夾會一起換成對應的位置；自己填過的那一個就不動了。"))
        hint.setStyleSheet("color:#777;")
        hint.setWordWrap(True)
        gl.addWidget(hint)
        self.p_root = PathPicker(T("遊戲根目錄"), is_dir=True)
        self.p_root.set(self.settings["game_root"])
        self.p_root.edit.textChanged.connect(self.on_root_changed)
        gl.addWidget(self.p_root)

        gl.addWidget(QLabel(T("Koikatsu Sunshine 根目錄（選填）")))
        hint2 = QLabel(T("有填的話，「人物卡合卡」的工單監看會同時監看這款遊戲的工單資料夾"
                         "（UserData\\chara\\female\\Temp），Sunshine 裡 F6 的合卡功能才有人接。"
                         "其他設定不受影響。"))
        hint2.setStyleSheet("color:#777;")
        hint2.setWordWrap(True)
        gl.addWidget(hint2)
        self.p_root_kks = PathPicker(T("Koikatsu Sunshine 根目錄"), is_dir=True)
        self.p_root_kks.set(self.settings.get("game_root_kks", ""))
        self.p_root_kks.edit.textChanged.connect(self.on_kks_root_changed)
        gl.addWidget(self.p_root_kks)

        gl.addWidget(QLabel(T("讀卡預設資料夾")))
        self.p_scene = PathPicker(
            T("場景卡資料夾"), is_dir=True, reveal=True,
            default=lambda: under(self.p_root.text(), SUB_SCENE),
            default_tip=T("換回「遊戲根目錄」算出來的位置"))
        self.p_scene.set(self.settings["scene_dir"])
        gl.addWidget(self.p_scene)

        gl.addWidget(QLabel(T("輸出預設資料夾")))
        self.p_out = PathPicker(
            T("輸出資料夾"), is_dir=True, reveal=True,
            default=lambda: under(self.p_root.text(), SUB_SCENE),
            default_tip=T("換回「遊戲根目錄」算出來的位置"))
        self.p_out.set(self.settings["output_dir"])
        self.p_out.edit.textChanged.connect(self.on_out_default_changed)
        gl.addWidget(self.p_out)

        gl.addWidget(QLabel(T("音頻根目錄（相對路徑從這裡往下算）")))
        self.p_audio = PathPicker(
            T("音頻根目錄"), is_dir=True, reveal=True,
            default=lambda: under(self.p_root.text(), SUB_AUDIO),
            default_tip=T("換回「遊戲根目錄」算出來的位置"))
        self.p_audio.set(self.settings.get("audio_root", ""))
        self.p_audio.edit.textChanged.connect(
            lambda t: self.audio_tab.edit_root.setText(t))
        self.audio_tab.edit_root.textChanged.connect(
            lambda t: self.p_audio.edit.setText(t))
        gl.addWidget(self.p_audio)

        rp = QHBoxLayout()
        rp.addWidget(QLabel(T("存進卡片的相對路徑前綴")))
        self.edit_prefix = QLineEdit(
            self.settings.get("audio_rel_prefix", "UserData\\audio"))
        self.edit_prefix.setToolTip(
            T("卡片裡存的是「前綴 + 音檔在根目錄底下的位置」。\n音檔實際放在別的磁碟也沒關係，只要遊戲那邊這個相對路徑讀得到。"))
        self.edit_prefix.textChanged.connect(
            lambda t: self.audio_tab.edit_prefix.setText(t))
        rp.addWidget(self.edit_prefix, 1)
        bp = QPushButton(T("回預設"))
        bp.setFixedWidth(66)
        bp.setToolTip(T("換回 UserData\\audio"))
        bp.clicked.connect(lambda: self.edit_prefix.setText(V.DEFAULT_REL_PREFIX))
        rp.addWidget(bp)
        gl.addLayout(rp)
        lay.addWidget(g)

        # ---- 詳細設定（本來擠在合併分頁的那一排）----
        adv = QGroupBox(T("詳細設定（預設值就是常用值，沒事不用動）"))
        av = QVBoxLayout(adv)

        self.chk_cam = QCheckBox(T("自動接管相機（生一台新相機，交界自動切換）"))
        self.chk_cam.setChecked(self.settings["camera_switch"])
        av.addWidget(self.chk_cam)

        a1 = QHBoxLayout()
        self.chk_group = QCheckBox(T("timeline 每個場景各包一層群組"))
        self.chk_group.setChecked(self.settings["group"])
        self.chk_nc = QCheckBox(T("NC 改名成「場景名 | 原名」"))
        self.chk_nc.setChecked(self.settings["nc_rename"])
        self.chk_sub = QCheckBox(T("建 (MAP)(FX)(CHAR)(SFX)"))
        self.chk_sub.setChecked(self.settings["subfolders"])
        for x in (self.chk_group, self.chk_nc, self.chk_sub):
            a1.addWidget(x)
        a1.addStretch(1)
        av.addLayout(a1)

        a2 = QHBoxLayout()
        self.chk_zero = QCheckBox(T("相機資料夾在場景結束時歸零"))
        self.chk_zero.setChecked(self.settings["zero_chain"])
        a2.addWidget(self.chk_zero)
        self.chk_autocam = QCheckBox(T("沒有相機時自動新增（鎖初始視角）"))
        self.chk_autocam.setToolTip(T("純過場 / 靜態場景常常沒有相機。\n勾起來就自動生一台鎖在那張卡存檔時的視角；\n不勾的話沒有相機的卡會擋住不給合併。"))
        self.chk_autocam.setChecked(self.settings.get("auto_camera", True))
        self.chk_autocam.toggled.connect(self.on_autocam_toggled)
        a2.addWidget(self.chk_autocam)
        self.chk_enable = QCheckBox(T("沒輪到的場景取消勾選（物件啟用軌道）"))
        self.chk_enable.setToolTip(T("每個場景的主資料夾與 (SFX) 各寫一條物件啟用軌道，\n輪到自己才勾選，其餘時間取消 —— 跟外太空停放搭配用。"))
        self.chk_enable.setChecked(self.settings.get("enable_tracks", True))
        a2.addWidget(self.chk_enable)
        a2.addStretch(1)
        av.addLayout(a2)

        a2b = QHBoxLayout()
        self.chk_tlclean = QCheckBox(T("按 id 整批拿掉那幾種軌道（大刀，預設關）"))
        self.chk_tlclean.setToolTip(
            T("把 tears／blush／itemColor／charClothes 等幾種 id 的軌道整批拿掉，不管有沒有問題。\n會連好的一起丟（例如演到一半脫衣服），只在 timeline 仍整棵失效時才開。"))
        self.chk_tlclean.setChecked(self.settings.get("tl_clean", False))
        a2b.addWidget(self.chk_tlclean)
        a2b.addStretch(1)
        av.addLayout(a2b)

        a2c = QHBoxLayout()
        self.chk_tlmis = QCheckBox(T("拿掉指錯節點的軌道（預設開）"))
        self.chk_tlmis.setToolTip(
            T("只拿掉 objectIndex 指到錯誤種類或不存在節點的軌道。\n這種軌道會讓 Timeline 從第 0 秒整棵中斷；好的軌道不動。"))
        self.chk_tlmis.setChecked(self.settings.get("tl_mismatch", True))
        a2c.addWidget(self.chk_tlmis)
        a2c.addStretch(1)
        av.addLayout(a2c)

        a2d = QHBoxLayout()
        a2d.addWidget(QLabel(T("場景 shaderType")))
        self.cmb_shader = QComboBox()
        self.cmb_shader.addItem(T("保留底卡的"), "")
        self.cmb_shader.addItem(T("整張用 0"), "0")
        self.cmb_shader.addItem(T("整張用 1"), "1")
        self.cmb_shader.setToolTip(
            T("shaderType 是整張卡一個值，合併只能留一個（預設用第 1 列的）。\n各段不一致時頭髮、眼睛可能變樣，紀錄裡會有 [注意]。"))
        want = str(self.settings.get("shader_type", "") or "")
        i = self.cmb_shader.findData(want)
        self.cmb_shader.setCurrentIndex(i if i >= 0 else 0)
        a2d.addWidget(self.cmb_shader)
        a2d.addStretch(1)
        av.addLayout(a2d)

        a2e = QHBoxLayout()
        self.chk_park = QCheckBox(T("把沒輪到的段落搬到外太空"))
        self.chk_park.setToolTip(
            T("沒輪到的段落搬到 (100,100,100)，輪到才搬回原位。\n瞬移可能甩亂頭髮等揺れ物；關掉就留在原地，只靠啟用軌道隱藏。"))
        self.chk_park.setChecked(bool(self.settings.get("park", True)))
        a2e.addWidget(self.chk_park)
        a2e.addWidget(QLabel(T("提早歸位(秒)")))
        self.spin_lead = QDoubleSpinBox()
        self.spin_lead.setDecimals(2)
        self.spin_lead.setRange(0.0, 30.0)
        self.spin_lead.setSingleStep(0.5)
        self.spin_lead.setToolTip(
            T("提早幾秒搬回原位，讓揺れ物在入鏡前先靜下來（1~3 秒通常夠）。0 = 不提早。"))
        self.spin_lead.setValue(float(self.settings.get("park_lead", 0.0) or 0.0))
        a2e.addWidget(self.spin_lead)
        a2e.addStretch(1)
        av.addLayout(a2e)

        a2f = QHBoxLayout()
        self.chk_ncen = QCheckBox(T("把作者的 NC 也寫進啟用軌道"))
        self.chk_ncen.setToolTip(
            T("每條 NC 加一條啟用軌道：只在自己那段開著，其餘時間關掉。\n已經有啟用軌道的 NC 會跳過。軌道會多很多。"))
        self.chk_ncen.setChecked(bool(self.settings.get("nc_enable_tracks", False)))
        a2f.addWidget(self.chk_ncen)

        self.chk_frame = QCheckBox(T("清掉外框"))
        self.chk_frame.setToolTip(
            T("拿掉底卡的外框（StudioImageEmbed 的 FrameData），背景圖不動。"))
        self.chk_frame.setChecked(bool(self.settings.get("clear_frame", True)))
        a2f.addWidget(self.chk_frame)

        self.chk_enall = QCheckBox(T("啟用相機軌道"))
        self.chk_enall.setToolTip(
            T("把沒打勾的時間流速、相機縮放/FOV、相機資料夾軌道打開，其他保留作者設定。\n⚠ 時間流速會改變播放速度，已量好的對應點可能要重量。"))
        self.chk_enall.setChecked(bool(self.settings.get("enable_all_tracks", True)))
        a2f.addWidget(self.chk_enall)

        self.chk_camtr = QCheckBox(T("每段相機縮放/FOV"))
        self.chk_camtr.setToolTip(
            T("每段開頭把相機縮放 / FOV 設成那張原卡的值，換段時瞬間切換。"))
        self.chk_camtr.setChecked(bool(self.settings.get("cam_tracks", True)))
        a2f.addWidget(self.chk_camtr)
        a2f.addStretch(1)
        av.addLayout(a2f)

        a3 = QHBoxLayout()
        a3.addWidget(QLabel(T("交界間隙(秒)")))
        self.spin_gap = QDoubleSpinBox()
        self.spin_gap.setDecimals(3)
        self.spin_gap.setRange(0.0, 10.0)
        self.spin_gap.setSingleStep(0.01)
        self.spin_gap.setValue(float(self.settings["gap"]))
        self.spin_gap.setFixedWidth(90)
        a3.addWidget(self.spin_gap)
        a3.addStretch(1)
        av.addLayout(a3)
        lay.addWidget(adv)

        flow = QGroupBox(T("流程"))
        fl = QHBoxLayout(flow)
        fl.addWidget(QLabel(T("合併完成後")))
        self.cmb_after = QComboBox()
        for txt, val in ((T("問我要去哪一頁"), "ask"),
                         (T("跳到 VNGE音頻"), "audio"),
                         (T("跳到 添加動畫音頻"), "cut"),
                         (T("跳到 整理"), "tree"),
                         (T("留在合併分頁"), "stay")):
            self.cmb_after.addItem(txt, val)
        cur = self.settings.get("after_merge", "ask")
        for i in range(self.cmb_after.count()):
            if self.cmb_after.itemData(i) == cur:
                self.cmb_after.setCurrentIndex(i)
                break
        fl.addWidget(self.cmb_after)
        tip = QLabel(T("不管選哪個，合併好的卡片都會自動填進其他分頁，差別只在要不要幫你切過去。"))
        tip.setStyleSheet("color:#777;")
        tip.setWordWrap(True)
        fl.addWidget(tip, 1)
        lay.addWidget(flow)

        b = QPushButton(T("儲存設定"))
        b.clicked.connect(self.save_ui_settings)
        lay.addWidget(b, alignment=Qt.AlignmentFlag.AlignLeft)

        note = QLabel(
            T("流程：加入場景卡 → 排順序 → 每張選一台相機 → 設資料夾名稱 →（要配音就選音頻）→ 執行合併。"))
        note.setWordWrap(True)
        note.setStyleSheet("color:#666;")
        lay.addWidget(note)
        ff = kkffmpeg.where()
        ffl = QLabel("ffmpeg：" + (ff if ff else T("找不到（影片／音訊功能不能用，把 ffmpeg 資料夾放在程式旁邊）")))
        ffl.setStyleSheet("color:#666;" if ff else "color:%s;" % RED)
        ffl.setWordWrap(True)
        lay.addWidget(ffl)
        lay.addStretch(1)

        # ---- 語言（放最下面）----
        lg = QGroupBox("Language / 語言 / 言語")
        ll = QHBoxLayout(lg)
        self.cmb_lang = QComboBox()
        self.cmb_lang.addItems(list(L.NAMES))
        self.cmb_lang.setCurrentIndex(L.Current)
        self.cmb_lang.currentIndexChanged.connect(self.change_lang)
        ll.addWidget(self.cmb_lang)
        hint = QLabel(T("換語言後要重新啟動才會生效"))
        hint.setStyleSheet("color:#777;")
        ll.addWidget(hint, 1)
        lay.addWidget(lg)
        return w

    def change_lang(self, index):
        """存下語言設定，問要不要馬上重開。介面是啟動時建的，所以要重開才完整換掉。"""
        old = L.Current
        if index == old:
            return
        self.settings["lang"] = index
        self.save_ui_settings()
        L.set_lang(index)                 # 這個對話框先用新的語言顯示
        ans = QMessageBox.question(
            self, T("語言"), T("已切換成 {0}。要現在重新啟動嗎？").format(L.name(index)))
        if ans != QMessageBox.StandardButton.Yes:
            L.set_lang(old)               # 不重開就維持目前介面的語言，下次啟動才換
            return
        if getattr(sys, "frozen", False):
            prog, args = sys.executable, sys.argv[1:]
        else:
            prog, args = sys.executable, [os.path.abspath(sys.argv[0])] + sys.argv[1:]
        QProcess.startDetached(prog, args)
        self.close()

    # ---- 行為 ----
    def on_root_changed(self, text):
        """遊戲根目錄一改，底下那幾個路徑跟著換 —— 除非使用者自己指定過。

        判斷方式不用額外的旗標：欄位裡放的如果還是「舊根目錄算出來的值」
        （或是空的），就代表沒被動過，可以覆蓋；否則就是使用者自己填的，不碰。
        """
        new = (text or "").strip()
        old = getattr(self, "_last_root", "")
        if new == old:
            return
        if not hasattr(self, "p_audio"):          # 建構到一半，還沒接完
            return
        for w, parts in ((self.p_scene, SUB_SCENE),
                         (self.p_out, SUB_SCENE),
                         (self.p_audio, SUB_AUDIO)):
            cur = w.text()
            if not cur or cur == under(old, parts):
                w.set(under(new, parts))
        self._last_root = new
        for w in (self.p_scene, self.p_out, self.p_audio):
            w.refresh_reset()
        if hasattr(self, "out"):
            self.out.refresh_reset()
        self.settings["game_root"] = new
        self.audio_tab._refresh_defaults()
        if hasattr(self, "bridge_tab"):
            self.bridge_tab.set_game_root(new)

    def on_kks_root_changed(self, text):
        """Koikatsu Sunshine 根目錄改了：只影響「人物卡合卡」多監看哪一個工單資料夾。"""
        new = (text or "").strip()
        self.settings["game_root_kks"] = new
        if hasattr(self, "bridge_tab"):
            self.bridge_tab.set_kks_root(new)

    def on_out_default_changed(self, text):
        """「輸出預設資料夾」改了，主頁面下方那格也跟著換（同樣只在沒被改過時）。"""
        new = (text or "").strip()
        old = getattr(self, "_last_out_default", "")
        if new == old or not hasattr(self, "out"):
            return
        cur = self.out.text()
        if not cur or cur == old:
            self.out.set(new)
        self._last_out_default = new

    def save_ui_settings(self):
        # 每個分頁自己把狀態寫進共用的 settings，這裡只負責收集 + 落地。
        # 不然新增分頁就得記得回來改這個函式，遲早會漏。
        for tab in (getattr(self, "cut_tab", None), getattr(self, "tree_tab", None)):
            if tab is not None and hasattr(tab, "save_state"):
                try:
                    tab.save_state()
                except Exception:
                    self.append_log(T("儲存分頁狀態時出錯：\n") + traceback.format_exc())
        if getattr(self, "bridge_tab", None) is not None:
            self.bridge_tab.collect()
        self.settings.update({
            "game_root": self.p_root.text(),
            "game_root_kks": self.p_root_kks.text(),
            "scene_dir": self.p_scene.text(),
            "output_dir": self.p_out.text(),
            "gap": self.spin_gap.value(),
            "camera_switch": self.chk_cam.isChecked(),
            "group": self.chk_group.isChecked(),
            "nc_rename": self.chk_nc.isChecked(),
            "subfolders": self.chk_sub.isChecked(),
            "zero_chain": self.chk_zero.isChecked(),
            "auto_camera": self.chk_autocam.isChecked(),
            "enable_tracks": self.chk_enable.isChecked(),
            "tl_clean": self.chk_tlclean.isChecked(),
            "tl_mismatch": self.chk_tlmis.isChecked(),
            "shader_type": self.cmb_shader.currentData() or "",
            "park": self.chk_park.isChecked(),
            "park_lead": float(self.spin_lead.value()),
            "nc_enable_tracks": self.chk_ncen.isChecked(),
            "clear_frame": self.chk_frame.isChecked(),
            "enable_all_tracks": self.chk_enall.isChecked(),
            "cam_tracks": self.chk_camtr.isChecked(),
            "cam_name": self.edit_cam.text().strip(),
            "audio_root": (self.audio_tab.edit_root.text().strip()
                           or self.p_audio.text()),
            "audio_rel_prefix": (self.audio_tab.edit_prefix.text().strip()
                                 or self.edit_prefix.text().strip()),
            "audio_dir": self.audio_tab.edit_dir.text().strip(),
            "audio_mode": self.audio_tab.combo_mode.currentData(),
            "after_merge": self.cmb_after.currentData(),
            "merge_cut": self.chk_cut.isChecked(),
        })
        save_settings(self.settings)
        self.append_log(T("設定已儲存（含各分頁的狀態）：") + str(SETTINGS_PATH))

    def add_cards(self):
        start = self.p_scene.text() or self.settings.get("scene_dir", "")
        paths, _ = QFileDialog.getOpenFileNames(self, T("選擇場景卡"), start,
                                                T("場景卡 (*.png)"))
        if paths:
            self.table.add_paths(paths)

    def on_autocam_toggled(self, on):
        self.table.auto_camera = bool(on)
        self.table.rebuild()
        self.refresh_out()

    def refresh_out(self):
        if hasattr(self, "chk_autocam"):
            self.table.auto_camera = self.chk_autocam.isChecked()
        for row in self.table.rows:
            if row.get("info") is None and not row.get("_queued"):
                row["_queued"] = True
                self.loader.submit(row["path"])
        if not self.out.text():
            d = self.p_out.text() or (str(Path(self.table.rows[0]["path"]).parent)
                                      if self.table.rows else "")
            if d:
                self.out.set(d)
        self._refresh_version_row()
        ok, why = self.table.ready()
        if ok and getattr(self, "_ver_kinds", None) and not self.chosen_version():
            ok, why = False, T("卡片的 studio 版本不一樣，請先在「選項」選要存成哪個版本")
        self.status.showMessage(T("就緒") if ok else why)

    @staticmethod
    def _version_label(v):
        if str(v).startswith("1.0."):
            return v + "（Koikatsu）"
        if str(v).startswith("1.1."):
            return v + "（Koikatsu Sunshine）"
        return str(v)

    def _refresh_version_row(self):
        """清單裡的卡 studio 版本不只一種時，顯示「存成哪個版本」那一列。"""
        if not hasattr(self, "cmb_ver"):
            return
        kinds = sorted({r["info"].get("version") for r in self.table.rows
                        if r.get("info") is not None and r["info"].get("version")},
                       key=KM._ver_key)
        if len(kinds) < 2:
            kinds = []
        if kinds == self._ver_kinds:
            return
        keep = self.chosen_version()
        self._ver_kinds = kinds
        self.cmb_ver.blockSignals(True)
        self.cmb_ver.clear()
        if kinds:
            self.cmb_ver.addItem(T("（請選擇）"), "")
            for v in kinds:
                self.cmb_ver.addItem(self._version_label(v), v)
            i = self.cmb_ver.findData(keep) if keep else -1
            self.cmb_ver.setCurrentIndex(i if i > 0 else 0)
            self.lbl_ver.setText(T("⚠ 偵測到不同的 studio 版本（{0}），合併卡存成：")
                                 .format("、".join(kinds)))
        self.cmb_ver.blockSignals(False)
        self.ver_row.setVisible(bool(kinds))

    def custom_out_name(self):
        """使用者填的合併後場景名稱（不含 .png）；沒填回傳空字串。

        檔名不能用的字元（\\ / : * ? " < > |）換成底線，頭尾的空白和句點拿掉。
        """
        if not hasattr(self, "edit_outname"):
            return ""
        nm = self.edit_outname.text().strip()
        if nm.lower().endswith(".png"):
            nm = nm[:-4]
        nm = "".join("_" if (c in '\\/:*?"<>|' or ord(c) < 32) else c for c in nm)
        return nm.strip(" .")

    def chosen_version(self):
        """使用者選的存檔版本；版本都一樣（不用選）或還沒選時回傳空字串。"""
        if not getattr(self, "_ver_kinds", None):
            return ""
        return self.cmb_ver.currentData() or ""

    def on_info(self, path, info):
        self.table.set_info(path, info)
        self.append_log(
            T("  {0}：版本 {1}、時長 {2}、節點 {3}、相機 {4} 台").format(Path(path).name, info['version'], fmt_dur(info['duration']), info['nodes'], len(info['cameras']))
            + (T("（用 timeline 相機路徑運鏡，會生一台相機接手）") if info.get("camera_path")
               else T("（沒有相機，會自動生一台鎖初始視角）") if info.get("auto_camera") else "")
            + (T("（已整理過）") if info["prepped"] else "")
            + (T("、內建地圖 %s") % (("#%d" % info["map"]) if info.get("map", -1) >= 0 else T("無"))
               if "map" in info else ""))
        self._check_maps()
        # 宣告時長跟實際內容對不上就講一聲。合併是照宣告時長排時間的，
        # 差一截的話後面每一段都會提早，而且在 Studio 裡拉時間軸看得到動作、
        # 從這裡卻看不出來 —— 不講的話只能靠手動一張一張試。
        end = info.get("content_end")
        dur = info.get("duration") or 0.0
        if end and end > dur + 0.05:
            self.append_log(
                T("    [時長] 宣告 {0}，實際內容到 {1}（多 {2:.2f} 秒）—— 時長欄已標紅，滑鼠移上去有說明").format(fmt_dur(dur), fmt_dur(end), end - dur))

    def _check_maps(self):
        """各張的內建地圖不一樣就提醒一次（清單內容變了才會再提醒）。

        內建地圖是整張卡一個的設定，合併卡只存得住一張；F7 播放時會照每段的
        記號切換/隱藏，但沒裝 F7 時每段都會看到同一張 —— 讀卡當下就該知道。
        """
        rows = [r for r in self.table.rows if r.get("info")]
        if len(rows) < 2 or any("map" not in r["info"] for r in rows):
            return
        maps = [r["info"]["map"] for r in rows]
        if len(set(maps)) < 2:
            return
        sig = tuple((r["path"], r["info"]["map"]) for r in rows)
        if sig == getattr(self, "_map_warned", None):
            return
        self._map_warned = sig
        desc = "、".join(T("第 %d 張 %s") % (i + 1, ("#%d" % m) if m >= 0 else T("無"))
                        for i, m in enumerate(maps))
        prim = next(m for m in maps if m >= 0)
        self.append_log(
            T("[提醒] 各張的內建地圖不一樣（%s）。合併卡只能存一張，會用 #%d；每段會放 [MAPINFO] 記號，F7 播放時自動切換/隱藏。沒裝 F7 或直接在 Timeline 播時，每段都會看到 #%d。") % (desc, prim, prim))

    def on_info_failed(self, path, msg):
        self.table.set_failed(path, msg)
        self.append_log(T("[讀卡失敗] {0}：{1}").format(Path(path).name, msg))

    def run(self):
        ok, why = self.table.ready()
        if not ok:
            QMessageBox.warning(self, T("還不能執行"), why)
            return
        self._refresh_version_row()
        if self._ver_kinds and not self.chosen_version():
            QMessageBox.warning(
                self, T("還不能執行"),
                T("卡片的 studio 版本不一樣（{0}）。\n請先在「選項」選合併卡要存成哪個版本。\n\n"
                  "混用不同版本的卡可能會出問題，合併後請進遊戲確認。")
                .format("、".join(self._ver_kinds)))
            return
        d = self.out.text()
        if not d:
            QMessageBox.warning(self, T("還不能執行"), T("請先指定輸出資料夾"))
            return
        p = Path(d)
        if p.suffix.lower() == ".png":       # 不小心填了檔名就取它的資料夾
            p = p.parent
        if not p.is_dir():
            QMessageBox.warning(self, T("還不能執行"), T("找不到資料夾：\n{0}").format(p))
            return
        self.out.set(str(p))
        out = str(p / auto_out_name([r["name"] for r in self.table.rows]))
        custom = self.custom_out_name()
        if custom:
            out = str(p / (custom + ".png"))
            same = os.path.normcase(os.path.abspath(out))
            if any(os.path.normcase(os.path.abspath(r["path"])) == same for r in self.table.rows):
                QMessageBox.warning(self, T("還不能執行"),
                                    T("合併後的場景名稱跟清單裡的來源卡一樣，會把來源卡蓋掉。請換一個名稱。"))
                return
            # 同名的卡、或同名的 F7 設定檔已經在那裡的話先問（自動命名帶時間，不會撞到）
            clash = [out] if os.path.isfile(out) else []
            if self.chk_cut.isChecked():
                jd = self.settings.get("cut_out_dir", "") or str(p)
                jp = os.path.join(jd, custom + ".cutscene.json")
                if os.path.isfile(jp):
                    clash.append(jp)
            if clash and QMessageBox.question(
                    self, T("已經有這個檔"),
                    T("%s 已存在，要覆蓋嗎？") % "、".join(os.path.basename(x) for x in clash)
                    ) != QMessageBox.StandardButton.Yes:
                return
        self.save_ui_settings()
        opts = {
            "gap": self.spin_gap.value(),
            "camera_switch": self.chk_cam.isChecked(),
            "group": self.chk_group.isChecked(),
            "nc_rename": self.chk_nc.isChecked(),
            "subfolders": self.chk_sub.isChecked(),
            "zero_chain": self.chk_zero.isChecked(),
            "auto_camera": self.chk_autocam.isChecked(),
            "enable_tracks": self.chk_enable.isChecked(),
            "tl_clean": self.chk_tlclean.isChecked(),
            "tl_mismatch": self.chk_tlmis.isChecked(),
            "shader_type": self.cmb_shader.currentData() or "",
            "park": self.chk_park.isChecked(),
            "park_lead": float(self.spin_lead.value()),
            "nc_enable_tracks": self.chk_ncen.isChecked(),
            "clear_frame": self.chk_frame.isChecked(),
            "enable_all_tracks": self.chk_enall.isChecked(),
            "cam_tracks": self.chk_camtr.isChecked(),
            "cam_name": self.edit_cam.text().strip(),
            "save_version": self.chosen_version(),
            "audio_root": self.settings.get("audio_root", ""),
            "audio_rel_prefix": self.settings.get("audio_rel_prefix", ""),
            "audio_dir": self.settings.get("audio_dir", ""),
            "audio_mode": self.settings.get("audio_mode", V.MODE_REL),
            "audio_rows": self.table.audio_rows(),
        }
        opts["cut"] = None
        if self.chk_cut.isChecked():
            cut = self._prepare_cut()
            if cut is False:
                return                       # 在配對視窗按了取消
            opts["cut"] = cut
        if opts["audio_rows"] and not opts["audio_root"]:
            QMessageBox.warning(self, T("還不能執行"),
                                T("有列選了音頻，但還沒在設定頁指定音頻根目錄（相對路徑是從那裡往下算的）"))
            self.btn_run.setEnabled(True)
            return
        self.btn_run.setEnabled(False)
        self.append_log("=" * 60)
        rows = self.table.rows
        self.append_log((T("開始合併 ") + " → ".join(r["name"] for r in rows))
                        if len(rows) > 1 else
                        (T("開始整理 ") + rows[0]["name"] + T("（只有一張卡，不接卡）")))
        self._last_out = out
        self.worker.submit(self.table.jobs(), out, opts)

    def _prepare_cut(self):
        """合併前先把「要不要接 F7 設定、配音怎麼配對」問清楚。

        回傳 None＝沒有設定檔可以接；False＝使用者取消整個合併；dict＝交給 MergeWorker。
        """
        cards = self.table.cut_cards()
        if not any(c["json"] for c in cards):
            return None
        out_dir = self.settings.get("cut_out_dir", "") or ""
        if hasattr(self, "cut_tab") and self.cut_tab.ed_out.text().strip():
            out_dir = self.cut_tab.ed_out.text().strip()
        cut = {"cards": cards, "rows": None, "out_dir": out_dir}
        try:
            parts = CM.make_parts(cards)
        except Exception as e:                          # noqa: BLE001
            self.append_log(T("[提醒] 讀 cutscene.json 時出錯，這次不接：{0}").format(e))
            return None
        aud = CM.audio_parts(parts)
        n_json = sum(1 for p in parts if p["cfg"])
        self.append_log(T("F7 設定：{0} 張卡裡有 {1} 張有 cutscene.json，合併完會一起接").format(len(parts), n_json))
        if len(aud) < 2:
            return cut                       # 只有一張卡有配音：音檔沿用它的，沒有東西要問
        dlg = CutMergeDialog(self, parts)
        if dlg.exec() != QDialog.DialogCode.Accepted:
            return False
        cut["rows"] = dlg.rows()
        return cut

    def on_started(self):
        one = len(self.table.rows) == 1
        self.status.showMessage((T("整理中…") if one else T("合併中…"))
                                + T("（大卡要幾分鐘，視窗沒有回應是正常的）"))

    def on_finished(self, ok, msg):
        self.btn_run.setEnabled(True)
        self.append_log(("✔ " if ok else "✘ ") + msg)
        self.status.showMessage(msg if ok else T("失敗：") + msg)
        if ok and getattr(self, "_last_out", ""):
            # 卡片一律帶到其他分頁（「添加動畫音頻」會順便讀既有的 pairs.txt），
            # 要不要把畫面切過去則看設定。
            self.audio_tab.edit_card.setText(self._last_out)
            self.cut_tab.set_card(self._last_out)
            self.tree_tab.set_card(self._last_out)
            self._after_merge()
        if not ok:
            QMessageBox.critical(self, T("合併失敗"), msg)

    def _after_merge(self):
        want = self.settings.get("after_merge", "ask")
        if self.cmb_after is not None:
            want = self.cmb_after.currentData() or want
        if want == "stay":
            return
        if want == "ask":
            box = QMessageBox(self)
            box.setWindowTitle(T("合併完成"))
            box.setText(T("卡片已經填進其他分頁了，接下來要去哪裡？"))
            b_audio = box.addButton(T("VNGE音頻"), QMessageBox.ButtonRole.AcceptRole)
            b_cut = box.addButton(T("添加動畫音頻"), QMessageBox.ButtonRole.AcceptRole)
            b_tree = box.addButton(T("整理"), QMessageBox.ButtonRole.AcceptRole)
            box.addButton(T("留在這裡"), QMessageBox.ButtonRole.RejectRole)
            box.exec()
            clicked = box.clickedButton()
            if clicked is b_audio:
                want = "audio"
            elif clicked is b_cut:
                want = "cut"
            elif clicked is b_tree:
                want = "tree"
            else:
                return
        target = {"audio": self.audio_tab,
                  "cut": self.cut_tab,
                  "tree": self.tree_tab}.get(want)
        if target is None:
            return
        i = self.tabs.indexOf(target)
        if i >= 0:
            self.tabs.setCurrentIndex(i)

    def append_log(self, text):
        """紀錄欄加一行。含「[提醒]」的那幾行用紅字，一眼就看得到。

        全部都走 appendHtml：混用 appendPlainText 的話，紅字之後的那一行
        會沿用上一段的字元格式，整段變紅。空白換成 &nbsp; 保住縮排。
        """
        import html as _html
        for line in text.rstrip().split("\n"):
            body = _html.escape(line).replace(" ", "&nbsp;") or "&nbsp;"
            if T("[提醒]") in line:
                self.log.appendHtml('<span style="color:#d00000;">%s</span>' % body)
            else:
                fg = self.log.palette().color(self.log.foregroundRole()).name()
                self.log.appendHtml('<span style="color:%s;">%s</span>' % (fg, body))

    def closeEvent(self, e):
        self.save_ui_settings()
        self.audio_tab.shutdown()
        self.cut_tab.shutdown()
        self.tree_tab.shutdown()
        self.bridge_tab.shutdown()
        for obj, th in ((self.loader, self.lthread), (self.worker, self.wthread)):
            obj.stop()
            th.quit()
            th.wait(2000)
        e.accept()


def install_excepthook():
    """讓沒接住的例外變成一個看得懂的對話框，而不是整個視窗瞬間消失。

    PyQt6 的行為跟一般 Python 程式不一樣：slot（按鈕、拖放、訊號的接收端）
    裡漏接的例外會走到 qFatal，直接 abort 整個行程 —— 沒有 traceback、
    沒有訊息，使用者只看到「閃退」。少一個套件、少一個檔案都會變成這樣，
    而且完全沒有線索可以追。

    掛上自己的 excepthook 之後：traceback 進 kkscenebridge_crash.log，
    畫面上出現最後一行錯誤和 log 的位置，程式繼續活著。
    """
    log_path = app_dir() / "kkscenebridge_crash.log"

    def hook(etype, value, tb):
        text = "".join(traceback.format_exception(etype, value, tb))
        try:
            with open(log_path, "a", encoding="utf-8") as f:
                f.write("\n" + "=" * 70 + "\n")
                f.write(datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S") + "\n")
                f.write(text)
        except Exception:
            pass
        try:
            sys.stderr.write(text)
        except Exception:
            pass
        try:
            last = text.strip().splitlines()[-1]
            QMessageBox.critical(
                None, T("出錯了（程式沒有關掉）"),
                T("%s\n\n完整內容寫在：\n%s") % (last, log_path))
        except Exception:
            pass

    sys.excepthook = hook


def main():
    app = QApplication(sys.argv)
    install_excepthook()        # 要在 QApplication 之後：hook 裡會開對話框
    win = MainWindow()
    win.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
