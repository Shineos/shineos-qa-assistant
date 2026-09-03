# run_all.ps1 - セットアップ手順を実行するオーケストレーター（並列版 v1.0.77）
# - Python ∥ Ollama、モデルDL ∥ Open WebUI を2フェーズで並列実行しインストール時間を短縮
# - 各ステップの開始・成功・失敗をコンソールと install.log の両方に記録する
# - いずれかのステップが失敗したら、そこで停止して終了コードを返す
# - 失敗時は実際のエラー内容（例外メッセージ・出力末尾）を step_error.txt に書き、
#   インストーラのエラーダイアログに表示する（install.logが作れない状況でも原因が分かる）
# - -DefenderExclusion 1 で、インストール中のみ Defender の検査対象外にする
#   （完了時に自動で解除。venv/pip の大量ファイル展開が高速化される場合がある）
# 終了コード: 0 = 全ステップ成功 / 非0 = 失敗（13 = ネットワーク起因のダウンロード失敗）
param(
    [string]$AppDir,
    [string]$TmpDir,
    [string]$PythonVersion = '3.12.10',
    [string]$Model = 'qwen2.5:3b',
    [string]$OpenWebuiVersion = '0.11.0',
    [string]$ProgressIni = '',
    [int]$DefenderExclusion = 0
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

# --- Defender の一時除外（v1.0.77・インストールタスクで有効化した場合のみ） ---
# venv/pip の数千ファイル展開に対するリアルタイム検査がボトルネックになるため、
# インストール中のみ対象外にし、完了時に必ず解除する。Defender 以外の環境では何もしない
$script:DefenderAdded = @()

function Add-DefenderExclusions {
    if ($DefenderExclusion -ne 1) { return }
    $paths = @(
        $AppDir,
        (Join-Path $env:LOCALAPPDATA 'Programs\Ollama'),
        (Join-Path $env:LOCALAPPDATA 'pip'),
        'C:\Windows\System32\config\systemprofile\.ollama'
    )
    foreach ($p in $paths) {
        if (-not $p) { continue }
        try {
            Add-MpPreference -ExclusionPath $p -ErrorAction Stop
            $script:DefenderAdded += $p
            Log-Console "defender exclusion added: $p"
        } catch {
            Log-Console "defender exclusion skipped: $p"
        }
    }
}

function Remove-DefenderExclusions {
    foreach ($p in $script:DefenderAdded) {
        try {
            Remove-MpPreference -ExclusionPath $p -ErrorAction Stop
            Log-Console "defender exclusion removed: $p"
        } catch {
            Log-Console "defender exclusion removal failed: $p"
        }
    }
    $script:DefenderAdded = @()
}

# --- 並列ジョブ（v1.0.77）: 各ステップを独立した PowerShell プロセスで起動し、
# 進捗はジョブごとの INI に書かせる。親（本スクリプト）が 1 秒ごとにポーリングし、
# 両ジョブの進捗を合成してインストーラ進捗 INI に書き込む ---
function Start-StepJob {
    param([string]$Name, [string]$Script, [string[]]$Params, [string]$JobIni)
    $childPath = Join-Path $here $Script
    $cmdLine = '-NoProfile -ExecutionPolicy Bypass -File "' + $childPath + '"'
    foreach ($p in $Params) { $cmdLine += ' "' + $p + '"' }
    $cmdLine += ' -ProgressIni "' + $JobIni + '"'
    # v1.0.77: Start-Process -PassThru（-Wait なし + リダイレクト）は終了コードを
    # 取れない場合がある（PS 5.1 実機検証）ため、.NET Process API で直接起動する。
    # CreateNoWindow でウィンドウ非表示、出力は親のコンソールへ継承される
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'powershell.exe'
    $psi.Arguments = $cmdLine
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    Write-Host "===== STEP START: $Name (parallel job) ====="
    $null = Log-Console "===== STEP START: $Name (parallel job) ====="
    return @{ Name = $Name; Proc = $proc; Ini = $JobIni; LastPct = -1; Label = '' }
}

function Get-IniValue {
    param([string]$Path, [string]$Key)
    if (-not (Test-Path $Path)) { return '' }
    $line = Select-String -Path $Path -Pattern ('^' + $Key + '=(.*)$') -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($line -and $line.Matches.Count -gt 0) { return $line.Matches[0].Groups[1].Value }
    return ''
}

function Wait-JobPhase {
    param([array]$Jobs, [int]$LoPct, [scriptblock]$Mapper)
    while ($true) {
        $allDone = $true
        $mapped = @()
        $labels = @()
        foreach ($j in $Jobs) {
            if (-not $j['Proc'].HasExited) { $allDone = $false }
            $p = Get-IniValue $j['Ini'] 'percent'
            if ($p -ne '') { $j['LastPct'] = [int]$p }
            if ($j['LastPct'] -ge 0) {
                $m = & $Mapper $j['Name'] $j['LastPct']
                if ($m -ge 0) { $mapped += $m }
            }
            $l = Get-IniValue $j['Ini'] 'label'
            if ($l -ne '') { $j['Label'] = $l }
            if ($j['Label'] -ne '') { $labels += ($j['Name'] + ': ' + $j['Label']) }
        }
        if ($mapped.Count -gt 0) {
            $overall = ($mapped | Measure-Object -Minimum).Minimum
            Write-InstallerProgress $overall ($labels -join ' ｜ ')
        }
        if ($allDone) { break }
        Start-Sleep -Seconds 1
    }
    # 失敗判定: ネットワーク起因（13）を優先して報告する
    $failedJob = $null
    $fallbackJob = $null
    foreach ($j in $Jobs) {
        $code = 0
        try {
            # ExitCode は WaitForExit 経由でのみ確実に取得できる（null 返しの回避）
            $j['Proc'].WaitForExit()
            $code = $j['Proc'].ExitCode
            if ($null -eq $code) { $code = 1 }
        } catch { $code = 1 }
        if ($code -ne 0) {
            $fallbackJob = @{ Name = $j['Name']; Code = $code; OutFile = $j['OutFile'] }
            if ($code -eq 13) { $failedJob = $fallbackJob; break }
        }
    }
    if (-not $failedJob) { $failedJob = $fallbackJob }
    if ($failedJob) {
        Write-Host ("===== STEP FAILED: " + $failedJob['Name'] + " (exit " + $failedJob['Code'] + ") =====")
        $null = Log-Console ("===== STEP FAILED: " + $failedJob['Name'] + " (exit " + $failedJob['Code'] + ") =====")
        Save-Error ("STEP FAILED: " + $failedJob['Name'] + " (exit " + $failedJob['Code'] + ") - details in install.log (ERROR / STACK lines)")
        return $failedJob
    }
    foreach ($j in $Jobs) {
        Write-Host ("===== STEP OK: " + $j['Name'] + " =====")
        $null = Log-Console ("===== STEP OK: " + $j['Name'] + " =====")
    }
    return $null
}

# 進捗％の配分（v1.0.77 並列化）:
#   Phase 1（1-35）: Python 1-10 / Ollama 10-35
#   Phase 2（35-98）: モデルDL 35-70 / Open WebUI 70-98
# 各ジョブは自スコープ内の絶対％を INI に書くため、マッパーで合成％へ変換する
$mapperPhase1 = {
    param($name, $p)
    if ($name -eq 'Python') {
        $v = 1 + (($p - 1) * 9) / 2
        return [math]::Min(10, [math]::Max(1, [int]$v))
    }
    $v = 10 + (($p - 3) * 25) / 32
    return [math]::Min(35, [math]::Max(10, [int]$v))
}
$mapperPhase2 = {
    param($name, $p)
    if ($name -eq 'Models') {
        $v = 35 + (($p - 35) * 35) / 45
        return [math]::Min(70, [math]::Max(35, [int]$v))
    }
    $v = 70 + (($p - 80) * 28) / 18
    return [math]::Min(98, [math]::Max(70, [int]$v))
}

Add-DefenderExclusions

try {
    # ---- Phase 1: Python ∥ Ollama（互いに依存しないため並列） ----
    Write-InstallerProgress 1 'Python環境とOllamaを並行で導入しています...'
    Log-Console '===== PHASE 1: Python + Ollama (parallel) ====='
    $jobs = @(
        (Start-StepJob -Name 'Python' -Script 'setup_python.ps1' -Params @('-AppDir', $AppDir, '-TmpDir', $TmpDir, '-Version', $PythonVersion) -JobIni (Join-Path $TmpDir 'progress_python.ini')),
        (Start-StepJob -Name 'Ollama' -Script 'setup_ollama.ps1' -Params @('-AppDir', $AppDir, '-TmpDir', $TmpDir) -JobIni (Join-Path $TmpDir 'progress_ollama.ini'))
    )
    $fail = Wait-JobPhase -Jobs $jobs -LoPct 1 -Mapper $mapperPhase1
    if ($fail) {
        Write-InstallerProgress 1 ("ステップ失敗: " + $fail.Name) $fail.Code
        exit $fail.Code
    }

    # ---- Phase 2: モデルDL ∥ Open WebUI（Ollama と Python が揃ったため並列） ----
    Write-InstallerProgress 35 'AIモデルのダウンロードとOpen WebUIの導入を並行で進めています...'
    Log-Console '===== PHASE 2: Models + Open WebUI (parallel) ====='
    $jobs = @(
        (Start-StepJob -Name 'Models' -Script 'setup_openwebui.ps1' -Params @('-AppDir', $AppDir, '-Mode', 'models', '-Model', $Model) -JobIni (Join-Path $TmpDir 'progress_models.ini')),
        (Start-StepJob -Name 'App' -Script 'setup_openwebui.ps1' -Params @('-AppDir', $AppDir, '-Mode', 'app', '-OpenWebuiVersion', $OpenWebuiVersion) -JobIni (Join-Path $TmpDir 'progress_app.ini'))
    )
    $fail = Wait-JobPhase -Jobs $jobs -LoPct 35 -Mapper $mapperPhase2
    if ($fail) {
        Write-InstallerProgress 35 ("ステップ失敗: " + $fail.Name) $fail.Code
        exit $fail.Code
    }

    Log-Console '===== ALL STEPS COMPLETED ====='
    Write-InstallerProgress 100 'ダウンロード完了。サービスを登録しています...' 0
    exit 0
}
finally {
    Remove-DefenderExclusions
}
