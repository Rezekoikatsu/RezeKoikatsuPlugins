@echo off
setlocal
cd /d "%~dp0"

REM Build kkscenebridge.exe (scene card merger + the former kkbridge, one GUI exe).
REM Requires Python 3.11+ in PATH.

where python >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Python not found in PATH.
    echo Install Python 3.11+ and tick "Add python.exe to PATH".
    pause
    exit /b 1
)

for %%F in (kkscenebridge.py kksblang.py kkscenemerge.py kkscene2.py kkmsgpack.py kkref.py kkcheck.py kkvnsound.py kkaudiotab.py kkcutmerge.py kkbridgetab.py kkmerge.py kklang.py) do (
    if not exist "%%F" (
        echo [ERROR] missing %%F
        pause
        exit /b 1
    )
)

echo [1/3] Installing dependencies...
python -m pip install --upgrade pip
REM numpy is needed to derive sync points from the cut audio (FFT cross-
REM correlation). PyInstaller freezes the exe, so pip cannot add it later --
REM it has to be installed here, before the build.
python -m pip install "kkloader==0.1.23" msgpack PyQt6 pyinstaller numpy
if errorlevel 1 (
    echo [ERROR] pip install failed.
    pause
    exit /b 1
)

echo.
echo [2/3] Building GUI...
python -m PyInstaller --clean --noconfirm kkscenebridge.spec
if errorlevel 1 (
    echo [ERROR] build failed.
    pause
    exit /b 1
)

echo.
echo [3/3] Done.
echo   dist\kkscenebridge.exe   - double click this one
echo   Video/audio features in the Cutscene audio tab need ffmpeg installed separately (see README).
echo.
echo Command line version: just run  python kkscenemerge.py  (no build needed).
pause
