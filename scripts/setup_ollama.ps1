# setup_ollama.ps1 - Ollama を公式インストーラでサイレント導入
# - 既に導入済みならスキップ（冪等）
# - 公式サービス「Ollama」が無い・停止している場合は NSSM フォールバック登録（ShineosOllama）
# - API (127.0.0.1:11434) の起動を最大60秒待つ
# 終了コード: 0 = 成功 / 13 = ダウンロード失敗（ネットワーク起因） / 非0 = 失敗
param(
    [string]$AppDir,
    [string]$TmpDir,
    [string]$ProgressFile = '',
    [string]$ProgressIni = ''
)

$ErrorActionPreference = 'Stop'
# v1.0.76: ダウンロード失敗（ネットワーク起因）は 13 で区別して返す
$script:NetFail = $false
$LogFile = Join-Path $AppDir 'install.log'
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
. (Join-Path $PSScriptRoot 'ollama_common.ps1')
# install.log はインストーラ画面（ログ末尾表示）と同時に読み書きされるため一瞬ロードで衝突しうる。
# ログは手段なので、書き込み失敗ではインストール全体を止めない（短リトライのみ・最悪黙って捨てる）v1.0.59
function Log { param([string]$Message) $l = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"; for ($i = 0; $i -lt 5; $i++) { try { $l | Out-File -FilePath $LogFile -Append -Encoding utf8 -ErrorAction Stop; return } catch { Start-Sleep -Milliseconds 150 } } }
function Progress {
    param([string]$Message)
    if ($ProgressFile) {
        [System.IO.File]::AppendAllText($ProgressFile, "$Message`n", (New-Object System.Text.UTF8Encoding($false)))
    }
}

# インストーラ進捗INIへの書き込み（％は 3〜35 の範囲を担当: v1.0.53）
function Write-InstallerProgress {
    param([int]$Percent, [string]$Label)
    if (-not $ProgressIni) { return }
    try {
        $lines = '[progress]', "percent=$Percent", "label=$Label", 'done=-1'
        Set-Content -Path $ProgressIni -Value $lines -Encoding Unicode
    } catch { }
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
    Write-InstallerProgress 3 'Ollama（AI実行エンジン）のダウンロード・導入中...'
    $installer = Join-Path $TmpDir 'OllamaSetup.exe'
    if (-not (Test-Path $installer)) {
        if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
            throw 'curl.exe not found (Windows 10 1803 以降が必要)'
        }
        $url = 'https://github.com/ollama/ollama/releases/latest/download/OllamaSetup.exe'
        Log "downloading $url"
        Progress 'downloading Ollama (1.5GB)...'
        Write-InstallerProgress 4 'Ollama本体をダウンロード中（約1.5GB・数分〜20分）...'
        # -sS: 進捗メーターを抑制（PowerShell 5.1 は stderr 出力をエラー扱いし、
        # ユーザーに「エラー」と誤解させるため。エラー時のみ表示する）
        # -C -: 途中で切断された場合は続きから再開（レジューム）
        # --retry-all-errors --retry 10: 接続リセット等の一時エラーでも自動再試行
        # v1.0.53: Start-Process で起動し、ファイルサイズをポーリングして進捗％を
        # インストーラに通知する（ユーザーが中断しないよう％と残り時間を表示するため）
        $dl = Start-Process -FilePath 'curl.exe' -ArgumentList @(
            '-sS','-L','--fail','-C','-','--retry','10','--retry-all-errors','--retry-delay','5',
            '--connect-timeout','30','-o',$installer,$url
        ) -PassThru -WindowStyle Hidden
        while (-not $dl.HasExited) {
            Start-Sleep -Milliseconds 900
            try {
                $sz = (Get-Item $installer -ErrorAction SilentlyContinue).Length
                if ($sz) {
                    $ratio = [math]::Min(1.0, $sz / 1.65GB)
                    $pct = 4 + [int]($ratio * 29)
                    if ($pct -gt 33) { $pct = 33 }
                    Write-InstallerProgress $pct ("Ollama本体をダウンロード中... {0:N0} MB / 約1,650 MB" -f ($sz / 1MB))
                }
            } catch { }
        }
        $dlCode = $dl.ExitCode
        if ($dlCode -ne 0) {
            # 不完全なファイルが残ると次回も破損ファイルでインストールを試みるため削除する
            Remove-Item $installer -Force -ErrorAction SilentlyContinue
            $script:NetFail = $true
            throw "OllamaSetup download failed (curl exit $dlCode)"
        }
        Write-InstallerProgress 34 'Ollama本体のダウンロード完了。インストール中...'
        $size = (Get-Item $installer).Length
        Log "downloaded: $([math]::Round($size / 1MB, 1)) MB"
        Progress "download complete: $([math]::Round($size / 1MB, 1)) MB"
        $script:NetFail = $true
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
    Write-InstallerProgress 33 'Ollamaは最新のためダウンロード不要です'
}
Log "ollama.exe: $ollamaExe"

# --- NSSM を永続場所（{app}\tools）に確保する ---
# {tmp} の nssm.exe はインストーラ終了時に消えるため、そのままサービス登録すると
# ImagePath が tmp を指し、PC 再起動後に ShineosOllama が起動しなくなる（v1.0.47 修正）
$nssmTmp = Join-Path $TmpDir 'nssm.exe'
if (-not (Test-Path $nssmTmp)) { throw "nssm.exe not found: $nssmTmp" }
$nssmDurable = Join-Path $AppDir 'tools\nssm.exe'
New-Item -ItemType Directory -Force -Path (Split-Path $nssmDurable) | Out-Null
try {
    Copy-Item $nssmTmp $nssmDurable -Force -ErrorAction Stop
    Log "nssm staged at durable path: $nssmDurable"
} catch {
    # 既に durable nssm が存在する場合（稼働中サービスがロック等で書き換えでき
    # なかった場合）は既存の利用を許容する。ファイルは次回更新時に置き換わる
    if (Test-Path $nssmDurable) {
        Log "WARNING: could not overwrite durable nssm (locked?), keeping existing: $($_.Exception.Message)"
    } else {
        throw
    }
}

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
    # GPU / CPU を検出し、機種ごとに最適な動作モードを決める（v1.0.58）。
    #   cuda / amddgpu: Ollama が自動で GPU オフロードする（我々は設定を妨げない）
    #   cpu           : 内蔵GPUのみ・専用GPUなし → CPU 推論。他アプリ優先の CPU 制御を適用する
    # どちらのモードでも「他のアプリへの影響を最小化」する方針は共通。
    $script:GpuMode = Get-GpuAcceleration
    $ramGB2 = Get-RamGB
    Log "detected GPU mode: $script:GpuMode (RAM: ${ramGB2}GB)"
    try {
        $gpuNote = switch ($script:GpuMode) {
            'cuda'    { 'NVIDIA GPU を検出: AI処理をGPUで実行します（高速）' }
            'amddgpu' { 'AMD GPU を検出: 対応カードの場合はGPUで実行します' }
            default   { 'GPU非搭載のため CPU で実行します（回答まで10〜20秒程度）' }
        }
        [System.IO.File]::WriteAllText((Join-Path $AppDir 'gpu_mode.txt'),
            "$($script:GpuMode)`n$gpuNote`n", (New-Object System.Text.UTF8Encoding($false)))
    } catch { Log "WARNING: could not write gpu_mode.txt: $($_.Exception.Message)" }

    # RAM を検出し、搭載量に応じて同時常駐モデル数を決める（v1.0.48）
    # 14GB 以上: LLM（約2GB）+ 埋め込み（約1.2GB）の2モデルを同時常駐させ、
    #            質問ごとのモデル切替（約4〜8秒の再ロード）を排除して応答を高速化
    # 14GB 未満: 従来どおり1モデル（メモリ節約優先）
    $ramGB = $ramGB2
    $maxLoaded = if ($ramGB -ge 14) { '2' } else { '1' }
    Log "detected RAM: ${ramGB}GB -> OLLAMA_MAX_LOADED_MODELS=$maxLoaded"
    $tuning = [ordered]@{
        'OLLAMA_MAX_LOADED_MODELS' = $maxLoaded   # RAM連動: 2モデル常駐でモデル切替の再ロード待ちを排除
        'OLLAMA_KV_CACHE_TYPE'     = 'q8_0' # KVキャッシュ量子化でメモリ約半減（品質影響ほぼなし）
        'OLLAMA_FLASH_ATTENTION'   = '1'    # 長文コンテキストの高速化（KV量子化の前提条件）
        'OLLAMA_KEEP_ALIVE'        = '60m'  # モデルを1時間常駐させ、再ロード待ちによる応答遅延を防止
        'OLLAMA_NUM_PARALLEL'      = '1'    # 単一ユーザー用途。並列数を抑えてメモリを節約
    }
    Log 'applying Ollama performance tuning (env vars)'
    foreach ($k in $tuning.Keys) {
        [Environment]::SetEnvironmentVariable($k, $tuning[$k], 'Machine')
    }
    # 公式トレイアプリ（ollama app.exe）のRunキー自動起動を無効化する。
    # 0.33系のOllamaSetupはユーザーログオン起動方式のため、サービスと二重起動すると
    # ポート11434が競合しNSSMフォールバックが「一時停止」になる（v1.0.47 実機検証）。
    # 常駐は Windowsサービスに一本化する
    foreach ($rk in 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run') {
        try {
            Remove-ItemProperty -Path $rk -Name 'Ollama' -ErrorAction Stop
            Log "removed autorun: $rk\Ollama"
        } catch { }
    }
    Get-Process -Name 'ollama', 'ollama app' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    # トレイアプリ本体を無効化（リネーム）する（v1.0.48）。
    # ユーザーに「見覚えのないアプリが起動した」と誤解されるのを防ぐため、
    # トレイ（ollama app.exe）は一切起動できないようにする。サービスで常駐する
    # ollama.exe（サーバー本体）には影響しない。元に戻す場合は .disabled をリネーム。
    foreach ($tray in @(
            (Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama app.exe'),
            (Join-Path $env:ProgramFiles 'Ollama\ollama app.exe')
        )) {
        if (Test-Path $tray) {
            try {
                Rename-Item -LiteralPath $tray -NewName ($tray + '.disabled') -Force
                Log "disabled tray app: $tray -> .disabled"
            } catch { Log "WARNING: could not disable tray: $tray : $($_.Exception.Message)" }
        }
        # 過去に無効化済みで本体が残るケースは掃除しない（冪等）
    }
    # NSSMフォールバックサービスにも直接注入（システム環境変数の再読込を待たず確実に反映）
    $fbSvc2 = Get-Service -Name 'ShineosOllama' -ErrorAction SilentlyContinue
    if ($fbSvc2) {
        $nssm2 = $nssmDurable
        if (Test-Path $nssm2) {
            $envList = @()
            foreach ($k in $tuning.Keys) { $envList += ('"' + $k + '=' + $tuning[$k] + '"') }
            & $nssm2 set ShineosOllama AppEnvironmentExtra $envList | Out-Null
            # プロセス優先度は CPU 推論機のみ「低」にする。NVIDIA GPU 機では
            # 生成処理がGPUへオフロードされCPUが遊ぶため、優先度制御は不要（速度最優先）
            if ($script:GpuMode -eq 'cuda') {
                & $nssm2 set ShineosOllama AppPriority NORMAL_PRIORITY_CLASS | Out-Null
            } else {
                & $nssm2 set ShineosOllama AppPriority BELOW_NORMAL_PRIORITY_CLASS | Out-Null
            }
        }
    }

    # ollama.exe を「通常以下」のCPU優先度で起動する（CPU推論機での他アプリ保護）
    # 実測（Ryzen 7 5700U・16GB RAM）では AI 応答中に CPU 占有率が約82%に達し、
    # 他アプリの操作感が損なわれる。IFEO の PerfOptions で優先度を BELOW NORMAL に固定すると、
    # CPU を取り合う場面で Windows スケジューラが必ず他アプリ（通常優先度）を優先する。
    # システムがアイドルの間は速度は低下しない（優先度は競合時の順序であり、
    # 空いている CPU は低優先スレッドにも与えられるため）。
    # NVIDIA GPU 機では生成がGPUへオフロードされCPU負荷が小さいため、この制御を
    # 適用せず速度を最優先する（GPU機でもCPUもみ合いは発生しにくい）。
    # 公式サービス・NSSMフォールバック・直接起動のすべての起動経路へ効くよう Image 単位で設定する。
    if ($script:GpuMode -eq 'cuda') {
        Log 'skip IFEO CPU priority (NVIDIA GPU offload active)'
    }
    else {
    $ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\ollama.exe\PerfOptions'
    try {
        New-Item -Path $ifeo -Force | Out-Null
        Set-ItemProperty -Path $ifeo -Name 'CpuPriorityClass' -Value 5 -Type DWord
        Log 'set ollama.exe CPU priority to BELOW NORMAL (IFEO PerfOptions)'
    }
    catch { Log "WARNING: failed to set IFEO priority: $($_.Exception.Message)" }
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
        $nssm = $nssmDurable
        Log "registering fallback service with durable nssm: $nssm"
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
    $nssm = $nssmDurable

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
    if ($script:NetFail) { exit 13 } else { exit 1 }
}
exit 0
