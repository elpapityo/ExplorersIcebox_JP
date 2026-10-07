@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "PLUGIN=ExplorersIceboxJP"
set "VERSION=0.1.1"
set "CONFIG=Release"
set "OUTDIR=%~dp0ExplorersIcebox\bin\x64\%CONFIG%"
set "SOURCEMANIFEST=%~dp0ExplorersIcebox\ExplorersIceboxJP.json"
set "OUTMANIFEST=%OUTDIR%\ExplorersIceboxJP.json"
set "ZROOT=Z:\ExplorersIceboxJP\Current"
set "STAGE=%~dp0release\ExplorersIceboxJP_v%VERSION%"
set "UPLOAD=%~dp0upload"
set "ZIP=%UPLOAD%\ExplorersIceboxJP_v%VERSION%.zip"
set "LOG=%~dp0build.log"
set "ERR=不明なエラーです。"

title ExplorersIceboxJP v%VERSION% ビルド

echo ============================================================
echo ExplorersIceboxJP v%VERSION% ビルド
echo ============================================================
echo.

if exist "%LOG%" del /q "%LOG%" >nul 2>nul

where dotnet >nul 2>nul
if errorlevel 1 (
  set "ERR=.NET SDK が見つかりません。"
  goto FAIL
)

echo [1/5] 復元しています...
dotnet restore "ExplorersIcebox.sln" > "%LOG%" 2>&1
if errorlevel 1 (
  set "ERR=dotnet restore に失敗しました。"
  goto FAIL
)

echo [2/5] Release ビルドしています...
dotnet build "ExplorersIcebox.sln" -c %CONFIG% --no-restore >> "%LOG%" 2>&1
if errorlevel 1 (
  set "ERR=dotnet build に失敗しました。"
  goto FAIL
)

if not exist "%OUTDIR%\ExplorersIceboxJP.dll" (
  set "ERR=ExplorersIceboxJP.dll が作成されていません。"
  goto FAIL
)

set "MANIFEST=%OUTMANIFEST%"
if not exist "%MANIFEST%" set "MANIFEST=%SOURCEMANIFEST%"
if not exist "%MANIFEST%" (
  set "ERR=ExplorersIceboxJP.json が見つかりません。"
  goto FAIL
)

if not exist "%~dp0images\icon.png" (
  set "ERR=images\icon.png が見つかりません。"
  goto FAIL
)
if not exist "%~dp0LICENSE.md" (
  set "ERR=LICENSE.md が見つかりません。"
  goto FAIL
)
if not exist "%~dp0NOTICE.md" (
  set "ERR=NOTICE.md が見つかりません。"
  goto FAIL
)
if not exist "%~dp0SOURCE.md" (
  set "ERR=SOURCE.md が見つかりませんでした。"
  goto FAIL
)

echo [3/5] 配布用フォルダーを作成しています...
if exist "%STAGE%" rmdir /s /q "%STAGE%" >nul 2>nul
mkdir "%STAGE%\images" >nul 2>nul
if errorlevel 1 (
  set "ERR=配布用フォルダーを作成できませんでした。"
  goto FAIL
)

copy /y "%OUTDIR%\ExplorersIceboxJP.dll" "%STAGE%\ExplorersIceboxJP.dll" >> "%LOG%" 2>&1
copy /y "%MANIFEST%" "%STAGE%\ExplorersIceboxJP.json" >> "%LOG%" 2>&1
copy /y "%~dp0images\icon.png" "%STAGE%\images\icon.png" >> "%LOG%" 2>&1
copy /y "%~dp0LICENSE.md" "%STAGE%\LICENSE.md" >> "%LOG%" 2>&1
copy /y "%~dp0NOTICE.md" "%STAGE%\NOTICE.md" >> "%LOG%" 2>&1
copy /y "%~dp0SOURCE.md" "%STAGE%\SOURCE.md" >> "%LOG%" 2>&1
copy /y "%~dp0README.md" "%STAGE%\README.md" >> "%LOG%" 2>&1
copy /y "%~dp0README_JP.md" "%STAGE%\README_JP.md" >> "%LOG%" 2>&1
if exist "%OUTDIR%\ExplorersIceboxJP.deps.json" copy /y "%OUTDIR%\ExplorersIceboxJP.deps.json" "%STAGE%\ExplorersIceboxJP.deps.json" >> "%LOG%" 2>&1
for %%F in (ECommons.dll Pictomancy.dll YamlDotNet.dll KamiToolKit.dll SharpDX.dll SharpDX.D3DCompiler.dll SharpDX.Direct2D1.dll SharpDX.Direct3D11.dll SharpDX.DXGI.dll SharpDX.Mathematics.dll SixLabors.ImageSharp.dll) do if exist "%OUTDIR%\%%F" copy /y "%OUTDIR%\%%F" "%STAGE%\%%F" >> "%LOG%" 2>&1

