# -*- coding: utf-8 -*-
"""kkaudiotab.py — kkscenebridge 的「音頻」分頁

對一張（通常是已經合併好的）場景卡：列出每一段場景 → 每段配音檔 →
寫進 VNGE 的 vnge_sound。實際的格式處理在 kkvnsound.py。
"""
from __future__ import annotations
from kksblang import T

import os
import queue
import traceback
from pathlib import Path

from PyQt6.QtCore import QObject, Qt, QThread, pyqtSignal
from PyQt6.QtWidgets import (
    QAbstractItemView, QCheckBox, QDialog, QDialogButtonBox, QFileDialog,
    QGroupBox, QHBoxLayout, QLabel, QLineEdit, QListWidget, QListWidgetItem,
    QComboBox, QMessageBox, QPushButton, QRadioButton, QTreeWidget,
    QTreeWidgetItem,
    QVBoxLayout, QWidget,
)

import kkscene2 as S
import kkvnsound as V

COL_NAME, COL_GROUP, COL_PRI, COL_TRIG, COL_REL = range(5)
HEADERS = [T("場景 / 音檔"), T("群組名稱"), "100%", T("觸發 (SFX)"), T("存進卡片的相對路徑")]



# ============================================================ 單一場景的挑檔對話框
def check_audio_root(parent, paths, audio_root, settings=None):
    """檢查音檔是不是都在音頻根目錄底下。

    在 → 相對路徑存得起來，直接過。
    不在 → 相對路徑算不出來，只能整張卡改成絕對路徑或夾帶模式
           （VNGE 的 saveMode 是全域設定，不能一部分相對一部分絕對），
           所以在這裡問清楚，順便把選到的模式寫回 settings。

    回傳要收下的清單（可能是空的）。
    """
    if not audio_root:
        return list(paths)
    out = [p for p in paths if V.is_under(p, audio_root)]
    bad = [p for p in paths if p not in out]
    if not bad:
        return out
    mode = (settings or {}).get("audio_mode", V.MODE_REL)
    if mode != V.MODE_REL:            # already not relpath -> 沒有問題
        return list(paths)
    names = "\n".join("  " + os.path.basename(p) for p in bad[:6])
    if len(bad) > 6:
        names += T("\n  …等共 {0} 個").format(len(bad))
    box = QMessageBox(parent)
    box.setIcon(QMessageBox.Icon.Warning)
    box.setWindowTitle(T("這些音檔不在音頻根目錄底下"))
    box.setText(T("下面的音檔不在\n{0}\n底下：\n\n{1}").format(audio_root, names))
    box.setInformativeText(
        T("算不出相對路徑。VNGE 的存檔模式是**整張卡共用**的，所以要嘛跳過這些檔案，要嘛把整張卡改成：\n\n· 絕對路徑 —— 卡片存完整路徑，換一台電腦或搬了資料夾就讀不到\n· 夾帶在卡片裡 —— 音檔本體塞進卡片，走到哪都能播，但卡片會膨脹約音檔大小的 1.5 倍"))
    skip = box.addButton(T("跳過這些"), QMessageBox.ButtonRole.AcceptRole)
    b_abs = box.addButton(T("改用絕對路徑"), QMessageBox.ButtonRole.ActionRole)
    b_dat = box.addButton(T("夾帶在卡片裡"), QMessageBox.ButtonRole.ActionRole)
    box.addButton(T("全部取消"), QMessageBox.ButtonRole.RejectRole)
    box.exec()
    hit = box.clickedButton()
    if hit is skip:
        return out
    if hit in (b_abs, b_dat) and settings is not None:
        settings["audio_mode"] = V.MODE_ABS if hit is b_abs else V.MODE_DATA
        return list(paths)
    return []


