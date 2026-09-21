# 実装・テスト計画: 図面PDF検索・Q&A 拡張パック

- **ブランチ**: `feature/drawing-search`
- **作成日**: 2026-09-20
- **位置づけ**: [plan-drawing-search.md](plan-drawing-search.md)（機能プラン・タスクA0〜A6/タスク1〜10）を実行可能なレベルに展開した実装仕様＋テスト仕様。コード参照（`file:line`）はすべて 2026-09-20 の `feature/drawing-search` 時点の実測
- **前提**: 拡張パック構造（設定ON/OFF・既定OFF・本体フロー不変）は plan §3.0 のとおり

---

## 0. プラン文書からの修正点（実装調査で判明）

plan-drawing-search.md を書いた時点の想定から、コード実態により**簡単になるものと難しくなるもの**があります。実装は本書の記載に従います。

| # | 修正 | 根拠 |
|---|---|---|
| 1 | **タスク「図番正規化」で再索引と回答キャッシュ無効化は不要になる**。チャンクのトークンはDBに保存されず、起動時に `ChunkIndex.LoadFrom` が `Rag.Tokenize` で計算する（`Rag.cs:160`・`chunks` テーブルにトークン列なし `Db.cs:28-31`）。よってトークナイザ拡張は**アプリ再起動のみで全データに適用**される。既存チャンクのテキストが不変のため `PromptVersion` 更新（`Rag.cs:16`）も不要（図面チャンクは新規追加のみで、既存の回答キャッシュはそのまま正しい） | Rag.cs / Db.cs 実測 |
| 2 | **WinRT OCRはバックエンドTFM変更が前提**。現行 `net10.0`（`ShineosQA.Backend.csproj:3`）では `Windows.Media.Ocr` を直接呼べず、`net10.0-windows10.0.19041` 以上への変更と、自己完結publish（CI: `-r win-x64 --self-contained true` `release.yml:71`）への影響検証が必要。WebViewラッパー（`ShineosQA.App` = .NET Framework 4.x・cscビルド・SDKプロジェクトなし）では呼べないため、OCRの実装場所はバックエンド一択。**T8スパイクのGO/NO-GOで分岐**（NOならStage 1はテキスト入力のみでリリース、OCRはPhase Cへ） | csproj / App ビルドスクリプト実測 |
| 3 | `LlmGateway` はストリーミング専用（`ChatStreamAsync` のみ `LlmGateway.cs:38`）。表題欄のLLM構造化のために **`ChatOnceAsync`（stream:false・max_tokens上限付き）を1つ追加**する。既存ストリーム経路は変更しない | LlmGateway.cs 実測 |
| 4 | CI（`release.yml`）は**テストを実行していない**（`dotnet test` のステップなし。`dev-v2.md:84` のbacklog）。本機能で単体テストを大量に増やすため、T10で「テストステップ追加」を推奨扱いで含める | release.yml 実測 |
| 5 | PDF抽出（`Ingest.ExtractPdf`）は**現状テストゼロ**。T1でフィクスチャテストを最初に作り、抽出器置換をテスト駆動で行う | テストプロジェクト実測 |

---

## 1. 共通設計

### 1.1 新規・変更ファイルの全体図

| ファイル | 新規/変更 | 役割 |
|---|---|---|
| `app/ShineosQA.Backend/Extensions.cs` | **新規** | 拡張パック定義と有効判定（§1.3） |
| `app/ShineosQA.Backend/PdfText.cs` | **新規** | PdfPigによるPDFテキスト抽出。プレーンテキスト版と、座標付きテキストrun版の2つを提供 |
| `app/ShineosQA.Backend/DrawingIngest.cs` | **新規** | 図面判定ヒューリスティック・表題欄抽出・図番正規表現・`drawing_meta` 書き込み |
| `app/ShineosQA.Backend/Ocr.cs` | **新規（T8でGOの場合）** | Windows.Media.Ocr ラッパー（ja） |
| `app/ShineosQA.Backend/Ingest.cs` | 変更 | `ExtractPdf` を PdfText へ委譲（フォールバック付き）。`IngestFileAsync` にパック分岐（元ファイル保存・図面パス） |
| `app/ShineosQA.Backend/Rag.cs` | 変更 | `NormalizeZuban` 追加・`Tokenize` へ記号結合英数トークン追加 |
| `app/ShineosQA.Backend/LlmGateway.cs` | 変更 | `ChatOnceAsync` 追加 |
| `app/ShineosQA.Backend/Db.cs` | 変更 | `files.kind` マイグレーション・`drawing_meta` テーブル・`files` 系ヘルパー |
| `app/ShineosQA.Backend/Program.cs` | 変更 | 設定API拡張・`/api/knowledge` 応答拡張・thumb/file/ocr エンドポイント |
| `app/ShineosQA.Web/src/api.ts` | 変更 | `Settings` 型・`KnowledgeFile` 型・`ocr()`/`thumbUrl()`/`fileUrl()` ヘルパー |
| `app/ShineosQA.Web/src/settings.ts` | 変更 | 「拡張機能」カード |
| `app/ShineosQA.Web/src/knowledge.ts` + `index.html` | 変更 | 一覧（サムネ/badge/図番/フィルタ） |
| `app/ShineosQA.Web/src/chat.ts` + `index.html` | 変更 | キャプチャボタン・確認チップ・drawing出典 |
| `app/ShineosQA.Backend.Tests/*.cs` | 新規4ファイル | §4 の単体テスト |
| `spikes/phase0/latency-cmp/scenarios-drawing.json` | 新規 | 図面E2E 10問 |
| `vendor/THIRD-PARTY-NOTICES.txt` | 変更 | PdfPig (Apache-2.0)・Docnet.Core/PDFium (MIT/BSD-3) 追記 |

