# プロモ動画 制作・更新手順

## 成果物

| ファイル | 内容 |
|---|---|
| `shineos-qa-usecase-1080p.mp4` | ユースケース動画（67.7秒 / 1920x1080 / 25fps / H.264 / 音声なし / 1.2倍速） |
| `thumbnail-1280x720.png` | YouTubeサムネイル（**動画の冒頭カードを1280x720に縮小したもの**） |

## サムネイルと冒頭カードの関係

サムネイルは**動画の1フレーム目（冒頭カード）から自動生成**されます。別々に管理しないため、
片方だけ更新されて食い違うことがありません。

```
cards/title.html ─(1920x1080で撮影)→ cards/title.png ─(MS Store公式バッジ+QRをoverlay)→ cards/title-final.png ─┬→ 動画の冒頭カード（01-title）
                                                                                              └─(1280x720へ縮小)→ thumbnail-1280x720.png
```

文言や配色を変えたい場合は `cards/title.html` を編集し、**1920x1080** のスクリーンショットを
撮って `cards/title.png` として保存 → `node build-promo.js` を実行してください
（バッジの合成と動画の冒頭カード・サムネイルの両方への反映は自動です）。

見出しの文字サイズは `title.html` 内のスクリプトが**幅に合わせて最大サイズへ自動調整**するため、
文言を変えても手動でのサイズ調整は不要です（左右に90pxずつ余白を確保した最大値になります）。

## QRコードの参照先（エンドカード）を差し替える

1. **`promo-config.json` の `siteUrl` を書き換える**（現在は Microsoft Store の Web ストアページ）
   ```json
   { "siteUrl": "https://apps.microsoft.com/store/detail/XP8C6NSGDR1N9F" }
   ```
2. **ビルドする**（QRコード生成 → エンドカード合成 → 動画全体の再生成まで自動）
   ```bash
   cd D:\dev\shineos-local-ai\output\promo
   node build-promo.js
   ```

QRコードは `siteUrl` から毎回自動生成されるので、画像を用意する必要はありません。
位置や大きさを変えたい場合は `build-promo.js` の `END_CARD` / `TITLE_CARD` を調整してください
（QRとバッジは下地画像に合成されるので、カード画像を撮り直す必要はありません）。

### エンドカードの仕様

- **QRコード**: 背景は透過（白モジュール）。暗い背景に直接置かれます。
  標準の暗色モジュール版ではなく**反転QR**になるため、読み取りは iPhone のカメラや
  Google レンズなど反転に対応したスキャナが必要です（多くの最新端末は対応）。
  標準の暗色モジュールにしたい場合は `buildEndCard()` の `color` を
  `{ dark: "#0d2b23", light: "#ffffff" }` に戻し、下地に白い角丸カードを復活させてください。
- **Microsoft Store公式バッジ**: `cards/msstore-badge-*.png`。公式SVG
  （`https://get.microsoft.com/images/ja dark.svg` / `ja light.svg`）を `rasterize-badges.js`
  で10倍解像度にラスタライズしたもの。エンドカード（暗い背景）には **light（白）版**、
  タイトルカード（明るい背景）には **dark（黒）版**を、Microsoftのガイドラインに従って使い分け。
  バッジを差し替えるときは SVG を `cards/` に保存して `node rasterize-badges.js` を実行。
- **QRコードは2種類**: エンドカード＝白モジュール透過（`qr.png`・反転QR）、
  タイトルカード＝濃色モジュール透過（`qr-dark.png`・標準QR）。どちらも同じ `siteUrl` から
  自動生成され、**両カードともバッジの横に並べられます**（`END_CARD` / `TITLE_CARD` の
  `pairCenterY` / `pairGap` / `qrSize` で配置調整）。
- **URLテキスト**: 旧来の緑の角丸カードは廃止。参照先はQRコードが担います。

### 確認だけしたいとき（動画全体を作り直さずエンドカードだけ再生成）

```bash
node build-promo.js --endcard-only
node verify-qr.js cards/end.png     # QRが読み取れるか検証
```

一時的に別URLで試す場合は環境変数で上書きできます。

```bash
PROMO_SITE_URL="https://example.com" node build-promo.js --endcard-only
```

## 構成（シーンと素材）

| ショット | 素材 | 内容 |
|---|---|---|
| 01 | `cards/title-final.png` | 冒頭カード（＝サムネイルの元画像。公式バッジ合成済み） |
| 02-06 | `recordings/sceneB-question.webm` | 質問→回答→出典→原文ハイライト |
| 07-08 | `recordings/sceneC-knowledge.webm` | ナレッジ登録（ドラッグ＆ドロップ） |
| 09-12 | `recordings/sceneD-ask-new.webm` | 追加資料への質問→出典 |
| 13-16 | `recordings/sceneEF-models-settings.webm` | モデル切替・設定画面 |
| 17 | `cards/end.png` | エンドカード（ロゴ＋QR＋Microsoft Storeバッジ） |

- テロップ文言は `build-promo.js` の `captions` オブジェクト（`caps/*.txt` に書き出されます）
- カット位置・速度・ズームは `build-promo.js` の `shots` 配列
- 入力フォームへのパンチイン（急なズームイン）は `crop()` と `BOX`（緑のハイライト枠）

## カード画像を作り直す場合

カード類はHTMLをブラウザで撮影して作っています。

1. `cards/title.html`（冒頭・サムネイル用）/ `cards/end.html`（エンドカード用）を編集
2. ローカルのバックエンド（`http://127.0.0.1:8300`）を起動し、`wwwroot/__promo/` に
   HTMLとロゴを置いて配信
3. ブラウザで開いてスクリーンショット（`title.html` は1920x1080、`end.html` は1920x1080）を撮り、
   `cards/title.png` / `cards/end-base.png` として保存
4. `node build-promo.js`（エンドカードは `end-base.png` にQRと公式バッジを合成して `end.png` を作り、
   `title.png` にQR（濃色版）と公式バッジを合成して `title-final.png` を作る）

> `end-base.png` はQRとバッジを**含まない**状態です。QR・バッジはビルド時に
> 合成されるので、URL変更のたびにカードを撮り直す必要はありません。
