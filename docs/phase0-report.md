# フェーズ0 検証レポート — llama.cpp 直結アーキテクチャの実機検証

- 日付: 2026-09-13
- 対象: [architecture-v2.md](architecture-v2.md) §8 のゲート検証（スパイク／ベークオフ）
- 検証環境: **AMD Ryzen 7 5700U（8C16T・15W）／RAM 15.4GB／AMD Radeon内蔵GPU／Windows 11**
  — 「16GB階級・GPUなしOffice PC」を代表する構成。llama.cpp公式プリビルドとGGUF直接配布で**一切コンパイルせず**に検証

## 0. 総括

| 検証項目 | 結果 |
|---|---|
| エンジン配布（プリビルドzip） | ✅ CPU版17.6MB／Vulkan版30.2MB。展開のみで動作。ビルド不要 |
| GGUF直接DL＋SHA256検証 | ✅ 3モデル（計3.51GB）取得し全件ハッシュ一致 |
| `/v1/embeddings`（bge-m3 GGUF Q8） | ✅ Ollama版bge-m3と**コサイン一致 ≥0.9993** — 乗換えで品質不减を实证 |
| `/v1/rerank`（bge-reranker-v2-m3 Q8） | ✅ 動作・top-1正解。スキーマは `results[{index, relevance_score}]`（ロジットスケータ・負値あり・降順ソート済み） |
| `/v1/chat/completions`（Qwen3-4B-2507 Q4_K_M） | ✅ 日本語RAG回答の品質極めて良好（結論先行＋出典引用＋数値正確） |
| RAGミニパイプライン（実knowledge/データ4文書） | ✅ 4問中4問正答、**ナレッジ外質問は「該当する記載がありません」で完全ガード** |
| ハイブリッド検索＋リランク | ✅ ベクトル検索だけでは取りこぼす規程チャンクをリランクが救出 — 設計の精度テーゼを実証 |
| プロンプト（prefix）キャッシュ | ✅ 同一prefix再質問で **prompt処理 24,758ms → 880ms（4%）** |
| Vulkan（AMD iGPU） | ✅ サーバE2E動作。**pp +37〜71%**／tg −17〜20%（TTFT重視なら価値あり） |
| CPU最適フラグ | ⚠️ **KV量子化(q8_0)はCPUではpp −20%と逆効果**（下記詳細） |

**結論: アーキテクチャv2の技術的前提はすべて実機で成立。フェーズ1（実装）に進める。**

## 1. 配布物の確定（カタログ）

| 項目 | 値 |
|---|---|
| llama.cpp ビルド | **b10936**（2026-09-13公開。ピン留め必須 — 1日数回リリース） |
| CPU版 | `llama-b10936-bin-win-cpu-x64.zip` 17.6MB |
| Vulkan版 | `llama-b10936-bin-win-vulkan-x64.zip` 30.2MB |
| チャットモデル | `unsloth/Qwen3-4B-Instruct-2507-GGUF` の `Qwen3-4B-Instruct-2507-Q4_K_M.gguf` **2.326GB** sha256=3605803b…（Qwen公式GGUFリポジトリは存在せず、unsloth版を採用） |
| 埋め込み | `gpustack/bge-m3-GGUF` の `bge-m3-Q8_0.gguf` **0.591GB** sha256=950f4a8e… |
| リランカ | `gpustack/bge-reranker-v2-m3-GGUF` の `bge-reranker-v2-m3-Q8_0.gguf` **0.592GB** sha256=a43c7c9b… |

b10936で設計前提フラグ（`-fa` `-ctk/-ctv` `-ub` `--cache-reuse` `--rerank` `--pooling` `--jinja` `--embd-normalize` `-np` `--spec-*`）すべて使用可を確認。curlによるDLは再開・リトライ・SHA256検証で安定動作。

## 2. 性能データ（実測）

### 2.1 llama-bench フラグ行列（Qwen3-4B Q4_K_M／5700U・8スレッド基準）

| 構成 | pp512 (t/s) | tg128 (t/s) |
|---|---:|---:|
| fa off・KV f16・t8 | 38.05 | 8.30 |
| **fa on・KV f16・t8（推奨）** | **41.12** | **9.25** |
| fa on・KV q8_0・t8（v1相当） | 33.24 | 8.89 |
| fa off・KV f16・t16 | 42.46 | 6.99 |
| fa off・KV f16・t4 | 31.00 | 9.41 |
| **Vulkan・fa off・ngl99** | **72.75** | 7.65 |
| Vulkan・fa on・ngl99 | 58.58 | 5.67 |

**重要発見: 現行v1が全機に設定する `OLLAMA_KV_CACHE_TYPE=q8_0` はCPU機ではppを約20%減速させる**（量子化KVの脱量子化オーバーヘッド）。v2のCPU構成は **KV f16＋fa on** を標準とし、q8_0は「メモリ節約が必要な8GB機のオプション」として位置づける。Vulkanではfa offが速い（fa onはシェーダ経路が不利）。

### 2.2 llama-server E2E（RAG現実的プロンプト・ガードレール+文書3件+質問）

| 構成 | ロード | RSS | pp | tg | 備考 |
|---|---:|---:|---:|---:|---|
| CPU・c8192・fa・q8KV | 13.2s | 4.63GB | 26.4 | 4.9〜5.3 | 初回の設計通り構成 |
| CPU・c4096・fa・f16KV | 5.7s | **4.63GB** | 44.4 | 9.6 | **推奨構成** |
| CPU・c8192・fa・f16KV | 5.1s | 4.97GB | 45.1 | 9.7 | |
| Vulkan・c4096・fa off | 10.9s | 3.65GB | 60.6 | 7.7 | シェーダコンパイルでロード遅 |

