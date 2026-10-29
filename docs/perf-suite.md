# 性能計測スイート（perf-suite.ps1）

- 対象: `spikes/phase0/latency-cmp/perf-suite.ps1`（新規・Phase 0 計測ツール）
- 関連: 既知実測の整理は [perf-baseline-v2.6.2.md](perf-baseline-v2.6.2.md) ／ 従来の簡易計測は `spikes/phase0/latency-cmp/measure.ps1`
- 最終更新: 2026-10-29

## 1. 目的と方式

**目的**: 質問×複数回の **TTFT（最初の1文字まで）** と **合計時間** を計測し、質問ごと・全体の p50/p95/平均/最小/最大を JSON とコンソール表で残す。Phase 1 以降の性能改善を、同じ手順・同じ粒度で before/after 比較できるようにする。

**方式**: UI が使うのと同一の `POST /api/chat`（SSE）を **curl.exe** で受ける（`measure.ps1` と同じ方式）。サーバ側の計測値 `done.ttfb_ms` / `done.ms` を採用し、クライアント実測（リクエスト開始→curl終了）も `client_wall_ms` として併記する。サーバ側値は検索・リランク・プロンプト処理を含むが、`client_wall_ms` は SSE 受信終了までの実時間である。

- 実装上の前提: `done` イベントは通常回答では `ttfb_ms` を持つが、**回答キャッシュ（`cached=true`）・拒否（`guard` 付き refusal）・clarify・下書きでは `ttfb_ms` を持たない**（`app/ShineosQA.Backend/ChatFlow.cs:368,479,523,556,716,937,1145`）。このため TTFT 統計は「`ttfb_ms` が存在するサンプル」だけで計算し、サンプル数 `n` を併記する。

## 2. 前提条件

| 項目 | 要件 |
|---|---|
| バックエンド | 起動済み（既定 `http://127.0.0.1:8300`）。未起動ならスクリプトが理由を表示して終了コード 3 で終了する |
| ナレッジ | 計測用の固定 KB が取り込み済みであること（質問は `questions.json` の内容に依存） |
| モデル | 計測する階級（quick/standard/quality）の GGUF が導入済みであること |
| 設定 | `tier`・`bg_friendly`・`web_search` を固定し、**計測中は変更しない**こと（スクリプトは計測開始時の値を JSON に記録する） |
| 併走プロセス | インストール済みアプリ・孤児 `llama-server`・他インスタンスを停止する。二重常駐は RAM と CPU を奪い、TTFT が大きくぶれる（過去に孤児エンジン蓄積で OOM 連鎖を実測: `docs/latency-verification.md:78`） |
| ツール | Windows 10/11 標準の `curl.exe`（8.x）。Windows PowerShell 5.1 で動作 |
| ポート | 8300 が別プロセスに使われていないこと（競合時は `-BaseUrl` で変更） |

> 計測前の確認: `Invoke-RestMethod http://127.0.0.1:8300/api/status` が返り、`chunks` が 0 でないこと（0 は KB 未取り込み＝別 DB を向いている）。

## 3. 実行方法

```powershell
# 既定（questions.json・3回・cold→warm 自動切替・結果は answers/ に JSON 保存）
powershell -NoProfile -ExecutionPolicy Bypass -File spikes\phase0\latency-cmp\perf-suite.ps1 -Label baseline

# 回数を増やす（p95 を安定させるには 10 回以上を推奨）
powershell -NoProfile -ExecutionPolicy Bypass -File spikes\phase0\latency-cmp\perf-suite.ps1 -Runs 10 -Label quick-r10

# 毎回キャッシュを消して「毎回コールド」を測る（回答キャッシュの効果を除外）
powershell -NoProfile -ExecutionPolicy Bypass -File spikes\phase0\latency-cmp\perf-suite.ps1 -Runs 5 -AllCold -Label quick-cold

# 収集ロジックのみ検証（ネットワーク不要）
powershell -NoProfile -ExecutionPolicy Bypass -File spikes\phase0\latency-cmp\perf-suite.ps1 -SelfTest
```

| パラメータ | 既定 | 説明 |
|---|---|---|
| `-BaseUrl` | `http://127.0.0.1:8300` | バックエンドの待受 URL |
| `-QuestionsFile` | `questions.json` | 質問 JSON。相対パスは**スクリプトと同じフォルダ**から解決し、無ければカレントから解決 |
| `-Runs` | `3` | 質問あたりの実行回数（1 以上） |
| `-Label` | `perf` | 出力名・コンソール見出し。既定の出力ファイル名に使う |
| `-OutFile` | 空 | JSON の保存先。空なら `answers/perf-<label>-<yyyyMMdd-HHmmss>.json`（`answers/` は `.gitignore` 対象） |
| `-AllCold` | OFF | 全ランで `/api/cache-clear` を実行（毎回コールド） |
| `-TimeoutSec` | `600` | 1 リクエストの curl `--max-time`（秒） |
| `-KeepChats` | OFF | 計測で作成したテストチャットを削除しない（既定は削除） |
| `-SelfTest` | OFF | 集計ロジック（nearest-rank p50/p95・平均・min/max・n=0/1）を検証して終了 |

