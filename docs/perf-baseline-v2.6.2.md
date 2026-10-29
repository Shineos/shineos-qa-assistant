# v2.6.2 性能ベースライン（既知実測の整理）

- 目的: Phase 1 以降の性能改善を評価するための **before 側の基準** を、既存の実測記録から出典付きで整理する。
- 位置づけ: 本書は**文献値の集約**であり、新規計測ではない。数値はすべて出典（ファイル:行）を持つ。Phase 1 完了後に [`perf-suite.ps1`](../spikes/phase0/latency-cmp/perf-suite.ps1) で再計測し、「Phase 1 後」列を埋めて更新する。
- 手順・出力形式・合格目標: [perf-suite.md](perf-suite.md)
- 最終更新: 2026-10-29

> **重要な注意（数値の混在）**: 以下の実測は **v2.0.0〜v2.6.2 の複数ビルド・複数環境**にまたがる。同一マシン（Ryzen 7 5700U / 15.4GB / CPUのみ）での計測が多いが、tier・KB・プロンプト版・エンジン設定が異なるため、**異なる行の値を直接引き算・比率比較しない**こと。同一条件の before/after は必ず `perf-suite.ps1` で取り直す。

## 1. 共通の計測環境（文献値）

| 項目 | 値 | 出典 |
|---|---|---|
| CPU / RAM / OS | Ryzen 7 5700U（8C/16T・15W） / 15.4GB / Windows 11・GPUなし | `spikes/phase0/latency-cmp/RESULTS.md:4`、`docs/latency-verification.md:6` |
| エンジン | llama.cpp **b10936**（CPU版・ピン留め） | `docs/phase0-report.md:29` |
| コンテキスト / スレッド | ctx 2048〜4096 / スレッド8 | `RESULTS.md:4`、`docs/phase0-report.md:59` |
| 計測経路 | UI と同一の `POST /api/chat`（SSE） | `RESULTS.md:3`、`docs/latency-verification.md:34` |
| 量子化 | チャットは IQ4_XS（quick=1.7B / standard=4B-2507）、埋め込み・リランカは Q8_0 | `RESULTS.md:116-124` |

## 2. 既知実測（文献値）

### 2.1 quick tier（Qwen3-1.7B IQ4_XS）

| シナリオ | 値 | 出典 |
|---|---|---|
| 新規質問の TTFT | **3.5〜4.6 s** | `README.md:91`、`RESULTS.md:70,104` |
| 起動直後の初回質問（ウォームアップ後） | 5.2 s（改修前 6.2 s）、さらに改善後は 7.2 s→ | `RESULTS.md:103`、`RESULTS.md:71` |
| 同一質問の再質問（回答キャッシュ） | TTFT/合計 **0.15 s / 1.1 s** | `RESULTS.md:69,106` |
| 回答キャッシュ即答（エンジン未ロードでも） | **153 ms** | `RESULTS.md:96` |
| 境界質問（リランク経由） | 6〜7 s | `RESULTS.md:105` |
| ナレッジ外（NO-HIT ガード拒否） | 約 3.5 s | `RESULTS.md:107`、`RESULTS.md:76` |
| 理論下限（リランク省略時） | ≈ **3.4 s**（埋め込み0.3 s＋pp約3.1 s） | `RESULTS.md:109-110` |
| コールド初回（最適化前・参考） | TTFT 10.3 s / 合計 11.2 s | `RESULTS.md:9` |
| llama-server RAM（単体） | 約 2.0 GB WS | `RESULTS.md:14` |
| 3エンジン同時常駐 RSS | **2.92 GB**（chat 1.94＋embed 0.49＋rank 0.49） | `docs/latency-verification.md:62` |

### 2.2 standard tier（Qwen3-4B-Instruct-2507 IQ4_XS）

| シナリオ | 値 | 出典 |
|---|---|---|
| 新規質問の TTFT | **約 8〜9 s** | `README.md:91` |
| コールド初回（最適化前・参考） | TTFT 17.6 s / 合計 19.4 s | `RESULTS.md:9` |
| 同一質問の再質問（warm） | 3.8 s / 5.7 s | `RESULTS.md:10` |
| フォローアップ（履歴あり） | 11.7 s | `RESULTS.md:74` |
| 実運用時 pp | 約 44 t/s（単体ベンチ 46.2 t/s） | `RESULTS.md:79`、`RESULTS.md:124` |
| 3エンジン同時常駐 RSS | **5.29 GB**（chat 4.32＋0.49＋0.49） | `docs/latency-verification.md:61` |
| 単体構成 RSS（c4096, fa, f16KV） | 4.63 GB | `docs/phase0-report.md:59` |

### 2.3 quality tier（Qwen3-30B-A3B MoE・参考）