class AudioPickDialog(QDialog):
    """為單一場景挑音檔：清單 + 100% 單選 + 加 / 移除 / 自動配對 / 清空。"""

    def __init__(self, parent, scene_name, files, primary,
                 audio_root="", audio_dir="", rel_prefix=V.DEFAULT_REL_PREFIX,
                 settings=None):
        super().__init__(parent)
        self.setWindowTitle(T("音頻 — {0}").format(scene_name))
        self.resize(760, 380)
        self.scene_name = scene_name
        self.audio_root = audio_root
        self.audio_dir = audio_dir
        self.rel_prefix = rel_prefix
        self.settings = settings if settings is not None else {}
        self.files = list(files or [])
        self.primary = primary
        self._radios = []

        lay = QVBoxLayout(self)
        lay.addWidget(QLabel(
            T("場景「{0}」的音頻。左邊的圓點＝這一段預設播哪一個（100%），其餘存成 0%，\n在 Studio 裡可以即時切換。").format(scene_name)))

        self.list = QListWidget()
        self.list.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
        row = QHBoxLayout()
        row.addWidget(self.list, 1)
        col = QVBoxLayout()
        for text, slot in ((T("自動配對"), self.auto),
                           (T("加音檔…"), self.add),
                           (T("移除選取"), self.remove),
                           (T("清空"), self.clear)):
            b = QPushButton(text)
            b.clicked.connect(slot)
            col.addWidget(b)
        col.addStretch(1)
        row.addLayout(col)
        lay.addLayout(row, 1)

        self.note = QLabel("")
        self.note.setStyleSheet("color:#777;")
        self.note.setWordWrap(True)
        lay.addWidget(self.note)

        bb = QDialogButtonBox(QDialogButtonBox.StandardButton.Ok
                              | QDialogButtonBox.StandardButton.Cancel)
        bb.accepted.connect(self.accept)
        bb.rejected.connect(self.reject)
        lay.addWidget(bb)
        self.rebuild()

    def rebuild(self):
        self.list.clear()
        self._radios = []
        if self.primary not in self.files:
            self.primary = V.pick_primary(self.files)
        for p in self.files:
            it = QListWidgetItem()
            it.setData(Qt.ItemDataRole.UserRole, p)
            w = QWidget()
            h = QHBoxLayout(w)
            h.setContentsMargins(4, 2, 4, 2)
            rb = QRadioButton()
            rb.setAutoExclusive(False)
            rb.setChecked(p == self.primary)
            rb.setFixedWidth(22)
            rb.clicked.connect(lambda _c, pp=p: self._set_primary(pp))
            h.addWidget(rb)
            self._radios.append((rb, p))
            lab = QLabel(os.path.basename(p))
            lab.setToolTip(p + "\n→ " + V.rel_path_of(p, self.audio_root,
                                                      self.rel_prefix))
            h.addWidget(lab, 1)
            pct = QLabel("100%" if p == self.primary else "0%")
            pct.setStyleSheet("color:#2d7d46;font-weight:bold;"
                              if p == self.primary else "color:#999;")
            pct.setFixedWidth(46)
            h.addWidget(pct)
            it.setSizeHint(w.sizeHint())
            self.list.addItem(it)
            self.list.setItemWidget(it, w)
        self.note.setText(
            T("{0} 個音檔").format(len(self.files))
            + (T("　群組名稱＝「{0}」").format(self.scene_name) if self.files else "")
            + (T("　（沒有音檔＝這一段不配音）") if not self.files else ""))

    def _set_primary(self, p):
        self.primary = p
        self.rebuild()

    def _start_dir(self):
        return self.audio_dir or self.audio_root or ""

    def add(self):
        paths, _ = QFileDialog.getOpenFileNames(
            self, T("選擇音檔"), self._start_dir(),
            T("音檔 (*.wav *.mp3 *.ogg *.aif *.aiff)"))
        if not paths:
            return
        paths = check_audio_root(self, paths, self.audio_root, self.settings)
        for p in paths:
            if p not in self.files:
                self.files.append(p)
        self.rebuild()

    def auto(self):
        folder = self.audio_dir
        if not folder:
            folder = V.find_audio_folder(self.audio_root, [self.scene_name]) or ""
        if not folder or not os.path.isdir(folder):
            QMessageBox.information(
                self, T("找不到音檔資料夾"),
                T("在音頻根目錄底下找不到跟「{0}」對得上的資料夾。\n用「加音檔…」自己選，或到設定頁確認音頻根目錄。").format(self.scene_name))
            return
        slot = {"index": 1, "name": self.scene_name}
        assign, _left = V.auto_match([slot], V.scan_audio(folder))
        got = assign.get(1) or []
        if not got:
            QMessageBox.information(self, T("沒配到"),
                                    T("{0}\n裡面沒有對得上「{1}」的音檔。").format(folder, self.scene_name))
            return
        self.files = got
        self.primary = None
        self.rebuild()

    def remove(self):
        drop = {i.data(Qt.ItemDataRole.UserRole) for i in self.list.selectedItems()}
        self.files = [f for f in self.files if f not in drop]
        self.rebuild()

    def clear(self):
        self.files = []
        self.primary = None
        self.rebuild()


def pick_audio(parent, scene_name, files, primary, settings):
    """開對話框挑音檔；按確定回 (files, primary)，取消回 None。"""
    d = AudioPickDialog(parent, scene_name, files, primary,
                        settings.get("audio_root", ""),
                        settings.get("audio_dir", ""),
                        settings.get("audio_rel_prefix", V.DEFAULT_REL_PREFIX),
                        settings)
    if d.exec() != QDialog.DialogCode.Accepted:
        return None
    return d.files, d.primary


