# register_service.ps1 - NSSM で Windowsサービス「ShineosQA」を登録・起動
# - システム環境変数は変更しない（NSSM の AppEnvironmentExtra に注入）
# - 再インストール時は既存サービスを削除し、data ディレクトリを初期化する
#   （WEBUI_AUTH=False は「ユーザー0の新規DB」でのみ有効なため）
# 終了コード: 0 = 成功 / 非0 = 失敗
param(
    [string]$AppDir,
    [int]$Port = 8080,
    [string]$Model = 'qwen2.5:3b'
)

$ErrorActionPreference = 'Stop'
$LogFile = Join-Path $AppDir 'install.log'
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null
# install.log はインストーラ画面（ログ末尾表示）と同時に読み書きされるため一瞬ロードで衝突しうる。
# ログは手段なので、書き込み失敗ではインストール全体を止めない（短リトライのみ・最悪黙って捨てる）v1.0.59
function Log { param([string]$Message) $l = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"; for ($i = 0; $i -lt 5; $i++) { try { $l | Out-File -FilePath $LogFile -Append -Encoding utf8 -ErrorAction Stop; return } catch { Start-Sleep -Milliseconds 150 } } }

try {
Log '--- register_service start ---'
$nssm = Join-Path $AppDir 'tools\nssm.exe'
if (-not (Test-Path $nssm)) { throw "nssm.exe not found: $nssm" }
$owui = Join-Path $AppDir 'venv\Scripts\open-webui.exe'
if (-not (Test-Path $owui)) { throw "open-webui.exe not found: $owui" }

$svc = 'ShineosQA'
$dataDir = Join-Path $AppDir 'data'
$logDir = Join-Path $AppDir 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# --- 既存サービス・データのクリーンアップ（アップグレード対応） ---
Log 'removing existing service (if any)'
& sc.exe stop $svc 2>$null | Out-Null
& sc.exe delete $svc 2>$null | Out-Null
Start-Sleep -Seconds 1

if (Test-Path $dataDir) {
    # v1.0.52: 画面から追加された文書を knowledge へ退避してからデータをバックアップ。
    # （アップグレードで社内文書が消えるのを防ぐ。古いバックアップは1世代のみ保持）
    $preservePy = Join-Path $AppDir 'scripts\preserve_uploads.py'
    $venvPy = Join-Path $AppDir 'venv\Scripts\python.exe'
    if ((Test-Path $preservePy) -and (Test-Path $venvPy)) {
        $pOut = & $venvPy $preservePy $AppDir 2>&1
        foreach ($l in $pOut) { Log "preserve: $l" }
    }
    # 古いバックアップを削除（1世代のみ保持）
    Get-ChildItem $AppDir -Directory -Filter 'data.backup-*' -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -Skip 1 | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    $bkName = 'data.backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
    try {
        Rename-Item -Path $dataDir -NewName $bkName -ErrorAction Stop
        Log "data backed up: $bkName （WEBUI_AUTH=False は新規DB前提のため初期化。文書は退避済み）"
    } catch {
        Log "WARNING: data backup failed, falling back to delete: $($_.Exception.Message)"
        Remove-Item -Recurse -Force $dataDir
    }
}

# --- サービス登録 ---
$secret = ([guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N'))
Log 'nssm install'
& $nssm install $svc $owui 'serve' "--port $Port" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'nssm install failed' }
& $nssm set $svc AppDirectory $AppDir | Out-Null
& $nssm set $svc Start SERVICE_AUTO_START | Out-Null
& $nssm set $svc AppStdout (Join-Path $logDir 'openwebui.log') | Out-Null
& $nssm set $svc AppStderr (Join-Path $logDir 'openwebui.err.log') | Out-Null
& $nssm set $svc AppRotateFiles 1 | Out-Null
& $nssm set $svc AppRotateBytes 10485760 | Out-Null

# 環境変数（値に空白を含むものは引用符で囲む。nssm 仕様）
# DEFAULT_MODELS には configure_model.ps1 が作成する「社内知恵袋」を指定
# （別名カスタムモデル。ツール無効化・思考モード無効化済みで「応答なし」を防止。
#   素のモデルタグを指定するとツールが有効のままで応答なしになるため）
# RAGのCHUNK_*は初回起動時のみ有効（PersistentConfig）→ 再インストールで確実に反映される
$envs = @(
    ('"DATA_DIR=' + $dataDir + '"'),
    ('"WEBUI_SECRET_KEY=' + $secret + '"'),
    '"WEBUI_AUTH=False"',
    '"ENABLE_SIGNUP=False"',
    '"OLLAMA_BASE_URL=http://127.0.0.1:11434"',
    '"DEFAULT_MODELS=社内知恵袋"',
    '"RAG_EMBEDDING_ENGINE=ollama"',
    '"RAG_EMBEDDING_MODEL=bge-m3"',
    # チャンク 300: 情報密度を上げ、スモールモデルが扱いやすい単位にする（500→300）
    '"CHUNK_SIZE=300"',
    '"CHUNK_OVERLAP=30"',
    '"RAG_TOP_K=3"',
    # embedding は直列処理（同期）にする。非同期だとローカル（Ollama）でキューが詰まり
    # ナレッジ登録が停滞するため（初回起動時の PersistentConfig）
    '"ENABLE_ASYNC_EMBEDDING=False"',
    # ハイブリッド検索（BM25 キーワード一致 + ベクトル意味一致のアンサンブル）。
    # 社内特有の型番・規程番号・固有名詞を漏らさずヒットさせる（初回起動時の PersistentConfig）
    '"ENABLE_RAG_HYBRID_SEARCH=True"',
    '"HYBRID_BM25_WEIGHT=0.5"',
    # Web 検索は既定 OFF（社内Q&Aツールとして、ON にすると質問内容が外部の
    # 検索サービスに送信されるため。必要な組織は管理画面から有効化する）
    '"ENABLE_WEB_SEARCH=False"',
    '"WEB_SEARCH_ENGINE=duckduckgo"',
    '"BYPASS_WEB_SEARCH_EMBEDDING_AND_RETRIEVAL=True"',
    '"BYPASS_WEB_SEARCH_WEB_LOADER=True"',
    '"ENABLE_NOTES=False"',
    '"ENABLE_VERSION_UPDATE_CHECK=False"',
    # v1.0.56: WEBUI_NAME は製品名のみ。Open WebUI への帰属は UI の2行目
    # 「powered by Open WebUI」（ラッパー挿入）と NOTICES で明示する。
    # （既定では " (Open WebUI)" が自動付加されるが patch_openwebui_branding.py で無効化）
    '"WEBUI_NAME=社内知恵袋"',
    # モデル一覧に表示しないモデル（bge-m3:latest は埋め込み用・qwen2.5:3b はカスタムモデルの土台。
    # patch_openwebui_models.py が /api/models から除外する → チャット一覧はプリセット3つのみ表示）
    '"OLLAMA_HIDDEN_MODELS=bge-m3:latest;qwen2.5:3b"',
    # Arena モデル（0.11 の評価用ダミーモデル）も一覧に出さない（社内Q&Aでは無用なため）
    '"ENABLE_EVALUATION_ARENA_MODELS=False"',
    # 背景のLLM生成タスクを無効化（v1.0.48: 応答速度優先。
    # タイトル/タグ/追質問候補/入力補完は回答後にLLMを追加呼び出しし、次の質問を遅くする）
    '"ENABLE_TITLE_GENERATION=False"',
    '"ENABLE_TAGS_GENERATION=False"',
    '"ENABLE_AUTOCOMPLETE_GENERATION=False"',
    '"ENABLE_FOLLOW_UP_GENERATION=False"',
    # RAG検索語のLLM生成を無効化（v1.0.48: 検索の安定化。
    # 3Bモデルが生成する検索語は揺らぎが大きく関連文書を取りこぼすため、
    # ユーザーの質問文をそのまま検索語として使用する）
    '"ENABLE_RETRIEVAL_QUERY_GENERATION=False"'
)
Log 'nssm set AppEnvironmentExtra'
& $nssm set $svc AppEnvironmentExtra $envs | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'nssm AppEnvironmentExtra failed' }

# --- 起動 ---
Log 'starting service'
& $nssm start $svc | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'service start failed' }

# --- 起動時モデルウォームアップ（v1.0.48）---
# PC起動後に bge-m3（検索用）と選択LLMをメモリへ事前ロードし、最初の質問から
# モデル再ロード待ちなしで即答できるようにする。タスクは SYSTEM 実行のため、
# サービスと同じ SYSTEM プロファイル配下のモデル格納域を共有できる。
# スクリプト側で RAM 14GB 未満は何もしないため、低メモリ機のメモリ収支は従来どおり。
$warmupPs = Join-Path $AppDir 'scripts\warmup_model.ps1'
if (Test-Path $warmupPs) {
    Log 'registering warmup scheduled task (ShineosWarmup)'
    try {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$warmupPs`" -Model $Model"
        $trigger = New-ScheduledTaskTrigger -AtStartup
        Register-ScheduledTask -TaskName 'ShineosWarmup' -Action $action -Trigger $trigger `
            -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
        # 登録直後に一度実行（再起動を待たずにウォームアップ済みで使い始められる）
        Start-ScheduledTask -TaskName 'ShineosWarmup'
        Log 'warmup task registered and triggered'
    } catch { Log "WARNING: warmup task registration failed: $($_.Exception.Message)" }
} else {
    Log "WARNING: warmup script not found: $warmupPs"
}

# --- ファイル生成ツールサーバー（PDF/PPTX/Word）をサービス登録 ---
# 1) 軽量サーバー（filegen_server.py・PDF/PPTX）
$fgSvc = 'ShineosFileGen'
$fgPy = Join-Path $AppDir 'venv\Scripts\python.exe'
$fgScript = Join-Path $AppDir 'tools\filegen_server.py'
if ((Test-Path $fgPy) -and (Test-Path $fgScript)) {
    Log "registering file generation server service ($fgSvc)"
    & sc.exe stop $fgSvc 2>$null | Out-Null
    & sc.exe delete $fgSvc 2>$null | Out-Null
    Start-Sleep -Seconds 1
    # アプリ（第2引数）のみ nssm install で登録（Application は nssm が引用符処理する）
    & $nssm install $fgSvc $fgPy | Out-Null
    if ($LASTEXITCODE -eq 0) {
        # 重要: AppParameters（スクリプトパス）は PowerShell 5.1 のネイティブ引数渡しでは
        # 引用符が剥がれ「C:\Program」で切れるため、レジストリに直接「引用符付きパス」を書く
        Set-ItemProperty -Path ("HKLM:\SYSTEM\CurrentControlSet\Services\" + $fgSvc + "\Parameters") `
            -Name 'AppParameters' -Value ('"' + $fgScript + '"') -Type ExpandString
        & $nssm set $fgSvc AppDirectory $AppDir | Out-Null
        & $nssm set $fgSvc Start SERVICE_AUTO_START | Out-Null
        & $nssm set $fgSvc AppStdout (Join-Path $logDir 'filegen.log') | Out-Null
        & $nssm set $fgSvc AppStderr (Join-Path $logDir 'filegen.err.log') | Out-Null
        & $nssm start $fgSvc | Out-Null
        Log 'file generation server service started'
    }
}

# 2) MCPO ファイル生成サーバー（PDF/PPTX/Word/XLSX/CSV・MCP対応）
$mcpoDir = Join-Path $AppDir 'tools\mcpo'
$mcpoExport = Join-Path $mcpoDir 'tools\file_export_server.py'
$mcpoMcp = Join-Path $mcpoDir 'tools\file_export_mcp.py'
$mcpoOut = Join-Path $AppDir 'data\mcpo_output'
New-Item -ItemType Directory -Force -Path $mcpoOut | Out-Null
if ((Test-Path $fgPy) -and (Test-Path $mcpoExport) -and (Test-Path $mcpoMcp)) {
    # ファイル配信サーバー（9003）
    $mcpoSvc = 'ShineosMcpoFiles'
    & sc.exe stop $mcpoSvc 2>$null | Out-Null
    & sc.exe delete $mcpoSvc 2>$null | Out-Null
    Start-Sleep -Seconds 1
    & $nssm install $mcpoSvc $fgPy | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Set-ItemProperty -Path ("HKLM:\SYSTEM\CurrentControlSet\Services\" + $mcpoSvc + "\Parameters") `
            -Name 'AppParameters' -Value ('"' + $mcpoExport + '"') -Type ExpandString
        & $nssm set $mcpoSvc AppDirectory $mcpoDir | Out-Null
        & $nssm set $mcpoSvc AppEnvironmentExtra ('"FILE_EXPORT_DIR=' + $mcpoOut + '"') '"FILE_EXPORT_BASE_URL=http://localhost:9003/files"' | Out-Null
        & $nssm set $mcpoSvc Start SERVICE_AUTO_START | Out-Null
        & $nssm set $mcpoSvc AppStdout (Join-Path $logDir 'mcpo_files.log') | Out-Null
        & $nssm set $mcpoSvc AppStderr (Join-Path $logDir 'mcpo_files.err.log') | Out-Null
        & $nssm start $mcpoSvc | Out-Null
        Log 'MCPO file server started'
    }
    # MCP サーバー（9004・SSE）
    $mcpoMcpSvc = 'ShineosMcpoMcp'
    & sc.exe stop $mcpoMcpSvc 2>$null | Out-Null
    & sc.exe delete $mcpoMcpSvc 2>$null | Out-Null
    Start-Sleep -Seconds 1
    & $nssm install $mcpoMcpSvc $fgPy | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Set-ItemProperty -Path ("HKLM:\SYSTEM\CurrentControlSet\Services\" + $mcpoMcpSvc + "\Parameters") `
            -Name 'AppParameters' -Value ('"' + $mcpoMcp + '"') -Type ExpandString
        & $nssm set $mcpoMcpSvc AppDirectory $mcpoDir | Out-Null
        & $nssm set $mcpoMcpSvc AppEnvironmentExtra '"MODE=sse"' '"MCP_HTTP_PORT=9004"' ('"FILE_EXPORT_DIR=' + $mcpoOut + '"') '"FILE_EXPORT_BASE_URL=http://localhost:9003/files"' | Out-Null
        & $nssm set $mcpoMcpSvc Start SERVICE_AUTO_START | Out-Null
        & $nssm set $mcpoMcpSvc AppStdout (Join-Path $logDir 'mcpo_mcp.log') | Out-Null
        & $nssm set $mcpoMcpSvc AppStderr (Join-Path $logDir 'mcpo_mcp.err.log') | Out-Null
        & $nssm start $mcpoMcpSvc | Out-Null
        Log 'MCPO MCP server started'
    }
}

Log '--- register_service done ---'
}
catch {
    Log "ERROR: $($_.Exception.Message)"
    Log "STACK: $($_.ScriptStackTrace)"
    exit 1
}
exit 0
