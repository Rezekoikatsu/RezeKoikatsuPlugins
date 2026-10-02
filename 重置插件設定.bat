@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul
rem ===================================================================
rem  Reze Studio Plugins - 設定備份／重置
rem
rem  把這幾支插件的「設定與狀態」搬到備份資料夾，讓它們下次啟動時
rem  以預設值重新生成。
rem
rem  只動這幾個檔案：
rem    BepInEx\config\reze.studio.chartools.cfg
rem    BepInEx\config\reze.studio.cutscene.cfg
rem    BepInEx\config\reze.studio.vrtools.cfg
rem    BepInEx\plugins\StudioCutScene.last.txt
rem
rem  **絕對不碰**你做好的內容：
rem    UserData\cutscene\*.cutscene.json   過場設定
rem    UserData\cutscene\*.view.json       VR 視角
rem    UserData\Studio\scene\*             場景卡
rem    任何影片、音檔、人物卡、服裝卡
rem
rem  是「搬移」不是刪除 —— 後悔了把檔案搬回去就好。
rem ===================================================================

rem 遊戲根目錄：預設用這個 bat 的所在位置往上找，找不到就改下面這一行
set "GAME=%~dp0"
if not exist "%GAME%BepInEx\config" (
  if exist "D:\Koikatu\BepInEx\config" (set "GAME=D:\Koikatu\")
)

if not exist "%GAME%BepInEx\config" (
  echo [錯誤] 找不到 BepInEx\config。
  echo         請把這個 bat 放到遊戲根目錄，或編輯檔案裡的 GAME 路徑。
  echo         目前試的是：%GAME%
  pause
  exit /b 1
)

rem 時間戳。不用 wmic —— 新版 Windows 11 已經把它移除了
set "STAMP="
for /f %%I in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set "STAMP=%%I"
if not defined STAMP set "STAMP=backup"
set "BACKUP=%GAME%BepInEx\config\_reze_backup\%STAMP%"

echo.
echo  遊戲目錄 : %GAME%
echo  備份到   : %BACKUP%
echo.
echo  會搬走這幾支插件的設定檔，下次開遊戲會以預設值重新生成。
echo  你做好的 .cutscene.json / .view.json / 場景卡 完全不會被碰。
echo.
set /p OK="確定要繼續嗎？(Y/N) "
if /i not "%OK%"=="Y" (
  echo  已取消，什麼都沒有動。
  pause
  exit /b 0
)

mkdir "%BACKUP%" 2>nul
set /a MOVED=0

call :MoveOne "%GAME%BepInEx\config\reze.studio.chartools.cfg"
call :MoveOne "%GAME%BepInEx\config\reze.studio.cutscene.cfg"
call :MoveOne "%GAME%BepInEx\config\reze.studio.vrtools.cfg"
call :MoveOne "%GAME%BepInEx\plugins\StudioCutScene.last.txt"


echo.
if %MOVED%==0 (
  echo  沒有找到任何設定檔 —— 可能已經是乾淨狀態了。
  rmdir "%BACKUP%" 2>nul
) else (
  echo  完成，共搬走 %MOVED% 個檔案。
  echo  下次啟動 CharaStudio 時會以預設值重新生成。
  echo.
  echo  要還原的話，把 %BACKUP% 裡的檔案搬回原位即可。
)
echo.
pause
exit /b 0

:MoveOne
if exist %1 (
  move /y %1 "%BACKUP%\%~nx1" >nul
  if errorlevel 1 (
    echo   [失敗] %~nx1  ^(檔案被佔用？請先關掉遊戲^)
  ) else (
    echo   [已備份] %~nx1
    set /a MOVED+=1
  )
)
goto :eof