### 1.2 パッケージ

- `UglyToad.PdfPig`（Apache-2.0・純マネージド・自己完結publishへの影響なし）: テキスト抽出＋座標取得
- `Docnet.Core`（MIT・PDFiumネイティブdllをwin-x64ランタイムで同梱）: サムネイル用レンダリング。**loose publish（`-o backend-pub`・単一ファイル化なし）なのでネイティブdllはexe横に展開される**。llama.cppのengine/配置と同じ形であり、MSIX/Innoのステージングへ `backend-pub` 全体コピーで乗る見込み（T3で実測確認）

### 1.3 拡張パック機構（T2の本体）

```csharp
// Extensions.cs
public sealed record PackDef(string Id, string Title, string Description, bool DefaultOn);
public static class Extensions
{
    public static readonly PackDef Drawing = new("drawing", "図面PDF検索・Q&A（製造業向け）",
        "図面PDFの取り込み時に表題欄（図番・品名・材質・改訂）を自動読み取りします。…", DefaultOn: false);
    public static bool IsEnabled(Db db, string id) =>
        db.GetSetting($"ext.{id}", "0") == "1";   // 既定OFF・リクエストごとに読む（即時反映）
}
```

- 設定保存: `settings` テーブルへ `ext.drawing = "0"/"1"`（`Db.GetSetting/SetSetting` は既存 `Db.cs:152-159`）
- `GET /api/settings`（`Program.cs:51-57`）: 応答へ `extensions: { drawing: bool }` を追加
- `POST /api/settings`（`Program.cs:59-84`）: `ext.drawing` キーを追加（既存3キーと同じ `if (p.Name == ...)` 追加分岐。bool値の検証は web_search と同様）
- **OFF時の保証**（ゲート2重化）:
  1. 取り込み分岐: `IngestFileAsync` 冒頭で `Extensions.IsEnabled` を判定し、OFFなら現行経路のみ（元ファイル保存なし・図面判定なし）
  2. エンドポイント: thumb/file/ocr はOFF時 503 `{error:"SHINE_E_EXTENSION_DISABLED"}`

### 1.4 DBスキーマ

```sql
-- Db.cs InitSchema 直後 + 既存マイグレーションパターン（Db.cs:49 の try/catch ALTER）で追加
CREATE TABLE IF NOT EXISTS drawing_meta(
  file_id INTEGER PRIMARY KEY REFERENCES files(file_id) ON DELETE CASCADE,
  zuban_raw TEXT, zuban_norm TEXT, hinmei TEXT, zairyo TEXT,
  scale TEXT, revision TEXT, approved_at TEXT);
-- files へ kind 列（既定 'doc'。図面は 'drawing'）
ALTER TABLE files ADD COLUMN kind TEXT NOT NULL DEFAULT 'doc';  -- try/catch 1回限り
```

- 元ファイルのパスはDBに持たず規約のみ: `data/files/{file_id}{拡張子}`・サムネ `data/files/{file_id}.thumb.png`。`DELETE /api/knowledge/{id}`（`Program.cs:221-226`）で当該ファイルを削除

---

## 2. タスク別実装仕様

### T1: PDFテキスト抽出の置き換え（本体品質改善・パック適用外・単独リリース可）

**手順（テスト駆動）**:
1. フィクスチャPDF3種を `app/ShineosQA.Backend.Tests/TestData/` に作成（§4.1）。**自作合成のみ**（外部図面の無断同梱を避ける）
2. `PdfText.cs`:

```csharp
public sealed record PdfTextRun(string Text, int Page, float X, float Y, float W, float H);
public static class PdfText
{
    public static string ExtractText(Stream s);                    // PdfPig。全ページの行順テキスト
    public static List<PdfTextRun> ExtractRuns(Stream s);          // 座標付き（T4で使用。原点左下・pt単位）
}
```

