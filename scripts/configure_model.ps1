# configure_model.ps1 - Open WebUI のモデル設定を最適化する
# - サインイン（admin@localhost / admin）して API 経由で「社内知恵袋」モデル設定を作成・上書き
# - 別名カスタムモデル（id=社内知恵袋, base_model_id=実モデル）として作成する。
#   同名（id=base_model_id）で作成した設定は Open WebUI 0.10.x でモデル一覧に
#   マージされず、ツールが無効化されないため（2026-08-22 実機検証）。
# - meta.builtinTools を全て無効化し、モデルにツール（write_note 等）を提供しない。
#   ツールがあるとモデルが関数呼び出しを選び、チャットにテキスト回答が残らず
#   「応答なし」になるため（実機検証済み）。
# - params.think=false で qwen3系の思考モードを無効化（応答なし防止）
# - 全モデルに num_ctx 4096 を設定（長文・RAG 対応）
# 冪等: 同一モデル id の設定は上書きされる
# 終了コード: 0 = 成功 / 非0 = 失敗
param(
    [string]$BaseUrl = 'http://localhost:8080',
    [string]$Model = 'qwen2.5:3b',
    [string]$Email = 'admin@localhost',
    [string]$Password = 'admin',
    [string]$LogFile = ''
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if ($LogFile) { New-Item -ItemType Directory -Force -Path (Split-Path $LogFile) | Out-Null }
function Log { param([string]$Message)
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"
    if ($LogFile) { $line | Out-File -FilePath $LogFile -Append -Encoding utf8 }
    Write-Host $line
}

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
    $baseRules = @'
あなたは社内ナレッジベースのQ&Aアシスタント「社内知恵袋」です。社内規定・業務マニュアルなどのナレッジ文書を参照して、正確に回答してください。

回答のルール:
1. 結論から先に述べること。理由や補足は結論の後に簡潔に。
2. 回答の根拠となった該当文書名・該当箇所（ページ数があればページ数）を明記すること。
3. 提示された参考文書内に回答がない場合は、推測せず「ナレッジに該当する記載がありません。〇〇課へ直接お問い合わせください」と回答すること。
4. 回答する前に、次の順序で考えること:
   (a) 質問の意図を整理する（何を・どの条件で聞いているか）
   (b) 参照文書から関連する事実・数字・条件だけを抜き出して整理する
   (c) その事実だけを使って回答を組み立てる（文書に書かれていないことを補わない）
5. 質問の種類に応じて回答形式を使い分けること:
   - 手順の質問 → 番号付きの手順で回答
   - 定義・条件・上限額の質問 → 箇条書きで簡潔に回答
   - 可否の質問 → 「可能です」「できません」と最初に明言してから理由を説明
6. 社内用語・型番・規程番号は、ナレッジに記載されている表記をそのまま使うこと（言い換えない）。
7. 質問が日本語の場合は日本語で回答すること。
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

    foreach ($preset in $presets) {
        $params = @{
            think   = $false
            num_ctx = 4096
            # 応答の安定性向上（表記ブレ・揺らぎ・幻覚の抑制のため温度を低くする。
            # Q&A用途では決定論的な回答が望ましい）
            temperature = 0.2
            # native でツール（Web検索）をモデルに提供する。
            # web_search ツールのみ有効化し、他のツールは無効（「回答なし」防止）。
            # ファイル生成ツール（PDF/PPT）は別途ツールサーバーとして登録する。
            function_calling = 'native'
            system  = $preset.system
        }
        $body = @{
            id             = $preset.id
            base_model_id  = $Model
            name           = $preset.id
            meta           = @{
                capabilities  = @{ tools = $true }
                builtinTools = $builtinTools
            }
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
    # ---------- 4. UI言語を日本語に設定（フロントの初回表示に反映） ----------
    try {
        $langBody = @{ ui = @{ locale = 'ja-JP'; language = 'ja-JP' } } | ConvertTo-Json -Depth 4
        $langBytes = [System.Text.Encoding]::UTF8.GetBytes($langBody)
        Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/users/user/settings/update" -Headers $headers -Body $langBytes -ContentType 'application/json' -TimeoutSec 30 | Out-Null
        Log 'ui language set to ja-JP'
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
