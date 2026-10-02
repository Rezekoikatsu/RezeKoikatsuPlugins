# -*- coding: utf-8 -*-
"""kkffmpeg —— 找 ffmpeg / ffprobe（PATH 或 exe 旁的 ffmpeg 資料夾），並讓它不閃主控台視窗。

找的順序（找到就用，都沒有才退回 PATH）：

    1. <exe 或 .py 所在資料夾>\\ffmpeg\\bin\\
    2. <exe 或 .py 所在資料夾>\\ffmpeg\\
    3. <exe 或 .py 所在資料夾>\\
    4. 打包進 exe 裡的 ffmpeg\\（PyInstaller 的 _MEIPASS；目前不打包，留著備用）
    5. PATH

ffmpeg 不隨工具發佈，使用者自己安裝（加進 PATH，或把解壓出來的資料夾改名成
ffmpeg 放在 kkscenebridge.exe 旁邊）。沒有 ffmpeg 時只有「添加動畫音頻」分頁的
影片／音訊處理功能不能用，其他都照常。

install() 會攔 subprocess.Popen：指令第一個字是 "ffmpeg" / "ffprobe" 的時候，
換成上面找到的完整路徑，並在 Windows 上加 CREATE_NO_WINDOW —— 不然打包成
視窗程式（console=False）之後，每叫一次 ffmpeg 就會閃一個黑色主控台視窗。
其他指令完全不動。這樣呼叫端原本寫的 ["ffmpeg", ...] 不用一個一個改。
"""
import os
import shutil
import subprocess
import sys

NAMES = ("ffmpeg", "ffprobe")
CREATE_NO_WINDOW = 0x08000000


def app_dir():
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))


def _dirs():
    base = app_dir()
    out = [os.path.join(base, "ffmpeg", "bin"), os.path.join(base, "ffmpeg"), base]
    mei = getattr(sys, "_MEIPASS", None)
    if mei:
        out += [os.path.join(mei, "ffmpeg", "bin"), os.path.join(mei, "ffmpeg")]
    return out


def find(name):
    """回傳 ffmpeg / ffprobe 的完整路徑；哪裡都沒有就回傳原本的名字（交給 PATH，失敗會丟 FileNotFoundError）。"""
    exe = name + (".exe" if os.name == "nt" else "")
    for d in _dirs():
        p = os.path.join(d, exe)
        if os.path.isfile(p):
            return p
    return shutil.which(name) or name


FFMPEG = find("ffmpeg")
FFPROBE = find("ffprobe")


def where():
    """給介面顯示用：目前用的是哪一支（或是找不到）。"""
    if os.path.isabs(FFMPEG) and os.path.isfile(FFMPEG):
        return FFMPEG
    return None


_installed = False


def install():
    """攔 subprocess.Popen（subprocess.run 也是走它）。重複呼叫沒關係。"""
    global _installed
    if _installed:
        return
    _installed = True
    orig = subprocess.Popen.__init__
    table = {"ffmpeg": FFMPEG, "ffprobe": FFPROBE}

    def init(self, args, *a, **kw):
        if isinstance(args, (list, tuple)) and args and args[0] in table:
            args = [table[args[0]]] + list(args[1:])
            if os.name == "nt":
                kw["creationflags"] = kw.get("creationflags", 0) | CREATE_NO_WINDOW
        orig(self, args, *a, **kw)

    subprocess.Popen.__init__ = init