3. `Ingest.ExtractText`（`Ingest.cs:23-33`）の `.pdf` 分岐を `PdfText.ExtractText` へ。**PdfPigが例外または空テキストを返した場合のみ、既存の自製抽出器（`ExtractPdf`）をフォールバックとして実行**。どちらも空なら現行どおり `InvalidDataException("pdf: no extractable text ...")`（エラーコード・UI表示が不変）
4. 受け入れ: フィクスチャ (a) CID日本語PDF で文字化けなく抽出（現行は失敗が想定されるケース）。(b) 既存docx/md/txt経路は無変更

**ファイル**: `PdfText.cs`(新規)・`Ingest.cs`・`Tests/PdfTextTests.cs`(新規)

### T2: 拡張パック機構

§1.3 のとおり。UI側:

- `api.ts`: `Settings` 型（`api.ts:12`）へ `extensions: { drawing: boolean }` を追加
- `settings.ts`: テンプレート（`settings.ts:24-57`）の「検索」カードの後に「拡張機能」カードを追加。`settings-card` + `setting-row` + `switch` の既存クラスを使用（Web検索トグル `settings.ts:26-33` と同形）。リスナは `#set-bg` パターン（`settings.ts:66-68`）:
  `change` → `api.saveSettings({ extensions: { drawing: checked } })` → POST側で `ext.drawing` に平坦化して保存
- POST契约: UIからは `extensions.drawing` で送る（API応答と対称）。バックエンドは `ext.drawing` キーも直接受け付ける
- カード内表示: 取り込み済み図面件数（`SELECT COUNT(*) FROM files WHERE kind='drawing'`）＋「無効化しても取り込んだ図面データは残ります」注記

**受け入れ**: OFF状態で全タブのUI・DOMが現行と同一（設定カードの追加以外）。ON/OFF切替が再起動なしで反映される。

### T3: 元ファイル保存＋サムネイル（パックON時のみ）

- `Ingest.IngestFileAsync`（`Ingest.cs:158`）: パックON時、`files` 行INSERT後にストリームを `data/files/{file_id}{ext}` へコピー（ストリームは再読取可能なMemoryStreamへ退避してから—既存実装は ExtractText でストリーム消費後に破棄のため、`ms.ToArray()` を保持）
- サムネ生成（PDFのみ）: Docnet.Core で1ページ目を幅300pxでラスタライズ→PNG保存。生成失敗はサムネなしで続行（取り込みを止めない）
- エンドポイント（`Program.cs` へ追加）:
  - `GET /api/knowledge/{id}/thumb` → `image/png`（なければ404）。`Cache-Control: private, max-age=86400`
  - `GET /api/knowledge/{id}/file` → 元ファイル（Content-Type拡張子判定・`Content-Disposition: inline`）
- 削除: `DELETE`（`Program.cs:221`）で `data/files/{id}.*` を削除

### T4: 図面判定＋表題欄抽出＋1枚1チャンク（`DrawingIngest.cs`）

**判定ヒューリスティック**（保守的＝取りこぼしより誤検出を嫌う）:

```
kind = drawing ⟺ パックON ∧ 拡張子.pdf ∧ ページ数 ≤ 3
       ∧ 抽出テキスト量 ≤ 1200字 ∧ 図番パターンが少なくとも1件
```

**図番パターン（正規表現・第1版）**:

```
[A-Z]{1,4}[-－_/]?[0-9]{2,6}(?:[-－_/][0-9]{1,4}[A-Z]?)?
```

パラメータ（閾値・パターン）は `DrawingIngest` 内の定数に集約し、Pilot先の様式で調整できるようにする。

**表題欄抽出**: `PdfText.ExtractRuns` でページ右下領域（`x > 0.55×ページ幅 ∧ y < 0.40×ページ高さ`・原点左下）のrunを行単位（y座標でグループ化）にまとめ、ラベル近接マッチで抽出:

| フィールド | 手がかりラベル | 取り方 |
|---|---|---|
| 図番 zuban | `図番`/`No.`/`DWG` | ラベルと同じ行または直下の行で図番パターンに一致する最初のrun |
| 品名 hinmei | `品名`/`名称`/`TITLE` | ラベル行の残りテキスト |
| 材質 zairyo | `材質`/`材料`/`MATERIAL` | 同上 |
| 改訂 revision | `改訂`/`REV` | 近接する `[A-Z]` 1文字 |
| 縮尺 scale | `SCALE`/`縮尺` | 近接テキスト |

失敗時は各フィールド null でよく、図番だけはLLM構造化（T6）に渡す。

**取り込み（図面パス）**: `Rag.Chunk` を使わず**1図面1チャンク**:

```
チャンクテキスト = "【図面】図番: {zuban_raw} / 品名: {hinmei} / 材質: {zairyo} / 改訂: {revision}\n" + 全抽出テキスト
```

- 埋め込みは1回・`files.kind='drawing'`・`drawing_meta` にINSERT。テキスト層ゼロPDF（両抽出器とも空）はエラーにせず `kind='drawing'`・チャンク0件・メタデータなしで登録（UIに「テキスト抽出なし」表示）
- 既存の `ChunkIndex.AddRange`・検索・出典経路はそのまま使う（出典の `doc` 名 = files.name。T7で図面風の表示に拡張）

