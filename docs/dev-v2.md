# v2 開発者ガイド（バックエンド＋UI ビルド／実行）

- 対象: `app/ShineosQA.Backend`（C# / .NET 10）と `app/ShineosQA.Web`（Vite＋TypeScript）
- 設計: [architecture-v2.md](architecture-v2.md) ／ 実測根拠: [latency-verification.md](latency-verification.md)

## 1. 前提

| 要件 | 用途 | 入手 |
|---|---|---|
| .NET SDK 10 | バックエンドのビルド | `spikes/phase0/install-sdk.ps1`（`tools/dotnet-sdk/` へ局所導入。マシン全体インストール不要） |
| Node.js 20+ / npm | UIのビルド | 本体導入済み前提 |
| llama.cpp b10936 + GGUF | 推論エンジン | `spikes/phase0/download-models.ps1` 等（実測値は [phase0-report.md](phase0-report.md) §1） |

## 2. ビルド

```powershell
# UI（dist/ 約17KB・gzip約6KB）
cd app\ShineosQA.Web
npm install
npm run build            # tsc + vite build → dist/

# バックエンド（UIのdistをwwwrootとして同梱）
$env:DOTNET_ROOT = "D:\dev\shineos-local-ai\tools\dotnet-sdk"
& .\tools\dotnet-sdk\dotnet.exe build app\ShineosQA.Backend -c Release
```

## 3. 実行（開発）

```powershell
# config.json でエンジン/モデル/データの場所を指定（サンプル: app/ShineosQA.Backend/config.dev.json）
cd app\ShineosQA.Backend\bin\Release\net10.0
$env:DOTNET_ROOT = "D:\dev\shineos-local-ai\tools\dotnet-sdk"
.\ShineosQA.Backend.exe --config config.json
# → http://127.0.0.1:8300
```

- embed＋rankエンジンは起動時に常駐。**LLMは初回チャット時に起動**（初回のみ+10秒前後）
- 60分無操作でLLMを自動解放（`idle_unload_minutes`）、次の質問で再起動

## 4. API 概要（UIが利用）

| endpoint | 機能 |
|---|---|
| `POST /api/chat` | SSE（`meta`/`web`/`delta`/`done`/`error`）。回答キャッシュ・ガード・prefixキャッシュ内蔵 |
| `GET/POST /api/chats`, `GET/DELETE /api/chats/{id}` | チャット履歴 |
| `GET /api/knowledge`, `POST /api/knowledge`（multipart）, `POST /api/knowledge/import`, `DELETE /api/knowledge/{id}` | ナレッジ登録（.md/.txt/.docx/.pdf※） |
| `GET/POST /api/settings` | Web検索ON/OFF・モデル階級（auto/standard/quick） |
| `GET /api/status`, `/health` | エンジン状態・階級・チャンク数 |

※PDFは簡易抽出のためベストエフォート（スキャンPDF・CIDフォントは失敗を返す。フェーズ2で本格パーサへ）。

## 5. 実装済みの検証済み挙動（latency-verification.md から移植）

- スニペット抽出240字（単純切り詰め禁止）／リランクtop8→top2／ガード閾値 -2.0／回答キャッシュ cos≥0.97
- E2E実績（本構成・実モデル）: 正答＋出典付き／反復質問0.2秒（キャッシュ）／NO-HIT 4.5秒（ガード）／同一文書追質問8.8秒／Web検索連携

## 5.5 モデルマネージャ／初回ウィザード（2026-09-13実装・検証済み）

- `GET /api/models`（カタログ＋導入状況＋needs_wizard）／`POST /api/models/install`（DL→SHA256検証→配置、ミラーfallback）／`GET /api/models/progress`／`POST /api/models/delete`
- UI: チャットモデル未導入時に初回ウィザード（階級選択→進捗バー付きDL→自動リロード）。設定タブにAIモデル一覧（追加DL・削除）
- **実機検証**: bge-m3を実DL（605MB・SHA一致 `950f4a8e…`）／ウィザード表示・消滅／UI操作による質問→回答（3,000円・3日とも正答＋出典）／キャッシュ即答／NO-HIT拒否／ナレッジ・設定タブ
- 既知の修正履歴: 設定タブrefreshの二重実行によるDOM競合（シリアライズ化で解消）、API JSON camelCaseとTS型PascalCaseの不一致（全snake/camel統一）

## 6. 既知の未完（フェーズ1残り→フェーズ2）

- [ ] 8GB実機でのクイック階級（1.7B）検証
- [ ] リランク入力のスニペット化（約-30%）
- [ ] PDF本格パーサ（PdfPig等への置換）
- [ ] WPFシェル（既存 `app/ShineosQA.App`）のポート差し替え
- [ ] Windowsサービス化（NSSM流用）＋ウォームアップタスク
- [ ] インストーラ統合（[error-codes-v2.md](error-codes-v2.md) の終了コード表に従う）
- [ ] テストのCI化（golden QAをxUnitへ移植）
