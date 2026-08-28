# configure_model.ps1 - Open WebUI のモデル設定を最適化する
# - サインイン（admin@localhost / admin）して API 経由で「社内知恵袋」モデル設定を作成・上書き
# - 別名カスタムモデル（id=社内知恵袋, base_model_id=実モデル）として作成する。
#   同名（id=base_model_id）で作成した設定は Open WebUI 0.10.x でモデル一覧に
#   マージされず、ツールが無効化されないため（2026-08-22 実機検証）。
# - meta.builtinTools を全て無効化し、モデルにツール（write_note 等）を提供しない。
#   ツールがあるとモデルが関数呼び出しを選び、チャットにテキスト回答が残らず
#   「応答なし」になるため（実機検証済み）。
# - params.think=false で qwen3系の思考モードを無効化（応答なし防止）
# - 全モデルに num_ctx を設定（RAM に応じて自動調整し、他アプリへのメモリ影響を抑制）
# 冪等: 同一モデル id の設定は上書きされる
# 終了コード: 0 = 成功 / 非0 = 失敗
param(
    [string]$BaseUrl = 'http://localhost:8080',
    [string]$Model = 'qwen2.5:3b',
    [int]$RamGB = 16,
    [string]$GpuMode = '',
    [string]$Email = 'admin@localhost',
    [string]$Password = 'admin',
    [string]$LogFile = ''
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ($LogFile) { New-Item -ItemType Directory -Force -Path (Split-Path $LogFile) | Out-Null }
function Log { param([string]$Message)
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"
    for ($i = 0; $i -lt 5; $i++) { try { if ($LogFile) { $line | Out-File -FilePath $LogFile -Append -Encoding utf8 -ErrorAction Stop }; break } catch { Start-Sleep -Milliseconds 150 } }
    Write-Host $line
}
. (Join-Path $PSScriptRoot 'ollama_common.ps1')

try {
    # ---------- 1. WebUI の起動を待つ（最大5分） ----------
    $health = "$BaseUrl/health"
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $r = Invoke-RestMethod -Uri $health -TimeoutSec 5
            if ($r.status) { $ready = $true; break }
        } catch { Start-Sleep -Seconds 10 }
        Start-Sleep -Seconds 10
    }
    if (-not $ready) { throw "Open WebUI が応答しません: $health" }
    Log "Open WebUI ready: $BaseUrl"

    # ---------- 2. サインインして JWT を取得 ----------
    $signinBody = @{ email = $Email; password = $Password } | ConvertTo-Json
    $session = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/auths/signin" -Body $signinBody -ContentType 'application/json' -TimeoutSec 30
    if (-not $session.token) { throw 'signin に成功しましたが token を取得できませんでした' }
    $headers = @{ Authorization = "Bearer $($session.token)" }
    Log "signed in as $Email"

    # ---------- 3. モデル設定を作成（同一 id は上書き） ----------
    # ツールを全て無効化（モデルが関数呼び出しを選んで「応答なし」になるのを防ぐ）
    $builtinTools = @{
        notes            = $false
        time             = $false
        knowledge        = $false
        chats            = $false
        memory           = $false
        web_search       = $true
        image_generation = $false
        code_interpreter = $false
        channels         = $false
        tasks            = $false
        automations      = $false
        calendar         = $false
    }

    # 社内Q&A共通ルール（ガードレール + CoT 思考プロセス）
    # - 結論先行・根拠（文書名・ページ数）明記・ハルシネーション完全遮断
    # - 回答前に参照文書から事実を整理してから回答（スモールモデルの論理飛躍防止）
    # - 段階的思考（CoT）を明示し、質問の種類に応じた回答形式を使い分ける
    # ※ 短縮版を試したところ 3B モデルが文書を無視して幻覚を生成したため
    #   （v1.0.48 実機検証）、詳細版を採用する。根拠性 > プロンプト評価時間
    $baseRules = @'
あなたは社内ナレッジベースのQ&Aアシスタント「社内知恵袋」です。社内規定・業務マニュアルなどのナレッジ文書を参照して、正確に回答してください。