### T5: 図番正規化（`Rag.cs`）

```csharp
public static string NormalizeZuban(string s)
    // NFKC正規化 → [^0-9a-z] 除去（正規化後小文字化してから） → 長さ下限2で採用
```

**Tokenize 拡張**（`Rag.cs:47-66`）: `TokenRegex` に第3の選択肢を追加:

```
既存:  [\u3040-\u30FF\u4E00-\u9FFF]+ | [A-Za-z0-9]+
追加:  [A-Za-z0-9](?:[A-Za-z0-9]|[-_－＿/／.．]){1,30}[A-Za-z0-9]   … 記号を含む英数連結
```

- マッチが記号を含む場合のみ、**既存トークン（a, 1234…）に加えて `NormalizeZuban(マッチ)` を追加トークンとして併記**
- 索引側（`ChunkIndex.LoadFrom`/`AddRange`）とクエリ側（`ChatFlow` の `Rag.Tokenize(質問)`）が同一関数を通るため、**両側へ自動適用**（修正点0のとおり再索引・キャッシュ無効化とも不要）
- 既存テストへの影響: `Tokenize` にトークンが**増える**だけなので既存アサーション（`Assert.Contains`）は崩れない。`ChunkIndex.Search` のキーワード一致率は分母がクエリトークン数のため、長い英文質問でわずかに変動しうる→既存118問E2E（§4.3）で劣化なしを確認

### T6: LLM構造化（表題欄・ルールベースのフォールバック）

- `LlmGateway.ChatOnceAsync(int port, IReadOnlyList<(string,string)> messages, double temperature, int maxTokens, CancellationToken)` を追加（llama-server `/v1/chat/completions`・`stream:false`・タイムアウト付き）。既存 `ChatStreamAsync` は無変更
- 用途: 表題欄領域テキスト → `{"zuban":"…","hinmei":"…","zairyo":"…","revision":"…"}` のみを返す指示（クイック1.7B・max_tokens 200・temperature 0）。JSONパース失敗・タイムアウト（10秒）は**ルールベース結果をそのまま採用**（取り込みを失敗させない）
- 実行契機: T4のルール抽出で**図番が取れなかった図面のみ**（無駄打ち回避）。設定 `ext.drawing.llm`（既定 "1"・v1ではUI非公開の内部キー）
- スコア: 取り込み時間への影響は図面1枚あたり+数秒（1.7B・200トークン）。フォルダ一括取り込みでは直列化済み（`_ingestLock`）なので追い立てられない

### T7: UI

**ナレッジ一覧**（`knowledge.ts:37-57`・`index.html:73`）:

- ヘッダへ「種別/図番」列を追加。行テンプレ（`knowledge.ts:44`）: 1列目にサムネ `<img class="file-thumb">`（`api.thumbUrl(id)`・なしは文書アイコン）、`kind==='drawing'` なら badge（`.badge-inuse` 相当のpill流用・新クラス `.badge-drawing`）＋図番・品名
- フィルタボックス（`#knowledge-filter`）を `index.html` のテーブル上に追加: 図番正規形部分一致（`LIKE`）＋品名・ファイル名部分一致。バックエンド `GET /api/knowledge`（`Program.cs:153`）へ `?q=` を追加（`drawing_meta` LEFT JOIN の応答拡張: `kind, zuban, hinmei, zairyo, revision`）
- 空行 `colspan`（`knowledge.ts:55`）を列数に合わせ更新

**チャット出典**（`chat.ts:92-106` `sourceElement`）:

- `SourceInfo`（`api.ts:3`）へ `kind?: 'drawing'`・`file_id?: number`・`meta?: {zuban,hinmei,revision}` を追加。`ChatFlow` が sources 生成時に files.kind/drawing_meta を付与
- drawing分岐: `【図】{zuban}（{hinmei}・改訂{revision}）` を太字＋「開く」ボタン → `window.open(api.fileUrl(file_id))`（WebView2の内蔵PDFビューアで表示）
- `openSourceModal`（`chat.ts:68-90`）はテキスト用のまま流用

**ユーザー発言への画像表示**（`appendMessage` `chat.ts:244-268`）: キャプチャ画像（後述）を `.msg-card` 内に `<img class="msg-image">` で表示（blob URL）

### T8: WinRT OCRスパイク（GO/NO-GO・`spikes/phase0/ocr-spike/`）

検証手順と判定基準をスパイクディレクトリのメモに残す:

1. バックエンドcsprojのTFMを一時的に `net10.0-windows10.0.19041` へ変更 → `dotnet publish -c Release -r win-x64 --self-contained true` が通るか
2. `Windows.Media.Ocr`（`OcrEngine.TryCreateFromUserProfileLanguages()` + ja エンジン）で日本語スクリーンショットのOCRが動くか
3. **ja言語パック不在環境**で `TryCreateFromUserProfileLanguages` が null を返すことの確認（機能無効化の分岐根拠）
4. 自己完結publishサイズの増分（CsWinRT投影の追加分）を記録
5. MSIX（`runFullTrust` Win32・Store再署名）実行環境でWinRT APIが利用可能か

