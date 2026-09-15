# テスト用ナレッジサンプル（実データではありません）

このフォルダは**動作確認・テスト専用のサンプル文書**です。実際の社内文書ではありません。

- `QA_list.md` … 一問一答形式のサンプル（経費精算・ITサポート・人事）
- `社内規程/` … 出張・旅費規程 / 情報セキュリティ規程 / 慶弔休暇規程のサンプル3点

## 使い方（品質テスト）

Golden QA 118問（`../latency-cmp/scenarios-100.json`）を実行する際の取り込み対象:

```
# アプリ稼働中に:
curl -X POST http://127.0.0.1:8300/api/knowledge/import -H "Content-Type: application/json" \
  -d '{"path": "<このリポジトリ>/spikes/phase0/latency-cmp/golden-corpus"}'
curl -X POST http://127.0.0.1:8300/api/knowledge -F "files=@<このリポジトリ>/spikes/phase0/testdocs/knowledge-sample/QA_list.md"
```

`../latency-cmp/golden-corpus/` はこのフォルダの規程3点を元にした検証用の再構成版
（QA_list込みの118問スイートの期待値と対応）。元ファイルとの差はMarkdown整形のみ。

## 履歴

v1インストーラが `{app}\knowledge` として同梱していたサンプル一式。
v2ではインストーラに含まれず、テスト専用としてこの場所に移動した。