| シナリオ | 値 | 出典 |
|---|---|---|
| 新規質問の TTFT | 約 11〜14 s | `README.md:91` |
| コールド初回 | 33.2 s / 36.9 s | `RESULTS.md:9` |
| ウォーム再質問（合計） | 2.0 s | `RESULTS.md:75` |
| エンジン RAM | 9.5 GB（mmap・ファイルバック除く） | `RESULTS.md:14` |
| 位置づけ | 16GB機向けの品質優先。既定・8GB機には不向き | `RESULTS.md:27-29` |

### 2.4 RAG パイプライン固定費（quick/standard 共通の支配要因）

| 項目 | 値 | 出典 |
|---|---|---|
| リランク（top8・フルサイズ） | **4.3〜4.7 s/問** | `docs/latency-verification.md:34,74` |
| リランク入力のスニペット化（240字） | **3.2 s（-29%）** | `docs/latency-verification.md:74` |
| 高信頼時のリランク省略 | 約 2.2 s の短縮（cos≥0.70・kw≥0.15 条件） | `RESULTS.md:54-56` |
| 埋め込み（短文1件） | 145 ms | `docs/phase0-report.md:78` |
| 取り込み | 約 500 ms/チャンク（逐次） | `docs/phase0-report.md:78` |
| NO-HIT 判定の分離 | hit ≥ +0.26 / nohit ≤ -7.84（閾値 -2.0） | `docs/latency-verification.md:55` |

### 2.5 キャッシュ・体感（v2-fast 構成の実測）

| シナリオ | 値 | 出典 |
|---|---|---|
| 同一文書への連続質問 TTFT | 0.53〜0.67 s（prefix キャッシュ Hit） | `docs/latency-verification.md:30,52` |
| prefix 再処理の短縮 | pp 8,851 ms → 486 ms（-94.5%） | `docs/latency-verification.md:51` |
| 10問平均（v1 比較） | 14.7 s → 8.8 s（-40%） | `docs/latency-verification.md:46` |
| 反復質問 | 0.3 s 未満 | `docs/latency-verification.md:44` |
| アプリ起動→入力可能 | 約 2 s（モデル事前ロード込み） | `README.md:93` |

### 2.6 v2.6.2 直後の main（未リリース・commit `8242d40`）の実測

> これらは **v2.6.2 タグより後の commit**（`git log v2.6.2..HEAD` の 5 件のうち `8242d40`）に記録された実測。v2.6.2 タグ時点の値ではない点に注意。

| 項目 | 値 | 出典 |
|---|---|---|
| Dify との head-to-head（TTFT） | **6.5 s vs 1.7 s**（Dify が優位） | commit `8242d40` のコミット本文（`git show -s --format=%B 8242d40`） |
| 全量コンテキストモード（KB 合計 ≤3,000字）の warm TTFT | **1.4〜1.9 s**（検索・リランク・スニペットを省略し KB 全量を system に注入） | 同上 |
| SystemPrompt の圧縮 | 950 → 約 400 文字 | 同上 |
| 基礎シナリオ | 16/18 → **17/18**（s07 回復、s12 は KB 欠け） | 同上 |

### 2.7 `bg_friendly`（他アプリ優先モード）の扱い

| 項目 | 値 | 出典 |
|---|---|---|
| 既定値 | **ON**（LLM 生成を BelowNormal 優先度で実行） | `app/ShineosQA.Backend/Config.cs:30` |
| 併走負荷時の効果 | 他アプリのスループット低下 -11% → なし（105%）。生成速度は 39.7 s vs 41.3 s で劣化なし | `CHANGELOG.md:443` |
| 一方、計測上の注意 | 「BelowNormal は pp -24% だったため Normal + `-ub 2048` を採用」との記録がある | `spikes/phase0/latency-cmp/RESULTS.md:62` |

> 上記 2 行は**矛盾ではなく計測条件の違い**（前者は並走負荷下の E2E、後者は当時のベンチ判断）と推定されるが、**同一条件での再計測は未実施**。Phase 1 の最初の計測で `bg_friendly` ON/OFF の quick cold p50 を比較し、本書に追記する。

### 2.8 アイドルアンロード・メモリ

| 項目 | 値 | 出典 |
|---|---|---|
| 既定のアイドルアンロード | 60 分（設定で 30/60/120/無効に変更可） | `app/ShineosQA.Backend/Config.cs:29`、`CHANGELOG.md:444` |
| 段階解放の効果 | 常駐メモリ 2.29GB → 約 0.08GB（バックエンドのみ） | `CHANGELOG.md:444` |
| 復帰時間 | 次の質問で自動再起動（実測 18 秒で回答まで復帰） | `CHANGELOG.md:444` |

## 3. Phase 1 後の再計測プレースホルダ

