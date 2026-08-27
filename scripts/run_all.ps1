# run_all.ps1 - 長いセットアップ手順を1つのコンソールで順に実行するラッパー
# - 各ステップの開始・成功・失敗をコンソールと install.log の両方に記録する
# - いずれかのステップが失敗したら、そこで停止して終了コードを返す
# - 失敗時は実際のエラー内容（例外メッセージ・出力末尾）を step_error.txt に書き、
#   インストーラのエラーダイアログに表示する（install.logが作れない状況でも原因が分かる）
# 終了コード: 0 = 全ステップ成功 / 非0 = 失敗
param(
    [string]$AppDir,
    [string]$TmpDir,
    [string]$PythonVersion = '3.12.10',
    [string]$Model = 'qwen2.5:3b',
    [string]$OpenWebuiVersion = '0.11.0',
    [string]$ProgressIni = ''
)

$ErrorActionPreference = 'Continue'
$LogFile = Join-Path $AppDir 'install.log'
$here = $PSScriptRoot
$ErrorFile = Join-Path $TmpDir 'step_error.txt'

# --- インストーラ進捗ファイル（％・ラベル・完了コード）への書き込み ---
# Inno Setup 側が毎秒読み取り、進捗バーに％と残り時間を表示する（v1.0.53）。
# UTF-16 (BOM) で書くことで Inno のワイド文字列 API で日本語ラベルが化けないようにする
function Write-InstallerProgress {
    param([int]$Percent, [string]$Label, [int]$Done = -1)
    if (-not $ProgressIni) { return }
    try {
        $lines = '[progress]', "percent=$Percent", "label=$Label", "done=$Done"
        Set-Content -Path $ProgressIni -Value $lines -Encoding Unicode
    } catch { }
}

function Save-Error {
    param([string]$Message)
    $Message | Out-File -FilePath $ErrorFile -Encoding ascii
}

# --- 事前チェック: アプリフォルダに書き込めるか（作成できない場合は即座に原因を報告） ---
try {
    New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
    $testFile = Join-Path $AppDir '.write_test'
    Set-Content -Path $testFile -Value 'ok' -ErrorAction Stop
    Remove-Item $testFile -ErrorAction SilentlyContinue
}
catch {
    $msg = "FATAL: cannot write to $AppDir - $($_.Exception.Message)"
    Save-Error $msg
    Write-Output $msg
    Write-InstallerProgress 0 $msg 90
    exit 90
}

function Log-Console {
    param([string]$Message)
    Write-Output $Message
    try { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message" | Out-File -FilePath $LogFile -Append -Encoding utf8 } catch { }
}

function Invoke-Step {
    param(
        [string]$Name,
        [string]$Script,
        [string[]]$Params,
        [int]$PctBefore,       # ステップ開始時の％
        [int]$PctAfter,        # ステップ成功時の％
        [string]$LabelBefore,  # ステップ中の表示ラベル
        [string]$LabelAfter    # ステップ完了直後のラベル
    )
    Log-Console "===== STEP START: $Name ====="
    Write-InstallerProgress $PctBefore $LabelBefore
    $outFile = Join-Path $TmpDir 'step_output.txt'
    # PS 5.1 は配列引数をネイティブコマンドに渡す際、空白を含むパスを
    # 「C:\Program」のように途中で切ってしまう（= 根本原因）。
    # cmd /c でコマンドライン文字列を組み立て、引用符を確実に保持する
    $childPath = Join-Path $here $Script
    $cmdLine = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $childPath + '"'
    foreach ($p in $Params) { $cmdLine += ' "' + $p + '"' }
    & cmd.exe /d /c $cmdLine 2>&1 | Tee-Object -FilePath $outFile
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Log-Console "===== STEP FAILED: $Name (exit $code) ====="
        $tail = Get-Content $outFile -Tail 15 -ErrorAction SilentlyContinue
        Save-Error ("STEP FAILED: $Name (exit $code)`n" + ($tail -join "`n"))
        Get-Content $outFile -Tail 15 -ErrorAction SilentlyContinue | ForEach-Object { Log-Console "  | $_" }
        Write-InstallerProgress $PctBefore "ステップ失敗: $Name" $code
        exit $code
    }
    Log-Console "===== STEP OK: $Name ====="
    Write-InstallerProgress $PctAfter $LabelAfter
}

# 進捗％の配分: Python 0-3 / Ollama 3-35 / AIモデル 35-80 / Open WebUI 80-98（実測時間に基づく）
$piArg = @()
if ($ProgressIni) { $piArg = @('-ProgressIni', $ProgressIni) }

Write-InstallerProgress 1 '準備中（Python環境）...'
Invoke-Step -Name 'Python (3.12)' -Script 'setup_python.ps1' -Params (@('-AppDir', $AppDir, '-TmpDir', $TmpDir, '-Version', $PythonVersion) + $piArg) -PctBefore 1 -PctAfter 3 -LabelBefore 'Python環境を導入中...' -LabelAfter 'Python環境が完了しました'
Invoke-Step -Name 'Ollama' -Script 'setup_ollama.ps1' -Params (@('-AppDir', $AppDir, '-TmpDir', $TmpDir) + $piArg) -PctBefore 3 -PctAfter 35 -LabelBefore 'Ollama（AI実行エンジン）を導入中...' -LabelAfter 'Ollamaが完了しました'
Invoke-Step -Name 'AI Models' -Script 'setup_openwebui.ps1' -Params (@('-AppDir', $AppDir, '-Mode', 'models', '-Model', $Model) + $piArg) -PctBefore 35 -PctAfter 80 -LabelBefore 'AIモデルをダウンロード中...' -LabelAfter 'AIモデルが完了しました'
Invoke-Step -Name 'Open WebUI' -Script 'setup_openwebui.ps1' -Params (@('-AppDir', $AppDir, '-Mode', 'app', '-OpenWebuiVersion', $OpenWebuiVersion) + $piArg) -PctBefore 80 -PctAfter 98 -LabelBefore 'Open WebUI（アプリ画面）を導入中...' -LabelAfter 'Open WebUIが完了しました'

Log-Console '===== ALL STEPS COMPLETED ====='
Write-InstallerProgress 100 'ダウンロード完了。サービスを登録しています...' 0
exit 0