# ============================================================ 背景執行緒
class AudioWorker(QObject):
    """讀卡與寫卡都在這裡跑（1 GB 的卡不能放主執行緒）。"""

    loaded = pyqtSignal(str, list)
    slots_updated = pyqtSignal(list)
    written = pyqtSignal(bool, str)
    log_line = pyqtSignal(str)

    def __init__(self):
        super().__init__()
        self.q: queue.Queue = queue.Queue()
        self.running = True
        self.scene = None
        self.scene_path = None

    def submit(self, job):
        self.q.put(job)

    def stop(self):
        self.running = False
        self.q.put(None)

    def release(self):
        self.scene = None
        self.scene_path = None

    def loop(self):
        while self.running:
            job = self.q.get()
            if job is None:
                break
            try:
                getattr(self, "_" + job[0])(*job[1:])
            except Exception as e:                          # noqa: BLE001
                self.log_line.emit(traceback.format_exc())
                if job[0] in ("write", "clear"):
                    self.written.emit(False, f"{type(e).__name__}: {e}")
                else:
                    self.loaded.emit("", [])
                    self.log_line.emit(T("[讀卡失敗] {0}: {1}").format(type(e).__name__, e))

    def _load(self, path):
        self.log_line.emit(T("讀卡：{0}").format(Path(path).name))
        self.scene = S.Scene(path)
        self.scene_path = path
        warn = []
        slots = V.scene_slots(self.scene, warn, name=Path(path).stem)
        data = V.read_sound(self.scene)
        if not V.is_prepped(self.scene):
            self.log_line.emit(T("  這張卡沒整理過（根節點沒有 (CAM)），當成一整段處理"))
        for w in warn:
            self.log_line.emit(T("[注意] ") + w)
        self.log_line.emit(
            T("  {0} 段場景、卡片裡本來有 {1} 個音頻").format(len(slots), len(data.get('Sound') or {})))
        self.loaded.emit(path, slots)

    def _addsfx(self, path, indices):
        if self.scene is None or self.scene_path != path:
            self.log_line.emit(T("重新讀卡：{0}").format(Path(path).name))
            self.scene = S.Scene(path)
            self.scene_path = path
        warn = []
        slots = V.scene_slots(self.scene, name=Path(path).stem)
        total = self.scene.duration() or 0.0
        done = 0
        for sl in slots:
            if sl["index"] not in indices:
                continue
            dk = V.add_sfx_folder(self.scene, sl, warn=warn)
            if dk is None:
                continue
            done += 1
            self.log_line.emit(
                T("  [{0}] {1} 補上 (SFX) dicKey={2}，啟用軌道 {3}").format(sl['index'], sl['name'], dk, V.sfx_points_text(sl, total)))
        for w in warn:
            self.log_line.emit(T("[注意] ") + w)
        self.log_line.emit(T("補了 {0} 個 (SFX)（還沒寫檔，按「寫入音頻」才會存）").format(done))
        self.slots_updated.emit(slots)

    def _clear(self, path, out):
        if self.scene is None or self.scene_path != path:
            self.log_line.emit(T("重新讀卡：{0}").format(Path(path).name))
            self.scene = S.Scene(path)
            self.scene_path = path
        res = V.clear_audio(self.scene)
        self.log_line.emit(T("寫檔中… {0}").format(out))
        V.save_atomic(self.scene, out)
        self.scene_path = out
        self.written.emit(
            True, T("已清除 {0} 個音頻、{1} 個群組 → {2}").format(res['sounds'], res['groups'], out))

    def _write(self, path, plan, out, audio_root, rel_prefix, clear, mode):
        if self.scene is None or self.scene_path != path:
            self.log_line.emit(T("重新讀卡：{0}").format(Path(path).name))
            self.scene = S.Scene(path)
            self.scene_path = path
        warn = []
        res = V.apply_audio(self.scene, plan, audio_root=audio_root,
                            rel_prefix=rel_prefix, clear=clear, warn=warn,
                            mode=mode)
        for w in warn:
            self.log_line.emit(T("[注意] ") + w)
        self.log_line.emit(T("寫檔中… {0}").format(out))
        V.save_atomic(self.scene, out)
        # 存過之後這份記憶體已經跟檔案一致，留著給下一次改
        self.scene_path = out
        size = os.path.getsize(out)
        self.written.emit(
            True, T("完成：{0}（{1}、音頻 {2} 個、群組 {3} 個、{4:,} bytes）").format(out, T(V.MODE_LABEL.get(res['mode'], res['mode'])), res['sounds'], res['groups'], size))


