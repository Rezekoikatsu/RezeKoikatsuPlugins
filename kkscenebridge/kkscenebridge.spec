# -*- mode: python ; coding: utf-8 -*-
# 打包 kkscenebridge.exe
#
# 兩件事一定要留意：
#
# 1. kkcheck.py 需要 kkloader 的 KoikatuSceneData.py「原始碼」（它會改幾行再載入），
#    凍結之後 spec.origin 讀不到，所以把那支 .py 打進 kkloader_src/。
#
# 2. kkloader 的 __init__.py 會把所有遊戲的 loader 都 import 進來，其中
#    SummerVacationSaveData 裡有 `import pandas`。PyInstaller 靜態掃到之後就會
#    順著把 pandas / numpy / scipy 一路拉進來；如果環境裡還裝了 torch、
#    transformers 那些，整包會腫到 2 GB。所以除了必要的以外一律排除。
#
# 3. numpy 從排除清單裡**拿掉了**。「從切好的音頻反推對應點」要做 FFT 互相關，
#    kkaudioalign / kkcutscene_prep 都需要它。排除掉的話 exe 跑起來會說
#    "No module named 'numpy'"，而且在 exe 裡沒辦法用 pip 補裝 ——
#    凍結進去的 Python 不吃 pip，只能重新打包。
#    pandas / scipy 仍然排除，所以 kkloader 那條鏈還是被擋住，
#    多出來的體積只有 numpy 本身（約 30~60 MB）。
import os
import kkloader

KKL = os.path.dirname(kkloader.__file__)

EXCLUDES = [
    # ML 那一整坨
    'torch', 'torchvision', 'torchaudio', 'transformers', 'tokenizers',
    'safetensors', 'huggingface_hub', 'sentencepiece', 'accelerate',
    'onnxruntime', 'numba', 'llvmlite', 'sklearn', 'cv2', 'timm', 'datasets',
    # 科學運算（numpy 不在這裡 —— 見檔頭第 3 點，音頻對齊要用）
    'pandas', 'scipy', 'matplotlib', 'pytz', 'dateutil',
    # 其他用不到的
    'PIL', 'IPython', 'notebook', 'jupyter', 'jupyter_core', 'zmq',
    'tkinter', 'PySide6', 'PyQt5', 'PySide2',
    'requests', 'urllib3', 'bs4', 'yaml', 'rich', 'regex', 'tqdm',
    'pytest', 'sphinx', 'docutils',
]

# 4. ffmpeg 不打包、也不附在發佈檔裡 —— 使用者要用影片／音訊功能的話自己安裝
#    （PATH，或放在 exe 旁邊的 ffmpeg\ 資料夾；kkffmpeg.py 會找）。

a = Analysis(
    ['kkscenebridge.py'],
    pathex=[],
    binaries=[],
    datas=[(os.path.join(KKL, 'KoikatuSceneData.py'), 'kkloader_src')],
    hiddenimports=['msgpack', 'lz4', 'lz4.block', 'lz4.frame', 'brotli',
                   'numpy',
                   # 這幾支都是在函式裡才 import 的（延後載入，少了也不會整個掛掉），
                   # 靜態分析不一定掃得到，所以明講。
                   'kkaudioalign', 'kkcutscene', 'kkcutscene_check', 'kkcutscene_prep',
                   'kkcuttab', 'kktreetab', 'kksblang', 'kkffmpeg',
                   'kkvnsound', 'kkaudiotab', 'kkscenemerge', 'kktl_scan',
                   'kkloader', 'kkloader.KoikatuSceneData',
                   'kkloader.KoikatuSceneObjectLoader',
                   'kkloader.SceneObjectLoaderBase',
                   'kkloader.KoikatuCharaData'],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=EXCLUDES,
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz, a.scripts, a.binaries, a.datas, [],
    name='kkscenebridge',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