**GO基準**: 1∧2∧3∧5 がすべて成立。NOの場合: T9は実装せず（キャプチャUIなし）、OCRはPhase Cへ。**TFM変更の恒久化はGO時に本体へ反映**（リスク: `InvariantGlobalization=false`・`SatelliteResourceLanguages=ja` の相互作用を実測で確認）

### T9: キャプチャ・写真入力（T8 GOの場合）

- `index.html` `.composer-bar`（`index.html:39-58`）の `#attach-btn`（46-49）の横へ `#capture-btn`（`tool-btn` 流用・ラベル「キャプチャ」）。**初期状態 `display:none`** で、`ChatView` 初期化時の設定プリロード（`chat.ts:153-156`）を拡張して `extensions.drawing` が真のときのみ表示
- 入力経路: ① `#chat-input` の `paste` イベント（`clipboardData.items` の image）② `#capture-btn` → クリップボード画像の貼り付け案内（v1は `Win+Shift+S` → ペーストで成立。矩形キャプチャUIの自作はv2）
- API: `POST /api/ocr`（FormData `image`）→ `{ text: string, zubans: [{ raw, norm }] }`。パックOFF時・jaエンジンなし時 503
- 確認チップ: `.composer` 内（textareaと `.composer-bar` の間）へ `「{zuban.raw}」で検索 [修正]` チップ。修正はチップのテキストを編集→確定。送信時、チップがあれば質問文へ `（図番: {zuban}）` を前置して `/api/chat` へ（**`/api/chat` の契約変更なし**・画像自体はチャット履歴表示のみでRAGには使わない）
- `Ocr.cs`: 画像デコード（PNG/JPEG）→ `Windows.Media.Ocr` → 図番パターン抽出（T4と同じ正規表現・`NormalizeZuban` を共用）

### T10: ドキュメント・CI・Store

- README「できること」へ図面検索行、`docs/store-submission.md` §7 掲載文・§7.3 キーワードへ「図面検索」を追加、「製造業向け拡張は設定から有効化」と明記
- `vendor/THIRD-PARTY-NOTICES.txt` へ PdfPig・Docnet.Core/PDFium 追記
- **推奨**: `release.yml` のpublish手前に `dotnet test app/ShineosQA.Backend.Tests` ステップを追加（現状CIはテスト未実行・`dev-v2.md:84` backlog。失敗でジョブ停止）
- CHANGELOG へ次期バージョン項目として記載

---

## 3. 実装順序とマイルストーン

| マイルストーン | 内容 | リリース単位 | ゲート（§4.4） |
|---|---|---|---|
| **M1** | T1（PdfPig置換）＋T2（パック機構） | 本体品質改善＋空の拡張カード。**単独でリリース可能** | G1 |
| **M2** | T3〜T7（元ファイル・表題欄・図番正規化・UI） | 拡張パック完成（テキスト入力） | G1＋G2＋G3 |
| **M3** | T8〜T9（OCRスパイク→キャプチャ入力） | 第2入力経路 | G1〜G4 |

並行性: T8はM1の時点から着手してよい（独立）。T9だけがT8の結果待ち。

---

## 4. テスト計画

既存基盤の踏襲: xUnit 2.9.2（56 [Fact]・`Method_Condition_Result` 命名・temp dir + `IDisposable` 分離・スタブは手書き）。実行は `tools/dotnet-sdk`（`DOTNET_ROOT` 設定後 `dotnet test app/ShineosQA.Backend.Tests`・`dev-v2.md:23-24,68`）。E2Eは `spikes/phase0/latency-cmp/scenario-test.ps1`（`/api/chat` SSE・キーワード一致採点・実行前に `/api/cache-clear`）の手法をそのまま使う。

### 4.1 テストデータ（フィクスチャ）

`app/ShineosQA.Backend.Tests/TestData/` に**自作合成**で3種を配置（生成スクリプトも同ディレクトリへ残す）:

| ファイル | 内容 | 使い道 |
|---|---|---|
| `cid-japanese.pdf` | CIDフォント（OpenType/Type0）で日本語文章を含む | T1: 現行抽出器の想定弱點。PdfPigで正しく抽出できること |
| `drawing-sample.pdf` | A4・1ページ・右下に表題欄（図番 `ST-1042A`・品名「サポートブラケット」・材質「SS400」・改訂「B」）を模した自作図面風PDF | T1/T4: 表題欄抽出・図面判定 |
| `no-text.pdf` | 画像のみ（テキスト層なし） | T1: 両抽出器が空→登録継続（チャンク0）の経路 |

### 4.2 単体テスト（新規4ファイル・想定35件前後）

