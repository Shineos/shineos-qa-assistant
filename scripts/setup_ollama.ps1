# setup_ollama.ps1 - Ollama を公式インストーラでサイレント導入
# - 既に導入済みならスキップ（冪等）
# - 公式サービス「Ollama」が無い・停止している場合は NSSM フォールバック登録（ShineosOllama）
# - API (127.0.0.1:11434) の起動を最大60秒待つ
# 終了コード: 0 = 成功 / 非0 = 失敗
param(
    [string]$AppDir,
    [string]$TmpDir,
    [string]$ProgressFile = ''
)

$ErrorActionPreference = 'Stop'
$LogFile = Join-Path $AppDir 'install.log'
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
function Log { param([string]$Message) "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message" | Out-File -FilePath $LogFile -Append -Encoding utf8 }
function Progress {
    param([string]$Message)
    if ($ProgressFile) {
        [System.IO.File]::AppendAllText($ProgressFile, "$Message`n", (New-Object System.Text.UTF8Encoding($false)))
    }
}

# インストーラがハングした場合に備えたタイムアウト付き実行
function Invoke-WithTimeout {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList,
        [int]$TimeoutSec = 600,
        [string]$Label = 'process'
    )
    $p = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -PassThru
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while (-not $p.HasExited) {
        if ((Get-Date) -gt $deadline) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            throw "$Label timed out after ${TimeoutSec}s and was killed: $FilePath"
        }
        Start-Sleep -Seconds 2
    }
    return $p.ExitCode
}

