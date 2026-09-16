# R2への大容量インストーラ配置手順（Worker + R2バインディング方式）

- **最終更新**: 2026-09-16
- **実績**: v2.0.5（2.08GiB・約5分で配置・検証済み）
- **対象**: Microsoft Store EXE提出用パッケージURLなど、**単一ファイルとして2GB超のexeをCloudflare R2で配信する**作業
- **関連**: [store-submission.md](store-submission.md)（Store申請の全体）／Worker本体: [tools/r2-worker/worker.js](../tools/r2-worker/worker.js)

---

## 1. なぜWorker + R2バインディングなのか

R2へのアップロード経路はすべて**300MB前後の上限**があり、2GB超のインストーラ（モデル同梱で約2.2GB）は通常経路で置けない:

| 経路 | 上限 | 備考 |
|---|---|---|
| ダッシュボードのUpload UI | 300MB/ファイル | 「S3互換APIまたはWorkersを使用してください」のエラーが出る |
| `wrangler r2 object put` | 300MiB | マルチパート非対応（単一PUT） |
| REST API `PUT /r2/buckets/{b}/objects/{key}` | 300MB | [公式ドキュメント](https://developers.cloudflare.com/api/resources/r2/subresources/buckets/subresources/objects/methods/upload/)記載の上限 |
| S3互換API（マルチパート） | 制約なし（パート5GiB×1万） | **Access Keyが必須**。発行はダッシュボードのみ（R2 → Manage API Tokens）。APIトークンからは発行できない |
| **Worker + R2バインディング（本手順）** | 制約なし | APIトークンのみで完結。[R2 Workers API](https://developers.cloudflare.com/r2/api/workers/)の `createMultipartUpload` / `uploadPart` を使用 |

S3キーを発行できるなら `aws s3 cp`（自動マルチパート）が最も簡単（§8）。**APIトークンしか使えない状況での代替が本手順**。

## 2. アーキテクチャ

WorkerのHTTP受信（workers.devルーティング）は**APIトークンでは有効化できない**（`PUT /workers/scripts/{name}/subdomain` がエラー10405 "Method not allowed for this authentication scheme" を返す。ダッシュボードからの操作は可）。そこで**Cronトリガーで起動**し、HTTP受信を不要にする:

```
毎分のCron ──▶ Worker起動（scheduledハンドラ）
                 │  1回の起動で1パート（1GiB）を処理
                 ▼
        GitHub Releaseの分割アセット（.part01/02/03・公開URL）
                 │  fetch() で取得（リダイレクト追従・Content-Length検証）
                 ▼
        R2マルチパート uploadPart()（ストリーム直渡し・メモリ非バッファ）
                 ▼
        進行状況をR2内の .upload-state.json に記録（再開可能・重複実行ガード付き）
                 ▼
        全パート完了 → complete() → サイズ検証 → status:"done"
```

データは **GitHub → Cloudflareエッジ → R2** を流れるだけで、手元のPCは制御・監視のAPI呼び出ししか行わない（2.2GBを自宅回線に通さない）。

分割アセットはCI（[release.yml](../.github/workflows/release.yml)）が生成する「GitHub Releaseの1アセット2GiB上限回避用の分割ファイル」をそのまま使う（分割→結合で元のexeとバイト一致）。

## 3. 前提条件

| 項目 | 値 |
|---|---|
| CloudflareアカウントID | `d5cc2f6694915127d0f0e75614a72dc6`（ShineosQA） |
| APIトークンに必要な権限 | **R2:Edit** ＋ **Workers Scripts:Edit**（スクリプトアップロード・Cron） |
| バケット | `shineos-downloads`（APAC・r2.dev公開済み） |
| 公開URLのベース | `https://pub-cbe981f96fcc423d8c28124ab0fccba5.r2.dev/` |

## 4. 手順

環境変数の設定（毎回）:

```bash
export CF_API_TOKEN='<APIトークン>'
export ACCT='d5cc2f6694915127d0f0e75614a72dc6'
```

### 4.1 バケット作成とr2.dev公開（初回のみ・実施済み）

```bash
curl -X POST "https://api.cloudflare.com/client/v4/accounts/$ACCT/r2/buckets" \
  -H "Authorization: Bearer $CF_API_TOKEN" -H "Content-Type: application/json" \
  --data '{"name":"shineos-downloads","locationHint":"apac"}'

# r2.dev公開URLの有効化（応答の domain が公開URLのベースになる）
curl -X PUT "https://api.cloudflare.com/client/v4/accounts/$ACCT/r2/buckets/shineos-downloads/domains/managed" \
  -H "Authorization: Bearer $CF_API_TOKEN" -H "Content-Type: application/json" \
  --data '{"enabled":true}'
```

### 4.2 Workerの定数をリリースごとに編集

[tools/r2-worker/worker.js](../tools/r2-worker/worker.js) 冒頭の3定数を更新:

| 定数 | 内容 | 確認方法 |
|---|---|---|
| `GH_PARTS` | GitHub Release分割アセットのURLとバイト数の配列 | `gh release view <タグ> --json assets --jq '.assets[] | "\(.name) \(.size)"'` |
| `KEY` | R2に置くオブジェクト名（例: `ShineosQA-Setup-2.0.6.exe`） | — |
| `EXPECT_TOTAL` | 完成ファイルの総バイト数 | 分割アセットのsize合計（リリースノートのSHA256と対応するexeのサイズ） |

### 4.3 デプロイ（レガシーREST PUT・wranglerは使わない）

```bash
cd tools/r2-worker
SECRET=$(node -e "console.log(require('crypto').randomBytes(24).toString('hex'))")
echo "$SECRET" > .upload-secret   # クリーンアップまで保存（Cron競合ガードにも使用）

METADATA=$(node -e "console.log(JSON.stringify({
  main_module:'worker.js', compatibility_date:'2026-09-01',
  bindings:[
    {type:'r2_bucket', name:'BUCKET', bucket_name:'shineos-downloads'},
    {type:'plain_text', name:'UPLOAD_SECRET', text:process.argv[1]}
  ]}))" "$SECRET")

curl -X PUT "https://api.cloudflare.com/client/v4/accounts/$ACCT/workers/scripts/r2-uploader" \
  -H "Authorization: Bearer $CF_API_TOKEN" \
  -F "metadata=$METADATA;type=application/json" \
  -F "worker.js=@worker.js;type=application/javascript+module"
```

注意:
- **`wrangler deploy` はR2のみのトークンでは失敗する**（wranglerが使う `/workers/services` APIがトークン認証を拒否）。上記のレガリー `PUT /workers/scripts/{name}` は通る
- metadataの `workers_dev:true` は**無視される**（workers.devルーティングはAPIトークンから変更不可）ため、起動はCronで行う
- workers.devサブドメイン未作成のアカウントでは `PUT /accounts/$ACCT/workers/subdomain` に `{"subdomain":"<名前>","enabled":true}`（名前の指定が必須）で作成できる（実績: `shineosqa`）。Cron駆動ならURLは不要だが、トラブル時の `wrangler tail` 用にあると便利

### 4.4 Cronトリガー設定

```bash
curl -X PUT "https://api.cloudflare.com/client/v4/accounts/$ACCT/workers/scripts/r2-uploader/schedules" \
  -H "Authorization: Bearer $CF_API_TOKEN" -H "Content-Type: application/json" \
  --data '[{"cron":"* * * * *"}]'
```

毎分起動。1起動で1パート（1GiB）処理するので、3分割なら約3〜4分で完了（実績: 約5分）。重複起動はWorker内のガード（10分以内のheartbeatでスキップ）で抑止。

### 4.5 進捗監視

状態オブジェクトをREST GETで読む:

```bash
curl -s "https://api.cloudflare.com/client/v4/accounts/$ACCT/r2/buckets/shineos-downloads/objects/.upload-state.json" \
  -H "Authorization: Bearer $CF_API_TOKEN"
```

| フィールド | 意味 |
|---|---|
| `uploadId` / `parts[]` | 進行中のマルチパートと完了パート数（parts=3で完了対象） |
| `running` | 実行中のheartbeat時刻（0=待機中） |
| `status` | `done`（成功）／ `size-error`（サイズ不一致）／ 未設定（進行中） |
| `lastError` | 直近のエラー（GitHub fetch失敗等。次のCronで自動再試行） |

### 4.6 完了検証

```bash
URL="https://pub-cbe981f96fcc423d8c28124ab0fccba5.r2.dev/ShineosQA-Setup-2.0.5.exe"
curl -sI "$URL"        # HTTP 200 + Content-Length が EXPECT_TOTAL と一致すること
```

厳密にやる場合、先頭・末尾1MBをレンジ取得し、公式SHA256と一致検証済みのローカルバイナリの同範囲と比較する（v2.0.5の実績では両方バイト一致を確認）。

### 4.7 クリーンアップ（使い捨てのため必ず実施）

```bash
# Cron削除
curl -X PUT "https://api.cloudflare.com/client/v4/accounts/$ACCT/workers/scripts/r2-uploader/schedules" \
  -H "Authorization: Bearer $CF_API_TOKEN" -H "Content-Type: application/json" --data '[]'
# Worker削除
curl -X DELETE "https://api.cloudflare.com/client/v4/accounts/$ACCT/workers/scripts/r2-uploader" \
  -H "Authorization: Bearer $CF_API_TOKEN"
# 状態オブジェクト削除（exe本体は残す）
curl -X DELETE "https://api.cloudflare.com/client/v4/accounts/$ACCT/r2/buckets/shineos-downloads/objects/.upload-state.json" \
  -H "Authorization: Bearer $CF_API_TOKEN"
```

## 5. 制約・注意

- **r2.devは開発用レート制限あり**。Store審査での取得には十分だが、一般ユーザーの大量DLが見込めるなら `shineos.com` ゾーンを同じアカウントに追加し、カスタムドメイン（`PUT .../domains/custom`）に移行する
- **Store提出後のバイナリは変更不可**。配置するexeはリリース公表SHA256と一致するものを使い、URLはリリースごとに新しくする（ファイル名にバージョンを含む運用）
- Workerの**CPU時間・壁時計制限**に注意: 本構成はfetch→uploadPartのストリーム直渡しでJS処理が最小。1パート1GiB・1起動の設計は実績あり
- `uploadPart` 途中で起動が死んだ場合も次のCronで同じパート番号を再送信する（S3系の仕様でパートは上書きされる）。`complete` がInvalidPartで失敗した場合は状態を削除してやり直す
- **APIトークンをチャット等に貼った場合は必ずロールオーバーする**

## 6. 恒常化する場合（推奨）

毎回手動で行う運用は上記のとおり。リリースごとの自動化には**R2 S3 APIキー**（ダッシュボード: R2 → Manage API Tokens → Create API Token・Object Read & Write・バケット限定）をGitHub Secretsに登録し、[release.yml](../.github/workflows/release.yml) に1ステップ追加するのが確実:

```yaml
      - name: Publish installer to R2
        env:
          AWS_ACCESS_KEY_ID: ${{ secrets.R2_ACCESS_KEY_ID }}
          AWS_SECRET_ACCESS_KEY: ${{ secrets.R2_SECRET_ACCESS_KEY }}
        run: aws s3 cp "dist/ShineosQA-Setup-${{ steps.ver.outputs.version }}.exe" \
              "s3://shineos-downloads/ShineosQA-Setup-${{ steps.ver.outputs.version }}.exe" \
              --endpoint-url "https://${{ secrets.CF_ACCOUNT_ID }}.r2.cloudflarestorage.com" \
              --content-type application/octet-stream
```

`aws s3 cp` は2GB超を自動マルチパート分割するため、Workerは不要になる。CIでの生成直後に配置できる点も利点（GitHub Releaseの分割資産を経由しない）。

## 7. 実績（v2.0.5・2026-09-16）

| 項目 | 結果 |
|---|---|
| 対象 | `ShineosQA-Setup-2.0.5.exe`（2,234,458,796バイト） |
| ソース | GitHub Release `2.0.5.part01/02/03`（合計サイズがexeと完全一致） |
| 所要 | 約5分（Cron起動3〜4回） |
| 検証 | HTTP 200・Content-Length一致・先頭/末尾1MBが公式SHA256検証済みバイナリとバイト一致 |
| クリーンアップ | Cron・Worker・状態オブジェクト削除済み（バケットにはexeのみ） |