if not exist "%STAGE%\ExplorersIceboxJP.dll" (
  set "ERR=配布用 DLL の作成確認に失敗しました。"
  goto FAIL
)
if not exist "%STAGE%\ExplorersIceboxJP.json" (
  set "ERR=配布用 JSON の作成確認に失敗しました。"
  goto FAIL
)
if not exist "%STAGE%\images\icon.png" (
  set "ERR=配布用アイコンの作成確認に失敗しました。"
  goto FAIL
)
if not exist "%STAGE%\LICENSE.md" (
  set "ERR=配布用 LICENSE.md の作成確認に失敗しました。"
  goto FAIL
)
if not exist "%STAGE%\NOTICE.md" (
  set "ERR=配布用 NOTICE.md の作成確認に失敗しました。"
  goto FAIL
)
if not exist "%STAGE%\SOURCE.md" (
  set "ERR=配布用 SOURCE.md の作成確認に失敗しました。"
  goto FAIL
)

echo [4/5] ローカルテスト用へ配置しています...
if not exist "Z:\" (
  set "ERR=Z: ドライブが見つかりません。ビルドは成功しましたが配置できませんでした。"
  goto FAIL
)
if exist "%ZROOT%" rmdir /s /q "%ZROOT%" >nul 2>nul
mkdir "%ZROOT%" >nul 2>nul
xcopy /e /i /y "%STAGE%\*" "%ZROOT%\" >> "%LOG%" 2>&1
if errorlevel 1 (
  set "ERR=Z: への配置に失敗しました。"
  goto FAIL
)
if not exist "%ZROOT%\ExplorersIceboxJP.dll" (
  set "ERR=Z: 配置後の DLL が見つかりません。"
  goto FAIL
)
if not exist "%ZROOT%\images\icon.png" (
  set "ERR=Z: 配置後のアイコンが見つかりません。"
  goto FAIL
)

echo [5/5] GitHub upload 用 ZIP を作成しています...
if not exist "%UPLOAD%" mkdir "%UPLOAD%" >nul 2>nul
if exist "%ZIP%" del /q "%ZIP%" >nul 2>nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%ZIP%' -Force" >> "%LOG%" 2>&1
if errorlevel 1 (
  set "ERR=配布 ZIP の作成に失敗しました。"
  goto FAIL
)
if not exist "%ZIP%" (
  set "ERR=配布 ZIP が見つかりません。"
  goto FAIL
)
for %%A in ("%ZIP%") do if %%~zA LEQ 0 (
  set "ERR=配布 ZIP のサイズが 0 です。"
  goto FAIL
)

title 成功 - ExplorersIceboxJP v%VERSION%
color 2F
cls
echo.
echo ============================================================
echo             ビルドと配置が正常に完了しました
echo ============================================================
echo.
echo Version : %VERSION%
echo.
echo ローカルテスト:
echo   %ZROOT%\ExplorersIceboxJP.dll
echo   %ZROOT%\images\icon.png
echo.
echo GitHub upload 用:
echo   %ZIP%
echo.
echo 実機確認後に公開してください。
echo.
pause
exit /b 0

:FAIL
title 失敗 - ExplorersIceboxJP v%VERSION%
color 4F
cls
echo.
echo ============================================================
echo                ビルドまたは配置に失敗しました
echo ============================================================
echo.
echo Version : %VERSION%
echo 原因    : %ERR%
echo.
echo エラー行:
echo ------------------------------------------------------------
if exist "%LOG%" findstr /I /C:": error " /C:" error CS" "%LOG%"
echo ------------------------------------------------------------
echo.
echo ログ: %LOG%
echo.
echo この画面の内容をそのまま送ってください。
echo.
pause
exit /b 1
