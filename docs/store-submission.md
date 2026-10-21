# Microsoft Store 公開マスタードキュメント（社内知恵袋 / ShineosQA）

- **最終更新**: 2026-09-16
- **対象バージョン**: v2.0.5（MSIXバージョン表記 `2.0.5.0`）
- **この文書の位置づけ**: Microsoft Store公開に必要な情報・手順の**単一のまとめ**。Partner Center申請時はこのファイルだけでフォーム入力が完結するよう構成している。詳細仕様の一次情報はそれぞれ [error-codes-v2.md](error-codes-v2.md)（終了コード）・[CODE_SIGNING.md](../CODE_SIGNING.md)（署名）・[PRIVACY.md](../PRIVACY.md)（プライバシー）を参照。

## 目次

1. [ロードマップと全体チェックリスト](#1-ロードマップと全体チェックリスト)
2. [前提条件（申請前に必要なもの）](#2-前提条件申請前に必要なもの)
3. [提出形式の選択 — EXE vs MSIX](#3-提出形式の選択--exe-vs-msix)
4. [申請フォーム入力情報（共通）](#4-申請フォーム入力情報共通)
5. [パッケージ情報 — EXE提出](#5-パッケージ情報--exe提出)
6. [パッケージ情報 — MSIX提出](#6-パッケージ情報--msix提出)
7. [Store掲載文（日本語・英語）](#7-store掲載文日本語英語)
8. [画像アセット](#8-画像アセット)
9. [年齢区分アンケート](#9-年齢区分アンケート)
10. [プライバシー関連の宣言](#10-プライバシー関連の宣言)
11. [AIポリシー対応](#11-aiポリシー対応)
12. [技術認証チェックリスト（実測済み）](#12-技術認証チェックリスト実測済み)
13. [審査メモ（Notes for Certification）案](#13-審査メモnotes-for-certification案)
14. [ビルド・CI・署名フロー](#14-ビルドci署名フロー)
15. [審査から公開後の運用](#15-審査から公開後の運用)
16. [一次資料リンク](#16-一次資料リンク)

---

## 1. ロードマップと全体チェックリスト

| フェーズ | 内容 | 状態 |
|---|---|---|
| **0. 前提整備** | Partner Center登録・プライバシーURL公開・（EXE提出の場合）署名 | ⚠️ 未（§2） |
| **1. アプリ登録** | Partner Centerでアプリ名予約・listing作成（§4〜§10を入力） | 未 |
| **2. パッケージ提出・審査** | MSIXアップロード（またはEXEのパッケージURL指定）→ 審査は**最大3営業日** | 未 |
| **3. 公開・運用** | 審査通過後**約15分**で掲載反映。ステータス "In the Store" を確認 | 未 |

### 全体チェックリスト（申請ボタンを押す前に）

- [ ] Partner Centerアカウント登録済み（§2）
- [ ] プライバシーポリシーを公開URLで閲覧できる（§2・§10）
- [ ] 提出形式を決定（§3。推奨: MSIX）
- [ ] MSIX: Identity（Name/Publisher）をPartner Centerの予約名・Publisher IDに差し替えて再ビルド（§6）
- [x] EXE: **lite版**（モデル非同梱・47MB）をR2へ配置済み・配信検証済み（§5のURL・2026-09-16）。フル版（2.23GB）はURL形式のサイズ上限に抵触するため直接配布専用
- [ ] 掲載文（日・英）・画像・キーワードを入力（§7・§8）
- [ ] 年齢区分アンケート回答済み（§9）
- [ ] データ収集宣言「収集しない」（§10）
- [ ] 審査メモ（§13）を貼り付け

---

## 2. 前提条件（申請前に必要なもの）

| # | 項目 | 状態 | 担当 | 補足 |
|---|---|---|---|---|
| 1 | **Partner Centerアカウント** | ⚠️ 未 | Shineos | 個人2025/9〜・企業2026/5〜は登録無料。企業アカウントはEntra ID必要。**Shineos Inc. 企業アカウントを推奨**（Publisher表示の信頼性） |
| 2 | **プライバシーポリシーの公開URL** | ⚠️ 未 | Shineos | [PRIVACY.md](../PRIVACY.md) の内容を `shineos.com` 上に公開。Web検索（任意ON）で質問文を外部送信する機能があるため、ポリシーURLは**必須**（Store Policies 10.5 個人情報） |
| 3 | **Authenticode署名** | ⚠️ SignPath Foundation承認待ち | 開発＋SignPath | **MSIX提出では不要**（StoreがMicrosoft証明書で再署名するため）。EXE提出では「強く推奨」（必須ではない）→ MSIX先行なら申請をブロックしない |
| 4 | **申請用バイナリ** | ✓ 技術的完成 | 開発 | CIがタグpush (`v*`) ごとにEXE＋MSIXを生成（§14） |

---

## 3. 提出形式の選択 — EXE vs MSIX

| 項目 | EXE提出（MSI/EXE形式） | MSIX提出 |
|---|---|---|
| 提出方法 | **パッケージURL（HTTPS）**で単一exeを指定 | Partner Centerへ `.msix` を**直接アップロード** |
| 署名 | 推奨（SignPath承認待ち） | **不要**（Storeが再署名） |
| ホスティング | **解決済み: Cloudflare R2バケット `shineos-downloads` で配信中**（2026-09-16・r2.dev公開URL・HTTP 200/サイズ一致/先頭末尾1MBバイト一致を検証）。⚠️ r2.devは開発用レート制限があるため、ダウンロードが伸びたら `shineos.com` ゾーンを同アカウントに追加してカスタムドメイン接続へ移行する。今後のリリースはR2 S3 APIキーをCIシークレットに登録し `aws s3 cp`（マルチパート）で自動配置が望ましい（配置手順の詳細: [r2-upload-worker.md](r2-upload-worker.md)） | 不要 |
| 更新 | 新しいパッケージURLで新規申請（提出済みバイナリは変更不可） | Partner Centerに新バージョンを申請（差分配信） |
| 固有の認証テスト | 無人インストール・スタンドアロン性・バンドルウェア等（§12・実測済み） | パッケージ検証（ローカル事前チェックにWACK利用可） |
| パッケージサイズ上限 | — （URLで指定） | **25GB/package**（本アプリ約2.2GBで余裕） |

**推奨: MSIX提出を第一候補とする。**

理由: (1) 署名承認を待たず今すぐ申請できる、(2) 2.2GBのホスティング問題が消える、(3) Store経由の差分更新・クリーンなインストール/アンインストール体感。CIは既にMSIXを署名なし（Store再署名前提）で生成済み。EXE提出はSignPath承認後・自社HTTPSホストを用意できたら並行検討（READMEの直接DL導線は継続）。

---

## 4. 申請フォーム入力情報（共通）

| 項目 | 値 |
|---|---|
| アプリ名（予約名・日本語圏） | 社内知恵袋（ShineosQA） |
| アプリ名（英語圏のlisting用） | ShineosQA |
| Publisher | Shineos Inc. |
| カテゴリ | 生産性 > ビジネス（Productivity > Business） |
| 価格 | 無料（ストア内課金なし） |
| 対象プラットフォーム | Windows Desktop / x64 / Windows 10 1809（build 17763）以降 |
| 審査期間 | 最大3営業日（平均ではより短い） |
| 公開反映 | 審査通過後、約15分 |

---

## 5. パッケージ情報 — EXE提出

### 5.1 Partner Centerフォーム入力値（Packages ページ）

フォームの項目名と入力値の対応。公式の項目定義: [MSI/EXEアプリのパッケージ アップロード](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/upload-app-packages)

| フォーム項目 | 入力値 |
|---|---|
| パッケージURL（Package URL・必須） | `https://pub-cbe981f96fcc423d8c28124ab0fccba5.r2.dev/ShineosQA-Setup-2.1.0-lite.exe`（**lite版**・モデル非同梱・**47,944,976バイト・SHA256 `b8ab7521…`（2026-10-10実DLで全文検証）・署名済みビルド**（2026-09-30に10.2.9対応の署名済みexeへ差し替え・方法A。ETag `6f09dbd536ced726e73aa9de4d4b9089`・HTTP 200確認）。初回配置は2026-09-16（46,796,695バイト・SHA256 `a41362f2…`・未署名）。**提出後のバイナリ変更は不可** — 現行オブジェクトが申請・審査・配信の対象） |
| アーキテクチャ（Architecture・必須） | `x64` |
| 言語（Languages・必須） | `日本語`（`ja`）（英語圏listingを登録する場合は英語も追加可） |
| アプリの種類（App type・必須） | `EXE` |
| **サイレント インストーラーの パラメーター**（Installer parameters・EXEは必須） | **`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`** ※exe名を含まないスイッチのみを入力する |
| インストーラー処理（Installer handling・任意だが強く推奨） | §5.3のリターンコード→標準シナリオ対応表どおりに入力 |

**lite版の設計**（2026-09-16決定）: モデル同梱フル版（2.23GB）はURL形式EXE/MSIのパッケージサイズ上限（約2GB・MSIは加えてWindows Installer自体の2GB上限）に抵触するため、Store提出用はモデル非同梱のlite版（47MB）とする。**インストーラ自体はダウンロードを行わない**（スタンドアロン要件を満たす）。AIモデル（埋め込み640MB＋リランカ636MB＋クイック1.7B・計約2.3GB）は**初回起動時にアプリ内ウィザードからダウンロード**する（実装・検証済み: 2026-09-16実機で`needs_wizard=true`→埋め込みモデルDL→SHA256一致を確認）。フル版はGitHub Releases/R2からの直接配布用に維持（完全オフライン導入の用途）。

### 5.2 サイレント パラメーターの内訳（Inno Setup・[error-codes-v2.md §5](error-codes-v2.md) が一次情報）

| スイッチ | 効果 |
|---|---|
| `/VERYSILENT` | ウィザード・進捗ダイアログを含むUIをすべて非表示にして無人インストール |
| `/SUPPRESSMSGBOXES` | メッセージボックス（確認・エラー警告）を抑制 |
| `/NORESTART` | 再起動を要求しない（本製品は全経路で再起動不要） |

参考 — 実行時のフルコマンド（手動テスト・Intune等での展開時に使用。フォームには**入力しない**）:

```
インストール:   ShineosQA-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/LOG="<path>"]
アンインストール: unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

### 5.3 EXEリターンコード値（フォーム「インストーラー処理（Installer handling）」に転記）

一次情報: [error-codes-v2.md §2](error-codes-v2.md)。実測済みのものに ✓。
フォームは標準シナリオごとにコードを登録するため、対応するシナリオ列を付記:

| コード | 意味 | Store標準シナリオ（フォームの選択肢） | 実測 |
|---|---|---|---|
| **0** | 正常終了（インストール完了・アンインストール完了） | インストールに成功しました（Installation successful） | ✓ |
| 1 | Inno標準: 初期化失敗 | その他のインストール エラー（ドキュメントURL=error-codes-v2.md） | — |
| 2 | Inno標準: 開始前にユーザーキャンセル | ユーザーによるインストールのキャンセル（Installation cancelled by user） | — |
| 3 | Inno標準: 準備中エラー | その他のインストール エラー | — |
| 4 | Inno標準: 処理中エラー | その他のインストール エラー | ✓（Program Files書込拒否で実測） |
| 5 | Inno標準: 処理中にユーザーキャンセル | ユーザーによるインストールのキャンセル | — |
| **11** | 同一バージョン完了済みでのサイレント再実行（何もせず即終了） | アプリケーションが既に存在します（Application already exists） | ✓ |
| **12** | ディスク空き容量不足（4GB未満） | ディスク領域がいっぱいです（Disk space is full） | コード実装済み |
| 13, 14 | 予約（v1互換。v2では返さない） | 登録しない | — |

注意:
- フォーム冒頭の「その他すべての EXE リターン コード値のドキュメント URL」には **`https://pub-cbe981f96fcc423d8c28124ab0fccba5.r2.dev/error-codes.html`** を入力する（インストーラー終了コードの公開説明ページ・2026-09-16にR2へ配置・HTTP 200確認済み。生成元: `output/r2-docs/error-codes.html`）。代替URL: [error-codes-v2.md（GitHub・全終了コードを含む一次情報）](https://github.com/Shineos/shineos-qa-assistant/blob/main/docs/error-codes-v2.md)
- 「再起動が必要」「ネットワーク エラー」「インストールは既に処理中です」「インストール中にパッケージが拒否されました」の4シナリオは**空欄のまま登録しない**（完全オフラインインストーラのため通信失敗がなく、再起動要求は設計上返さず、処理中多重実行・ポリシー拒否に対応する終了コードも定義していないため）

---

## 6. パッケージ情報 — MSIX提出

| 項目 | 値 |
|---|---|
| ファイル | `dist/ShineosQA-2.0.5.0.msix`（CI生成・**署名なしで提出可**: StoreがMicrosoft証明書で再署名） |
| 形式 | Win32フルトラスト・デスクトップブリッジ（`runFullTrust`） |
| マニフェスト | [installer/msix/AppxManifest.xml](../installer/msix/AppxManifest.xml) |
| MinVersion / MaxTested | 10.0.17763.0 / 10.0.26100.0 |
| サイズ | 約2.2GB（上限25GB/package） |

**申請時に必要な作業**:

1. **Identityの差し替え**: Partner Centerでアプリ名を予約すると発行される パッケージID（`Name` = 予約名、`Publisher` = `CN=<アカウント固有のPublisher ID>`）をマニフェストに反映して再ビルド。ローカル検証用の `CN=Shineos Inc.` とは異なるので注意（`build-msix.ps1` の `-Publisher` / `-Version` パラメータで注入可能）
2. **バージョン規則**: 4セグメント必須（タグ `v2.0.5` → `2.0.5.0`）。**申請ごとに必ず増加**させる
3. **マニフェスト内の表示名は英字**: makeappxの検証で日本語が落ちるためCIが英字化（`ShineosQA`）している。Store掲載の表示名・説明はPartner Centerの**言語別listingで上書き**されるため問題ない
4. **ローカル事前チェック**: Windows App Certification Kit（WACK）での検証を推奨（Microsoft自身も審査通過のベストプラクティスに挙げている）
5. 初回MSIX起動時の `%LOCALAPPDATA%\Packages\...\LocalState` ディレクトリ生成は実装済み（v2.0.4の修正）

---

## 7. Store掲載文（日本語・英語）

> **2026-10-20 全面刷新（v2.4.0）**: 「図面と実績が繋がるローカルQA」を最前列に置く差別化配置へ。
> 10.1.1整合（絶対表現を避け、Web検索の既定OFFと報告機能を明記）を最優先で文言を作成。
> 貼り付けは Partner Center の 提出開始 → プロパティ/説明 の各欄へ。

### 7.1 日本語

**簡単な説明（最大200字）**:

> 図面と実績表を登録すると、図番の過去見積・原価・注記まで出典付きで答える社内Q&A。文書のAI処理はPC内で完結し、初期設定後はオフラインで動作します。ナレッジに無い質問には「該当する記載がありません」と回答します。

**詳しい説明（最大1500字）**:

> 社内知恵袋は、製造業の現場から生まれた完全ローカル型の社内Q&Aツールです。図面・実績表・規定・マニュアルを登録するだけで、誰でも日本語で質問でき、回答には必ず出典が付きます。
>
> ■ 製造業向け機能（インストール直後から有効）
> ・図面QA: 図面PDFの表題欄（図番・品名・材質・改訂）を自動で読み取り、図番や品名で検索・質問できます。図番の表記ゆれ（ST-1042A／ST1042A）も同一視。画面キャプチャを貼り付けての質問にも対応（OCRはPC内で処理）
> ・実績接続QA: 実績表（Excel/CSV）を登録すると図番列を自動検出し、「ST-1042Aの過去見積と原価は？」といった質問に過去見積・原価・納期を出典付きで回答
> ・スキャン図面対応: 紙スキャンの図面PDFも内蔵OCRで読み取り（コントラストの低いスキャンは自動で前処理）。視覚モデルを導入すると図面の注記・寸法をさらに高精度に読み取れます
> ・DXF（CAD図面）の取り込みにも対応
>
> ■ 全機能
> ・回答には必ず「出典（文書名・該当箇所）」が表示され、ナレッジに無い質問には「該当する記載がありません」と答えます（根拠のない回答をしません）
> ・文書のAI処理はすべて自分のPC内で完結し、自動送信はありません。Web検索は既定でOFFで、ONにした場合のみ検索語が検索サービスへ送信されます。各AI回答には「問題を報告」ボタンがあります
> ・インストール後の初回起動時にAIモデル（クイック1.7B・合計約2.3GB）を取得します。以降は完全オフラインで動作します（モデル取得時のみ通信）。閉域ネットワークのPC向けに、ダウンロード済みモデルファイルをUSB等で持ち込んで導入するオフライン取り込みも可能
> ・PDF・Word・Excel・CSV・Markdown・テキスト・DXF・図面PDFをドラッグ＆ドロップでナレッジ化
> ・ハイブリッド検索（キーワード＋意味検索）で型番・規程番号・固有名詞も正確にヒット
> ・速度と精度でモデルを切り替え可能（クイック1.7B／標準4B／高品質30B・アプリ内で追加ダウンロード）
> ・閉じるとAIエンジンも完全停止。他アプリの作業を優先する低負荷モード搭載
> ・複合質問をまとめて調べる試験的なエージェント機能（設定で有効化）
> ・作成モード: 参照する文書を選んで指示するだけで、FAQ集・手順書・報告書などの下書きを生成（テンプレートは指示から自動判定）。各回答から「資料にする」で下書きに展開もできます。生成物はAI下書きとして履歴に残り、.mdで保存可能
>
> ■ 動作環境
> Windows 10/11（64bit）・メモリ8GB以上・空き容量4GB以上（GPU不要）。初回起動時のモデル取得にのみインターネット接続が必要です
>
> ■ 提供形態
> 本体は無償（MIT License）。導入支援・保守サポートは shineos.com まで。

### 7.2 英語（英語圏listing用）

**Short description**:

> Register drawings and result sheets, and this local Q&A answers past estimates, costs and drawing notes with citations. AI processing stays on your PC; it works offline after setup. If it's not in your documents, it says "not found".

**Long description**:

> ShineosQA is a fully local Q&A tool born from the factory floor. Register drawings, result sheets, regulations and manuals — anyone on the team can query them in natural Japanese, and every answer cites its source.
>
> - Drawing QA (enabled by default): reads title blocks (drawing number, part name, material, revision) from drawing PDFs and finds them even with notation variants (ST-1042A / ST1042A). Ask with a screenshot too — OCR runs on your PC
> - Results-linked QA: register a result sheet (Excel/CSV) and ask "What was the past estimate and cost for ST-1042A?" — answers cite the sheet
> - Scanned drawings: paper-scanned drawing PDFs are read by built-in OCR with adaptive preprocessing; an optional vision model reads notes and dimensions with higher accuracy
> - DXF (CAD drawing) import supported
> - Every answer shows its source (document and section). Questions not covered by your documents are answered with "not found" — it never guesses
> - All AI processing stays on your PC; nothing is sent automatically. Web search is OFF by default — only the search terms are sent to a search service when you turn it on. Each AI answer has a "Report an issue" button
> - On first launch, the app downloads the AI models (~2.3 GB); after that it runs fully offline. For air-gapped PCs, models can be imported from a local file (e.g. USB)
> - Register PDF / Word / Excel / CSV / Markdown / text / DXF / drawing PDFs by drag & drop
> - Hybrid search (keyword + semantic) finds model numbers and regulation IDs accurately
> - Quick (1.7B) / Standard (4B) / Quality (30B) models switch in-app; extras download on demand
> - Closing the app stops the AI engine completely; a low-load mode prioritizes your other apps
> - Experimental agent mode (opt-in) answers compound questions in one pass
> - Draft mode: pick reference documents, give an instruction, and get a draft FAQ / procedure / report (template auto-detected) — each answer also has a "turn into a document" button. Drafts stay in history and export as .md
>
> Requirements: Windows 10/11 (64-bit), 8 GB+ RAM, ~4 GB free disk space, internet on first launch only. No GPU needed.
>
> Free (MIT License). Installation support: shineos.com

### 7.3 検索キーワード（フォーム: 最大7個・合計21単語まで）

**案A（現行維持・2026-10-01 Partner Center確認済みに近い）**:

```
社内QA, 社内文書検索, オフラインAI, ローカルAI, 図面検索, マニュアル検索, 社内規定
```

**案B（製造業差別化強化・「ナレッジベース」等を汎用語から図面系へ入れ替え）**:

```
社内QA, 製造業 図面, 図面検索, 図面QA, ローカルAI, オフラインAI, 実績検索
```

- 申請時は **Partner Centerの現在値を画面で突合** してから上書きする（本書の記録と画面は差異が生じうる。2026-10-01確認時は「ナレッジベース」が含まれ「図面検索」は未記録だった）。
- 上限: 7個/合計21単語。案Bは「製造業 図面」が2単語扱いに注意（合計13単語で可）。
- 第三者商標（ChatGPT等）はキーワード入力すると認証で却下されうるため使わない（Storeポリシー 10.1 関連）。

---

## 8. 画像アセット

| アセット | ファイル | 状態 |
|---|---|---|
| Storeロゴ 1:1 Box art（必須・1080x1080） | `assets/store-logo-1080.png`（78KB・app.icoデザインの青→緑版・角透過） | ✓ 2026-09-16更新 |
| Storeロゴ 2:3 Poster（推奨・720x1080） | `assets/store-poster-720x1080.png`（411KB） | ✓ 2026-09-16生成 |
| ヒーロー/宣伝画像（SNS・OGP・スクショ1枚目候補・1920x1080） | `assets/promo-hero.png`（935KB） | ✓ 2026-09-16生成 |
| Storeロゴ 300x300 PNG（従来・MSIXパッケージ内ロゴのソース） | `assets/store-logo-300.png` | ✓ 生成済み |
| スクリーンショット1 初回モデルDL（Store版の特徴・listing文の「初回起動時にモデル取得」と整合） | `assets/screenshots/app-00-model-download.png`（1481x987・要件1366x768以上を満たす） | ✓ 2026-09-16追加 |
| スクリーンショット2 メイン画面 | `assets/screenshots/app-01-main.png`（1500x1000・v2.2.1 UI） | ✓ 2026-10-16更新 |
| スクリーンショット3 出典付き回答（「問題を報告」ボタン・出典スニペット展開） | `assets/screenshots/app-02-chat.png`（1500x1000・v2.2.1 UI・11.16対応の提示） | ✓ 2026-10-16更新 |
| スクリーンショット4 ナレッジ管理 | `assets/screenshots/app-03-knowledge.png`（1500x1000） | ✓ 2026-10-16更新 |
| スクリーンショット5 モデル選択 | `assets/screenshots/app-04-models.png`（1500x1000） | ✓ 2026-10-16更新 |
| スクリーンショット6 図面QA（図番ST-1042Aの材質・品名+出典。製造業向けの差別化） | `assets/screenshots/app-05-drawing.png`（1500x1000・v2.2.0追加） | ✓ 2026-10-16追加 |
| スクリーンショット7 **実績接続QA**（「ST-1042Aの過去見積と原価は？」→150,000円/98,000円を出典付き回答。差別化の中核ビジュアル・**スクショ一覧の2枚目に配置推奨**） | `assets/screenshots/app-06-jisseki.png`（1500x1000） | ✓ 2026-10-20追加 |
| スクリーンショット8 **作成モード**（参照ファイル選択→指示→章立て生成中のFAQ下書き+.md保存。仕事効率ツールとしての訴求） | `assets/screenshots/app-07-draft.png`（1500x1000） | ✓ 2026-10-21追加 |
| MSIXパッケージ内ロゴ（150/44/50px） | CIが `store-logo-300.png` から自動生成 | ✓ |

※ 1枚目（モデルDL画面）は日本語UIのまま掲載してよい（対象市場が日本のため。多言語展開時にキャプションのローカライズを検討）。

---

## 9. 年齢区分アンケート

- アプリのカテゴリ: 生産性ツール
- 暴力・性的表現・ギャンブル等: すべて「いいえ」
- ユーザー生成コンテンツ（UGC）: **なし**（自PC内の文書のみ。共有・投稿機能なし）
- 続きの設問はすべて否定的回答で「全年代対応」になる見込み

---

## 10. プライバシー関連の宣言

| 項目 | 対応 |
|---|---|
| プライバシーポリシーURL | `shineos.com` 上に [PRIVACY.md](../PRIVACY.md) の内容を公開したURLを入力 ⚠️ 公開待ち |
| データ収集宣言（Partner Center） | **「データの収集なし」**と宣言（テレメトリ・アナリティクス・クラッシュレポートなしのため） |
| データ収集の開示 | 初回起動時のモデル取得（huggingface.co・質問や文書は送信しない）とWeb検索トグル（既定OFF・任意）のみ外部送信あり → 掲載文・審査メモに明記済み |
| ローカルデータ | ナレッジ・履歴は `data\knowledge.db`（SQLite）にPC内保存。図面拡張（設定で有効時）のみ取り込み元ファイルの原本を `data\files\` に保存（キャプチャ画像は保存されない）。アンインストール時に削除確認あり |

---

## 11. AIポリシー対応

Microsoft Store Policies（v7.19）は**生成AIが作る動的コンテンツもポリシー準拠を要求**する。本アプリの態勢:

| 要件 | 本アプリの状況 |
|---|---|
| AI生成コンテンツのポリシー準拠 | ナレッジ登録文書のみから回答（RAG）。ナレッジ外は「該当する記載がありません」と推測しない（ハルシネーションガード・回答ガード閾値運用） |
| 有害コンテンツ | 一般知識の質問には応答しない設計（社内文書Q&A特化）。UGC・共有機能なし |
| AIであることの透明性 | 掲載文に「AIで検索」「AIモデル（Qwen3）同梱」と明記 |
| **11.16 報告メカニズム（2026-09-30認定不合格の対応済み項目）** | **各AI回答の下に「問題を報告」ボタン**（旗アイコン＋ラベル）。クリックで報告ダイアログ: 問題の種類（不適切・有害な内容／事実と異なる・誤解を招く内容／その他）＋詳細入力＋報告内容（質問とAI回答の抜粋・バージョン・日時を含む）のコピー、または `https://shineos.com/contact/` をブラウザで開く。設定タブ「サポート」にも「問題を報告」リンクあり。**アプリ自身は何も自動送信しない**（報告はユーザー自身のコピー／フォーム送信のみ。PRIVACY.mdの「外部通信なし」主張と整合） |

**審査リスクと対策**: 審査員が一般知識の質問（「富士山の高さは？」等）をすると「該当なし」が返る。これは仕様だが、説明がないと「機能しないアプリ」と誤判定されうる → **審査メモ（§13）に使い方を必ず記載する**。

---

## 12. 技術認証チェックリスト（実測済み）

MicrosoftのEXE認証テスト項目（[MSI/EXE認証プロセス](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-certification-process)）に対する本アプリの状況:

| 要件 | 状態 |
|---|---|
| 完全オフラインのスタンドアロンインストーラ（インストーラ自体はDLしない） | ✓ lite版47MB（モデルは初回起動時にアプリ内DL。スタンドアロン要件＝「起動時にバイナリをDLするダウンローダーでない」を満たす） |
| 宣言コマンドでの無人インストール | ✓ exit=0実測 |
| 再起動要求なし | ✓ |
| 一意なリターンコード | ✓ §5 |
| クリーンアンインストール（本体完全削除・データは対話確認） | ✓ 実測 |
| 管理者権限不要（ユーザー単位 `%LOCALAPPDATA%\Programs`） | ✓ |
| スタートメニュー・プログラム一覧への登録 | ✓ Inno Setup標準 |
| プログラムの追加と削除での情報（製品名・発行元・バージョン） | ✓ Inno Setupが設定 |
| ネットワーク切断時にクラッシュしない | ✓ 完全オフライン動作（バージョンチェック通信なし） |
| バンドルウェア（第三者アプリの同梱インストール）なし | ✓ |
| 非Microsoftドライバ・NTサービスへの依存なし | ✓（v2はサービス常駐なし・オンデマンド起動） |
| Authenticode署名 | ⚠️ SignPath承認待ち（MSIX提出では不要） |
| プライバシーポリシーURL | ⚠️ 公開待ち |

---

## 13. 審査メモ（Notes for Certification）案

申請フォームの「認定のための注意書き」欄にそのまま貼る（英語で記載・lite版提出の流れに対応）:

> ShineosQA ("社内知恵袋") is an internal-document Q&A tool for Japanese companies. The UI is in Japanese. No account or sign-in is required.
>
> 1. **First launch requires internet**: the installer itself downloads nothing, but on the first app launch a welcome wizard downloads the required AI models (~2.3 GB total) from huggingface.co, with SHA-256 verification and a progress bar. In the wizard, we recommend choosing "クイック（既定・推奨）" (Quick 1.7B). After the download completes, the app reloads and then works fully offline. If a download fails, relaunching retries it.
> 2. After the models are ready, a one-time guide dialog about registering knowledge documents appears (can be dismissed).
> 3. **Important — intended behavior**: the app answers only from documents registered in the "ナレッジ (Knowledge)" tab. With an empty knowledge base, any general question (e.g., "How tall is Mt. Fuji?") is answered with "該当する記載がありません" ("not found in the knowledge base"). This is the intended anti-hallucination design, not a malfunction.
> 4. To evaluate Q&A: open the "ナレッジ" tab and drag & drop any PDF / Word / Markdown / text file, wait a few seconds for indexing, then ask a question about the file's content in Japanese. The answer includes citations (document name and section).
> 5. The web-search toggle is OFF by default. Only when explicitly turned ON is the typed query sent to DuckDuckGo.
> 6. Closing the app stops all backend/engine processes and releases localhost port 8300 — nothing stays resident.
> 7. **First-launch model download**: the installer downloads nothing. On the first app launch, a welcome wizard downloads the required AI models (~2.3 GB total: embedding, chat 1.7B, reranker) from huggingface.co with SHA-256 verification. **An internet connection is required on first launch**; after that the app is fully offline. Download progress is shown; if it fails, relaunching retries.
> 8. **Reporting AI-generated content (Store policy 11.16 compliance)**: every AI answer has a 「問題を報告」 (Report an Issue) button below it. Clicking it opens a dialog to pick a reason (inappropriate content / factual error / other), optionally add details, and review the pre-filled report (question + answer excerpt + app version + timestamp). The 「問い合わせフォームを開く」 button opens the official contact form at https://shineos.com/contact/ **with the report automatically filled into the form's type and message fields** (company/name/email are entered by the user; the report is also copied to the clipboard as a fallback). The same form also opens from Settings > サポート (Support). The app itself never transmits anything automatically; the user sends the report themselves.
> 9. **Drawing features are enabled by default (v2.4.0)**: the app reads title blocks (drawing number / part name / material / revision) from drawing PDFs on import and answers drawing-number questions with citations. Scanned drawing PDFs are read by the built-in OCR (processed on the PC); an optional vision model (~1.7 GB, downloadable in Settings) improves accuracy. A knowledge list banner may suggest installing it after a scanned drawing is imported — it is optional and OFF unless the user installs it.
>
> Silent install: `ShineosQA-Setup-<version>-lite.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`

---

## 14. ビルド・CI・署名フロー

- **ビルド**: [GitHub Actions](../.github/workflows/release.yml) が `v*` タグpushで生成。ローカルビルドは公開に使わない
  - Web UI (npm) → バックエンド自己完結publish (dotnet) → WebView2ラッパー (csc) → llama.cppエンジン＋モデル3点DL（SHA256検証）→ Inno Setup EXE（約2.2GB）＋ MSIX（Store再署名前提・署名なし）
- **GitHub Releaseの制約**: EXE/MSIXはGitHub Releaseの1アセット上限2GiBを超えるため**分割添付**（Actions成果物からは単一ファイルをDL可能・**成果物には期限がある**）。Store申請のパッケージURLには分割ファイルは使えないため、**単一ファイルをR2へ配置**する（v2.0.5の実績と再利用手順: [r2-upload-worker.md](r2-upload-worker.md)・Worker + R2バインディングのマルチパート方式）
- **署名**: 現在はテスト証明書。SignPath Foundation承認後にCI署名ステップを追加（[CODE_SIGNING.md](../CODE_SIGNING.md)）。Store提出分はStoreが再署名するため、SignPath署名はGitHub Releases向け
- **バイナリ不変の原則**: 申請後の差し替え不可。更新は新しいタグ → 新バイナリ → 新URL（EXE）／新バージョン申請（MSIX）

---

## 15. 審査から公開後の運用

| タイミング | 内容 |
|---|---|
| 審査中 | 最大3営業日。進捗はPartner Centerダッシュボードで確認 |
| 不合格時 | 認証レポート（メール）で原因確認 → 修正 → 新規申請。問い合わせ先: reportapp@microsoft.com |
| 公開後 | **READMEのダウンロード案内を「Microsoft Store 配布準備中」→ Storeリンク付き「入手可能」に差し替え**（日本語・英語両方） |
| スポットチェック | 公開後もMicrosoftによる定期チェックあり。ポリシー違反で即座削除の可能性があるため、掲載文の正確性を維持 |
| バージョン更新 | タグを打つ → CI生成 → MSIXはPartner Centerへ新バージョン申請（バージョン番号の増加必須） |
| 公開後の施策 | 初期評価の獲得（導入検討者へのレビュー依頼）・キーワード/掲載文のA/B調整・Store分析レポートの定期確認 |

---

## 16. 一次資料リンク

| 資料 | URL |
|---|---|
| MSI/EXE認証プロセス（審査の内容・よくある不合格理由） | https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-certification-process |
| MSI/EXEパッケージ アップロード（フォーム項目の定義・サイレント パラメーター・リターンコードシナリオ） | https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/upload-app-packages |
| MSIXパッケージ要件（サイズ上限25GB等） | https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements |
| Microsoft Store Policies（生成AIコンテンツ規定を含む） | https://learn.microsoft.com/en-us/windows/apps/publish/store-policies |
| アプリの申請作成（Partner Center手順） | https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/create-app-submission |
