@echo off
setlocal
cd /d "%~dp0"

REM ASCII ONLY -- see run_verify.bat for why.
REM
REM Run kkscenebridge from SOURCE, not from dist\kkscenebridge.exe.
REM The exe is frozen by PyInstaller: pip cannot add packages to it, and
REM edits to the .py files do not take effect until you rebuild.
REM Running from source picks up every edit immediately.

set "PY="
if exist "%LOCALAPPDATA%\Programs\Python\Python312\python.exe" set "PY=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
if not defined PY if exist "%LOCALAPPDATA%\Programs\Python\Python313\python.exe" set "PY=%LOCALAPPDATA%\Programs\Python\Python313\python.exe"
if not defined PY if exist "%LOCALAPPDATA%\Programs\Python\Python311\python.exe" set "PY=%LOCALAPPDATA%\Programs\Python\Python311\python.exe"
if not defined PY where python >nul 2>nul && set "PY=python"
if not defined PY (
    echo [ERROR] Python not found.
    pause
    exit /b 1
)

echo Python: %PY%
"%PY%" -c "import sys; print('   ', sys.executable); print('    version', sys.version.split()[0])"

"%PY%" -c "import PyQt6, numpy, msgpack, kkloader" 2>nul
if errorlevel 1 (
    echo.
    echo Missing packages, installing into the Python above...
    "%PY%" -m pip install PyQt6 numpy msgpack "kkloader==0.1.23"
    if errorlevel 1 (
        echo [ERROR] pip install failed.
        pause
        exit /b 1
    )
)

echo.
echo Starting kkscenebridge from source...
"%PY%" kkscenebridge.py
if errorlevel 1 pause