# ============================================================ 分頁
class AudioTab(QWidget):
    log_line = pyqtSignal(str)
    status = pyqtSignal(str)

    def __init__(self, settings: dict):
        super().__init__()
        self.settings = settings
        self.slots: list[dict] = []
        self.card = ""
        self.dirty = False          # 補過 (SFX) 但還沒寫檔
        self._total = 0.0

        lay = QVBoxLayout(self)

        # -- 卡片 --
        row = QHBoxLayout()
        row.addWidget(QLabel(T("場景卡")))
        self.edit_card = QLineEdit()
        self.edit_card.setPlaceholderText(T("通常是合併好的那張（也可以拖進下面的清單）"))
        row.addWidget(self.edit_card, 1)
        b = QPushButton(T("瀏覽…"))
        b.setFixedWidth(70)
        b.clicked.connect(self.pick_card)
        row.addWidget(b)
        self.btn_load = QPushButton(T("讀取場景"))
        self.btn_load.clicked.connect(self.load_card)
        row.addWidget(self.btn_load)
        lay.addLayout(row)

        # -- 樹 --
        self.tree = QTreeWidget()
        self.tree.setColumnCount(len(HEADERS))
        self.tree.setHeaderLabels(HEADERS)
        self.tree.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
        self.tree.setColumnWidth(COL_NAME, 300)
        self.tree.setColumnWidth(COL_GROUP, 170)
        self.tree.setColumnWidth(COL_PRI, 80)
        self.tree.setColumnWidth(COL_TRIG, 150)
        self.tree.setAcceptDrops(True)
        self.tree.itemChanged.connect(self.on_item_changed)
        self.tree.dragEnterEvent = self._drag
        self.tree.dragMoveEvent = self._drag
        self.tree.dropEvent = self._drop

        btns = QVBoxLayout()
        for text, slot, tip in (
                (T("自動配對"), self.auto_match, T("依包裝資料夾名稱去音頻資料夾找對應的音檔")),
                (T("加音檔…"), self.add_files, T("把選到的音檔加進目前選取的場景")),
                (T("移除選取"), self.remove_selected, ""),
                (T("清空音檔"), self.clear_files, T("只清畫面上的清單，還沒寫進卡片")),
                (T("補 (SFX) 資料夾"), self.add_sfx,
                 T("為選到的場景（沒選就是全部缺的）建一個 (SFX)，\n並照該段的起訖時間把物件啟用軌道一起寫好")),
                (T("清除卡片音頻"), self.wipe_card,
                 T("把卡片裡已經存在的音頻與群組全部刪掉並寫檔")),
                (T("釋放記憶體"), self.release, T("放掉留在記憶體裡的那張卡"))):
            x = QPushButton(text)
            if tip:
                x.setToolTip(tip)
            x.clicked.connect(slot)
            btns.addWidget(x)
        btns.addStretch(1)
        hint = QLabel(T("每段場景一個群組\n群組名稱可直接改\n100% 那欄每段只能勾一個"))
        hint.setStyleSheet("color:#777;")
        btns.addWidget(hint)

        mid = QHBoxLayout()
        mid.addWidget(self.tree, 1)
        mid.addLayout(btns)
        lay.addLayout(mid, 1)

        # -- 路徑設定 --
        g = QGroupBox(T("音頻路徑"))
        gv = QVBoxLayout(g)
        r1 = QHBoxLayout()
        r1.addWidget(QLabel(T("音頻根目錄")))
        self.edit_root = QLineEdit(settings.get("audio_root", ""))
        self.edit_root.setPlaceholderText(T("例如 D:\\Koikatu\\UserData\\audio"))
        self.edit_root.setToolTip(T("相對路徑就是從這裡往下算。\n音檔實際放在別的磁碟（例如 F:\\Koikatu_Audio）也沒關係，\n只要下面的前綴是遊戲讀得到的位置就好。"))
        r1.addWidget(self.edit_root, 1)
        b2 = QPushButton(T("瀏覽…"))
        b2.setFixedWidth(70)
        b2.clicked.connect(self.pick_root)
        r1.addWidget(b2)
        self.btn_root_def = QPushButton(T("回預設"))
        self.btn_root_def.setFixedWidth(66)
        self.btn_root_def.clicked.connect(self.reset_root)
        self.edit_root.textChanged.connect(self._refresh_defaults)
        r1.addWidget(self.btn_root_def)
        r1.addWidget(QLabel(T("相對路徑前綴")))
        self.edit_prefix = QLineEdit(settings.get("audio_rel_prefix",
                                                  V.DEFAULT_REL_PREFIX))
        self.edit_prefix.setFixedWidth(150)
        self.edit_prefix.setToolTip(T("存進卡片的路徑會是「前綴 + 檔案相對根目錄的位置」。"))
        r1.addWidget(self.edit_prefix)
        self.btn_pre_def = QPushButton(T("回預設"))
        self.btn_pre_def.setFixedWidth(66)
        self.btn_pre_def.setToolTip(T("換回 ") + V.DEFAULT_REL_PREFIX)
        self.btn_pre_def.clicked.connect(
            lambda: self.edit_prefix.setText(V.DEFAULT_REL_PREFIX))
        self.edit_prefix.textChanged.connect(self._refresh_defaults)
        r1.addWidget(self.btn_pre_def)
        gv.addLayout(r1)

        r2 = QHBoxLayout()
        r2.addWidget(QLabel(T("音檔資料夾")))
        self.edit_dir = QLineEdit()
        self.edit_dir.setPlaceholderText(T("留空＝自動從根目錄底下找同名資料夾"))
        r2.addWidget(self.edit_dir, 1)
        b3 = QPushButton(T("瀏覽…"))
        b3.setFixedWidth(70)
        b3.clicked.connect(self.pick_dir)
        r2.addWidget(b3)
        self.chk_clear = QCheckBox(T("寫入前清掉卡片原有的音頻"))
        self.chk_clear.setChecked(True)
        r2.addWidget(self.chk_clear)
        gv.addLayout(r2)

        r2b = QHBoxLayout()
        r2b.addWidget(QLabel(T("存檔模式")))
        self.combo_mode = QComboBox()
        for m in V.MODES:
            self.combo_mode.addItem(T(V.MODE_LABEL[m]), m)
        cur = settings.get("audio_mode", V.MODE_REL)
        self.combo_mode.setCurrentIndex(
            list(V.MODES).index(cur) if cur in V.MODES else 0)
        self.combo_mode.setToolTip(
            T("VNGE 的 saveMode 是整張卡共用的，不能一部分相對一部分絕對。\n相對路徑：最省、最好搬，但音檔必須在音頻根目錄底下。\n絕對路徑：換電腦或搬資料夾就讀不到。\n夾帶：音檔塞進卡片，走到哪都能播，卡片會膨脹約音檔大小的 1.5 倍。"))
        self.combo_mode.currentIndexChanged.connect(self._mode_changed)
        r2b.addWidget(self.combo_mode)
        self.lab_mode = QLabel("")
        self.lab_mode.setStyleSheet("color:#777;")
        r2b.addWidget(self.lab_mode, 1)
        gv.addLayout(r2b)
        lay.addWidget(g)

        # -- 輸出 --
        r3 = QHBoxLayout()
        r3.addWidget(QLabel(T("輸出")))
        self.edit_out = QLineEdit()
        self.edit_out.setPlaceholderText(T("留空＝原地覆寫"))
        r3.addWidget(self.edit_out, 1)
        b4 = QPushButton(T("另存…"))
        b4.setFixedWidth(70)
        b4.clicked.connect(self.pick_out)
        r3.addWidget(b4)
        self.btn_write = QPushButton(T("寫入音頻"))
        self.btn_write.setMinimumHeight(34)
        self.btn_write.setMinimumWidth(130)
        self.btn_write.clicked.connect(self.write)
        r3.addWidget(self.btn_write)
        lay.addLayout(r3)

        # -- 執行緒 --
        self.worker = AudioWorker()
        self.thread = QThread(self)
        self.worker.moveToThread(self.thread)
        self.thread.started.connect(self.worker.loop)
        self.worker.log_line.connect(self.log_line)
        self.worker.loaded.connect(self.on_loaded)
        self.worker.slots_updated.connect(self.on_slots_updated)
        self.worker.written.connect(self.on_written)
        self.thread.start()
        self._mode_changed()
        self._refresh_defaults()

    # ---- 拖放 ----
    def _drag(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()

    def _drop(self, e):
        paths = [u.toLocalFile() for u in e.mimeData().urls()]
        cards = [p for p in paths if p.lower().endswith(".png")]
        audio = [p for p in paths if p.lower().endswith(V.AUDIO_EXT)]
        if cards:
            self.edit_card.setText(cards[0])
            self.load_card()
        if audio:
            self._add_to_current(audio)
        e.acceptProposedAction()

    # ---- 選檔 ----
    def pick_card(self):
        p, _ = QFileDialog.getOpenFileName(
            self, T("選擇場景卡"),
            self.edit_card.text() or self.settings.get("output_dir", ""),
            T("場景卡 (*.png)"))
        if p:
            self.edit_card.setText(p)

    def pick_root(self):
        p = QFileDialog.getExistingDirectory(self, T("音頻根目錄"), self.edit_root.text())
        if p:
            self.edit_root.setText(p)

    def pick_dir(self):
        p = QFileDialog.getExistingDirectory(
            self, T("音檔資料夾"), self.edit_dir.text() or self.edit_root.text())
        if p:
            self.edit_dir.setText(p)

    def pick_out(self):
        start = self.edit_out.text() or self.edit_card.text()
        p, _ = QFileDialog.getSaveFileName(self, T("另存場景卡"), start, T("場景卡 (*.png)"))
        if p:
            self.edit_out.setText(p)

    # ---- 讀卡 ----
    def load_card(self):
        p = self.edit_card.text().strip()
        if not p or not os.path.isfile(p):
            QMessageBox.warning(self, T("讀不到"), T("請先選一張場景卡"))
            return
        self.btn_load.setEnabled(False)
        self.status.emit(T("讀卡中…（大卡要幾分鐘）"))
        self.worker.submit(("load", p))

    def on_loaded(self, path, slots):
        self.btn_load.setEnabled(True)
        if not path:
            self.status.emit(T("讀卡失敗"))
            return
        self.card = path
        self.slots = slots
        self.dirty = False
        self._total = max([s.get("end") or 0.0 for s in slots] or [0.0])
        self.rebuild()
        self.status.emit(T("讀到 {0} 段場景").format(len(slots)))
        if self.edit_root.text().strip():
            self.auto_match()

    # ---- 樹 ----
    def rebuild(self):
        self.tree.blockSignals(True)
        self.tree.clear()
        for s in self.slots:
            it = QTreeWidgetItem([f"[{s['index']}] {s['name']}",
                                  s.get("_group") or s["name"], "",
                                  f"{s['sfx_name']}  dicKey={s['sfx']}"
                                  + (T("  ← 新補的") if s.get("sfx_added") else ""),
                                  ""])
            it.setFlags(it.flags() | Qt.ItemFlag.ItemIsEditable)
            it.setData(0, Qt.ItemDataRole.UserRole, s["index"])
            if s.get("start") is not None:
                it.setToolTip(COL_NAME,
                              T("{0:.2f} ~ {1:.2f} 秒").format(s['start'], s['end']))
            self.tree.addTopLevelItem(it)
            for f in (s.get("_files") or []):
                self._make_child(it, s, f)
            it.setExpanded(True)
        self.tree.blockSignals(False)
        self._refresh_primary()

    def _make_child(self, parent, slot, path):
        c = QTreeWidgetItem([os.path.basename(path), "", "", "",
                             V.rel_path_of(path, self.edit_root.text().strip(),
                                           self.edit_prefix.text().strip())])
        c.setData(0, Qt.ItemDataRole.UserRole, path)
        c.setToolTip(COL_NAME, path)
        c.setFlags(c.flags() | Qt.ItemFlag.ItemIsUserCheckable)
        c.setCheckState(COL_PRI, Qt.CheckState.Unchecked)
        if not os.path.isfile(path):
            c.setForeground(COL_NAME, Qt.GlobalColor.red)
            c.setToolTip(COL_NAME, path + T("\n（檔案不存在）"))
        parent.addChild(c)
        return c

    def _slot_by_index(self, idx):
        for s in self.slots:
            if s["index"] == idx:
                return s
        return None

    def _refresh_primary(self):
        """每段沒有勾 100% 的話，自動依偏好標籤勾一個。"""
        self.tree.blockSignals(True)
        for i in range(self.tree.topLevelItemCount()):
            top = self.tree.topLevelItem(i)
            kids = [top.child(j) for j in range(top.childCount())]
            if not kids:
                continue
            checked = [k for k in kids
                       if k.checkState(COL_PRI) == Qt.CheckState.Checked]
            if not checked:
                want = V.pick_primary([k.data(0, Qt.ItemDataRole.UserRole)
                                       for k in kids])
                for k in kids:
                    k.setCheckState(
                        COL_PRI,
                        Qt.CheckState.Checked
                        if k.data(0, Qt.ItemDataRole.UserRole) == want
                        else Qt.CheckState.Unchecked)
            for k in kids:
                on = k.checkState(COL_PRI) == Qt.CheckState.Checked
                k.setText(COL_PRI, "100%" if on else "0%")
        self.tree.blockSignals(False)

    def on_item_changed(self, item, column):
        parent = item.parent()
        if parent is None:
            s = self._slot_by_index(item.data(0, Qt.ItemDataRole.UserRole))
            if s is None:
                return
            if column == COL_GROUP:
                s["_group"] = item.text(COL_GROUP).strip() or s["name"]
            elif column == COL_NAME:          # 場景那一欄不給改，改了就還原
                want = f"[{s['index']}] {s['name']}"
                if item.text(COL_NAME) != want:
                    self.tree.blockSignals(True)
                    item.setText(COL_NAME, want)
                    self.tree.blockSignals(False)
            return
        if column != COL_PRI:
            return
        self.tree.blockSignals(True)
        if item.checkState(COL_PRI) == Qt.CheckState.Checked:
            for j in range(parent.childCount()):
                k = parent.child(j)
                if k is not item:
                    k.setCheckState(COL_PRI, Qt.CheckState.Unchecked)
        for j in range(parent.childCount()):
            k = parent.child(j)
            k.setText(COL_PRI,
                      "100%" if k.checkState(COL_PRI) == Qt.CheckState.Checked
                      else "0%")
        self.tree.blockSignals(False)

    # ---- 操作 ----
    def _collect(self):
        """把樹上的內容收回 self.slots。"""
        for i in range(self.tree.topLevelItemCount()):
            top = self.tree.topLevelItem(i)
            s = self._slot_by_index(top.data(0, Qt.ItemDataRole.UserRole))
            if s is None:
                continue
            s["_group"] = top.text(COL_GROUP).strip() or s["name"]
            files, pri = [], None
            for j in range(top.childCount()):
                k = top.child(j)
                p = k.data(0, Qt.ItemDataRole.UserRole)
                files.append(p)
                if k.checkState(COL_PRI) == Qt.CheckState.Checked:
                    pri = p
            s["_files"] = files
            s["_primary"] = pri

    def auto_match(self):
        if not self.slots:
            QMessageBox.warning(self, T("還沒讀卡"), T("請先讀取一張場景卡"))
            return
        root = self.edit_root.text().strip()
        folder = self.edit_dir.text().strip()
        if not folder:
            folder = V.find_audio_folder(root, [s["name"] for s in self.slots]) or ""
            if folder:
                self.edit_dir.setText(folder)
        if not folder or not os.path.isdir(folder):
            QMessageBox.warning(self, T("找不到音檔資料夾"),
                                T("根目錄底下找不到跟包裝資料夾同名的資料夾，\n請手動指定「音檔資料夾」。"))
            return
        files = V.scan_audio(folder)
        assign, left = V.auto_match(self.slots, files)
        for s in self.slots:
            s["_files"] = assign.get(s["index"], [])
            s["_primary"] = None
        self.rebuild()
        self.log_line.emit(T("自動配對：{0}（{1} 個音檔）").format(folder, len(files)))
        for s in self.slots:
            self.log_line.emit(
                T("  [{0}] {1} ← {2} 個").format(s['index'], s['name'], len(s.get('_files') or [])))
        for f in left:
            self.log_line.emit(T("  [配不出去] {0}").format(os.path.basename(f)))
        if left:
            self.status.emit(T("有 {0} 個音檔配不出去，請看紀錄").format(len(left)))

    def _current_top(self):
        it = self.tree.currentItem()
        if it is None:
            return None
        return it if it.parent() is None else it.parent()

    def _add_to_current(self, paths):
        top = self._current_top()
        if top is None:
            QMessageBox.information(self, T("先選一段場景"), T("請先點一下要加音檔的那段場景"))
            return
        self._collect()
        s = self._slot_by_index(top.data(0, Qt.ItemDataRole.UserRole))
        if s is None:
            return
        s["_files"] = (s.get("_files") or []) + [p for p in paths
                                                 if p not in (s.get("_files") or [])]
        self.rebuild()

    def add_files(self):
        start = self.edit_dir.text().strip() or self.edit_root.text().strip()
        paths, _ = QFileDialog.getOpenFileNames(
            self, T("選擇音檔"), start,
            T("音檔 (*.wav *.mp3 *.ogg *.aif *.aiff)"))
        if paths:
            self._add_to_current(paths)

    def remove_selected(self):
        self._collect()
        drop = {}
        for it in self.tree.selectedItems():
            if it.parent() is None:
                continue
            idx = it.parent().data(0, Qt.ItemDataRole.UserRole)
            drop.setdefault(idx, set()).add(it.data(0, Qt.ItemDataRole.UserRole))
        for s in self.slots:
            if s["index"] in drop:
                s["_files"] = [f for f in (s.get("_files") or [])
                               if f not in drop[s["index"]]]
        self.rebuild()

    def clear_files(self):
        for s in self.slots:
            s["_files"] = []
            s["_primary"] = None
        self.rebuild()

    def _default_root(self):
        """遊戲根目錄底下的 UserData\\audio。"""
        root = (self.settings.get("game_root") or "").strip()
        return str(Path(root) / "UserData" / "audio") if root else ""

    def reset_root(self):
        d = self._default_root()
        if d:
            self.edit_root.setText(d)

    def _refresh_defaults(self):
        d = self._default_root()
        self.btn_root_def.setEnabled(bool(d) and self.edit_root.text().strip() != d)
        self.btn_root_def.setToolTip(
            (T("已經是預設值：\n") + d) if d and self.edit_root.text().strip() == d
            else (T("換回遊戲根目錄底下的位置：\n") + d) if d
            else T("設定頁還沒填遊戲根目錄"))
        self.btn_pre_def.setEnabled(
            self.edit_prefix.text().strip() != V.DEFAULT_REL_PREFIX)

    def sync_mode(self):
        """別的地方（例如合併分頁的挑檔視窗）改了模式，把下拉拉到一致。"""
        m = self.settings.get("audio_mode", V.MODE_REL)
        if m in V.MODES and self.combo_mode.currentData() != m:
            self.combo_mode.setCurrentIndex(list(V.MODES).index(m))

    def _mode_changed(self):
        m = self.combo_mode.currentData()
        self.settings["audio_mode"] = m
        self.lab_mode.setText(
            T("音檔必須在音頻根目錄底下") if m == V.MODE_REL else
            T("換電腦或搬資料夾就會讀不到") if m == V.MODE_ABS else
            T("卡片會膨脹約音檔大小的 1.5 倍"))

    def add_sfx(self):
        """為場景補 (SFX) 資料夾（含啟用軌道）。"""
        if not self.card or not self.slots:
            QMessageBox.warning(self, T("還沒讀卡"), T("請先讀取一張場景卡"))
            return
        top = self._current_top()
        if top is not None:
            idx = [top.data(0, Qt.ItemDataRole.UserRole)]
            targets = [s for s in self.slots if s["index"] in idx]
        else:
            targets = [s for s in self.slots if not s.get("has_sfx", True)]
            if not targets:
                targets = [s for s in self.slots
                           if "(SFX)" not in (s.get("sfx_name") or "")]
        if not targets:
            QMessageBox.information(self, T("不用補"),
                                    T("每一段都已經有 (SFX) 了。\n要為特定一段再加一個的話，先點選那一段。"))
            return
        lines = "\n".join(f"  [{s['index']}] {s['name']}"
                           f"（{V.sfx_points_text(s, self._total)}）"
                           for s in targets)
        r = QMessageBox.question(
            self, T("補 (SFX) 資料夾"),
            T("要為下面這幾段建 (SFX) 並寫好物件啟用軌道嗎？\n\n{0}\n\n（只改記憶體裡的卡片，按「寫入音頻」才會存檔）").format(lines))
        if r != QMessageBox.StandardButton.Yes:
            return
        self._collect()
        self.dirty = True
        self.worker.submit(("addsfx", self.card, [s["index"] for s in targets]))

    def on_slots_updated(self, slots):
        """補完 (SFX) 之後，把新的 sfx dicKey 併回目前的清單（音檔不動）。"""
        keep = {s["index"]: s for s in self.slots}
        for ns in slots:
            old = keep.get(ns["index"])
            if old is not None:
                ns["_files"] = old.get("_files") or []
                ns["_primary"] = old.get("_primary")
                ns["_group"] = old.get("_group")
        self.slots = slots
        self.dirty = True
        self.rebuild()
        self.status.emit(T("已補 (SFX)，記得按「寫入音頻」存檔"))

    def wipe_card(self):
        if not self.card:
            QMessageBox.warning(self, T("還沒讀卡"), T("請先讀取一張場景卡"))
            return
        out = self.edit_out.text().strip() or self.card
        r = QMessageBox.question(
            self, T("清除卡片音頻"),
            T("要把卡片裡所有的音頻與群組刪掉嗎？\n\n來源：{0}\n寫到：{1}").format(self.card, out))
        if r != QMessageBox.StandardButton.Yes:
            return
        self.btn_write.setEnabled(False)
        self.status.emit(T("清除中…"))
        self.worker.submit(("clear", self.card, out))

    def release(self):
        self.worker.release()
        self.log_line.emit(T("已放掉記憶體裡的卡片（下次寫入會重新讀一次）"))

    # ---- 寫入 ----
    def write(self):
        if not self.card or not self.slots:
            QMessageBox.warning(self, T("還不能寫"), T("請先讀取一張場景卡"))
            return
        self._collect()
        if not any(s.get("_files") for s in self.slots) and not self.dirty:
            QMessageBox.warning(self, T("還不能寫"), T("每一段都沒有音檔"))
            return
        out = self.edit_out.text().strip() or self.card
        d = os.path.dirname(os.path.abspath(out))
        if not os.path.isdir(d):
            QMessageBox.warning(self, T("還不能寫"), T("輸出資料夾不存在：\n{0}").format(d))
            return
        root = self.edit_root.text().strip()
        if root and not os.path.isdir(root):
            QMessageBox.warning(self, T("還不能寫"), T("音頻根目錄不存在：\n{0}").format(root))
            return
        if os.path.abspath(out) == os.path.abspath(self.card):
            r = QMessageBox.question(
                self, T("原地覆寫"), T("要直接覆寫這張卡嗎？\n{0}").format(self.card))
            if r != QMessageBox.StandardButton.Yes:
                return
        plan = [{"slot": s, "group": s.get("_group") or s["name"],
                 "files": s.get("_files") or [], "primary": s.get("_primary")}
                for s in self.slots]
        self.settings["audio_root"] = self.edit_root.text().strip()
        self.settings["audio_rel_prefix"] = self.edit_prefix.text().strip()
        self.settings["audio_dir"] = self.edit_dir.text().strip()
        self.btn_write.setEnabled(False)
        self.status.emit(T("寫入中…（大卡要幾分鐘）"))
        self.log_line.emit("=" * 60)
        self.log_line.emit(T("寫入音頻 → {0}").format(out))
        self.worker.submit(("write", self.card, plan, out,
                            self.edit_root.text().strip(),
                            self.edit_prefix.text().strip(),
                            self.chk_clear.isChecked(),
                            self.combo_mode.currentData()))

    def on_written(self, ok, msg):
        self.btn_write.setEnabled(True)
        if ok:
            self.dirty = False
        self.log_line.emit(("✔ " if ok else "✘ ") + msg)
        self.status.emit(msg if ok else T("失敗：") + msg)
        if not ok:
            QMessageBox.critical(self, T("寫入失敗"), msg)
        else:
            out = self.edit_out.text().strip()
            if out:
                self.edit_card.setText(out)
                self.card = out

    def shutdown(self):
        self.worker.stop()
        self.thread.quit()
        self.thread.wait(2000)