回答のルール:
1. 結論から先に述べること。理由や補足は結論の後に簡潔に。
2. 参考文書の中に根拠となる記載が見つかった場合のみ、回答の末尾に根拠（文書名・該当箇所、ページ数があればページ数）を明記すること。
3. 参考文書内に回答となる記載が無い場合は、推測・創作を一切せず「ナレッジに該当する記載がありません。〇〇課へ直接お問い合わせください」とだけ回答すること。この場合は【参照文書】などの引用欄を書いてはいけない。
4. 過去の会話や質問文の中に文書名が登場しても、参考文書にその文書の記載が無ければ引用してはいけない。会話履歴に含まれる文書名は根拠にならない。
5. 回答する前に、参照文書から関連する事実・数字・条件だけを抜き出して整理し、そこに書かれていないことを補わない。
6. 質問の種類に応じて回答形式を使い分けること:
   - 手順の質問 → 番号付きの手順で回答
   - 定義・条件・上限額の質問 → 箇条書きで簡潔に回答
   - 可否の質問 → 「可能です」「できません」と最初に明言してから理由を説明
7. 社内用語・型番・規程番号は、ナレッジに記載されている表記をそのまま使うこと（言い換えない）。
8. 質問が日本語の場合は日本語で回答すること。
'@

    # 用途別プリセット（社内知恵袋 = 汎用 / 経費精算ガイド / ITヘルプデスク）
    $presets = @(
        @{ id = '社内知恵袋'; system = $baseRules },
        @{
            id     = '経費精算ガイド'
            system = $baseRules + @'

あなたは経費精算専門のアシスタントです。経費精算ナレッジを参照し、経費精算の手順・ルール・上限額について正確に案内してください。該当する記載がない場合は、経理課への問い合わせを案内してください。
'@
        },
        @{
            id     = 'ITヘルプデスク'
            system = $baseRules + @'

あなたはITサポート専門のアシスタントです。ITサポートナレッジを参照し、社内システム・PC・パスワードなどの問い合わせに正確に案内してください。該当する記載がない場合は、ITヘルプデスクへの問い合わせを案内してください。
'@
        }
    )

    # num_ctx を GPU / RAM に応じて自動調整（KV キャッシュのメモリ使用量を抑え、
    # 他のアプリへの影響を防ぐ）（v1.0.58: GPU 機は VRAM に KV を置けるため 8192 固定）
    #   GPU（NVIDIA検出）: 8192（KVはVRAMに載る。RAM制約を受けない）
    #   CPU 推論機     : 8GB 未満 2048 / 8〜16GB 4096 / 16GB 以上 8192
    $gpuMode = if ($GpuMode) { $GpuMode } else { Get-GpuAcceleration }
    $numCtx = if ($gpuMode -eq 'cuda') {
        8192
    } elseif ($RamGB -lt 8) { 2048 } elseif ($RamGB -lt 16) { 4096 } else { 8192 }
    Log "performance profile: gpu=$gpuMode ram=${RamGB}GB -> num_ctx=$numCtx"

    foreach ($preset in $presets) {
        # 既存モデルの knowledge 紐付けを引き継ぐ（v1.0.52）。
        # このスクリプトはモデル設定を全文上書きするため、引き継ぎがないと
        # setup_knowledge で紐付けたナレッジが外れ、RAGが止まって幻覚の原因になる
        # （README に手動実行手順が載っているため、再実行でも安全である必要がある）。
        # ※ GET の応答は PS 5.1 で文字化けするため curl + ファイル読みで取得する
        $existingKnowledge = $null
        try {
            $tmpGet = Join-Path $env:TEMP ('cfg_model_' + [guid]::NewGuid().ToString('N') + '.json')
            $idEnc = [Uri]::EscapeDataString($preset.id)
            & curl.exe -sS -m 30 -o "$tmpGet" "$BaseUrl/api/v1/models/model?id=$idEnc" -H "Authorization: Bearer $($session.token)" 2>$null
            if (Test-Path $tmpGet) {
                $existing = ([System.IO.File]::ReadAllText($tmpGet, [System.Text.Encoding]::UTF8)) | ConvertFrom-Json
                if ($existing -and $existing.meta -and $existing.meta.knowledge) {
                    $existingKnowledge = @($existing.meta.knowledge)
                }
                Remove-Item $tmpGet -Force -ErrorAction SilentlyContinue
            }
        } catch { Log "WARNING: could not read existing model knowledge for $($preset.id): $($_.Exception.Message)" }

        $params = @{
            think   = $false
            num_ctx = $numCtx
            # 応答の安定性向上（表記ブレ・揺らぎ・幻覚の抑制のため温度を低くする。
            # Q&A用途では決定論的な回答が望ましい）
            temperature = 0.2
            # 応答長の上限（v1.0.48: 長文生成による応答遅延の防止。社内Q&Aの回答は
            # 簡潔にまとまるため 512 トークンで十分）
            num_predict = 512
            # レガシー指定でツール（Web検索）をモデルに提供する。
            # v1.0.47: 'legacy' に変更。ナレッジの常時RAG注入は legacy のみ対応し、
            # native は小規模モデルがツール呼び出しを誤選択して「応答なし」の原因になる
            # （0.11.0 実機検証）。ナレッジ注入は patch_openwebui_rag.py + attach スクリプトとセット。
            function_calling = 'legacy'
            system  = $preset.system
        }
        # meta は変数化し、knowledge は既存がある場合のみ含める
        # （$null を入れると "knowledge": null として送信され紐付けがクリアされるため）
        $meta = @{
            capabilities  = @{ tools = $true }
            builtinTools = $builtinTools
        }
        if ($existingKnowledge) { $meta['knowledge'] = $existingKnowledge }

        $body = @{
            id             = $preset.id
            base_model_id  = $Model
            name           = $preset.id
            meta           = $meta
            params         = $params
            access_grants  = @()
            is_active      = $true
        } | ConvertTo-Json -Depth 6

        # PS 5.1 の Invoke-RestMethod は文字列 Body を ISO-8859-1 で送信し日本語が
        # 化ける（既知のバグ）ため、UTF-8 バイト配列に変換して送る
        $jsonBytes = [System.Text.Encoding]::UTF8.GetBytes($body)

        # 0.10.x は同名 id の再作成がエラーになるため、update を試してから create にフォールバック
        $updated = $null
        try {
            $updated = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/models/model/update" -Headers $headers -Body $jsonBytes -ContentType 'application/json' -TimeoutSec 60
        } catch {
            $updated = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/models/create" -Headers $headers -Body $jsonBytes -ContentType 'application/json' -TimeoutSec 60
        }
        Log "model configured: $($preset.id) (base=$Model, think=$($params.think), num_ctx=$($params.num_ctx), tools=disabled)"
    }
    # ---------- 3.5 RAGテンプレートの日本語化（v1.0.55） ----------
    # 既定（英語・長文）のRAGテンプレートは、3Bモデルが出力に指示文をそのまま
    # 漏洩させる原因（実機検証: 英語の "### Task:..." が回答に混入）。
    # 日本語の簡潔なテンプレートに差し替え、英語出力とテンプレート漏洩を防ぐ
    try
    {
        $ragTemplate = @'
### 依頼
次の資料だけを使って、質問に日本語で答えてください。

### 出力のルール
- 質問も回答も日本語で行うこと（英語は使わない）。
- 資料の内容やこの指示文を、そのまま出力に貼り付けないこと。要点を整理して答える。
- 数値は資料どおりに正確に写すこと。期間の単位は「ヶ月」と書かずに必ず「か月」と書く（例: 18か月）。「円」「%」なども誤字にしない。
- 資料に id がある場合は [1] のように引用番号を付ける。id が無い資料には番号を付けない。
- 資料に答えが無い場合は「ナレッジに該当する記載がありません」とだけ答える。

<context>
{{CONTEXT}}
</context>
'@
        $ragBody = @{ RAG_TEMPLATE = $ragTemplate } | ConvertTo-Json -Depth 3
        $ragBytes = [System.Text.Encoding]::UTF8.GetBytes($ragBody)
        Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/retrieval/config/update" -Headers $headers -Body $ragBytes -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        Log 'RAG template set to Japanese (anti template-leak)'
    } catch { Log "WARNING: RAG template update failed: $($_.Exception.Message)" }

    # ---------- 4. UI言語を日本語に設定（フロントの初回表示に反映） ----------
    try {
        # showChangelog=false: 「新機能」ダイアログ（What's New）の表示を無効化（v1.0.49）。
        # UserSettings は extra=allow のため任意キーを保存できる
        $langBody = @{ ui = @{ locale = 'ja-JP'; language = 'ja-JP' }; showChangelog = $false } | ConvertTo-Json -Depth 4
        $langBytes = [System.Text.Encoding]::UTF8.GetBytes($langBody)
        Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/users/user/settings/update" -Headers $headers -Body $langBytes -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        Log 'ui language set to ja-JP / showChangelog=false'
    } catch { Log 'WARNING: ui language setting failed' }
    Log "preset models configured: $($presets.id -join ', ')"
    Log 'configure_model done'
    exit 0
}
catch {
    Log "ERROR: $($_.Exception.Message)"
    if ($_.ErrorDetails.Message) { Log "DETAIL: $($_.ErrorDetails.Message)" }
    Log "STACK: $($_.ScriptStackTrace)"
    exit 1
}
