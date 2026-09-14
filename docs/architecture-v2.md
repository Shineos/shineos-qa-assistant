# アーキテクチャ v2 設計書 — llama.cpp 直結・ゼロビルド・超軽量ローカルRAG

- ブランチ: `feature/architecture-redesign`
- 日付: 2026-09-13
- 状態: **ドラフト（設計レビュー用）**

---

## 1. 背景と現行課題

現行 v1（v1.0.78）は **Ollama + Open WebUI** をパッケージ化した構成。動作は実績があるが、以下が課題:

| # | 課題 | 根本原因 |
|---|------|---------|
| 1 | **インストールが重い**（合計約6GB・15〜40分） | Ollama本体1.5GB + Python + venv + torch(CPU) + open-webui 1〜1.5GB が「エンジン以外」で巨大 |
| 2 | **構築が脆弱** | インストール時に Open WebUI の site-packages を Python スクリプトで5箇所以上ソースパッチ（`patch_openwebui_*.py`）。バージョン固定 0.11.0、パッチ逸脱チェック/修復仕組みまで必要 |
| 3 | **メモリオーバーヘッド** | Python+uvicorn+torch+OWUI 常駐で推論に使えない RAM 約1.5〜2GB を消費。8GB機では 3B モデルが上限 |
| 4 | **Ollama が5層に硬結合** | インストーラ・サービス階梯・envチューニング・OWUIルータ・直接API呼び出し（`/api/generate`, `/api/embed`）の全部が Ollama 固有 |
| 5 | **モデル取得が不透明** | `ollama pull` はレジストリ形式。量子化・リビジョンを指定できず、SHA検証もない |

**推論コア自体は現行も llama.cpp（Ollama 内蔵）なので、生成速度は v2 でも原理的に同程度。v2 で変わるのは「同じ llama.cpp を仲介層なし・最小フットプリントで直接使う」こと。得られる恩恵は (a) インストール大幅軽量化、(b) RAM解放によるワンランク上のモデル搭載、(c) パッチ消失による堅牢化、(d) モデル配布の完全制御。**

## 2. 設計目標と非目標

### 目標
1. **低スペックPCで速く**: 8GB RAM・GPUなしOffice PC で現行同等以上の応答速度（TTFT）
2. **精度を上げる**: 解放されたRAMで大きいモデル＋リランカ導入で出典正解率を改善
3. **RAG注入維持**: ハイブリッド検索（BM25+ベクトル）＋根拠付き回答＋ハルシネーション抑制を完全再現
4. **インストール軽量化**: ダウンロードをモデルGGUF本体のみに近づける（目標: 約3GB・半減）
5. **ゼロビルド**: ユーザーPCでもCIでも**一切コンパイルしない**。エンジンは llama.cpp 公式プリビルド配布を使う
6. **完全オフライン運用・既存UX維持**: WPFシェル・自動起動・ウォームアップ・閉じたらRAM解放はそのまま

### 非目標（やらないこと）
- **推論エンジンの自作（C/C++スクラッチ）はしない** — llama.cpp こそが求めている「C/C++製・超軽量・高速エンジン」そのもの。自作は数ヶ月のコストに対し速度メリットゼロ
- Open WebUI の多ユーザー・モデルプレイグラウンド等の汎用機能の再現
- マルチGPU分散、複数モデル同時チャット
- **ユーザーPC上でのフロントエンド構築**（v1のpip/venv/パッチと同等の苦痛は再現しない。CIでのビルド→静的成果物配布は許容する）

## 3. 結論サマリ（何をやめ、何に変えるか）