function Get-OllamaExe {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'),
        (Join-Path $env:ProgramFiles 'Ollama\ollama.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

try {
Log '--- setup_ollama start ---'
Progress 'preparing Ollama'
$ollamaExe = Get-OllamaExe

# --- 古いバージョンは最新に更新する ---
# （旧クライアントは新しいモデル（qwen2.5系など）のマニフェストを取得できないため。
#   例: 0.13.5 のような旧版では pull が即失敗する）
$needUpgrade = $false
if ($ollamaExe) {
    $ver = & $ollamaExe --version 2>&1
    Log "ollama version: $ver"
    Progress "ollama version: $ver"
    # stderr（"Warning: could not connect..."）と stdout が別要素の配列で返るため、
    # 先にスカラーへ結合しないと $Matches が設定されず $Matches[1] が例外になる（PS 5.1）
    $verText = $ver -join ' '
    if ($verText -match 'version is (\d+)\.(\d+)') {
        if ([int]$Matches[1] -eq 0 -and [int]$Matches[2] -lt 30) { $needUpgrade = $true }
    }
    else { $needUpgrade = $true }
}
else { $needUpgrade = $true }

if ($needUpgrade) {
    $installer = Join-Path $TmpDir 'OllamaSetup.exe'
    if (-not (Test-Path $installer)) {
        if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
            throw 'curl.exe not found (Windows 10 1803 以降が必要)'
        }
        $url = 'https://github.com/ollama/ollama/releases/latest/download/OllamaSetup.exe'
        Log "downloading $url"
        Progress 'downloading Ollama (1.5GB)...'
        # -sS: 進捗メーターを抑制（PowerShell 5.1 は stderr 出力をエラー扱いし、
        # ユーザーに「エラー」と誤解させるため。エラー時のみ表示する）
        # -C -: 途中で切断された場合は続きから再開（レジューム）
        # --retry-all-errors --retry 10: 接続リセット等の一時エラーでも自動再試行
        & curl.exe -sS -L --fail -C - --retry 10 --retry-all-errors --retry-delay 5 --connect-timeout 30 -o $installer $url
        if ($LASTEXITCODE -ne 0) {
            # 不完全なファイルが残ると次回も破損ファイルでインストールを試みるため削除する
            Remove-Item $installer -Force -ErrorAction SilentlyContinue
            throw "OllamaSetup download failed (curl exit $LASTEXITCODE)"
        }
        $size = (Get-Item $installer).Length
        Log "downloaded: $([math]::Round($size / 1MB, 1)) MB"
        Progress "download complete: $([math]::Round($size / 1MB, 1)) MB"
        if ($size -lt 100MB) { throw "OllamaSetup download looks invalid (${size} bytes) - proxy/block page の可能性" }
    }
    # 旧バージョンのOllamaプロセス/サービスが動作していると、実行中ファイルを
    # 置換できずインストールが失敗する（ERROR_ACCESS_DENIED = exit code 5）ため先に停止する
    Log 'stopping old Ollama services/processes before upgrade'
    Progress 'stopping old Ollama services...'
    & sc.exe stop ShineosOllama 2>$null | Out-Null
    & sc.exe stop Ollama 2>$null | Out-Null
    Get-Process -Name ollama -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    # プロセスの終了を確認（最大15秒）
    for ($i = 0; $i -lt 15; $i++) {
        if (-not (Get-Process -Name ollama -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Seconds 1
    }

    # 上書き更新は旧版アンインストーラがハングすることがあるため、
    # 先に旧版をアンインストールしてからクリーン導入する（各ステップはタイムアウト付き）
    $oldUnins = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\unins000.exe'
    if (Test-Path $oldUnins) {
        Log "uninstalling old Ollama: $oldUnins"
        Progress 'uninstalling old Ollama...'
        $uninsCode = Invoke-WithTimeout -FilePath $oldUnins -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -TimeoutSec 300 -Label 'old Ollama uninstaller'
        Log "old Ollama uninstaller exit code: $uninsCode"
    }

    Log 'installing Ollama (silent)'
    Progress 'installing Ollama (latest version)...'
    $installCode = Invoke-WithTimeout -FilePath $installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -TimeoutSec 900 -Label 'OllamaSetup'
    Log "OllamaSetup exit code: $installCode"
    if ($installCode -ne 0 -and $installCode -ne 3010) {
        # 診断情報（実行中プロセス・サービスの状態）
        Get-Process -Name ollama -ErrorAction SilentlyContinue | ForEach-Object { Log "ollama process running: $($_.Path)" }
        Get-Service -Name Ollama, ShineosOllama -ErrorAction SilentlyContinue | ForEach-Object { Log "service state: $($_.Name) = $($_.Status)" }
        throw "OllamaSetup exit code $installCode"
    }
    $ollamaExe = Get-OllamaExe
    if (-not $ollamaExe) { throw 'ollama.exe not found after installation' }
    $ver = & $ollamaExe --version 2>&1
    Log "ollama version after upgrade: $ver"
    Progress "ollama upgraded: $ver"
}
else {
    Log 'ollama version is current (no upgrade needed)'
    Progress 'ollama is up to date'
}
Log "ollama.exe: $ollamaExe"

# --- Ollama の起動（公式サービス → NSSM フォールバック → 直接起動 の3段構え） ---
function Test-OllamaApi {
    try {
        $r = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/version' -TimeoutSec 3
        return [bool]$r.version
    }
    catch { return $false }
}
function Wait-OllamaApi {
    param([int]$Seconds = 20)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-OllamaApi) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

# --- 低スペック機向け性能チューニング（メモリ最適化＋応答高速化） ---
# 設定はすべて冪等。システム環境変数（公式サービス・直接起動に効く）と
# NSSMフォールバックサービスへの直接注入の両方を行い、再起動で反映する
function Set-OllamaTuning {
    $tuning = [ordered]@{
        'OLLAMA_MAX_LOADED_MODELS' = '1'    # LLM/埋め込みモデルの同時常駐を防ぎRAMを節約
        'OLLAMA_KV_CACHE_TYPE'     = 'q8_0' # KVキャッシュ量子化でメモリ約半減（品質影響ほぼなし）
        'OLLAMA_FLASH_ATTENTION'   = '1'    # 長文コンテキストの高速化（KV量子化の前提条件）
        'OLLAMA_KEEP_ALIVE'        = '10m'  # モデルを10分常駐させ連続利用の応答を高速化
        'OLLAMA_NUM_PARALLEL'      = '1'    # 単一ユーザー用途。並列数を抑えてメモリを節約
    }
    Log 'applying Ollama performance tuning (env vars)'
    foreach ($k in $tuning.Keys) {
        [Environment]::SetEnvironmentVariable($k, $tuning[$k], 'Machine')
    }
    # NSSMフォールバックサービスにも直接注入（システム環境変数の再読込を待たず確実に反映）
    $fbSvc2 = Get-Service -Name 'ShineosOllama' -ErrorAction SilentlyContinue
    if ($fbSvc2) {
        $nssm2 = Join-Path $TmpDir 'nssm.exe'
        if (Test-Path $nssm2) {
            $envList = @()
            foreach ($k in $tuning.Keys) { $envList += ('"' + $k + '=' + $tuning[$k] + '"') }
            & $nssm2 set ShineosOllama AppEnvironmentExtra $envList | Out-Null
        }
    }
    # 環境変数はプロセス起動時に読まれるため、サービスを再起動して反映する
    Log 'restarting Ollama to apply tuning'
    & sc.exe stop Ollama 2>$null | Out-Null
    & sc.exe stop ShineosOllama 2>$null | Out-Null
    Get-Process -Name ollama -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    $official = Get-Service -Name 'Ollama' -ErrorAction SilentlyContinue
    if ($official) {
        & sc.exe config Ollama start= auto | Out-Null
        & sc.exe start Ollama | Out-Null
    }
    elseif ($fbSvc2) {
        & sc.exe start ShineosOllama | Out-Null
    }
    else {
        Start-Process -FilePath $ollamaExe -ArgumentList 'serve' -WindowStyle Hidden
    }
    if (-not (Wait-OllamaApi -Seconds 30)) {
        Log 'WARNING: Ollama did not become ready after tuning restart'
    }
}

function Complete-OllamaSetup {
    Set-OllamaTuning
    Log 'Ollama ready'
    Progress 'Ollama ready'
    Progress 'PROGRESS_DONE:0'
    exit 0
}

# 1) 既に起動していればサービス状態を確認し、未登録なら NSSM フォールバックを登録する
# （API が動いているだけでは PC 再起動後に自動起動しないため。
#   OllamaSetup が serve を直接起動した場合などにサービスが無いことがある）
if (Test-OllamaApi) {
    Log 'Ollama API already running'
    $svc = Get-Service -Name 'Ollama' -ErrorAction SilentlyContinue
    $fbSvc = Get-Service -Name 'ShineosOllama' -ErrorAction SilentlyContinue
    if (-not $svc -and -not $fbSvc) {
        Log 'no Ollama service registered - registering NSSM fallback (ShineosOllama)'
        Progress 'registering Ollama service...'
        $nssm = Join-Path $TmpDir 'nssm.exe'
        if (-not (Test-Path $nssm)) { throw "nssm.exe not found: $nssm" }
        $logDir = Join-Path $AppDir 'logs'
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        # 既存プロセスを停止してから登録（ポート競合防止）
        Get-Process -Name 'ollama','ollama app' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        & $nssm install ShineosOllama $ollamaExe 'serve' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'nssm install ShineosOllama failed' }
        & $nssm set ShineosOllama Start SERVICE_AUTO_START | Out-Null
        & $nssm set ShineosOllama AppStdout (Join-Path $logDir 'ollama.log') | Out-Null
        & $nssm set ShineosOllama AppStderr (Join-Path $logDir 'ollama.err.log') | Out-Null
        & $nssm start ShineosOllama | Out-Null
        if (Wait-OllamaApi -Seconds 30) {
            Log 'Ollama ready (NSSM fallback registered)'
        } else {
            Log 'WARNING: Ollama API did not become ready after NSSM registration'
        }
    }
    Complete-OllamaSetup
}

$svc = Get-Service -Name 'Ollama' -ErrorAction SilentlyContinue
$fbSvc = Get-Service -Name 'ShineosOllama' -ErrorAction SilentlyContinue

# 2) 公式サービスを優先して起動
if ($svc) {
    Log 'service "Ollama" found: ensure auto start and start'
    Progress 'starting Ollama service...'
    & sc.exe config Ollama start= auto | Out-Null
    if ($svc.Status -ne 'Running') { & sc.exe start Ollama | Out-Null }
    if (Wait-OllamaApi -Seconds 20) {
        # 公式が動いたのでフォールバックを削除（起動時競合防止）
        if ($fbSvc) {
            Log 'removing fallback service (ShineosOllama)'
            & sc.exe stop ShineosOllama 2>$null | Out-Null
            & sc.exe delete ShineosOllama 2>$null | Out-Null
        }
        Log 'Ollama ready (official service)'
        Complete-OllamaSetup
    }
    Log 'official service did not become ready - trying fallback'
}

# 3) NSSM フォールバックサービス（既存 or 新規登録）を起動
if (-not $fbSvc) {
    Log 'registering NSSM fallback (ShineosOllama)'
    Progress 'registering Ollama service...'
    $nssm = Join-Path $TmpDir 'nssm.exe'
    if (-not (Test-Path $nssm)) { throw "nssm.exe not found: $nssm" }
    $logDir = Join-Path $AppDir 'logs'
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    & $nssm install ShineosOllama $ollamaExe 'serve' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'nssm install ShineosOllama failed' }
    & $nssm set ShineosOllama Start SERVICE_AUTO_START | Out-Null
    & $nssm set ShineosOllama AppStdout (Join-Path $logDir 'ollama.log') | Out-Null
    & $nssm set ShineosOllama AppStderr (Join-Path $logDir 'ollama.err.log') | Out-Null
}
Log 'starting fallback service (ShineosOllama)'
Progress 'starting Ollama service...'
& sc.exe config ShineosOllama start= auto | Out-Null
if ((Get-Service -Name 'ShineosOllama' -ErrorAction SilentlyContinue).Status -ne 'Running') {
    & sc.exe start ShineosOllama | Out-Null
}
if (Wait-OllamaApi -Seconds 20) {
    Log 'Ollama ready (fallback service)'
    Complete-OllamaSetup
}

# 4) 最終フォールバック: サービスが機能しない場合はプロセスを直接起動
Log 'services failed - starting ollama.exe serve directly'
Progress 'starting Ollama directly...'
Start-Process -FilePath $ollamaExe -ArgumentList 'serve' -WindowStyle Hidden
if (Wait-OllamaApi -Seconds 15) {
    Log 'Ollama ready (direct process)'
    Complete-OllamaSetup
}

# 5) 診断情報を記録して失敗
Get-Service -Name Ollama, ShineosOllama -ErrorAction SilentlyContinue | ForEach-Object { Log "service state: $($_.Name) = $($_.Status)" }
Get-CimInstance Win32_Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -in @('Ollama', 'ShineosOllama') } | ForEach-Object { Log "service info: $($_.Name) state=$($_.State) path=$($_.PathName)" }
throw 'Ollama API (127.0.0.1:11434) did not become ready'
Log '--- setup_ollama done ---'
Progress 'Ollama ready'
Progress 'PROGRESS_DONE:0'
}
catch {
    Log "ERROR: $($_.Exception.Message)"
    Log "STACK: $($_.ScriptStackTrace)"
    Progress 'PROGRESS_DONE:1'
    exit 1
}
exit 0
