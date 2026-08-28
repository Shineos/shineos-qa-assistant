# setup_openwebui.ps1 - AIモデルのダウンロード / open-webui 実行環境の構築
# モード:
#   -Mode models : Ollama に LLM モデルと埋め込みモデルをダウンロード（長い処理）
#   -Mode app    : venv 作成 → torch(CPU) → open-webui をインストール（長い処理）
# 各処理は冪等（再実行時はスキップ）
# 終了コード: 0 = 成功 / 非0 = 失敗
param(
    [string]$AppDir,
    [string]$Mode,
    [string]$Model = 'qwen2.5:7b',
    [string]$EmbeddingModel = 'bge-m3',
    [string]$OpenWebuiVersion = '0.11.0',
    [string]$ProgressFile = '',
    [string]$ProgressIni = ''
)

$ErrorActionPreference = 'Stop'
$LogFile = Join-Path $AppDir 'install.log'
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
# install.log はインストーラ画面（ログ末尾表示）と同時に読み書きされるため一瞬ロードで衝突しうる。
# ログは手段なので、書き込み失敗ではインストール全体を止めない（短リトライのみ・最悪黙って捨てる）v1.0.59
function Log { param([string]$Message) $l = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"; for ($i = 0; $i -lt 5; $i++) { try { $l | Out-File -FilePath $LogFile -Append -Encoding utf8 -ErrorAction Stop; return } catch { Start-Sleep -Milliseconds 150 } } }
function Progress {
    param([string]$Message)
    if ($ProgressFile) {
        [System.IO.File]::AppendAllText($ProgressFile, "$Message`n", (New-Object System.Text.UTF8Encoding($false)))
    }
}

# インストーラ進捗INIへの書き込み（v1.0.53）。
# models モードは 35〜80、app モードは 80〜98 の範囲を担当
function Write-InstallerProgress {
    param([int]$Percent, [string]$Label)
    if (-not $ProgressIni) { return }
    try {
        $lines = '[progress]', "percent=$Percent", "label=$Label", 'done=-1'
        Set-Content -Path $ProgressIni -Value $lines -Encoding Unicode
    } catch { }
}

# ollama pull の出力行（"pulling xxx: 45% ... 850 MB/1.9 GB" 等）を解析して
# 指定範囲（$Base〜$Base+$Span）の％に変換しながら進捗INIへ書く
function Invoke-PullWithProgress {
    param([string]$OllamaExe, [string]$ModelName, [int]$Base, [int]$Span, [string]$DisplayName)
    $lastPct = $Base
    $out = & $OllamaExe pull $ModelName 2>&1 | ForEach-Object {
        Progress $_
        if ($_ -is [string] -and $_ -match '(\d{1,3})\s*%') {
            $p = [int]$Matches[1]
            if ($p -ge 0 -and $p -le 100) {
                $lastPct = $Base + [int]($p * $Span / 100)
                Write-InstallerProgress $lastPct ("AIモデルをダウンロード中: {0} ({1}%)" -f $DisplayName, $p)
            }
        }
        $_
    }
    return ,$out
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
if ($Mode -eq 'models') {
    # ---------- モデルダウンロード ----------
    Log "--- models: $Model / $EmbeddingModel ---"
    $ollama = Get-OllamaExe
    if (-not $ollama) { throw 'ollama.exe not found' }
    $ver = & $ollama --version 2>&1
    Log "ollama version: $ver"

    Log "pulling $Model"
    Progress "downloading model $Model..."
    # 進捗配分: LLM 35→62%、埋め込み 62→80%（サイズ比で重み付け: v1.0.53）
    Write-InstallerProgress 35 "AIモデルをダウンロード中: $Model"
    # 外部コマンドの stderr 出力（進捗）で NativeCommandError が発生しないよう
    # キャプチャ中のみ ErrorActionPreference を緩める
    $ErrorActionPreference = 'Continue'
    $pullOut = Invoke-PullWithProgress -OllamaExe $ollama -ModelName $Model -Base 35 -Span 27 -DisplayName $Model
    $pullCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($pullCode -ne 0) {
        $pullOut | ForEach-Object { Log "ollama-pull: $_" }
        $errLog = Join-Path $AppDir 'logs\ollama.err.log'
        if (Test-Path $errLog) {
            Log '--- ollama.err.log (last 30 lines) ---'
            Get-Content $errLog -Tail 30 | ForEach-Object { Log "ollama-err: $_" }
        }
        throw "model pull failed: $Model (exit $pullCode)"
    }
    Log "pulled $Model"
    Progress "model $Model downloaded"
    # 注: qwen3系の思考モード無効化は Modelfile では行わない（Ollama が
    # enable_thinking パラメータをサポートしていないため）。思考モード対応
    # モデルは configure_model.ps1 が Open WebUI のモデル設定経由で
    # think:false を適用する。

    Log "pulling $EmbeddingModel"
    Progress "downloading embedding model $EmbeddingModel (274MB)..."
    $ErrorActionPreference = 'Continue'
    $pullOut2 = Invoke-PullWithProgress -OllamaExe $ollama -ModelName $EmbeddingModel -Base 62 -Span 18 -DisplayName $EmbeddingModel
    $pullCode2 = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($pullCode2 -ne 0) {
        $pullOut2 | ForEach-Object { Log "ollama-pull: $_" }
        throw "embedding model pull failed: $EmbeddingModel (exit $pullCode2)"
    }
    Log "pulled $EmbeddingModel"
    Progress "embedding model downloaded"
    Log 'models ready'
}
elseif ($Mode -eq 'app') {
    # ---------- venv + torch(CPU) + open-webui ----------
    Log '--- app install ---'
    $pyExe = Join-Path $AppDir 'python\python.exe'
    # setup_python.ps1 が既存Pythonを再利用した場合は python-path.txt に実パスが記録される
    $pathFile = Join-Path $AppDir 'python-path.txt'
    if (Test-Path $pathFile) {
        $candidate = (Get-Content $pathFile -Raw).Trim()
        if ($candidate -and (Test-Path $candidate)) { $pyExe = $candidate }
    }
    if (-not (Test-Path $pyExe)) { throw "python.exe not found: $pyExe" }

    $venvDir = Join-Path $AppDir 'venv'
    $venvPython = Join-Path $venvDir 'Scripts\python.exe'
    Write-InstallerProgress 80 'Open WebUI（アプリ画面）を導入中...'
    if (-not (Test-Path $venvPython)) {
        Log 'creating venv'
        Progress 'creating python venv...'
        & $pyExe -m venv $venvDir
        if ($LASTEXITCODE -ne 0) { throw 'venv creation failed' }
    }

    Log 'upgrading pip'
    Progress 'upgrading pip...'
    & $venvPython -m pip install --upgrade pip
    if ($LASTEXITCODE -ne 0) { throw 'pip upgrade failed' }

    # torch は CPU 版を先に導入（Windows 既定の CUDA 版 2.5GB 超を回避）
    Log 'installing torch (CPU)'
    Progress 'installing torch (CPU)...'
    Write-InstallerProgress 82 'Open WebUI導入中: AI演算ライブラリ (torch) を導入...'
    & $venvPython -m pip install torch --index-url https://download.pytorch.org/whl/cpu
    if ($LASTEXITCODE -ne 0) { throw 'torch install failed' }
    Write-InstallerProgress 90 'Open WebUI導入中: 本体をインストール中...'

    Log "installing open-webui==$OpenWebuiVersion"
    Progress 'installing open-webui...'
    & $venvPython -m pip install "open-webui==$OpenWebuiVersion"
    if ($LASTEXITCODE -ne 0) { throw 'open-webui install failed' }
    Write-InstallerProgress 96 'Open WebUI導入中: ファイル生成ライブラリを導入...'

    # ファイル生成ツールサーバー用ライブラリ（PDF/PPTX/Word 生成）
    Log 'installing file generation libraries'
    & $venvPython -m pip install reportlab python-pptx python-docx openpyxl py7zr markdown2 beautifulsoup4 emoji "mcp<2"
    if ($LASTEXITCODE -ne 0) { throw 'file generation libraries install failed' }

    Log 'app install done'
    Progress 'open-webui installed'
    Write-InstallerProgress 98 'Open WebUIの導入が完了しました'
}
else {
    throw "unknown mode: $Mode"
}
Progress 'PROGRESS_DONE:0'
}
catch {
    Log "ERROR: $($_.Exception.Message)"
    Log "STACK: $($_.ScriptStackTrace)"
    Progress 'PROGRESS_DONE:1'
    exit 1
}
exit 0
