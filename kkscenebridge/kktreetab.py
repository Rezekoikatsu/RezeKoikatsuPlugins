#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""kktreetab — kkscenebridge 的「整理」分頁

不開遊戲，直接整理場景卡的物件樹：分資料夾、搬位置、改名字、隱藏物件。

為什麼敢這樣做（HANDOFF3 §3.1 的規則）：

    RANK = 全部節點的 dicKey 排序後的名次。timeline 的 objectIndex、
    NodesConstraints 的 parent/childObjectIndex 用的都是 RANK。

    · 重新掛父子關係 → RANK 是「排序」不是「樹序」，所以完全不受影響
    · 新增節點 dicKey 取 max+1 → 排在最後 → 既有 RANK 全部不變
    · 改名字 / 改 visible / 改 treeState → 根本不碰 RANK
    · 刪節點 → 後面的 RANK 全部往前縮，所有參照都要重編號

前三項本來就安全。刪除以前不做，是因為「所有參照都要重編號」這件事沒人做；
現在 kkscenemerge.cleanup_deleted 就是在做這件事（kkprune.py 靠它砍了幾十輪），
所以這一頁也開放刪除，走同一條路：

    timeline 軌道、NodesConstraints、kkpe、MaterialEditor、UAR、
    LightSettings、itemlayeredit 的參照一起清乾淨，RANK 重算。

