@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

REM Build kkbridge.exe (GUI) and kkmerge.exe (CLI) on Windows.
REM Requires Python 3.10+ in PATH.

where python >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Python not found in PATH.
    echo Install Python 3.10+ and tick "Add python.exe to PATH".
    pause
    exit /b 1
)

if not exist "kkmerge.py" (
    echo [ERROR] kkmerge.py not found next to this script.
    pause & exit /b 1
)
if not exist "kkbridge.py" (
    echo [ERROR] kkbridge.py not found next to this script.
    pause & exit /b 1
)
if not exist "kklang.py" (
    echo [ERROR] kklang.py not found next to this script.
    pause & exit /b 1
)
REM The icon ships with the source. Regenerate it with:  python make_icon.py
if not exist "kkbridge.ico" (
    echo [WARN] kkbridge.ico missing - building without a custom icon.
    set "ICON="
) else (
    set "ICON=--icon kkbridge.ico"
)

echo [1/4] Installing dependencies...
python -m pip install --upgrade pip
python -m pip install msgpack PyQt6 pyinstaller
if errorlevel 1 ( echo [ERROR] pip install failed. & pause & exit /b 1 )

echo.
echo [2/4] Building kkmerge.exe (command line)...
python -m PyInstaller --onefile --console --clean --name kkmerge %ICON% --hidden-import msgpack kkmerge.py
if errorlevel 1 ( echo [ERROR] kkmerge build failed. & pause & exit /b 1 )

echo.
echo [3/4] Building kkbridge.exe (GUI)...
python -m PyInstaller --onefile --windowed --clean --name kkbridge %ICON% --hidden-import msgpack kkbridge.py
if errorlevel 1 ( echo [ERROR] kkbridge build failed. & pause & exit /b 1 )

echo.
echo [4/4] Self test:
"dist\kkmerge.exe" --version

echo.
echo Done.
echo   dist\kkbridge.exe  - double click this one
echo   dist\kkmerge.exe   - command line, optional
pause