**`PdfTextTests.cs`**（T1）
- `ExtractText_CidJapanesePdf_ExtractsKanji` — cid-japanese.pdf から特定の日本語文字列が抽出できる
- `ExtractText_PlainEnglishPdf_ExtractsWords`
- `ExtractText_NoTextPdf_ReturnsEmpty`
- `ExtractRuns_DrawingSample_HasRunsInTitleBlockRegion` — 右下領域にrunsが存在
- `Ingest_ExtractText_PdfFallsBackToLegacyExtractor_WhenPdfPigFails`（PdfPigが例外のPDFを無理やり作るのが困難なため、内部メソッドのフォールバック分岐を直接検証する形式に置き換えてもよい）

**`ZubanTests.cs`**（T5・表駆動）
- `Normalize_Variants_ProduceSameKey`: `("ST-1042A")("st1042a")("ＳＴ－１０４２Ａ")("ST 1042 A")("ST_1042_A")` → すべて `st1042a`
- `Normalize_RejectsTooShort` — 1文字以下は空を返す（トークン汚染防止）
- `Tokenize_ZubanWithSeparators_IncludesNormalizedToken` — `Tokenize("ST-1042A")` が `"st1042a"` を含む
- `Tokenize_PlainAlnum_UnchangedBehavior` — 既存トークンも併存（`"st"`,`"1042a"`…の回帰確認）
- `Tokenize_JapaneseSentence_NotAffected` — 日本語文で余分なトークンが増えない（記号結合英数が存在しない場合）
- `ChunkIndex_Search_ZubanVariant_FindsDoc` — `A-1234` で索引したチャンクが `A1234` クエリで hit（既存 `ChunkIndexTests` パターン・`DbTests.cs:77-145` 踏襲）

**`DrawingIngestTests.cs`**（T4）
- `Detect_DrawingSamplePdf_ClassifiedAsDrawing`（閾値パラメータを渡して判定）
- `Detect_LongTextPdf_NotDrawing` — 1200字超の通常PDFは除外
- `ExtractTitleBlock_DrawingSample_ParsesZubanHinmeiZairyo` — `ST-1042A`/サポートブラケット/SS400
- `ExtractTitleBlock_NoLabels_ReturnsNullFields`
- `IngestFileAsync_Drawing_CreatesSingleChunkAndMeta` — temp Db で files.kind/drawing_meta/チャンク1件を検証
- `IngestFileAsync_PackOff_NoFileSavedAndKindDoc` — OFF時 `data/files/` に何も書かれない

**`ExtensionsTests.cs`**（T2）
- `IsEnabled_DefaultFalse`
- `SetSetting_Toggle_RoundTrip`
- `SettingsApi_Keys` は結合手順書レベル（HTTPホストを立てない本プロジェクトの単体方針では、GET/POSTの組立ロジックを純関数化して検証）

### 4.3 E2E（ライブアプリ・既存手法の拡張）

**`scenarios-drawing.json`（10問・`scenario-test.ps1` 形式・タグ d01〜d10）**。取り込み: `golden-corpus/` に `drawing-sample.pdf` ＋検査基準書風ダミー文書1件を追加し `/api/knowledge/import` で投入:

| タグ | 質問 | 期待 |
|---|---|---|
| d01 | 図番完全一致「ST-1042Aの材質は？」 | SS400 |
| d02 | 表記ゆれ「st1042a 改訂は？」（ハイフンなし小文字） | B（T5の実効確認） |
| d03 | 全角「ＳＴ－１０４２Ａの品名は？」 | サポートブラケット |
| d04 | 品名検索「サポートブラケットの図番は？」 | ST-1042A |
| d05 | 周辺文書横断「ST-1042Aの検査基準は？」 | 検査基準書の記載キーワード |
| d06 | 図面の注記についての質問 | 注記内容 |
| d07 | 存在しない図番「ZZ-9999の材質は？」 | refuse（該当なし） |
| d08 | 一般知識「富士山の高さは？」 | refuse（既存挙動の回帰） |
| d09/d10 | 通常文書2問（golden-corpusから抽出） | 既存どおり（混在時の回帰） |

**パックOFF parity（最重要ゲート）**: 拡張OFFの状態で既存 `scenarios-100.json`（118問）を実行し、現行の成功率（108/118）から**劣化していないこと**。T5のトークナイザ変更はOFFでも全ファイルへ影響するため、このゲートはM2必須

**UIスモーク拡張**（`spikes/phase0/ui-smoke/ui-smoke.ps1` に追加、または手順書化）:
1. パックOFF: 設定カード以外のDOM変化なし（`#capture-btn` 非表示・ナレッジ一覧の列変更なし）
2. パックON→図面PDFドロップ→サムネ・badge・図番表示
3. ON→OFF→データ残存・UI非表示→再ON→復帰
4. （M3）キャプチャ貼り付け→チップ表示→修正→送信→出典に【図】表示

### 4.4 リリースゲート（各マイルストーン）