刪除跟其他編輯不一樣的地方：**它會連帶砍掉指向那些節點的 timeline 軌道**。
所以按下去之前會先算出「這一刀會砍掉幾條軌道、幾個 NC」讓你確認，
而且刪除只是先標記，真正動手是在存檔的時候 —— 按錯了重讀就回來了。
"""
from __future__ import annotations
from kksblang import T

import os
import shutil
import sys
import time
import traceback

from PyQt6.QtCore import (QMetaObject, QObject, Qt, QThread,
                          pyqtSignal, pyqtSlot)
from PyQt6.QtGui import QBrush, QColor, QFont
from PyQt6.QtWidgets import (
    QAbstractItemView, QCheckBox, QDoubleSpinBox, QFileDialog, QGridLayout,
    QGroupBox, QHBoxLayout, QHeaderView, QLabel, QLineEdit, QMessageBox,
    QPushButton, QTreeWidget, QTreeWidgetItem, QVBoxLayout, QWidget,
)

RED = "#c0392b"
GREY = "#7f8c8d"
FOLDER_TYPE = 3
NODE_ROLE = Qt.ItemDataRole.UserRole
DEAD_ROLE = Qt.ItemDataRole.UserRole + 1     # 這一項被標記為刪除
SLOT_ROLE = Qt.ItemDataRole.UserRole + 2     # 角色接點的假列，存的是接點編號
LABEL_ROLE = Qt.ItemDataRole.UserRole + 3    # 建樹時顯示的名稱（判斷使用者有沒有改名）

# 節點種類就是 type 欄位（Studio 的 ObjectInfo 種類）。
# 以前用「有沒有 bones」猜角色 —— 但道具也有 bones（FK 骨架），所以道具被標成了角色。
TYPE_NAME = {0: T("角色"), 1: T("道具"), 2: T("燈光"), 3: T("資料夾"), 4: T("路線"), 5: T("相機")}


def kind_of(node):
    t = node.get("type")
    return TYPE_NAME.get(t, "type %s" % t)


def vis_of(node):
    """樹狀圖那個勾。

    實測（拿「遊戲裡手動關掉後另存的卡」跟原卡比對）：Studio 會把
    **enable 和 visible 兩個欄位一起翻**，不是只有 visible。
    只改 visible 的話存回去進遊戲還是打著勾 —— 這就是第一版的 bug。
    """
    d = node.get("data") or {}
    return bool(d.get("visible", True)) and bool(d.get("enable", True))


def set_vis(node, on):
    """兩個欄位一起設，但只設本來就存在的那些（不同物件型別欄位不一樣）。"""
    d = node.get("data") or {}
    touched = []
    for k in ("visible", "enable"):
        if k in d:
            d[k] = bool(on)
            touched.append(k)
    if not touched:                      # 兩個都沒有就補 visible，至少留個記號
        d["visible"] = bool(on)
        touched.append("visible")
    return touched


def scene_names(scene):
    """卡片裡找得到的顯示名稱：{dicKey: 名稱}。

    Studio 工作面板顯示的名字，大部分不在節點本身（道具的名字在遊戲的道具目錄裡），
    但卡片裡其實有兩份記錄：
      1. TreeNodeNaming（org.njaecha.plugins.treenodenaming）—— Studio 存檔時把每個節點
         當下顯示的名字都存了一份，跟工作面板看到的一樣。最優先。
      2. KKPE 的 <itemInfo name="…">：道具的名字。
    角色另外用角色卡裡的姓名。
    """
    import html
    import re
    import kkscene2 as S
    import kkscenemerge as KM
    from kkmsgpack import map_split, unpack
    out = {}
    try:
        for n, _, _ in S.iter_nodes(scene.objects):
            if n.get("type") == 0:
                try:
                    p = n["data"]["character"]["Parameter"]
                    nm = ("%s %s" % (p["lastname"] or "", p["firstname"] or "")).strip()
                    if nm:
                        out[n["data"]["dicKey"]] = nm
                except Exception:                           # noqa: BLE001
                    pass
    except Exception:                                       # noqa: BLE001
        pass
    try:
        vb = scene.kkex.get("kkpe", "sceneInfo")
        x = unpack(vb) if vb is not None else None
        if isinstance(x, str):
            for m in re.finditer(r'<itemInfo\b[^>]*>', x):
                mi = re.search(r'\bindex="(\d+)"', m.group(0))
                mn = re.search(r'\bname="([^"]*)"', m.group(0))
                if mi and mn and mn.group(1):
                    out[int(mi.group(1))] = html.unescape(mn.group(1))
    except Exception:                                       # noqa: BLE001
        pass
    try:
        vb = scene.kkex.get(KM.TNN, "names")
        if vb is not None:
            _kind, payload = KM._container(vb)
            for k, _kb, v in (map_split(payload) if payload else []):
                v = unpack(v)
                if isinstance(k, int) and isinstance(v, str) and v.strip():
                    out[k] = v
    except Exception:                                       # noqa: BLE001
        pass
    return out


def label_of(node, names=None):
    """給人看的名稱：資料夾用自己的 name；其他先看卡片裡存的顯示名稱（scene_names），
    都沒有才標「（未命名）」+ 編號。"""
    d = node.get("data") or {}
    nm = d.get("name")
    if node.get("type") == FOLDER_TYPE and nm not in (None, ""):
        return str(nm)
    if names:
        v = names.get(d.get("dicKey"))
        if v:
            return str(v)
    if nm not in (None, ""):
        return str(nm)
    for k in ("no", "id", "itemId", "no_"):
        if k in d:
            return T("（未命名）#%s") % d[k]
    return T("（未命名）dicKey %s") % d.get("dicKey")


def is_container(node):
    """能不能把東西丟進去。角色的 child 是接點字典，不能當資料夾用。"""
    return isinstance((node.get("data") or {}).get("child"), list)


def char_slots(node):
    """角色底下的接點：回傳 [(接點編號, [子節點…])]，只留有東西的。

    角色節點的 child 不是 list 而是 **dict**：鍵是接點編號（0~19），
    值是掛在那個接點上的節點清單。遊戲裡看到的「組：頭髮 / 部位：…」
    就是這些接點，使用者常常把位移用的資料夾放在裡面 ——
    以前這一頁整個不展開角色，那些資料夾就完全看不到也改不到。

    空的接點不列出來：一個角色有 20 個接點，大部分是空的，
    全部畫出來只會讓樹變得沒辦法看。
    """
    ch = (node.get("data") or {}).get("child")
    if not isinstance(ch, dict):
        return []
    out = []
    for k in sorted(ch.keys(), key=lambda x: (str(type(x)), x)):
        v = ch[k]
        if isinstance(v, list) and v:
            out.append((k, v))
        elif isinstance(v, dict) and "type" in v:
            out.append((k, [v]))
    return out


# ------------------------------------------------------------------ 讀寫

class SceneWorker(QObject):
    loaded = pyqtSignal(bool, object, str)     # ok, Scene 或 None, 訊息
    saved = pyqtSignal(bool, str)

    def __init__(self):
        super().__init__()
        self._path = ""
        self._scene = None
        self._out = ""
        self._backup = True

    def want_load(self, path):
        self._path = path

    def want_save(self, scene, out, backup):
        self._scene, self._out, self._backup = scene, out, backup

    @pyqtSlot()
    def load(self):
        try:
            here = os.path.dirname(os.path.abspath(__file__))
            if here not in sys.path:
                sys.path.insert(0, here)
            import kkscene2 as S
            t0 = time.time()
            sc = S.Scene(self._path)
            n = sum(1 for _ in S.iter_nodes(sc.objects))
            self.loaded.emit(True, sc, T("讀入 %d 個節點，花了 %.1f 秒") % (n, time.time() - t0))
        except Exception:
            self.loaded.emit(False, None, traceback.format_exc())

    @pyqtSlot()
    def save(self):
        try:
            if self._backup and os.path.isfile(self._out):
                bak = self._out + ".bak"
                if not os.path.isfile(bak):
                    shutil.copy2(self._out, bak)
            t0 = time.time()
            self._scene.save(self._out)
            self.saved.emit(True, T("已寫出 %s（%.1f 秒）") % (self._out, time.time() - t0))
        except Exception:
            self.saved.emit(False, traceback.format_exc())


# ------------------------------------------------------------------ 樹

class Tree(QTreeWidget):
    changed = pyqtSignal()

    def __init__(self):
        super().__init__()
        self.resolve = lambda item: None      # 由 TreeTab 指定
        self.setColumnCount(4)
        self.setHeaderLabels([T("名稱"), T("類型"), T("顯示"), "dicKey"])
        self.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
        self.setDragDropMode(QAbstractItemView.DragDropMode.InternalMove)
        self.setDefaultDropAction(Qt.DropAction.MoveAction)
        self.setEditTriggers(QAbstractItemView.EditTrigger.DoubleClicked
                             | QAbstractItemView.EditTrigger.EditKeyPressed)
        h = self.header()
        h.setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        for c in (1, 2, 3):
            h.setSectionResizeMode(c, QHeaderView.ResizeMode.ResizeToContents)

    @staticmethod
    def slot_of(item):
        """item 往上找（不含自己）最近的「角色接點」假列；不在角色裡回傳 None。"""
        p = item.parent() if item is not None else None
        while p is not None:
            if p.data(0, SLOT_ROLE) is not None:
                return p
            p = p.parent()
        return None

    def slot_id(self, slot):
        """接點假列的身分：(角色的 dicKey, 接點編號)；None 表示不在角色裡。"""
        if slot is None:
            return None
        node = self.resolve(slot.parent())
        return ((node or {}).get("data", {}).get("dicKey"), slot.data(0, SLOT_ROLE))

    def dropEvent(self, e):
        """丟的位置要合法：
          ・丟「到」某一列上：那一列要是資料夾，或角色的接點假列
          ・丟在某一列的上/下：等於放進那一列的上層，上層不能是角色本身
            （角色的 child 是接點字典，只能放進接點裡）
          ・掛在角色接點裡的東西只能在**同一個接點**裡搬（接點裡的資料夾之間），
            不能拖出角色、也不能換到別的接點或別的角色 —— 換掛點等於換了父骨頭，
            位置是相對骨頭的，Studio 也不是這樣用的。反過來，外面的東西也不能拖進接點。
        """
        target = self.itemAt(e.position().toPoint())
        pos = self.dropIndicatorPosition()
        DIP = QAbstractItemView.DropIndicatorPosition
        if target is None or pos == DIP.OnViewport:
            dest = None
        elif pos == DIP.OnItem:
            if target.data(0, SLOT_ROLE) is not None:
                dest = target
            else:
                node = self.resolve(target)
                if not (node and is_container(node)):
                    e.ignore()
                    return
                dest = target
        else:
            dest = target.parent()
        if dest is not None and dest.data(0, SLOT_ROLE) is None:
            dn = self.resolve(dest)
            if not (dn and is_container(dn)):
                e.ignore()                  # 上層是角色本身（或不是資料夾）
                return
        dest_slot = dest if (dest is not None and dest.data(0, SLOT_ROLE) is not None) \
            else self.slot_of(dest) if dest is not None else None
        want = self.slot_id(dest_slot)
        for src in self.selectedItems():
            if self.slot_id(self.slot_of(src)) != want:
                e.ignore()
                return
        super().dropEvent(e)
        self.changed.emit()


class TreeTab(QWidget):
    log_line = pyqtSignal(str)
    status = pyqtSignal(str)

    def __init__(self, settings=None):
        super().__init__()
        self.settings = settings if settings is not None else {}
        self.scene = None
        self._dirty = False
        self._next_key = 0
        # 節點一律放在這張表裡，item 只存索引。
        #
        # 血淚：本來是 item.setData(0, NODE_ROLE, node) 直接塞 dict。
        # PyQt 會把 Python 的 dict 轉成 QVariantMap，**每次 data() 拿回來的都是新的複本**，
        # 所以「套用位置」寫進去的是複本、切走再切回來讀到的是另一個複本 —— 看起來就像沒改到。
        # 資料夾搬家卻正常，因為那是靠樹的結構重建的，不靠節點身分。
        # 整數不會被轉換，所以改存索引。
        self._nodes = []

        root = QVBoxLayout(self)

        tip = QLabel(
            T("・拖曳搬家、新增資料夾、改名、勾顯示、改位置／旋轉／縮放：不影響 timeline 和 NC<br>・<span style='color:%s'>刪除</span>：先標記，存檔時才真的刪，並自動修正相關參照<br>・角色接點裡的東西只能在同一個接點裡拖，不能拖出角色") % RED)
        tip.setWordWrap(True)
        tip.setTextFormat(Qt.TextFormat.RichText)
        tip.setStyleSheet("color:#555;")
        root.addWidget(tip)

        box = QGroupBox(T("場景卡（可以直接拖進來）"))
        bl = QHBoxLayout(box)
        self.ed_card = QLineEdit()
        bl.addWidget(self.ed_card, 1)
        b = QPushButton(T("瀏覽…"))
        b.clicked.connect(self._pick)
        bl.addWidget(b)
        self.btn_load = QPushButton(T("讀取"))
        self.btn_load.clicked.connect(self.load)
        bl.addWidget(self.btn_load)
        root.addWidget(box)

        self.tree = Tree()
        self.tree.resolve = self._node
        self.tree.changed.connect(self._touch)
        self.tree.itemChanged.connect(self._item_changed)
        self.tree.currentItemChanged.connect(lambda *_: self._read_transform())
        root.addWidget(self.tree, 1)

        row = QHBoxLayout()
        for txt, fn, tipx in (
            (T("新增資料夾"), self._new_folder, T("在選取項目的同一層新增；選的是資料夾就放進去")),
            (T("重新命名"), self._rename, T("也可以直接雙擊名稱")),
            (T("上移"), lambda: self._move(-1), ""),
            (T("下移"), lambda: self._move(1), ""),
            (T("升一層"), self._outdent, T("搬到父資料夾的外面")),
            (T("隱藏 / 顯示"), self._toggle_vis, T("等於樹狀圖那個勾")),
            (T("刪除"), self._mark_delete,
             T("標記為刪除（連同底下整棵）。存檔時才真的砍，而且會先告訴你會連帶砍掉幾條 timeline 軌道")),
            (T("取消刪除"), self._unmark_delete, T("把標記為刪除的還原")),
            (T("全部展開"), lambda: self.tree.expandAll(), ""),
            (T("全部收合"), lambda: self.tree.collapseAll(), ""),
        ):
            btn = QPushButton(txt)
            btn.clicked.connect(fn)
            if tipx:
                btn.setToolTip(tipx)
            row.addWidget(btn)
        row.addStretch(1)
        root.addLayout(row)

        row2 = QHBoxLayout()
        self.chk_state = QCheckBox(T("把展開/收合狀態一起存進卡片"))
        self.chk_state.setToolTip(T("勾了的話，下次在遊戲裡開這張卡，資料夾的展開狀態會跟你現在看到的一樣"))
        row2.addWidget(self.chk_state)
        row2.addStretch(1)
        self.chk_backup = QCheckBox(T("備份 .bak"))
        self.chk_backup.setToolTip(T("存回這張卡之前，先把原檔複製成 <卡名>.png.bak（已經有 .bak 就不再覆蓋）"))
        self.chk_backup.setChecked(True)
        row2.addWidget(self.chk_backup)
        self.btn_save = QPushButton(T("存回這張卡"))
        self.btn_save.clicked.connect(lambda: self.save(False))
        row2.addWidget(self.btn_save)
        self.btn_saveas = QPushButton(T("另存新檔…"))
        self.btn_saveas.clicked.connect(lambda: self.save(True))
        row2.addWidget(self.btn_saveas)
        root.addLayout(row2)

        root.addWidget(self._transform_box())

        self.lbl_state = QLabel("")
        self.lbl_state.setStyleSheet("color:%s" % GREY)
        root.addWidget(self.lbl_state)

        self.worker = SceneWorker()
        self.thread = QThread(self)
        self.worker.moveToThread(self.thread)
        self.worker.loaded.connect(self._on_loaded)
        self.worker.saved.connect(self._on_saved)
        self.thread.start()

        # 還原上次「儲存設定」時的狀態
        self.ed_card.setText(self.settings.get("tree_card", ""))
        self.chk_state.setChecked(bool(self.settings.get("tree_save_treestate", False)))
        self.chk_backup.setChecked(bool(self.settings.get("tree_backup", True)))

        self.setAcceptDrops(True)
        self._set_busy(False)

    # ---- transform ----
    ROWS = ((T("位置"), "position", 0.0), (T("旋轉"), "rotation", 0.0), (T("縮放"), "scale", 1.0))

    def _transform_box(self):
        """改 transform 只動節點自己的欄位，不新增也不刪節點，RANK 完全不變。"""
        box = QGroupBox(T("位置 / 旋轉 / 縮放（選好物件，改完按「套用」）"))
        g = QGridLayout(box)
        self._spins = {}
        self._use = {}
        for r, (cap, key, dflt) in enumerate(self.ROWS):
            chk = QCheckBox(cap)
            chk.setChecked(True)
            chk.setToolTip(T("沒打勾的那幾項按「套用」時不會動"))
            self._use[key] = chk
            g.addWidget(chk, r, 0)
            row = []
            for c, axis in enumerate("xyz"):
                g.addWidget(QLabel(axis.upper()), r, 1 + c * 2)
                sp = QDoubleSpinBox()
                sp.setRange(-99999.0, 99999.0)
                sp.setDecimals(3)
                sp.setSingleStep(0.1)
                sp.setValue(dflt)
                sp.setFixedWidth(92)
                g.addWidget(sp, r, 2 + c * 2)
                row.append(sp)
            self._spins[key] = row
        g.setColumnStretch(7, 1)

        bar = QHBoxLayout()
        for txt, fn, tipx in (
            (T("讀取選取的值"), self._read_transform, T("把目前選取那個物件的數值填進上面")),
            (T("套用到選取的全部"), self._apply_transform,
             T("打勾的那幾項會寫進所有選取的物件（可以多選）")),
            (T("位置歸零"), lambda: self._quick("position", 0.0), ""),
            (T("縮放設 1"), lambda: self._quick("scale", 1.0), ""),
        ):
            b = QPushButton(txt)
            b.clicked.connect(fn)
            if tipx:
                b.setToolTip(tipx)
            bar.addWidget(b)
        bar.addStretch(1)
        self.lbl_tf = QLabel("")
        self.lbl_tf.setStyleSheet("color:%s" % GREY)
        bar.addWidget(self.lbl_tf)
        g.addLayout(bar, len(self.ROWS), 0, 1, 8)
        return box

    def _quick(self, key, val):
        for sp in self._spins[key]:
            sp.setValue(val)
        for k, chk in self._use.items():
            chk.setChecked(k == key)

    def _read_transform(self):
        if not hasattr(self, "_spins"):      # 面板還沒建好（__init__ 途中）
            return
        it = self._sel()
        if it is None:
            return
        d = (self._node(it) or {}).get("data") or {}
        for _, key, dflt in self.ROWS:
            v = d.get(key) or {}
            for sp, axis in zip(self._spins[key], "xyz"):
                sp.setValue(float(v.get(axis, dflt)))
        self.lbl_tf.setText(T("已讀入 %s") % it.text(0))

    def _apply_transform(self):
        items = [i for i in self.tree.selectedItems() if self._node(i)]
        if not items:
            it = self._sel()
            items = [it] if self._node(it) else []
        keys = [k for _, k, _ in self.ROWS if self._use[k].isChecked()]
        if not items or not keys:
            self.lbl_tf.setText(T("沒有選取物件，或三項都沒打勾"))
            return
        for it in items:
            d = self._node(it)["data"]
            for key in keys:
                d[key] = {axis: sp.value()
                          for sp, axis in zip(self._spins[key], "xyz")}
        self._touch()
        names = "、".join(i.text(0) for i in items[:3]) + ("…" if len(items) > 3 else "")
        self.lbl_tf.setText(T("已套用 %s 到 %d 個物件（%s）")
                            % ("/".join(k for k in keys), len(items), names))
        self.log_line.emit(T("套用 %s 到 %d 個物件：%s")
                           % (",".join(keys), len(items),
                              "、".join(i.text(0) for i in items)))

    # ---- 拖放 ----
    def dragEnterEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()

    def dragMoveEvent(self, e):
        if e.mimeData().hasUrls():
            e.acceptProposedAction()

    def dropEvent(self, e):
        for url in e.mimeData().urls():
            p = url.toLocalFile()
            if p.lower().endswith(".png"):
                self.ed_card.setText(p)
                e.acceptProposedAction()
                self.load()
                return

    # ---- 讀取 ----
    def _pick(self):
        p, _ = QFileDialog.getOpenFileName(self, T("場景卡"), self.ed_card.text(),
                                           T("場景卡 (*.png)"))
        if p:
            self.ed_card.setText(p)

    def set_card(self, path):
        self.ed_card.setText(path)

    def load(self):
        path = self.ed_card.text().strip()
        if not path or not os.path.isfile(path):
            QMessageBox.warning(self, T("讀不了"), T("先指定場景卡"))
            return
        if self._dirty and QMessageBox.question(
                self, T("還沒存檔"), T("目前的修改還沒存，要放棄嗎？")) != QMessageBox.StandardButton.Yes:
            return
        self._set_busy(True)
        self.status.emit(T("讀取中…（大卡要幾十秒）"))
        self.worker.want_load(path)
        QMetaObject.invokeMethod(self.worker, "load", Qt.ConnectionType.QueuedConnection)

    def _on_loaded(self, ok, scene, msg):
        self._set_busy(False)
        if not ok:
            self.log_line.emit(msg)
            QMessageBox.critical(self, T("讀取失敗"), msg.strip().splitlines()[-1])
            return
        self.scene = scene
        self.log_line.emit(msg)
        self.status.emit(msg)
        self._rebuild()
        self._dirty = False
        self._update_state()

    # ---- 建樹 ----
    def _rebuild(self):
        import kkscene2 as S
        self.tree.blockSignals(True)
        self.tree.clear()
        self._nodes = []
        keys = [n["data"]["dicKey"] for n, _, _ in S.iter_nodes(self.scene.objects)]
        self._next_key = (max(keys) + 1) if keys else 1
        names = scene_names(self.scene)

        def add(node, parent_item, in_char=False):
            d = node["data"]
            lab = label_of(node, names)
            it = QTreeWidgetItem([lab, kind_of(node), "",
                                  str(d.get("dicKey", ""))])
            it.setData(0, LABEL_ROLE, lab)
            self._put(it, node)
            flags = (Qt.ItemFlag.ItemIsEnabled | Qt.ItemFlag.ItemIsSelectable
                     | Qt.ItemFlag.ItemIsEditable | Qt.ItemFlag.ItemIsDragEnabled
                     | Qt.ItemFlag.ItemIsUserCheckable)
            if is_container(node):
                flags |= Qt.ItemFlag.ItemIsDropEnabled
            # 角色接點裡的東西也可以拖：只限同一個接點裡搬（見 Tree.dropEvent）
            it.setFlags(flags)
            vis = vis_of(node)
            it.setCheckState(2, Qt.CheckState.Checked if vis
                             else Qt.CheckState.Unchecked)
            if node.get("type") == FOLDER_TYPE:
                f = it.font(0)
                f.setBold(True)
                it.setFont(0, f)
            if not vis:
                for c in range(4):
                    it.setForeground(c, QBrush(QColor(GREY)))
            (parent_item.addChild(it) if parent_item is not None
             else self.tree.addTopLevelItem(it))
            ch = d.get("child")
            if isinstance(ch, list):
                for c in ch:
                    add(c, it, in_char)
            elif isinstance(ch, dict):
                # 角色：child 是接點字典。每個有東西的接點畫成一列假的，
                # 底下才是真正的節點。假列沒有對應節點，不能選、不能拖、
                # 不能改名、也沒有顯示勾 —— 它只是個分隔標題。
                for key, lst in char_slots(node):
                    sl = QTreeWidgetItem([T("接點 %s") % key, T("（角色接點）"), "", ""])
                    sl.setData(0, SLOT_ROLE, key)
                    # 可以丟東西進來（同一個接點裡的東西搬回接點最上層）
                    sl.setFlags(Qt.ItemFlag.ItemIsEnabled | Qt.ItemFlag.ItemIsDropEnabled)
                    f2 = sl.font(0)
                    f2.setItalic(True)
                    for c in range(4):
                        sl.setFont(c, f2)
                        sl.setForeground(c, QBrush(QColor(GREY)))
                    it.addChild(sl)
                    for c in lst:
                        add(c, sl, True)
            return it

        for node in self.scene.objects.values():
            item = add(node, None)
            if node["data"].get("treeState", 1) == 0:
                item.setExpanded(True)
        self.tree.blockSignals(False)

    # ---- 編輯 ----
    def _node(self, item):
        """item → 節點本體。存的是索引，不是 dict（見 __init__ 的說明）。"""
        if item is None:
            return None
        i = item.data(0, NODE_ROLE)
        if isinstance(i, int) and 0 <= i < len(self._nodes):
            return self._nodes[i]
        return None

    def _put(self, item, node):
        self._nodes.append(node)
        item.setData(0, NODE_ROLE, len(self._nodes) - 1)

    def _touch(self):
        self._dirty = True
        self._update_state()

    def _item_changed(self, item, col):
        node = self._node(item)
        if not node:
            return
        d = node["data"]
        if col == 0:
            txt = item.text(0)
            if txt == item.data(0, LABEL_ROLE) or txt.startswith(T("（未命名）")):
                return                      # 沒改名（或還是佔位字串）就什麼都不寫
            item.setData(0, LABEL_ROLE, txt)
            import kkscenemerge as KM
            had_tnn = self.scene.kkex.get(KM.TNN, "names") is not None
            if "name" not in d and not had_tnn:
                self.log_line.emit(T("[提醒] 這張卡沒有 TreeNodeNaming 資料，%s「%s」的新名字存不進去")
                                   % (kind_of(node), txt))
            KM.set_node_name(self.scene, node, txt)
        elif col == 2:
            vis = item.checkState(2) == Qt.CheckState.Checked
            set_vis(node, vis)
            # 標記為刪除的保持紅色劃掉，不要被「顯示」的顏色蓋掉 ——
            # 顏色是現在唯一看得出「這一列要被砍」的線索。
            if not item.data(0, DEAD_ROLE):
                col_brush = QBrush(QColor("#000000" if vis else GREY))
                for c in range(4):
                    item.setForeground(c, col_brush)
        self._touch()

    def _sel(self):
        it = self.tree.currentItem()
        return it

    def _new_folder(self):
        if self.scene is None:
            return
        import kkscene2 as S
        it = self._sel()
        node = S.new_folder(self._next_key, T("新資料夾"))
        self._next_key += 1
        self.tree.blockSignals(True)
        child = QTreeWidgetItem([node["data"]["name"], T("資料夾"), "",
                                 str(node["data"]["dicKey"])])
        self._put(child, node)
        child.setFlags(Qt.ItemFlag.ItemIsEnabled | Qt.ItemFlag.ItemIsSelectable
                       | Qt.ItemFlag.ItemIsEditable | Qt.ItemFlag.ItemIsDragEnabled
                       | Qt.ItemFlag.ItemIsDropEnabled | Qt.ItemFlag.ItemIsUserCheckable)
        child.setCheckState(2, Qt.CheckState.Checked)
        f = child.font(0)
        f.setBold(True)
        child.setFont(0, f)
        sel_node = self._node(it) if it is not None else None
        if it is not None and (it.data(0, SLOT_ROLE) is not None
                               or (sel_node or {}).get("type") == FOLDER_TYPE):
            # 選的是資料夾或角色接點 → 放進去（選道具則放在它旁邊，不塞進道具底下）
            it.addChild(child)
            it.setExpanded(True)
        elif it is not None and sel_node is not None and sel_node.get("type") == 0:
            # 選的是角色本身：角色底下只能是接點，放到角色旁邊
            par = it.parent()
            if par is None:
                self.tree.insertTopLevelItem(self.tree.indexOfTopLevelItem(it) + 1, child)
            else:
                par.insertChild(par.indexOfChild(it) + 1, child)
        elif it is not None and it.parent() is not None:
            it.parent().insertChild(it.parent().indexOfChild(it) + 1, child)
        else:
            idx = (self.tree.indexOfTopLevelItem(it) + 1) if it is not None \
                else self.tree.topLevelItemCount()
            self.tree.insertTopLevelItem(idx, child)
        self.tree.blockSignals(False)
        self.tree.setCurrentItem(child)
        self.tree.editItem(child, 0)
        self._touch()
        self.log_line.emit(T("新增資料夾，dicKey = %d（取 max+1，既有 RANK 不變）")
                           % node["data"]["dicKey"])

    def _rename(self):
        it = self._sel()
        if it is not None:
            self.tree.editItem(it, 0)

    def _move(self, delta):
        it = self._sel()
        if it is None:
            return
        p = it.parent()
        if p is None:
            i = self.tree.indexOfTopLevelItem(it)
            j = i + delta
            if 0 <= j < self.tree.topLevelItemCount():
                self.tree.takeTopLevelItem(i)
                self.tree.insertTopLevelItem(j, it)
                self.tree.setCurrentItem(it)
                self._touch()
        else:
            i = p.indexOfChild(it)
            j = i + delta
            if 0 <= j < p.childCount():
                p.takeChild(i)
                p.insertChild(j, it)
                self.tree.setCurrentItem(it)
                self._touch()

    def _outdent(self):
        it = self._sel()
        if it is None or it.parent() is None:
            return
        p = it.parent()
        # 直接掛在角色接點底下的東西，再升一層就會跑到角色本身底下 ——
        # 角色的 child 是接點字典，不是清單，塞不進去。
        if p.data(0, SLOT_ROLE) is not None:
            self.status.emit(T("這一項掛在角色的接點上，不能再升一層"))
            return
        gp = p.parent()
        p.takeChild(p.indexOfChild(it))
        if gp is None:
            self.tree.insertTopLevelItem(self.tree.indexOfTopLevelItem(p) + 1, it)
        else:
            gp.insertChild(gp.indexOfChild(p) + 1, it)
        self.tree.setCurrentItem(it)
        self._touch()

    # ---- 刪除 ----
    #
    # 為什麼是「先標記、存檔才砍」：真正砍下去要重算整個 RANK 空間並重寫
    # 每一條軌道的 objectIndex，那是不可逆的。標記的話按錯了重讀卡片就回來了，
    # 而且可以一次選好幾塊再一起算成本。

    def _subtree_items(self, item):
        out = []
        stack = [item]
        while stack:
            it = stack.pop()
            out.append(it)
            stack.extend(it.child(i) for i in range(it.childCount()))
        return out

    def _dead_keys(self):
        """目前標記為刪除的所有 dicKey（含底下整棵）。"""
        keys = []
        stack = [self.tree.topLevelItem(i) for i in range(self.tree.topLevelItemCount())]
        while stack:
            it = stack.pop()
            node = self._node(it)
            if it.data(0, DEAD_ROLE):
                if node is not None:
                    keys.append(node["data"]["dicKey"])
            stack.extend(it.child(i) for i in range(it.childCount()))
        return keys

    def _ref_cost(self, keys):
        """這些節點被刪掉會連帶砍掉幾條 timeline 軌道、幾個 NodesConstraints。

        算法跟 cleanup_deleted 一樣：軌道和 constraint 裡記的是 RANK，
        先把要刪的 dicKey 換成 RANK，再數有多少條指到它們。
        """
        import re
        import kkscene2 as S
        from kkmsgpack import unpack

        dead_rank = set()
        rank = S.rank_map(S.node_dickeys(self.scene.objects))
        for k in keys:
            if k in rank:
                dead_rank.add(rank[k])

        n_track = 0
        try:
            xml = self.scene.timeline_xml()
            if xml:
                _h, body, _t = S.split_root(xml)
                for m in S.RE_INTERP.finditer(body):
                    hm = S.RE_INTERP_HEAD.match(m.group(0))
                    head = hm.group(0) if hm else m.group(0)
                    mo = re.search(r'objectIndex="(-?\d+)"', head)
                    if mo and int(mo.group(1)) in dead_rank:
                        n_track += 1
        except Exception:
            n_track = -1

        n_nc = 0
        try:
            raw = self.scene.kkex.get("nodesConstraints", "constraints")
            if raw is not None:
                s = unpack(raw)
                if isinstance(s, str):
                    for m in re.finditer(
                            r'<constraint\b[^>]*?/>|<constraint\b[^>]*?>.*?</constraint>',
                            s, re.S):
                        idx = [int(x) for x in re.findall(
                            r'(?:parent|child)ObjectIndex="(-?\d+)"', m.group(0))]
                        if any(i in dead_rank for i in idx):
                            n_nc += 1
        except Exception:
            n_nc = -1

        return n_track, n_nc

    def _paint_dead(self, item, dead):
        item.setData(0, DEAD_ROLE, bool(dead))
        f = item.font(0)
        f.setStrikeOut(bool(dead))
        for c in range(4):
            item.setFont(c, f)
        if dead:
            for c in range(4):
                item.setForeground(c, QBrush(QColor(RED)))
        else:
            node = self._node(item)
            vis = vis_of(node) if node is not None else True
            for c in range(4):
                item.setForeground(c, QBrush(QColor("#000000" if vis else GREY)))

    def _mark_delete(self):
        if self.scene is None:
            return
        items = [it for it in (self.tree.selectedItems()
                               or ([self._sel()] if self._sel() else [])) if it]
        if not items:
            return

        # 已經被上層標掉的就不必重複列入
        victims = []
        seen = set()
        for it in items:
            for sub in self._subtree_items(it):
                if id(sub) in seen:
                    continue
                seen.add(id(sub))
                victims.append(sub)

        keys = []
        for it in victims:
            node = self._node(it)
            if node is not None and not it.data(0, DEAD_ROLE):
                keys.append(node["data"]["dicKey"])
        if not keys:
            return

        n_track, n_nc = self._ref_cost(keys)
        names = ", ".join(str(self._node(it)["data"].get("name") or "?")[:20]
                          for it in items[:4] if self._node(it))
        if len(items) > 4:
            names += " …"

        msg = [T("要刪：%s") % names,
               T("連同底下總共 %d 個節點。") % len(keys),
               ""]
        if n_track < 0 or n_nc < 0:
            msg.append(T("（算不出會連帶砍掉多少軌道 —— 這張卡的 timeline 讀不動）"))
        else:
            msg.append(T("會連帶砍掉 %d 條 timeline 軌道、%d 個 NodesConstraints。")
                       % (n_track, n_nc))
            if n_track or n_nc:
                msg.append(T("那些軌道是**指向這些節點**的，節點不在了它們也沒有意義，留著會讓 timeline 從那一條開始整棵中斷。"))
        msg += ["",
                T("現在只是標記，按「存回這張卡」或「另存新檔」才真的動手。"),
                T("按錯的話重新讀取卡片就回來了。")]

        if QMessageBox.question(
                self, T("標記為刪除"), "\n".join(msg),
                QMessageBox.StandardButton.Ok | QMessageBox.StandardButton.Cancel,
                QMessageBox.StandardButton.Cancel) != QMessageBox.StandardButton.Ok:
            return

        self.tree.blockSignals(True)
        for it in victims:
            self._paint_dead(it, True)
        self.tree.blockSignals(False)
        self.log_line.emit(T("標記刪除：%d 個節點（存檔時才真的砍）") % len(keys))
        self._touch()

    def _unmark_delete(self):
        items = [it for it in (self.tree.selectedItems()
                               or ([self._sel()] if self._sel() else [])) if it]
        if not items:
            return
        self.tree.blockSignals(True)
        n = 0
        for it in items:
            for sub in self._subtree_items(it):
                if sub.data(0, DEAD_ROLE):
                    n += 1
                self._paint_dead(sub, False)
        self.tree.blockSignals(False)
        if n:
            self.log_line.emit(T("取消刪除標記：%d 個節點") % n)
        self._touch()

    def _toggle_vis(self):
        items = self.tree.selectedItems() or ([self._sel()] if self._sel() else [])
        for it in items:
            if it is None:
                continue
            now = it.checkState(2) == Qt.CheckState.Checked
            it.setCheckState(2, Qt.CheckState.Unchecked if now else Qt.CheckState.Checked)

    # ---- 存檔 ----
    def _collect(self):
        """把樹狀圖的結構寫回節點。child 是字典的（角色）不動。

        標記為刪除的項目在這裡整棵略過 —— 不進 child 清單就等於不存在了。
        真正的善後（重算 RANK、清參照）由 save() 拿到回傳的 dicKey 之後交給
        cleanup_deleted 處理。
        """
        def walk(item):
            node = self._node(item)
            if node is None:
                raise RuntimeError(T("樹裡有對不到節點的項目，請重新讀取卡片"))
            d = node["data"]
            if self.chk_state.isChecked():
                d["treeState"] = 0 if item.isExpanded() else 1
            ch = d.get("child")
            if isinstance(ch, list):
                d["child"] = [walk(item.child(i)) for i in range(item.childCount())
                              if not item.child(i).data(0, DEAD_ROLE)]
            elif isinstance(ch, dict):
                # 角色：照樹上的假列把接點字典重建回去。
                # **空的接點一定要原樣留著** —— 它們沒有畫在樹上，
                # 重建時漏掉的話等於把角色的接點表改短，遊戲那邊會對不上。
                for i in range(item.childCount()):
                    sl = item.child(i)
                    key = sl.data(0, SLOT_ROLE)
                    if key is None or key not in ch:
                        continue
                    kids = [walk(sl.child(j)) for j in range(sl.childCount())
                            if not sl.child(j).data(0, DEAD_ROLE)]
                    if isinstance(ch[key], list):
                        ch[key] = kids
                    elif kids:
                        ch[key] = kids[0]
            return node

        roots = [walk(self.tree.topLevelItem(i))
                 for i in range(self.tree.topLevelItemCount())
                 if not self.tree.topLevelItem(i).data(0, DEAD_ROLE)]
        self.scene.objects = type(self.scene.objects)(
            (n["data"]["dicKey"], n) for n in roots)

    def save(self, as_new):
        if self.scene is None:
            return
        out = self.ed_card.text().strip()
        if as_new:
            base, ext = os.path.splitext(out)
            out, _ = QFileDialog.getSaveFileName(self, T("另存新檔"),
                                                 base + "_edit" + ext, T("場景卡 (*.png)"))
            if not out:
                return

        dead = self._dead_keys()

        # 有刪除就一定要留退路。RANK 重算是不可逆的 ——
        # 覆蓋掉原卡又沒有 .bak，發現砍錯的時候已經沒有東西可以回去了。
        if dead and not as_new and not self.chk_backup.isChecked():
            r = QMessageBox.question(
                self, T("這次有刪除"),
                T("這次會真的刪掉 %d 個節點，而且「覆蓋前先備份 .bak」沒有勾。\n\n刪除會重算整個 RANK 空間並重寫每一條軌道，沒辦法還原。\n原卡被蓋掉之後就沒有東西可以回去了。\n\n要幫你把備份打開再存嗎？") % len(dead),
                QMessageBox.StandardButton.Ok | QMessageBox.StandardButton.Cancel,
                QMessageBox.StandardButton.Ok)
            if r != QMessageBox.StandardButton.Ok:
                return
            self.chk_backup.setChecked(True)

        try:
            import kkscene2 as S
            old_nodes = S.node_dickeys(self.scene.objects) if dead else None
            self._collect()
            if dead:
                from kkscenemerge import cleanup_deleted
                warn = []
                stat = cleanup_deleted(self.scene, old_nodes, dead, warn)
                self.log_line.emit(T("刪掉 %d 個節點，參照清理：") % len(dead))
                for k, v in (stat or {}).items():
                    self.log_line.emit("    %-22s %s" % (k, v))
                for w in warn:
                    self.log_line.emit(T("    [注意] %s") % w)
        except Exception:
            self.log_line.emit(traceback.format_exc())
            QMessageBox.critical(self, T("整理失敗"),
                                 T("把樹寫回場景時出錯，看紀錄。\n這張卡在記憶體裡可能已經改了一半，請重新讀取再試一次。"))
            return
        self._just_deleted = len(dead)
        self._set_busy(True)
        self.status.emit(T("寫出中…"))
        self.worker.want_save(self.scene, out, self.chk_backup.isChecked() and not as_new)
        QMetaObject.invokeMethod(self.worker, "save", Qt.ConnectionType.QueuedConnection)

    def _on_saved(self, ok, msg):
        self._set_busy(False)
        self.log_line.emit(msg)
        if ok:
            # 刪除過就得重畫。記憶體裡的 scene 已經沒有那些節點了，
            # 樹上卻還留著劃掉的那幾列 —— 再按一次存檔會拿已經不存在的
            # dicKey 去跑 cleanup_deleted，算出來的 RANK 全是錯的。
            if getattr(self, "_just_deleted", 0):
                self._just_deleted = 0
                self._rebuild()
            self._dirty = False
            self._update_state()
            self.status.emit(msg)
        else:
            QMessageBox.critical(self, T("寫出失敗"), msg.strip().splitlines()[-1])

    # ---- 雜項 ----
    def _set_busy(self, busy):
        for w in (self.btn_load, self.btn_save, self.btn_saveas, self.tree):
            w.setEnabled(not busy)

    def _update_state(self):
        if self.scene is None:
            self.lbl_state.setText("")
            return
        n_hidden = 0
        n_dead = 0
        stack = [self.tree.topLevelItem(i) for i in range(self.tree.topLevelItemCount())]
        n = 0
        while stack:
            it = stack.pop()
            stack.extend(it.child(i) for i in range(it.childCount()))
            if it.data(0, SLOT_ROLE) is not None:
                continue                     # 角色接點那一列是畫出來的，不是節點
            n += 1
            if it.checkState(2) != Qt.CheckState.Checked:
                n_hidden += 1
            if it.data(0, DEAD_ROLE):
                n_dead += 1
        txt = T("%d 個節點，其中 %d 個隱藏") % (n, n_hidden)
        if n_dead:
            txt += T("　<span style='color:%s'><b>%d 個待刪除（存檔時才真的砍）</b></span>") \
                   % (RED, n_dead)
        if self._dirty:
            txt += T("　（有未存檔的修改）")
        self.lbl_state.setTextFormat(Qt.TextFormat.RichText)
        self.lbl_state.setText(txt)

    def save_state(self):
        """把這一頁的狀態寫進共用的 settings —— 主視窗按「儲存設定」時會叫這個。"""
        self.settings.update({
            "tree_card": self.ed_card.text().strip(),
            "tree_save_treestate": self.chk_state.isChecked(),
            "tree_backup": self.chk_backup.isChecked(),
        })

    def shutdown(self):
        self.thread.quit()
        self.thread.wait(3000)