**終了コード**: `0`=全サンプル成功 / `2`=一部サンプル失敗（JSON は保存される。失敗理由は `samples[].error`）/ `3`=前提エラー（未起動・質問ファイル不正など）。

### cold と warm の定義

| 区分 | 実行内容 | 意図 |
|---|---|---|
| **cold** | 計測直前に `POST /api/cache-clear` を実行してから質問 | 回答キャッシュ（cos≥0.97）のヒットを除外した「初回」に近い状態 |
| **warm** | キャッシュを消さずに同一質問を再実行 | 実利用で多い「同じ質問・同じ文書への再質問」の体感 |

既定（`-AllCold` なし）は **1 回目=cold、2 回目以降=warm**。`-AllCold` を付けると全回 cold。

**限界（重要）**:
- `/api/cache-clear` が消すのは**回答キャッシュのみ**で、llama-server の prefix/KV キャッシュは残る。したがって cold は「プロセス再起動直後の完全コールド」ではない。
- 既定の warm は **回答キャッシュ即答（`cached=true`）を含む**。warm の `ttfb_ms` は存在しないため TTFT 統計から外れる。prefix キャッシュのみの効果を測るには、回答キャッシュを消さずに**別の質問**（同文書）を投げる設計にするか、`-AllCold` との差分で解釈する。
- 厳密なフルコールド（エンジン再起動込み）はこのスクリプトの範囲外。必要な場合はアプリ終了→起動→ウォームアップ完了待ち→計測、を手作業で行い、その旨を結果に記録する。

### 質問ファイル形式（重要）

既定の `questions.json` は **`tag` と `q` だけ**の配列:

```json
[
  { "tag": "q1", "q": "国内出張の日当はいくらですか" },
  { "tag": "q2", "q": "交通費の精算方法を教えてください" }
]
```

`scenarios-100.json`（品質シナリオ）は**形式が異なる**ため、そのままでは使えない。差分は以下:

| フィールド | questions.json | scenarios-100.json | perf-suite の扱い |
|---|---|---|---|
| `tag` | あり | あり（s01-日当国内 等） | 使用（無ければ `q1..` を自動採番） |
| `q` | あり | あり | 必須 |
| `expect` | 無し | あり（正解キーワードの配列） | **無視**（採点は品質シナリオの役割） |
| `refuse` / `refusekw` | 無し | あり | **無視**（拒否は `guard` として結果に出る） |
| `pre`（フォローアップ前段） | 無し | あり | **未対応**（指定すると単発質問として実行される） |

変換例（PowerShell）:

```powershell
$src = Get-Content spikes\phase0\latency-cmp\scenarios-100.json -Raw -Encoding UTF8 | ConvertFrom-Json
$src | Select-Object tag, q | ConvertTo-Json -Depth 3 |
  Set-Content spikes\phase0\latency-cmp\questions-100.json -Encoding UTF8
```

> 注意: 品質シナリオの `pre`（前段質問）を使う問は、変換すると文脈なしの単発質問になる。性能計測では `pre` 付きの問を除外するか、前段を手動で先に送ってから計測すること。

## 4. 出力 JSON のフィールド

保存は **UTF-8（BOM 付き）**。主なフィールド:

| フィールド | 内容 |
|---|---|
| `schema` | `perf-suite/v1` |
| `label` / `generated_at` / `questions_file` / `runs` / `all_cold` / `timeout_sec` | 実行条件 |
| `environment` | `base_url`・`ps_version`・`os`・`server_version`・`tier`・`chat_model`・`chunks`・`ram_gb`（`GET /api/status` の値） |
| `settings` | `web_search`・`tier`・`bg_friendly`・`idle_unload_minutes`・`drawing_enabled`（`GET /api/settings` の値） |
| `process_stats` | 計測終了時の `llama-server` の `working_set_mb` / `private_mb`（存在する場合） |
| `samples[]` | 1 実行 = 1 要素。`tag` `question` `run` `phase`(cold/warm) `ok` `error` `cached` `guard` `kind` `server_ttfb_ms` `server_total_ms` `client_wall_ms` `answer_chars` |
| `summary.per_question[]` | 質問ごとの `samples` `ok` `cached` と `ttfb`/`total` の `n` `mean` `min` `max` `p50` `p95` |
| `summary.overall` | 全体の同上 |

