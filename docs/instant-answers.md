# 即答ルート（FAQ・図面台帳・実績）— Phase B 実装メモ

- 目的: 小型LLM（Qwen3-1.7B）の品質と速度を、モデル変更ではなく**LLMを使わない即答**で引き上げる。
- 設計: DifyのQuestion Classifier / Annotation Replyに相当するが、分類にLLMを使わず、汎用ワークフローも作らない。
- 実装: `2026-10-29`（commit未定。Phase 0/1の`842f790`の後）

## 1. ルート（先勝ち）

| route | 条件 | 処理 | 実測 |
|---|---|---|---|
| `faq` | 正規化質問が公開FAQ（または別名）に完全一致。日付感応質問は除外 | DB検索のみ | 42 ms（サーバ） / 96 ms（クライアント） |
| `drawing` | 図番＋表題欄の属性（材質/品名/改訂/尺度/承認日/図番）のみ。公差・寸法・注記・見積などが混ざる質問は対象外 | `drawing_meta`を直接返す | 数十ms |
| `jisseki` | 図番＋見積/原価/納期/実績。属性質問が混ざる場合は対象外 | `jisseki_meta`を直接返す | 数十ms |
| `rag` / `rag-drawing` / `rag-multi` | 上記以外 | 従来のRAG | 従来どおり |
| `clarify` | 3文字以下など | 従来の確認促進 | — |

分類は [IntentRouter.cs](../app/ShineosQA.Backend/IntentRouter.cs) の純粋関数。LLM・DB・時刻に依存しないため、
`IntentRouterTests`で固定している。決定論ルートは高精度パターンのみで、外れたら通常RAGへフォールバックする。

## 2. FAQ

- テーブル: `faq`（`question_norm` UNIQUE、`answer`、`sources_json`、`status`、`hits`）、`faq_variants`（別名）。
- 正規化: [QuestionNorm.cs](../app/ShineosQA.Backend/QuestionNorm.cs)。NFKC・小文字・空白/句読点/括弧/長音の除去、
  カタカナ→ひらがな、末尾の「は/が/を」除去。
- 照合: 完全一致（別名含む）。LLM・埋め込みなし。`hits`を加算。
- API:
  - `GET /api/faq?status=published`
  - `POST /api/faq` — `{ question, answer, sources?, variants?, status?, tags? }`
  - `DELETE /api/faq/{id}`
- 制約:
  - 自動生成・承認フローは未実装（Phase Fで「回答キャッシュのヒット≥3＋👎なし」を候補化）。
  - 出典ファイル更新時の陳腐化検出（`source_hashes`）は列だけ用意し、判定は未実装。
  - FAQはプロンプト版変更では消さない（人間承認済みのため）。出典更新で管理する設計。

## 3. 図面台帳

- `drawing_meta`の値をそのまま回答に使う。LLMを介さないため、図番・材質・改訂の取り違えが起きない。
- 複数改訂が登録されている場合は**改訂ごとに分けて**返す（値を混ぜない）。質問に「改訂B」等があれば絞り込む。
- 回答例:
  - `図番 ST-1042A（改訂 B）の材質は SUS304 です。`
  - 複数: `図番 ST-2000 は複数の改訂が登録されています。- 改訂 A: 材質 SUS304 ...`
- 出典: `SourceInfo`に`is_drawing=true`・図番・品名・改訂を設定。既存の図面出典UI・プレビューがそのまま使える。

## 4. 実績

- `jisseki_meta`の見積・原価・納期を図番で引く。質問の指標語（見積/原価/納期）だけを返す。
- 複数行は行番号付きで列挙。金額・納期の要約はしない（取り違え防止）。
- 列型・集計（数量/合計/平均/期間）はPhase Dで追加する。

## 5. 変更ファイル

- 追加: `IntentRouter.cs`, `QuestionNorm.cs`, `FaqStore.cs`, `DrawingQuery.cs`, `JissekiQuery.cs`
- 変更: `ChatFlow.cs`（即答分岐・SSE `route`/`confidence`/`instant`）、`Db.cs`（schema v3）、`Program.cs`（`/api/faq`）
- 追加テスト: `IntentRouterTests.cs`, `InstantAnswerTests.cs`（FAQ・図面・実績）
- UI: `chat.ts`（即答時のサーバ実測msとルート表示）、`api.ts`（done型）

## 6. 既知の制約・次にやること

- 図面台帳の回答は**表題欄に存在する値のみ**。寸法・公差・注記は従来のRAG（`rag-drawing`）に任せる。
- 実績は3列（見積/原価/納期）のみ。列マッピング・集計はPhase D。
- FAQの候補生成・承認UI・パック入出力はPhase B後半〜Phase F。
- `ChatGate`は現状、即答ルートでも取得している。長い生成中のFAQ即答を詰まらせないため、
  Phase B後半で即答をゲート外へ出す（DB書き込みは直列化）。
- E2Eシナリオ（実図面の取り込み→図番質問）は未追加。`scenarios-drawing.json`に決定論ルートの問を追加する。