| ゲート | 内容 | 適用 |
|---|---|---|
| **G1** | `dotnet test` 全緑（既存56件＋追加）／docx・md・txt取り込みの回帰確認 | M1〜M3 |
| **G2** | 図面E2E 10問（d01〜d06 かつ d08 は必須パス）／パックOFF 118問 parity | M2〜M3 |
| **G3** | 実機（開発機 Ryzen 7 5700U 相当）でlite/fullインストーラ動作・`data/files/` 掃除確認（アンインストール時の削除確認フロー） | M2〜M3 |
| **G4** | （Store提出時）WACKローカル実行・`THIRD-PARTY-NOTICES` 更新・掲載文反映 | M3以降の提出前 |

---

## 5. 残リスクと後回し項目

| 項目 | 対応 |
|---|---|
| Docnet.Core のネイティブdllが自己完結publish/MSIXステージングで正しく配置されるか | T3の最初に実測（`backend-pub` の中身確認）。失敗時はpdfium.dllをllama.cppと同様に明示配置 |
| TFM変更（net10.0-windows）がpublish・MSIX・`SatelliteResourceLanguages` に与える影響 | T8スパイクで実測。恒久化はGO判定後 |
| 表題欄様式の多様性 | フィクスチャ1様式で開始。Pilot先様式を取得し次第 `DrawingIngest` の定数調整＋フィクスチャ追加 |
| 図面判定の誤検出（図番風文字列を含む通常文書） | 保守的ヒューリスティック（図番パターン必須・テキスト量上限）。誤検出時の影響は1チャンク化のみで検索不能にはならない。手動再分類UIはv2 |
| キーワード一致率の分母変化（T5）による既存検索順位への影響 | G2の118問 parity で検出 |
| OCR（T8）NOの場合 | T9はスキップし Phase C へ。プラン文書の見直しはその時に行う |

---

## 6. 実装結果（2026-09-20・T1〜T10完了・コミット d9f54aa〜6ae1d78）

### 実装とプランの差分

| 項目 | 差分 | 理由 |
|---|---|---|
| §4.2 IngestFileAsync のパック分岐単体テスト | 実施せず（コンポーネント単位の24件＋E2Eで代替） | Ingest の ctor が Supervisor（llama-server プロセス管理）を要求し、単体構築が困難なため |
| T3 サムネイル | `w=GetPageWidth()`・`h=raw.Length/(4*w)` の検証付き導出 | Docnet 2.6 の `GetImage()` が引数なしで寸法を返さないため |
| T1 PdfPig | ページ寸法を単語座標の最大値から導出 | PdfPig 0.1.16 の `PageSize` 型メンバーがバージョン差異を持つため |
| T9 確認チップ | 実装どおり（ペースト＋ファイル名表示） | v2.1.4マージ後の18問セットとの相互作用を都度検証した |
| パッケージID | `UglyToad.PdfPig` でなく **`PdfPig` 0.1.16** | NuGet のパッケージID実態 |

### 検証結果（最終・`PromptVersion fact-grounding-8` ビルド）

- 単体: **88/88** ／ 図面E2E: **10/10** ／ 18問: 16/18（不合格はmain既存の揺らぎs07/s12のみ）／ 118問: **104/118**（main 103/118・固有失敗ゼロ）
- 追加の品質修正（本ブランチで実施・詳細はCHANGELOG）: v2.1.4マージ、混同禁止規則の独立文復元（-2）、規則⑤（-3）、規則⑥＋言い換え許容（-8）、規則⑦は副作用のため撤回
- T8スパイク: **GO**（TFM `net10.0-windows10.0.19041`・自己完結publish・ja-JP OCR・publish済みexeの `/health` 応答を確認）

### 残ゲート（リリース前）

G3: 実機インストーラ（lite/full）／ G4: WACK・Store提出／ Pilot先の図面実態確認 → `DrawingIngest` の閾値・表題欄フィクスチャ拡充（§7）

---

## 7. 実図面検証と根本対応（2026-09-21・feature/drawing-realdata-fixes）

### 検証材料（実ダウンロード・`tmp/cad-validate/`）

JAVADA 技能検定試験問題公開サイト（令和6年度後期 D24 機械製図CAD作業 3級: 実技課題図・解答例）と
若年者ものづくり競技大会 機械製図(CAD)職種（競技課題概要・第20回課題図・金賞作品図面）。
ベクトル図面2枚・スキャン図面1枚・表紙付きPDF1冊の実物で全経路を検証した。

### 判明した課題と根本原因 → 対応