| 層 | 現行 v1 | 新 v2 |
|---|---------|-------|
| 推論エンジン | Ollama 1.5GB インストーラ | **llama.cpp 公式プリビルド `llama-server.exe`**（CPU版zip 約15MB、Vulkan版 約38MB） |
| モデル取得 | `ollama pull`（不透明） | **HuggingFace から GGUF を直接DL**（curlで再開対応・SHA256検証・ミラーfallback） |
| RAG/アプリ層 | Open WebUI 0.11.0 + Python/venv/torch + ソースパッチ5種 | **自作軽量バックエンド1バイナリ**（SQLite + FTS5 + 静的Web UI 内蔵） |
| ベクトルDB | ChromaDB | **SQLite 単一ファイル**（float32 BLOB + 全件コサイン。社内文書規模なら<20ms） |
| 埋め込み | Ollama `bge-m3` | llama-server 埋め込み専用プロセス（`--embedding --pooling cls`）で `bge-m3` GGUF |
| リランク | なし（top_k=3 直） | **オプションで `bge-reranker-v2-m3`**（top8取得→リランク→top3注入）※RAM余裕時 |
| UI | Open WebUI の Web UI in WebView2 | **TS+Vite 製チャットUI（CIで静的ビルド→バックエンドが配信）** in 同じWebView2シェル（将来Tauri差し替え可） |
| プリセット | OWUIのmodel偽装3種 | `config.json` のデータ駆動プリセット（システムプロンプト+ナレッジ集合） |
| プロセス管理 | NSSM×OWUI + Ollamaサービス階梯 + IFEO優先度ハック | **バックエンド1サービスがスーパーバイザとして子プロセス管理**（優先度は自前Set） |
| ファイル生成 | 常駐Pythonサービス群 | フェーズ3でC#ライブラリ化（QuestPDF等）。移行期は既存サイドカー併用可 |

## 4. 全体アーキテクチャ

```
┌─────────────────────────────────────────────────────────────┐
│ デスクトップ「社内知恵袋」ShineosQA.exe（WPF+WebView2・ほぼ変更なし）  │
│   ・ServiceController でバックエンドサービス起動/停止                 │
│   ・/health ポーリング → http://127.0.0.1:<port> を表示             │
└──────────────────────────┬──────────────────────────────────┘
                           │ localhost HTTP (SSE)
┌──────────────────────────▼──────────────────────────────────┐
│ ShineosQA.Backend（Windowsサービス・単一バイナリ）                  │
│  ┌─────────┐ ┌──────────┐ ┌──────────┐ ┌───────────────┐    │
│  │ WebUI   │ │ ChatApi  │ │ Retrieval│ │ Ingestion     │    │
│  │ 静的配信 │ │ REST/SSE │ │ BM25+cos │ │ PDF/DOCX/MD/TXT│    │
│  └─────────┘ └────┬─────┘ └────┬─────┘ └──────┬────────┘    │
│  ┌────────────────▼──────────────▼─────────────▼────────┐   │
│  │ LlmGateway（OpenAI互換クライアント: chat/embed/rerank）  │   │
│  └───┬──────────────────┬───────────────────┬───────────┘   │
│  ┌───▼────────┐  ┌──────▼───────┐  ┌────────▼────────┐      │
│  │ Supervisor │  │ SQLite       │  │ 設定/カタログ     │      │
│  │ 子proc管理  │  │ knowledge.db │  │ config.json     │      │
│  │ idle unload│  │ (FTS5+vec)   │  │ models.json     │      │
│  └───┬────────┘  └──────────────┘  └─────────────────┘      │
└──────┼──────────────────────────────────────────────────────┘
       │ 子プロセス起動・ヘルス監視・優先度設定（IFEO不要）
       ├─▶ llama-server-llm.exe    -m qwen3-4b.gguf  --port 127.0.0.1:8101
       │     （-fa --cache-type-k/v q8_0 -c <RAM階級> -ngl <GPU時999> -ub 512）
       ├─▶ llama-server-embed.exe  -m bge-m3.gguf --embedding --pooling cls --port 8102
       └─▶ llama-server-rank.exe   -m bge-reranker.gguf --rerank --pooling rank --port 8103
             ※リランクはオプション（RAM≥16GB等の条件で有効化）
```

### リクエストフロー（チャット）
```
UI → POST /api/chat (SSE)
  → Retrieval: FTS5 BM25 top8 ∥ ベクトル cos top8 → ハイブリッド統合(0.5/0.5)
  → （有効時）Rerank top8 → top3
  → PromptBuilder: 日本語ガードレール系统プロンプト + [1][2][3]出典付きコンテキスト
  → LlmGateway → llama-server-llm POST /v1/chat/completions (stream, temp=0, max_tokens=512)
  → SSE: トークン流し + 完了時に出典メタデータ（文書名・ページ・該当箇所）
  → ヒット0件なら「該当する記載がありません」を直接返す（推論スキップ）
```

## 5. コンポーネント設計

### 5.1 推論エンジン層 — llama.cpp プリビルド（変更の核心）

**バイナリ調達**: GitHub `ggml-org/llama.cpp` Releases の公式ビルドをダウンロードして同梱展開。**ユーザーPC・CIどちらでもコンパイルしない**:

| 用途 | asset（bNNNNN=ビルド番号） | サイズ目安 | 選択基準 |
|------|---------------------------|-----------|---------|
| CPU 既定 | `llama-bNNNNN-bin-win-cpu-x64.zip` | 約15MB | すべてのPCのフォールバック |
| AMD/Intel GPU | `llama-bNNNNN-bin-win-vulkan-x64.zip` | 約38MB | Vulkan対応GPU検出時（iGPUのprompt処理高速化） |
| NVIDIA | `llama-bNNNNN-bin-win-cuda-12.4-x64.zip`（DLL同梱） | 約400MB | NVIDIA検出時のみDL |

- バックエンドは `engine/<variant>/llama-server.exe` を起動時に選択。ビルド番号は `models.json` カタログで**ピン留め**（フラグ互換性の劣化防止）
- インストーラに CPU 版を**最初から同梱**（+15MB）。Vulkan/CUDA はハード検出時に追加DL
- 参考: [llama.cpp Releases](https://github.com/ggml-org/llama.cpp/releases)

**プロセス拓張（Ollama env チューニングの置換）**:

| 現行（Ollama） | v2（llama-serverフラグ） |
|---|---|
| `OLLAMA_FLASH_ATTENTION=1` | `-fa on`（CPU。**Vulkanではfa offが実測14〜20%高速**） |
| `OLLAMA_KV_CACHE_TYPE=q8_0` | **CPU標準はKV f16**（フェーズ0実測: q8_0はCPUでpp約20%減速。q8_0は8GB機のメモリ優先オプション） |
| `num_ctx` RAM階級 2048/4096/8192 | `-c 2048/4096/8192`（同一階級ロジックを流用） |
| `OLLAMA_NUM_PARALLEL=1` | `-np 1` ＋ `-ub 512`（バッチ最適化） |
| `OLLAMA_KEEP_ALIVE=60m` | スーパーバイザのidleタイマー（60分無操作でLLM子プロセス終了、再問い合わせ時2〜5秒で再起動） |
| GPU自動判定（`gpu_mode.txt`） | 同一WMI判定をバックエンドに移植 → NVIDIA:`-ngl 999`(CUDA)／AMD・Intel iGPU: Vulkan版オプション（実測pp +37〜71%・検索系2〜3倍高速。ただしb10936でキャッシュ再利用経路に初chunk遅延の特輪を検出→**既定はCPU・Vulkanは設定切替**。ビルド更新時に再判定） |
| IFEO `CpuPriorityClass=5` | **不要** — 子プロセスなので `SetPriorityClass(BELOW_NORMAL)` を直接設定 |
| モデルアンロード（`/api/generate keep_alive:0`） | アプリ終了時にスーパーバイザが子プロセス停止（API不要） |

**実測済み速度レバー（フェーズ0検証、詳細は [phase0-report.md](phase0-report.md)）**:
- **prefixキャッシュ（最重要）**: システムプロンプト等の静的prefixを前方固定にすると、同一prefix再質問のprompt処理が 24,758ms→880ms（4%）。**プロンプト設計は「静的prefix前方＋可変コンテキスト後置」を必須とする**
- `-ub 512`: prompt処理（RAGでは1〜2Kトークン投下がTTFTの支配要因）のバッチ高速化
- `--jinja`: Qwen系チャットテンプレート正確適用
- 仕様検討: `--spec-draft`（0.6Bドラフトによる投機的デコーディング）。フェーズ4の実験項目

### 5.2 モデル配布 — GGUF 直接ダウンロード

**モデル配布（Microsoft Store対応の確定設計 — 2026-09要件検証済み）**:
- **インストーラには実行ファイルのみ同梱**（バックエンド＋UI＋llama-server ≈60〜80MB・全PE署名・完全スタンドアロン）。Store の「web installer禁止」要件を満たす
- **モデルGGUF（非実行データ）は初回起動時にアプリ内DL**: 階級選択（クイック1.7B=1.0GB／標準4B=2.3GB＋埋め込み0.6GB）→ SHA256検証 → 進捗UI。「モデル管理」画面と同じ経路で追加・削除・切替も対応
- フォールバック: 全同梱版（≈3.6GB）を同一Innoスクリプトの `-DBundleModels=all` バリアントで随时生成可（GitHub配布用・審査リスク時の切替）
- エンタープライズ完全オフライン: `models/` への事前配置を検出してDL省略（無人展開対応）
- 詳細は [error-codes-v2.md](error-codes-v2.md) §7.1

`ollama pull` を廃止し、GGUF を直接取得:

```
https://huggingface.co/<org>/<repo>/resolve/main/<file>.gguf
  ↓ curl.exe -L -C -（レジューム）+ リトライ + 進捗コールバック
  ↓ SHA256 検証（カタログ記載値と照合）
  ↓ 失敗時ミラーfallback（hf-mirror.com 等、カタログに候補リスト）
models/<model-id>/model.gguf
models/<model-id>/model.json   ← repo・revision・sha・dim・ctx・量子化・ライセンス
```

**モデルカタログ（`models.json`、データ駆動＝コード変更なしで差し替え可能）**:

```jsonc
{
  "engine": { "build": 10936, "variants": ["cpu", "vulkan", "cuda-12.4"] },
  "chat_models": [
    { "id": "qwen3-4b-instruct-2507-q4km", "repo": "unsloth/Qwen3-4B-Instruct-2507-GGUF",
      "file": "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", "size_bytes": 2497254435,
      "sha256": "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597",
      "min_ram_gb": 8, "tier": "standard" },
    { "id": "qwen3-1.7b-q4km", "size_bytes": 1180000000, "min_ram_gb": 6, "tier": "light" },
    { "id": "qwen3-8b-q4km", "size_bytes": 5300000000, "min_ram_gb": 16, "tier": "quality" }
  ],
  "embedding": { "id": "bge-m3-q8", "repo": "gpustack/bge-m3-GGUF",
      "file": "bge-m3-Q8_0.gguf", "dim": 1024, "size_bytes": 634553760,
      "sha256": "950f4a8e5e19477a6d3c26d2f162233c20002c601f75e4b002e3239997821167" },
  "reranker": { "id": "bge-reranker-v2-m3-q8", "repo": "gpustack/bge-reranker-v2-m3-GGUF",
      "file": "bge-reranker-v2-m3-Q8_0.gguf", "size_bytes": 635676416,
      "sha256": "a43c7c9b11a4c1517e5bf95151960e1621d1b72f7a493364b01e386cf1aaa1d3",
      "min_ram_gb": 16 }
}
```

> **フェーズ0検証済み**（[phase0-report.md](phase0-report.md)）: b10936プリビルド＋上記3GGUFで全機能動作・SHA256一致・RAG全問正答。Qwen公式GGUFリポジトリは存在せず unsloth 版を採用。チャットモデルはゴールデンQA拡充時に再評価可能なデータ駆動構造。

**インストーラのモデル選択（現行UIを踏襲）**:
| プリセット | モデル | サイズ | 対象 |
|---|---|---|---|
| 高速（既定） | Qwen3-4B-Instruct Q4_K_M | 約2.4GB | 8GB機 |
| 高品質 | Qwen3-8B Q4_K_M | 約5GB | 16GB機 |
| 軽量 | Qwen3-1.7B Q4_K_M | 約1.1GB | 省RAM機 |

埋め込み `bge-m3 Q8_0`（約0.6GB）は全構成共通で必須DL。リランカは16GB機のみ自動追加。

### 5.3 バックエンド — ShineosQA.Backend

**言語選定**（「C/C++で」との相談への回答）:

| 選択肢 | 実行形態 | サイズ | 開発効率 | 評価 |
|---|---|---|---|---|
| **C# (.NET NativeAOT)** ★推奨 | 単一自己完結exe | 約15〜30MB | ◎（既存WPFがC#。PdfPig/OpenXML/QuestPDF等の資産がそのまま使える） | ランタイムインストール不要・起動高速 |
| Go | 単一静的exe | 約12MB | ○（新言語導入） | 実質同等だが資産を使えない |
| C++ | 単一exe | 約2MB | △（JSON/HTTP/Unicode処理のコスト、メモリ安全性） | 最小フットプリントだがRAG処理は<20msでボトルネックにならず、速度メリットが浮かない |

**推奨の理由**: ホットパス（行列演算）は100% llama.cpp（C/C++/CUDA）が担い、バックエンドに求められるのは検索・プロンプト組立・プロセス管理・HTTP配信（いずれも数十ms以下）。グルー層をC++化しても体感速度は変わらず、保守コストだけ増える。チーム既存技能（C#）とファイル生成ライブラリ資産を最大化する **C# NativeAOT 単一バイナリ** を推す。CI でのみ .NET SDK が必要（ユーザーPCには何も不要）。※最小主義を取るなら .NET Framework 4.8 + csc.exe（現行WPFと同一ビルド方式、sqlite3.dll 同梴）も可能。

**モジュール構成**:
```
ShineosQA.Backend/
  Host/          サービスエントリ・設定読込・ログ（構造化・ローテーション）
  Supervisor/    llama-server 子プロセス管理（起動引数生成・/health監視・クラッシュ再起動
                 ・idle unload・優先度設定・ウォームアップ）
  LlmGateway/    OpenAI互換クライアント（/v1/chat/completions・/v1/embeddings・/v1/rerank
                 ・SSEデコード・リトライ・タイムアウト）
  Ingestion/     ファイル監視＋パーサ（PDF=PdfPig / DOCX=ZipArchive+XML（XXE無効化）
                 / MD / TXT）＋文末境界チャンカー
  Retrieval/     FTS5 BM25 ＋ float32全件コサインのハイブリッド ＋ リランク
  ChatApi/       /api/chat(SSE) /api/knowledge /api/models /api/search /api/settings
  WebUi/         静的ファイル配信（wwwroot/）
```

### 5.4 RAG パイプライン（精度の要）

**ストレージ: SQLite 単一ファイル `data/knowledge.db`**（ChromaDB 廃止）
- `files(file_id, name, path, hash, status, added_at)`
- `chunks(chunk_id, file_id, seq, text, page, embedding BLOB /*float32×dim*/)`
- `chunks_fts`: **FTS5**。日本語はアプリ層トークナイザで「CJKバイグラム＋英数字トークン」に分割して挿入（trigramは2文字クエリ「経費」等が拾えないため不採用。形態素器は導入しない）
- 規模想定: 社内文書1万チャンク（1024次元）= ベクトル40MB。全件コサインはSIMDで**<20ms**。10万チャンク超の将来要件が出たら初めてANN（HNSW等）を検討

**チャンキング改善（精度向上その1）**: 現行 300/30 文字の固定長から、**文末（。）境界を尊重した 300〜400文字・オーバーラップ50** に変更。引用の切れ目が文境界に揃い、出典表示とモデルの根拠参照の両方が改善される。

**取得（精度向上その2）**:
```
現行: top_k=3 をハイブリッドで取得 → そのまま注入
v2 :  top_k=8 をハイブリッド取得（BM25 0.5 + cos 0.5、RELEVANCE_THRESHOLD=0.55踏襲）
      → bge-reranker-v2-m3 でリランク → 上位3件を注入
      ※リランク無効構成(8GB機)は従来どおり top_k=3
```
リランカ（cross-encoder）は bi-encoder 埋め込みより精度が高く、RAG の出典正解率に対する最大の改善レバー。llama-server は `--rerank --pooling rank` と `/v1/rerank` エンドポイントでネイティブ対応している。

**フェーズ0実測の追加知見**（[phase0-report.md](phase0-report.md)）:
- API契約は `results[{index, relevance_score}]`（ロジットスケール・負値あり・降順ソート済み）
- 実データで「ベクトル検索上位がQ&A形式の表層類似チャンクに奪われ、規程本文が下位」のケースをリランクが正しく救出（正値 vs 負値の分離が明瞭）
- **関連文書が一切ない場合、全候補が負値（≤−8）になる** → 推論前に「該当する記載がありません」を固定文返答する**ガードのシグナル**として利用（ハルシネーション抑制を二重化）

**プロンプト注入（現行から踏襲）**: `configure_model.ps1` の日本語ガードレール（結論先行・文書名引用・「該当する記載がありません」）と RAG テンプレートをそのまま `prompts/ja-rag.md` に移植。`temperature=0`（greedy）・`max_tokens=512` も踏襲。ヒット0件は推論をスキップして固定文返答（高速化＋確実な抑制）。

**プリセット**: 用途別ボット（経費精算ガイド等）は config.json のデータに:
```jsonc
{ "presets": [
    { "id": "keihi", "name": "経費精算ガイド", "system_prompt": "…",
      "knowledge": ["経費精算"], "llm_model": "auto" } ] }
```

### 5.5 フロントエンド

「フロントエンド」は2層に分かれる。**UI本体**（チャット・ナレッジ管理・モデル管理・設定画面）と**デスクトップシェル**（WPFラッパー）である。

**UI本体 — TypeScript + Vite + フレームワーク（CIビルド・静的配信）**:
- Vite + TypeScript + Svelte または React + コンポーネントライブラリ（shadcn/ui 等）で構築し、**CI で静的ファイルにビルド** → `wwwroot/` をバックエンドが配信
- **ユーザーPCには何もビルドしない**（成果物は静的ファイルのみ）。v1 の苦痛は「ユーザーPC上での pip/venv/パッチ」であり、CI でのフロントビルドはまったく別物で許容する
- 素の JavaScript で SSE ストリーミング＋Markdown 描画＋XSS サニタイズ＋管理画面を書くのは品質リスクが高く、型付き TS ＋既存ライブラリ資産（react-markdown 等）の方が開発・保守とも有利
- バックエンド API は OpenAPI スキーマから型生成し、UI と C# の契約を型で固定

**シェル — v2.0 は現行 WPF+WebView2 維持、将来 Tauri 差し替え可**:
- v2.0 では現行シェルをほぼ無変更で流用（`port.txt`・サービス起動・/health待ち・終了時停止）。変更は OWUI 向け JS 注入の削除のみ
- **将来オプション: Tauri 2 + TypeScript** — `create-tauri-app` テンプレートから構築可。UI 資産はそのまま流用でき、差し替えるのはシェル層のみ（サービス起動/停止・ヘルス待ちを Rust の薄い層で再実装、約200行）。Windows 版 Tauri は WebView2 を使うためランタイム要件は現行と同一。採用時は CI に Rust+MSVC ツールチェーンが必要（コールドビルド5〜10分）
- **全面 Rust 化（Tauri バックエンドに RAG/スーパーバイザを統合し C# を廃止）は非採用**: 承認済みの C# 決定を覆す開発リスクに見合わず、PC 起動時（ログイン前）の自動起動には結局 Windows サービスが必要で二重管理になる

### 5.6 ファイル生成（/pdf /pptx /docx）と Web検索

- **フェーズ3でC#ライブラリ化**: PDF=QuestPDF・XLSX=ClosedXML・PPTX=ShapeCrawler・DOCX=OpenXML。常駐サービスではなく**利用時のみバックエンド内で生成**（約1〜2分の処理をSSEで進捗表示）
- 移行期間は現行 `filegen_server.py` をオプション側車として併置可。ただし `doc_pipeline.py` の LLM 呼び出しは Ollama 固有（`/api/generate format=schema`）なので LlmGateway 経由の OpenAI 互換 (`response_format`/tools) に書換え
- Web検索（既定OFF）はバックエンドに移植。DuckDuckGo・APIキー不要・ON時のみ外部送信の仕様は踏襲

## 6. リソース予算（目安・フェーズ0で計測確定）

### インストールサイズ
| | 現行 v1 | v2 |
|---|---|---|
| エンジン/ミドル | Ollama 1.5GB + Python/venv/torch/OWUI 約1.5GB | llama.cpp 約0.02GB + バックエンド約0.02GB |
| モデル | qwen2.5:3b 1.9GB + bge-m3 1.2GB | qwen3-4b Q4_K_M 2.4GB + bge-m3 Q8 0.6GB |
| **合計DL** | **約6GB** | **約3.1GB**（NVIDIA機はCUDA分+0.4GB） |
| 所要 | 15〜40分 | 回線比例で約半分。**pip/venv/パッチ工程ゼロ**でインストール後の初動も速い |

### 実行時メモリ（フェーズ0実測・Ryzen 7 5700U/16GB機）
```
llama-server-llm (Qwen3-4B Q4_K_M, -c 4096, fa on, KV f16)   RSS実測 4.63GB
  （-c 8192 で 4.97GB。KV q8_0 化で −0.6GB だが pp −20%）
llama-server-embed (bge-m3 Q8)                                約0.7GB（常駐）
ShineosQA.Backend + SQLite                                    約0.1〜0.2GB
─────────────────────────────────────
```
**3サーバー同時常駐の実測（latency-verification.md §4）**:
| 階級 | chatモデル | 3サーバー合計RSS | 対象 |
|---|---|---|---|
| 標準 | Qwen3-4B Q4_K_M（c2048） | **5.29GB** | 16GB機 |
| クイック | Qwen3-1.7B Q4_K_M | **2.92GB** | **8GB機（OS込み約5.5GBで余裕）** |

- **8GB機の構成確定: クイック階級（1.7B）** — hit 8/8を維持しつつpp 125 t/s・tg 20.8 t/s・2.92GBを実測
- 16GB機は **4B標準＋リランク**。quality tier で 8B も選択可（現行16GB構成は3B止まりだった精度上限を引き上げ）
- 精度オプションのリランカは +4.3〜4.7s/質問（top8・フルサイズ）。スニペット化で約-30%をフェーズ1で実装

### 遅延予算（8GB CPU機・1質問あたり）
| 区分 | 予算 | 備考 |
|---|---|---|
| 検索（BM25+cos） | <30ms | SQLite＋SIMD |
| リランク（有効時） | 0.3〜1s | 8ペア×cross-encoder |
| prompt処理 1.5〜2K tok | 10〜25s | **TTFTの支配項**。`-ub 512`・prefixキャッシュで削減目標 |
| 生成 512 tok | 15〜30s | モデル・メモリ帯域依存（llama.cpp本体性能＝現行同等） |

## 7. プロセス・運用設計

- **サービス**: `ShineosQA`（バックエンド）1本のみをOSサービス登録（当初は既存 vendor/nssm.exe を流用、将来的に self-host 化）。自動起動・PC再起動後復帰は現行踏襲
- **ウォームアップ**: 起動タスクで embed＋LLM プリロード（RAM≥14GBのみ、現行 `ShineosWarmup` 踏襲）
- **終了時解放**: アプリ終了 → スーパーバイザが LLM 子プロセス停止（embed は次回起動高速化のため常駐継続も可、設定化）
- **堅牢化**: 子プロセスクラッシュ→自動再起動（3回/指数バックオフ）。ポートは 127.0.0.1 のみバインド（外部露出なし・ファイアウォール設定不要）
- **セキュリティ**: DOCX パースは `DtdProcessing=Ignore`＋外部エンティティ無効（v1.0.78 の XXE 対策を継承）。GGUF は SHA256 検証後にのみロード。DLはHTTPSのみ

### アンインストールの劇的簡素化
現行は Ollama アンインストーラ起動・`%USERPROFILE%\.ollama` 削除・env/IFEO/Runキー掃除（`installer.iss:799-861`）。v2 は**自ディレクトリ＋サービス1つ＋ウォームアップタスクのみ**。他アプリ（ユーザーが個別に入れた Ollama）に一切触らない。

### 既存インストールからの移行
- ナレッジ原典は `{app}\knowledge` にファイルとして残っているのが信頼できる真実源 → **v2 インストーラが同フォルダを再取り込み**（新パイプラインで再チャンク＋再埋め込み。100文書で数分）
- ChromaDB ベクトルの変換は行わない（埋め込みモデルが同一でもスキーマ移行のコストに見合わない）
- チャット履歴は現行 webui.db からのエクスポート→新形式取り込みをオプション提供（必須ではない）

## 8. 精度評価（フェーズ0ゲート）

**ゴールデンQAセット**: `knowledge/` テスト文書＋50〜100問（出典正解付き）を用意。

| 指標 | 合格基準 |
|---|---|
| 出典正解率 recall@3 | 現行 v1 以上 |
| 回答忠実度（強モデル LLM-judge・オフライン） | 現行以上 |
| ハルシネーション（ナレッジ外質問で「該当なし」率） | 100%維持 |
| TTFT / p95 応答時間（4コア8GB GPUなし実機） | 現行以下 |

**ベークオフ対象**: チャットモデル（qwen3-4b-2507 vs qwen2.5-3b vs gemma-3-4b）、量子化（Q4_K_M vs Q5/Q8）、`-ub`/`--cache-reuse` 効果、Vulkan iGPU 有効性（現行はAMD iGPU無効としているが、prompt処理はVulkanで改善する可能性を再検証）。

## 9. 実装フェーズ

| フェーズ | 内容 | 出口条件 |
|---|---|---|
| **0. スパイク／ベークオフ**（先行） | llama-server 手動検証: フラグ・メモリ・速度・/v1/embeddings・/v1/rerank を最小HWで計測。モデル評価 | §8 ゲート通過・カタログ確定 |
| → **完了（2026-09-13、[phase0-report.md](phase0-report.md)）**: カタログ確定（b10936＋3GGUF）、全スモーク合格、RAG実データ全問正答、CPU最適フラグ発見（KV f16）、Vulkan有効性実証。8GB実機検証のみフェーズ1へ引継ぎ | | |
| → **回帰テストも完了（2026-09-13、[regression-test.md](regression-test.md)）**: v1実スタック（稼働中のOWUI+Ollama）との同一コーパス・同一10問比較。**エンジン同一重みパリティ ±6%（劣化なし）、精度 v2(4B) 8/8=v1超（出典形式改善）、速度はv2(3B)がv1上回る・4Bは+30%を設計レバーで相殺** | | |
| → **UXレイテンシ実証も完了（2026-09-13、[latency-verification.md](latency-verification.md)）**: v2-fast構成（痩身＋リランク＋prefixキャッシュ＋ガード＋回答キャッシュ）で**精度10/10維持のまま v1比 平均-35%（CPU）／-48%（Vulkan）**。同文書連続質問TTFT 0.6s・反復0.3s未満・NO-HIT即時拒否。3サーバー同時常駐RSS実測（4B=5.29GB／1.7B=2.92GB→**8GB機構成を1.7Bに確定**） | | |
| **1. コア** | バックエンド骨格＋Supervisor＋ChatApi（RAGなし）＋静的UI最低版＋WPFシェル接続 | ローカルでチャット完結 |
| → **コア＋R＋UIを実装済み（2026-09-13、[dev-v2.md](dev-v2.md)）**: C#バックエンド9ファイル（Supervisor/LlmGateway/SQLite/取り込み（md/txt/docx/pdf簡易）/Web検索/設定）＋Vite+TS UI（17KB・チャット/ナレッジ/履歴/設定）。**実機E2E合格: 正答＋出典・反復0.2s（キャッシュ）・NO-HIT 4.5s（ガード）・Web検索連携・履歴**。残: 8GB実機／WPFシェル接続／サービス化／インストーラ | | |
| **2. RAG** | Ingestion＋ハイブリッド検索＋出典表示＋プリセット＋移行再取り込み | ゴールデンQAで v1同等以上 |
| **3. インストーラ／配布** | installer.iss 全面簡素化・GGUF DL・SHA検証・ミラー・アップグレード／アンインストール・smoke_test 全面書換。**終了コード・サイレントインストール・MS Store要件は [error-codes-v2.md](error-codes-v2.md) の設計に従う** | クリーンインストールE2E＋サイレント/無人インストール検証 |
| **4. 拡張** | リランク自動有効化・ファイル生成C#化・Web検索・投機的デコーディング実験・Vulkan | — |

CI 変更: WPFシェルビルドは現行どおり。バックエンドは .NET SDK で NativeAOT 単一exe をビルド（ユーザーPCにランタイム不要）。署名（SignPath）は現行フロー継続。

## 10. リスクと対策

| リスク | 影響 | 対策 |
|---|---|---|
| llama.cpp フラグ/挙動のビルド間変更 | 起動失敗 | ビルド番号をカタログでピン留め。アップグレードはカタログ更新とセットでE2Eテスト |
| 日本語FTS5の検索品質 | 出典取りこぼし | バイグラム＋BM25は日本語検索の定石。ベークオフで現行（OWUI実装）と recall 比較 |
| bge-m3 GGUF の pooling/正規化差異 | 埋め込み品質低下 | `--pooling cls` + L2正規化をフェーズ0でOllama出力とコサイン一致検証 |
| 8GB機のメモリ圧迫 | スワップ・遅延 | RAM階級でモデル/ctx/リランクを段階的に縮小（現行階級ロジック拡張）。軽量1.7Bへの自動フォールバック |
| ProgressDialog/進捗表示の再実装 | UX退化 | 現行 INI ポーリング方式を踏襲しインストーラUIは極力温存 |
| WebView2シェルとの接続差異 | 小 | port.txt・/health 継続利用で変更最小 |
| ファイル生成のC#ポート手戻り | フェーズ3遅延 | 移行期はPython側車併置で機能欠落を許さない |

## 11. 決定を求める項目

1. **バックエンド言語**: 本設計は C# (NativeAOT) を推奨（§5.3）。Go／C++ を取る場合は同機能構成で実装言語のみ変更
2. **既定チャットモデル**: Qwen3-4B-Instruct-2507 を予定（ベークオフで確定）
3. **リランクの既定**: 16GB以上で自動有効・8GBでは無効（フェーズ0の計測で 8GB でも許容なら有効化へ緩和）
4. **旧環境のOllama完全削除**: v1からのアップグレード時、Ollama を残すか削除するか（現行アンインストーラは削除する。v2はユーザー選択にするのが無難）

---
参考: [llama.cpp Releases（プリビルド）](https://github.com/ggml-org/llama.cpp/releases) / [llama.cpp reranking サポート](https://github.com/ggml-org/llama.cpp/issues/8555) / [bge-reranker-v2-m3-GGUF](https://huggingface.co/gpustack/bge-reranker-v2-m3-GGUF) / [bge-m3-GGUF](https://modelscope.cn/models/gpustack/bge-m3-GGUF)