- 実RAGプロンプト（約650トークン）でTTFT相当（prompt処理）は推奨CPU構成で約15秒、Vulkanで約10秒
- **prefixキャッシュ**: 同一システムプロンプト＋同一文書コンテキストへの2問目は prompt_ms 24,758→880ms。**プロンプト設計は「静的prefixを前方に置く」構成が必須**（設計書§5.1の`--cache-reuse`/LCPキャッシュ戦略の実証）
- RSS: 4B Q4_K_M＋c4096で約4.6GB。**8GB機ではc2048＋KV q8（メモリ優先）または軽量1.7Bモデルが現実的**。8GB実機での検証はフェーズ1の別途項目

### 2.3 RAGパイプライン実測（knowledge/実データ・4文書→6チャンク）

| 質問 | 期待 | ハイブリッド上位 | リランク後top3 | 最終回答 | 所要 |
|---|---|---|---|---|---:|
| 宿泊費上限 | 旅費規程 | QA_list中心 | **規程chunkが正値(2.16)で救出** | ✅ 15,000円＋出典 | 35.2s |
| パスワード周期 | セキュリティ | QA_list中心 | 規程chunk(1.82) | ✅ 90日＋出典 | 32.2s |
| 結婚休暇 | 慶弔 | QA_list中心 | 規程chunk(2.04) | ✅ 3日＋出典 | 24.1s |
| 宇宙開発予算 | NO-HIT | 低cos | 全体負値(≤−8.6) | ✅ **「該当する記載がありません」** | 19.5s |

- **リランカが精度の要であることを実証**: ベクトル検索のみでは規程本文よりQ&A形式の表面類似チャンクが上位になるケースを、cross-encoderが正しく再順位付け
- NO-HIT判定はリランクスコアが全負値という明確なシグナルでも判別可能 → **推論前ガード（固定文即答）の実装根拠**
- 埋め込み: 短文1件145ms。取り込み（バッチ1リクエスト逐次）約500ms/チャンク → 1000チャンク概算8分。**本番は並列リクエスト（n_slots=4）で3〜4倍化を図る**
- リランク: 短文5件274ms／フルサイズ6チャンク約4.5〜5s → 16GB階級の精度オプションとして+5秒のコストを明示

### 2.4 埋め込み互換性（移行の根拠）

bge-m3 GGUF Q8（`--pooling cls`、`--embd-normalize`デフォルト=L2）vs Ollama bge-m3:
同一テキスト4種のコサイン **0.9993〜0.9995**。語義的順位も一致。
→ v1→v2移行時の再埋め込みは「スキーマ変換」で済み、品質劣化なし。

## 3. 設計への反映事項（確定）

1. **エンジンカタログ**: b10936・unsloth/gpustack の3GGUFで確定（§1表の通り）
2. **CPU標準フラグ**: `-fa on -ub 512 -np 1 -t <物理コア数>`・**KVはf16**（q8_0は8GB機のメモリ優先オプション）
3. **Vulkan**: AMD iGPUでpp +37〜71%。**v1の「AMD iGPU除外」を改め、検出時に有効化可能なオプション（既定ON・設定でCPUに切替可）とする**。Vulkan時はfa off
4. **プロンプト設計**: 静的prefix（システム＋ガードレール）前方固定＋可変コンテキスト後置。キャッシュ命中率がTTFTを支配（880msまで低下実績）
5. **rerank API契約**: `results[{index, relevance_score}]`・全負値=NO-HITシグナル
6. **8GB階級**: 4B+c4096+f16KVはRSS 4.6GBで厳しい。c2048+q8KV（pp−20%受容）か1.7B軽量tier。**8GB実機検証をフェーズ1に追加**
7. **タイムアウト/SLO再設定**: smoke_testのRAG 180秒制限は維持可能（実測最遅35秒）

## 4. 未検証・フェーズ1引継ぎ項目

- [ ] 8GB実機での4B構成検証（c2048+q8KV vs 1.7B）とRAM階級ロジック調整
- [ ] embed＋rerank＋chatの**同時常駐**RSS実測（本検証はRAM空き4.8GBの制約で逐次起動）
- [ ] `--cache-reuse` の部分一致再利用率（同一prefix完全一致は実証済み）
- [ ] `--spec-*` 投機的デコーディング（0.6Bドラフト）のCPU効果
- [ ] Qwen3-8B／Gemma-3-4Bとの品質ベークオフ（ゴールデンQA拡充後。現行データは4Bが全問正答のため追加else不要と判断可）
- [ ] CUDA（NVIDIA実機）確認

## 5. 再現手順

`spikes/phase0/` にスクリプト一式（エンジン・モデルはgitignore済み）:
`download-models.ps1` → `smoke-embed.ps1` → `smoke-rerank.ps1` → `smoke-chat.ps1` → `bench-flags.ps1` / `bench-vulkan.ps1` / `bench-final.ps1` → `rag-pipeline.ps1`

注意: PowerShell 5.1で実行。日本語込みスクリプトはUTF-8 BOM必須（Add-BOM処理済み）。HTTPボディは`[Text.Encoding]::UTF8.GetBytes()`で送ること。