| 課題 | 根本原因 | 対応（本ブランチ） |
|---|---|---|
| スキャン図面（画像のみPDF）が取り込み不可 | テキスト抽出失敗で即エラー。OCR救済経路が無い | **`Ingest.OcrPdfFallbackAsync`**: 拡張パックON時・3頁以下なら WinRT描画→内蔵OCR（回転最良）でテキスト化。図面判定を通れば `kind=drawing`、通らなければ通常文書として救済。白紙（OCR≈0文字）と長文スキャンは誠実にエラー |
| 解答例PDFで 図番="1:1"・品名="Ø160"・材質="8 Ø126 Rc1/16" を誤抽出 | (a) 表題欄抽出が「ページ1」固定で表紙を読んだ (b) LLM出力を無検証で採用 | (a) **`PickSheetPage`**: 表題欄ラベル+図番パターンのスコアで図面シート頁を選択 (b) **検証器**（`IsPlausibleZuban/Hinmei/Zairyo/Scale/Revision`）をルール・LLM双方の出力に適用 (c) 表題欄の兆候（図番系ラベル/図番パターン）が無いとLLMを起こさないゲート |
| 90°回転の組立図キャプチャが文字化け | WinRT OCRは単一方向1回のみ | **`Ocr.RecognizeBestAsync`**: 0/90/180/270°でOCRし 文字量×平均語長² スコアで最良を選ぶ。単語矩形も返し表題欄抽出と共用 |
| 図面内情報（JIS B 0405等）の質問で濃密文書チャンクに埋もれる | 図面チャンクは寸法数値ノイズでキーワード一致が希薄。検索は kind を知らない | **図面意図ブースト**: 質問が 図面/図番/図番パターン を含むとき図面チャンクのハイブリッドスコアを×1.4（プール24→8）。`HasDrawingIntent` が false の通常質問では分岐ごと不活性 |
| （副原因）Docnet/PDFiumがスキャンPDFのCMYK-JPEGを黒つぶし描画 | レンダラ差。OCR入力・サムネイルが真っ黒に | OCR救済の描画を **Windows.Data.Pdf（WinRT）** に変更。サムネイルは `IsNearlyBlack` 検出でWinRT描画へフォールバック |
| （派生）スキャン図面の「尺度: 不明」前置きがベクトル図面の回答を妨げた | 「不明」の断言も誤りの一種 | **`BuildChunkText` は抽出できた欄のみ載せる**。尺度も前置きに追加（cr05対策） |

| （追加）240字スニペットが表題欄の記載（JIS B 0405-m）を切り落とし、チャンク内に存在する規格の併記に失敗（cr02） | 参照情報は 240字の関連文窓に切り詰められる設計。CAD PDFはテキストが読み順でなく記載が寸法ノイズ間に散らばるため、窓が表題欄の一部を欠いた（精密再現で has0405=False を確認。当初の「1.7B模型の参照限界」判断は誤り） | **図面チャンクはスニペット化せず全文注入**（ChatFlow ctxDocs）。1枚=1チャンクでテキスト量は取り込み時に上限（1,200字）があるためコスト有限 |

### 検証結果（スニペット免除後に最終更新）

- 単体: **124/124**（検証器・ページ選択・回転OCR・画像のみPDF救済を含む新規20件追加）
- 図面E2E 10問: **10/10**（回帰なし）
- 実図面5問（`scenarios-cadreal.json` 新設）: **5/5**（全文注入後にcr02が合格。0403-CT8・0405-m を併記）
- 18問: 15/18（s05/s07 は本開発環境コーパスの言い回しが期待語と不一致のテスト環境差、s12 はmain既存の揺らぎ。
  修正前後で同一結果。本ブランチの変更は 図面/図番 質問でのみ発動するため、これら3問に因果経路はない）

### 残課題

- 長文スキャン（4頁以上）は救済対象外。段書きOCR（ページバッチ）は需要確認後に検討
- 回転スキャンの取り込みは 4方向OCRのコスト（1頁あたり最大4回）が乗る。実測では許容範囲
- cr02の回答は規格を寸法値とペアにする冗長な列挙になることがある（1.7B模型の文体癖・事実は正しい）

### §7.1 実利用フィードバック対応（2026-09-21・第2弾）

| 要望/不具合 | 対応 |
|---|---|
| 過去チャットにキャプチャ画像が残らない（blob URLはセッション限定の仕様） | `messages.image` 列を新設し、貼り付け画像を `data/files/captures/` に保存。過去チャットでは `/api/messages/{id}/image` で復元表示（完全ローカル保存） |
| チャットのアーカイブがない | `chats.archived` 列 + `/api/chats/{uuid}/archive` + サイドバーの📦/↩操作と「アーカイブ済みを表示」切替 |
| 図面の出典表示が正しくない | **根本原因: 出典JSONの命名不一致**（バックエンドCamelCase `fileId` vs UI `file_id`）で図面出典分岐が発火していなかった。`JsonPropertyName("file_id"/"is_drawing")` で契約を固定しUIは旧データのcamelCaseにもフォールバック。図番が無い図面も【図面】+ファイル名+「開く」を表示 |
| CADファイルをそのまま検索したい | **DXF直接取り込み**（`DxfText` 新設: TEXT/MTEXT/ATTRIB+挿入点→表題欄抽出と共用）。DWGはクローズド形式のため対象外（DXF/PDFエクスポートを案内） |
