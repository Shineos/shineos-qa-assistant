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
- 既知の修正履歴（v2.1.5実機検証）: インストーラの`/VERYSILENT`「無効」は誤検知（Git Bashのパス変換が原因・cmd経由で仕様どおり動作を確認・CHANGELOG参照）、アンインストール時データ保持確認は仕様どおり動作。設定タブrefreshの二重実行によるDOM競合（シリアライズ化で解消）、API JSON camelCaseとTS型PascalCaseの不一致（全snake/camel統一）

## 6. テスト資産（2026-09-20 拡充・feature/drawing-search）

| 種別 | 場所 | 実行方法 | 現況 |
|---|---|---|---|
| 単体テスト（xUnit・**88件**） | `app/ShineosQA.Backend.Tests/` | `dotnet test`（ portable SDK は `DOTNET_ROOT=tools/dotnet-sdk`） | **88/88 合格**。従来32件＋v2.1.4のWebSearch 8件＋図面拡張24件（フィクスチャPDF抽出・図番正規化・表題欄・拡張パック・OCR・PNGエンコーダ）。フィクスチャPDFはEdgeヘッドレス生成（再生成手順: `TestData/src/README.md`） |
| Golden QA 118問 | `spikes/phase0/latency-cmp/scenarios-100.json` | アプリ稼働中に `scenario-test.ps1 -Scenarios scenarios-100.json`（コーパスは `golden-corpus/` **と `../../testdocs/`** を `/api/knowledge/import`。docx/PDF問は後者に依存） | feature/drawing-search: **104/118**（mainベースライン103/118・本ブランチ固有失敗ゼロ）。失敗分類と知見は RESULTS.md 第9ラウンド＋本ブランチの帰属分析（共通失敗14問はmain既存） |
| 図面E2E 10問 | `spikes/phase0/latency-cmp/scenarios-drawing.json` | 拡張パックONで `golden-corpus-drawing/` を取り込み実行 → 完了後パックOFFに戻す | **10/10**（図番完全一致・表記ゆれ小文字/全角・品名逆引き・周辺文書横断・注記・拒否2問・通常文書回帰2問） |
| UIスモーク | `spikes/phase0/ui-smoke/ui-smoke.ps1` | インストール済みアプリに対して実行（起動→タブ切替→モデルメニュー開閉を実ウィンドウ操作で検証） | 7/7 合格（図面拡張のUI分は手順書: impl-drawing-search §4.3） |
| インストーラ失敗系 | `spikes/phase0/installer-tests.ps1` | `-Phase d1`（アップグレード）/ `d2`（モデル破損・復元付き）/ `d3`（対話アンインストール） | d1 PASS（exit=0・データ保持・2.0.1登録）、d2 PASS（エンジンロード失敗をログ記録・バックエンド生存・自動復元）、d3 はスクリプト済み（単一インスタンス保証あり。対話UIはクリーンな環境で実行のこと） |

テスト時の注意:
- `d2` はモデルファイルを一時破損させる（try/finally で必ず復元）
- 対話アンインストールの自動化は残存 `unins000` インスタンスがあると排他ロックで破綻するため、必ず単一起動する（スクリプトが保証済み）
- **refuse問には `"refusekw"` が必須**（無いと常にFAIL）。v2.1.4で拒否文言が正しく変わった問は文言を両立するkwに更新済み（t61/t73）
- **プロンプト規則を変更したら18問＋図面10問の両方を実行する**（規則リスト肥大は小モデルの網羅性を劣化させる。規則⑦撤回の経緯はCHANGELOG）
- バックエンドTFMは `net10.0-windows10.0.19041`（WinRT OCR用。テストプロジェクトも同一TFM）

## 7. 既知の未完（品質バックログ）

- [ ] 8GB実機でのクイック階級（1.7B）検証
- [x] PDF本格パーサ（PdfPig置換） → **対応済み（feature/drawing-search T1）**。ただし `year-end-policy.pdf` は375バイトの破損スタブで抽出対象外（旧来同一挙動）
- [ ] Golden QA 残失敗の改善: main既存14問（横断recall・罠質問。RESULTS.md 第9ラウンド参照）＋**t74「対象置換」捏造はプロンプト耐性 → 生成後バリデーションガード（回答の対象語が引用元本文に現れるか検証）で対応予定**
- [x] モデル破損時にsupervisorの再試行が続く間ユーザー応答が遅延する問題の早期エラー化 → **対応済み（RESULTS.md 第10ラウンド）**: 起動前SHA256検証（永続キャッシュ付き）＋ヘルス待ち瞬死検知＋OOM誤診阻止＋サーキットブレーカーで240秒無応答→2.5秒エラー化
- [ ] Web検索のマルチエンジン化
- [x] テストのCI化 → **対応済み（feature/drawing-search）**: release.yml に `dotnet test` ゲート追加
- [ ] 図面拡張のリリースゲート残件: 実機インストーラ（lite/full）検証・WACK・Pilot先の表題欄様式に合わせた `DrawingIngest` 閾値調整（docs/impl-drawing-search.md §4.4/§7）
