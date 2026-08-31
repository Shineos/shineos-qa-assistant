@echo off
rem 社内知恵袋 修復スクリプト（v1.0.74）
rem Open WebUI のパッチ状態を修復し、サービスを再起動します。
rem 使い方: デスクトップの「ShineosQA-repair」またはこのファイルをダブルクリック

rem --- 管理者権限が無ければ昇格して再実行する ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo 管理者権限で再実行します...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b 0
)

cd /d "%~dp0"
"%~dp0..\venv\Scripts\python.exe" "%~dp0repair_middleware.py"
if %errorlevel% neq 0 (
    echo.
    echo 修復に失敗しました。%ProgramFiles%\ShineosQA\logs\repair.log を確認してください。
) else (
    echo.
    echo 修復が完了しました。約30秒後に「社内知恵袋」を開いてください。
)
echo.
pause