Phase 1 完了後、[perf-suite.md](perf-suite.md) §7 の手順で計測し、下表の「Phase 1 後」列を埋める。**空欄は未計測**を意味する（数値を推測で埋めない）。

計測条件テンプレート: `tier`=quick / `bg_friendly`=ON / `web_search`=OFF / `chunks`=（記録）/ `questions.json` 4問 / `Runs`=10 / 冷 warm 自動（cold 1 + warm 9） / 同一マシン・他プロセス停止。

| # | 指標（phase） | v2.6.2 既知（文献値・出典） | Phase 1 後 p50 | Phase 1 後 p95 | n | 計測日 | commit | JSON |
|---|---|---|---|---|---|---|---|---|
| 1 | quick 新規質問 TTFT（cold: 1回目） | 3.5〜4.6 s（`README.md:91`） | 未計測 | 未計測 | | | | |
| 2 | quick 新規質問 合計（cold） | 4.0〜11.7 s（質問依存: q1=4.0 / q2=7.7 / q3=9.8、IQ4_XS。`RESULTS.md:129-131`） | 未計測 | 未計測 | | | | |
| 3 | quick 再質問（warm・回答キャッシュ含む） | 0.15 s / 1.1 s（`RESULTS.md:69`） | 未計測 | 未計測 | | | | |
| 4 | quick 境界（リランク経由） | 6〜7 s（`RESULTS.md:105`） | 未計測 | 未計測 | | | | |
| 5 | quick 拒否（NO-HIT ガード） | 約 3.5 s（`RESULTS.md:107`） | 未計測 | 未計測 | | | | |
| 6 | standard 新規質問 TTFT（cold） | 8〜9 s（`README.md:91`） | 未計測 | 未計測 | | | | |
| 7 | standard 再質問（warm） | 3.8 s / 5.7 s（`RESULTS.md:10`） | 未計測 | 未計測 | | | | |
| 8 | quality 新規質問 TTFT（cold・参考） | 11〜14 s（`README.md:91`） | 未計測 | 未計測 | | | | |
| 9 | 全量モード（KB≤3,000字）warm TTFT | 1.4〜1.9 s（commit `8242d40`） | 未計測 | 未計測 | | | | |
| 10 | `bg_friendly` ON/OFF 差分（quick cold p50） | 未計測（§2.7 の矛盾を解消） | 未計測 | 未計測 | | | | |
| 11 | 3エンジン同時常駐 RSS（quick / standard） | 2.92 GB / 5.29 GB（`latency-verification.md:61-62`） | 未計測 | — | | | | |
| 12 | 構造化即答（Phase 2 導入後） | 未実装（目標 ≤0.6 s） | — | — | | | | |

## 4. 再計測手順（チェックリスト）

1. インストール済みアプリ・孤児 `llama-server` を停止（`Get-Process llama-server` が 0 件）。併走すると TTFT が大きくぶれる。
2. バックエンド起動 → `/api/status` で `chunks > 0`・`tier`・`chat_model`・`version` を確認。
3. `/api/settings` で `web_search=false`・`bg_friendly`・`idle_unload_minutes` を固定。
4. 初回ウォームアップ完了を待つ（起動時に事前ロード＋prefix ウォームアップが走る: `app/ShineosQA.Backend/Program.cs:754-768`）。
5. `perf-suite.ps1 -Runs 10 -Label <tier>-<条件>` を実行し JSON を保存。
6. 条件（tier・bg_friendly・web_search・KB・モデル）を変えて繰り返す。**1 条件 1 JSON**。
7. 本書 §3 の表へ転記（JSON パス・commit・計測日を併記）。
8. 結果が §6 の合格目標を満たすか判定する（[perf-suite.md](perf-suite.md) §6）。

## 5. 既知の限界・未検証

- **数値は複数ビルドの混在**（v2.0.0〜v2.6.2＋未リリース main）。特に §2.6 は v2.6.2 タグ後。
- **8GB 実機での quick tier 検証は未実施**（`docs/dev-v2.md:85` の未完項目）。文献値の多くは 15.4GB 機。
- **p50/p95 の統計**は従来の記録に存在しない（平均・個別値のみ）。Phase 1 の `perf-suite.ps1` で初めて p50/p95 が揃う。
- **cold の定義**が文献ごとに揺れる（プロセス再起動 / 回答キャッシュのみクリア）。比較時は必ず条件を併記する。
- `bg_friendly` の pp -24% と「劣化なし」の関係（§2.7）は未解明。ON/OFF の同一条件計測で解消する。
- Dify 比較（6.5 s vs 1.7 s）は単発の head-to-head であり、条件・KB・プロンプトが本書の他行と同一でない。
- 計測対象はチャット経路のみ。取り込み・要約・作成モード（下書き）・OCR・図面 E2E の性能は対象外。
