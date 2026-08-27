# setup_knowledge.ps1 - ナレッジ（社内文書）を Open WebUI に自動登録する
# - 対象フォルダ: {app}\knowledge（既定。引数 -KnowledgeDir で変更可）
# - サブフォルダごとにナレッジコレクションを作成し、PDF/Markdown/テキストを
#   アップロードしてベクトル化する（RAG 検索の事前準備）
# - サブフォルダが無い場合は「社内ナレッジ」コレクションに登録
# - 冪等: 同名コレクションが既に存在する場合は再利用（ファイルは毎回アップロードし直す）
# - 推奨: ファイル名や文書の先頭に【人事規程】【ITサポート】などのタグを付けると検索精度が向上
# 終了コード: 0 = 成功（対象なし含む） / 非0 = 失敗

param(
    [string]$BaseUrl = 'http://localhost:8080',
    [string]$KnowledgeDir = '',
    [string]$Email = 'admin@localhost',
    [string]$Password = 'admin',
    [string]$LogFile = '',
    [string]$AppDir = 'C:\Program Files\ShineosQA'
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ($LogFile) { New-Item -ItemType Directory -Force -Path (Split-Path $LogFile) | Out-Null }
function Log {
    param([string]$Message)
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"
    if ($LogFile) { $line | Out-File -FilePath $LogFile -Append -Encoding utf8 }
    Write-Host $line
}

try {
    # ---------- 1. 対象フォルダの特定 ----------
    if (-not $KnowledgeDir) {
        # インストーラ実行時は {tmp} に展開されるため、{app} 配下を探す
        $candidates = @(
            (Join-Path 'C:\Program Files\ShineosQA' 'knowledge'),
            (Join-Path $env:ProgramFiles 'ShineosQA\knowledge')
        )
        foreach ($c in $candidates) { if (Test-Path $c) { $KnowledgeDir = $c; break } }
    }
    if (-not $KnowledgeDir -or -not (Test-Path $KnowledgeDir)) {
        Log "knowledge dir not found (skipped): $KnowledgeDir"
        exit 0
    }
    Log "knowledge dir: $KnowledgeDir"

    # ---------- 2. Open WebUI の起動を待つ ----------
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $r = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 5
            if ($r.status) { $ready = $true; break }
        } catch { }
        Start-Sleep -Seconds 5
    }
    if (-not $ready) { throw "Open WebUI が応答しません: $BaseUrl" }
    Log "Open WebUI ready: $BaseUrl"

    # ---------- 3. サインインして JWT を取得 ----------
    $signinBody = @{ email = $Email; password = $Password } | ConvertTo-Json
    $session = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/auths/signin" -Body $signinBody -ContentType 'application/json' -TimeoutSec 30
    if (-not $session.token) { throw 'signin に成功しましたが token を取得できませんでした' }
    $headers = @{ Authorization = "Bearer $($session.token)" }
    Log "signed in as $Email"

    # ---------- 4. 既存コレクション一覧（冪等化のため） ----------
    $existing = @{}
    try {
        # PS 5.1 はネイティブコマンド出力を OEM コードページでデコードし日本語が化けるため、
        # curl -o でファイルに保存し UTF-8 で読み直してからパースする
        $tmpFile = Join-Path $env:TEMP ("knowledge_list_" + [guid]::NewGuid().ToString('N') + ".json")
        $cmdLine = 'curl.exe -sS -m 30 -o "' + $tmpFile + '" "' + $BaseUrl + '/api/v1/knowledge/" -H "Authorization: Bearer ' + $session.token + '"'
        & cmd.exe /d /c $cmdLine 2>&1 | Out-Null
        $colls = ([System.IO.File]::ReadAllText($tmpFile, [System.Text.Encoding]::UTF8) | ConvertFrom-Json)
        Remove-Item $tmpFile -Force -ErrorAction SilentlyContinue
        # レスポンスは {"items": [...]} 形式（0.11.x）。旧形式（配列直返し）にも対応する
        $collList = if ($colls.items) { @($colls.items) } else { @($colls) }
        foreach ($c in $collList) { if ($c.name) { $existing[$c.name] = $c.id } }
        Log "existing collections: $($existing.Count)"
    } catch { Log "WARNING: could not list collections: $($_.Exception.Message)" }

    # ---------- 5. コレクション単位の登録処理 ----------
    function Add-CollectionFiles {
        param([string]$CollName, [string]$Dir)
        $collId = $script:existing[$CollName]
        if (-not $collId) {
            $body = @{ name = $CollName; description = "社内知恵袋の自動登録ナレッジ（$CollName）" } | ConvertTo-Json -Depth 4
            $jsonBytes = [System.Text.Encoding]::UTF8.GetBytes($body)
            $created = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/knowledge/create" -Headers $headers -Body $jsonBytes -ContentType 'application/json' -TimeoutSec 60
            $collId = $created.id
            Log "collection created: $CollName ($collId)"
        } else {
            Log "collection reused: $CollName ($collId)"
        }

        $files = @(Get-ChildItem -Path $Dir -File -Recurse | Where-Object { $_.Extension -match '^\.(pdf|md|txt|docx|doc)$' })
        if ($files.Count -eq 0) { Log "no supported files in $Dir (skipped)"; return }
        Log "uploading $($files.Count) file(s) to $CollName ..."

        foreach ($f in $files) {
            # multipart 送信は curl を使用（PS 5.1 は -Form 非対応）
            # パスに空白が含まれても「file=@...」全体を引用符で囲めば curl が正しく処理する
            $cmdLine = 'curl.exe -sS -m 900 -X POST "' + $BaseUrl + '/api/v1/files/" -H "Authorization: Bearer ' + $session.token + '" -F "file=@' + $f.FullName + '"'
            $resp = & cmd.exe /d /c $cmdLine 2>&1
            $fileId = ''
            try { $json = ($resp -join '') | ConvertFrom-Json; $fileId = $json.id } catch { }
            if (-not $fileId) {
                Log "WARNING: upload failed: $($f.Name) -> $resp"
                continue
            }
            Log "uploaded: $($f.Name) (id=$fileId)"

            # コレクションに明示的に追加
            # （multipart メタデータの knowledge_id は Open WebUI 0.11.0 で無視されるため、
            #   /api/v1/knowledge/{id}/file/add で確実に紐付ける）
            try {
                $addBody = @{ file_id = $fileId } | ConvertTo-Json
                $addBytes = [System.Text.Encoding]::UTF8.GetBytes($addBody)
                Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/knowledge/$collId/file/add" -Headers $headers -Body $addBytes -ContentType 'application/json' -TimeoutSec 60 | Out-Null
                Log "added to collection ${CollName}: $($f.Name)"
            } catch {
                Log "WARNING: could not add to collection: $($f.Name) - $($_.Exception.Message)"
            }

            # ベクトル化完了をポーリング（最大5分）
            for ($i = 0; $i -lt 60; $i++) {
                Start-Sleep -Seconds 5
                try {
                    $st = Invoke-RestMethod -Uri "$BaseUrl/api/v1/files/$fileId/process/status" -Headers $headers -TimeoutSec 15
                    if ($st.status -eq 'completed') { Log "vectorized: $($f.Name)"; break }
                    if ($st.status -eq 'error' -or $st.status -eq 'failed') {
                        Log "WARNING: vectorization failed: $($f.Name) ($($st.status))"; break
                    }
                } catch { }
                if ($i -eq 59) { Log "WARNING: vectorization timeout: $($f.Name)" }
            }
        }
    }

    $subDirs = @(Get-ChildItem -Path $KnowledgeDir -Directory)
    if ($subDirs.Count -gt 0) {
        foreach ($d in $subDirs) {
            Add-CollectionFiles -CollName $d.Name -Dir $d.FullName
        }
    } else {
        Add-CollectionFiles -CollName '社内ナレッジ' -Dir $KnowledgeDir
    }

    # ---------- 6. コレクションをプリセットモデルへ紐付ける ----------
    # （RAGはモデルの meta.knowledge 単位で検索されるため。未紐付けだと
    #   社内文書が参照されずハルシネーションの原因になる:v1.0.47実機検証）
    $attachPy = Join-Path $AppDir 'scripts\attach_knowledge_to_models.py'
    $venvPy = Join-Path $AppDir 'venv\Scripts\python.exe'
    if ((Test-Path $attachPy) -and (Test-Path $venvPy)) {
        $attachOut = & $venvPy $attachPy --base-url $BaseUrl --email $Email --password $Password 2>&1
        $attachCode = $LASTEXITCODE
        foreach ($l in $attachOut) { Log "attach: $l" }
        if ($attachCode -ne 0) { Log "WARNING: knowledge attach failed (exit $attachCode)" }
    } else {
        Log "WARNING: attach script not found: $attachPy"
    }

    Log 'setup_knowledge done'
    exit 0
}
catch {
    Log "ERROR: $($_.Exception.Message)"
    if ($_.ErrorDetails.Message) { Log "DETAIL: $($_.ErrorDetails.Message)" }
    exit 1
}