- `ok=true` は SSE で `done` を受信できたことを意味する（拒否応答も `done` が来るので `ok=true`。`guard` で区別する）。
- `samples[].error`: `curl_exit_<n>` / `sse_error` / `no_done_event`。
- `summary.*.ttfb.n` は TTFT を取得できたサンプル数。`cached` や拒否が多いほど小さくなる（0 の場合は `p50` 等は `null`）。

## 5. 集計方法

- **p50/p95 は最近順位法（nearest-rank）**。昇順ソート後、`rank = ceil(P/100 × n)` 番目（1 始まり）を採用する。n=1 では p50=p95=その値。平均は算術平均、min/max は実測値。
- 丸めは ms 単位で小数 1 桁（JSON）、コンソール表は秒で小数 2 桁。
- 外れ値は除外しない（そのまま含める）。ノイズ判定は実施者が `Runs` を増やし、p50/p95 と min/max の差で判断する。

## 6. 合格目標（Phase 0 プラン）

以下は本タスクで指定された**計画上の合格目標**（Lead プラン。リポジトリ内文書には未記載）。現行値は [perf-baseline-v2.6.2.md](perf-baseline-v2.6.2.md)、再計測は本スクリプトで行う。

| 階級 | 条件 | p50 目標 | p95 目標 |
|---|---|---|---|
| quick（Qwen3-1.7B） | 既定 tier・warm 相当 | **≤ 2.8 s** | **≤ 4.5 s** |
| standard（Qwen3-4B） | 既定 tier・warm 相当 | **≤ 4.5 s** | **≤ 7.0 s** |
| 構造化即答（Phase 2 の新経路） | 事前定義 Q&A 等 | — | **≤ 0.6 s**（Phase 2 で導入後に計測） |

> 注意: 目標値は「計測条件を固定したうえでの p50/p95」であり、cold（エンジン再起動込み）や初回モデルロードは含めない。報告時は `environment`・`settings`・`runs`・`phase` を必ず併記する。

## 7. ベースラインの取り方（手順）

1. インストール済みアプリ・孤児エンジンを停止し、`Get-Process llama-server` が 0 件であることを確認する。
2. バックエンドを起動する（開発ビルドなら `ShineosQA.Backend.exe --config config.json`、導入済みなら `launch.vbs`）。
3. `/api/status` で `chunks > 0`・意図した `tier`/`chat_model` を確認する。
4. `/api/settings` で `web_search=false`・`bg_friendly`・`idle_unload_minutes` を確認し、**計測中は変更しない**。
5. 初回質問を 1 回流してウォームアップ完了を待つ（バックエンド起動時に事前ロード＋ウォームアップが走る: `app/ShineosQA.Backend/Program.cs:754-768`）。
6. `-Runs 10` 以上・`-Label <tier>-<条件>` で実行する（例: `-Label quick-bg-on`）。
7. 条件を変えて（例: `bg_friendly` ON/OFF、tier quick/standard）同じ手順を繰り返す。**1 条件 1 JSON** で保存する。
8. 結果を [perf-baseline-v2.6.2.md](perf-baseline-v2.6.2.md) の「Phase 1 後」列へ転記する（JSON パス・commit・計測日を併記）。

**ノイズ**: 同一マシンでも機械負荷で ±15% 程度ぶれる（`spikes/phase0/latency-cmp/RESULTS.md:65`）。比較は「同一条件で前後を測る」「n を増やす」「p50 と p95 を併記する」の 3 点を守る。

## 8. 既知の制約・注意

- **PowerShell 5.1 互換**: 三項演算子・`??` は不使用。スクリプト本体は **UTF-8 BOM で保存**すること（BOM が無いと PS5.1 が日本語を ANSI として読む）。JSON 出力も BOM 付き UTF-8。
- **curl.exe 必須**（Windows 10 1803 以降は標準）。`--noproxy "*"` でプロキシ設定の影響を避けている。
- 計測後のテストチャット削除は `/api/chats/{uuid}` を DELETE する（既定）。履歴を汚したくない場合も既定のままでよい。`-KeepChats` で残せる。
- 回答キャッシュは**モデル別に分離されていない**（`prompt_version` 変更で全消去される運用: `spikes/phase0/latency-cmp/RESULTS.md:63`）。モデルを切り替えて計測する場合は計測前に `/api/cache-clear` を通すこと（本スクリプトは cold で自動実行する）。
- 計測対象は**チャット経路のみ**。取り込み（Ingest）・要約・作成モード・OCR の性能は対象外。
- 日本語を含む JSON を curl で送るため、本文は一時ファイルへ UTF-8（BOM なし）で書いて `--data-binary @file` で送る（シェルの文字コード依存を避ける）。
