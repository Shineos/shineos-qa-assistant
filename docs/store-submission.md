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
- [x] EXE: 単一exeをR2へ配置済み・配信検証済み（§5のURL・2026-09-16）
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

| 項目 | 値 |
|---|---|
| インストーラ形式 | EXE（Inno Setup） |
| ファイル名 | `ShineosQA-Setup-2.0.5.exe`（約2.2GB・モデル同梱） |
| パッケージURL | `https://pub-cbe981f96fcc423d8c28124ab0fccba5.r2.dev/ShineosQA-Setup-2.0.5.exe`（**配信中・検証済み**: HTTP 200 / Content-Length 2,234,458,796 / GitHub公式分割アセットとバイト一致 / リリース公表SHA256 `03e8354a…` と同一バイナリ・**提出後のバイナリ変更は不可**） |
| サイレントインストールコマンド（宣言用） | `ShineosQA-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` |
| サイレントアンインストールコマンド | `unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` |

### EXEリターンコード値（申請フォーム「EXE リターン コード値」に転記）

一次情報: [error-codes-v2.md §2](error-codes-v2.md)。実測済みのものに ✓:

| コード | 意味 | 実測 |
|---|---|---|
| 0 | 正常終了（インストール完了・アンインストール完了） | ✓ |
| 1, 2, 3, 4, 5 | Inno Setup標準（初期化失敗／開始前キャンセル／準備中エラー／処理中エラー／処理中キャンセル） | 4=Program Files書込拒否で実測 |
| 11 | 同一バージョン完了済みでのサイレント再実行（何もせず即終了） | ✓ |
| 12 | ディスク空き容量不足（4GB未満） | コード実装済み |
| 13, 14 | 予約（v1互換。v2では返さない） | — |

注意: 空欄なく申請フォームに入力できるよう全コードを記載する。

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

### 7.1 日本語

**簡単な説明（最大200字）**:

> 社内規定・業務マニュアルをAIで検索できる社内Q&Aツール。回答には必ず出典（文書名・該当箇所）が付き、根拠のない回答はしません。社内文書はPCの外に出ない完全オフライン設計。AIモデルを内蔵しインストール直後から使えます。

**詳しい説明（最大1500字）**:

> 社内知恵袋は、社内規定・業務マニュアルを登録するだけで、誰でも自然な日本語で質問できる社内Q&Aツールです。
>
> ■ 特徴
> ・回答には必ず「出典（文書名・該当箇所）」が表示され、ナレッジにない質問には「該当する記載がありません」と答えます（根拠のない回答をしません）
> ・すべての処理が自分のPC内で完結。社内文書・質問・回答が外部に送信されません（テレメトリもありません）
> ・AIモデル（Qwen3 1.7B・高速量子化版）をインストーラに同梱。インストール直後からすぐ使えます
> ・速度と精度のバランスでモデルを切り替え可能（クイック1.7B／標準4B／高品質30B・追加モデルはアプリ内からダウンロード）
> ・PDF・Word・Markdown・テキストをドラッグ＆ドロップでナレッジ化。追加した文書は即座に検索対象になります
> ・ハイブリッド検索（キーワード＋意味検索）で型番・規程番号・固有名詞も正確にヒット
> ・閉じるとAIエンジンも完全停止。他アプリの作業を優先する低負荷モード搭載
>
> ■ 動作環境
> Windows 10/11（64bit）・メモリ8GB以上・空き容量約3GB（GPU不要）
>
> ■ 提供形態
> 本体は無償（MIT License）。導入支援・保守サポートは shineos.com まで。

### 7.2 英語（英語圏listing用・新規作成）

**Short description**:

> Offline internal Q&A for company rules and manuals. Every answer cites its source (document & section); if the answer is not in your documents, it says so. Nothing ever leaves your PC.

**Long description**:

> ShineosQA turns your company regulations and business manuals into a knowledge base that anyone can query in natural Japanese.
>
> - Every answer shows its source (document name and section). Questions not covered by your documents are answered with "not found" — it never guesses
> - All processing stays on your PC. Documents, questions and answers are never sent anywhere (no telemetry)
> - A fast AI model (Qwen3 1.7B) is bundled with the installer — ready to use immediately after installation
> - Switch between Quick (1.7B) / Standard (4B) / Quality (30B) models right in the app; extra models download on demand
> - Register PDF / Word / Markdown / text files by drag & drop; newly added documents are searchable immediately
> - Hybrid search (keyword + semantic) so model numbers and regulation IDs are found accurately
> - Closing the app stops the AI engine completely; a low-load mode prioritizes your other apps
>
> Requirements: Windows 10/11 (64-bit), 8 GB+ RAM, ~3 GB free disk space. No GPU needed.
>
> Free (MIT License). Installation support: shineos.com

### 7.3 検索キーワード（優先順位順・フォームの文字数上限に収まるよう上位から入力）

```
社内QA, 社内文書検索, AI チャット, オフライン AI, ChatGPT オフライン, RAG,
ナレッジベース, 社内規定, マニュアル検索, ローカル AI, 文書 検索, 社内AI
```

※「ChatGPT オフライン」「AI チャット」は実際の検索語として流入が見込める語。掲載文・機能と乖離しない範囲で含める。

---

## 8. 画像アセット

| アセット | ファイル | 状態 |
|---|---|---|
| Storeロゴ 300x300 PNG | `assets/store-logo-300.png` | ✓ 生成済み |
| スクリーンショット1 メイン画面 | `assets/screenshots/app-01-main.png`（1500x1000・要件1366x768以上を満たす） | ✓ |
| スクリーンショット2 出典付き回答 | `assets/screenshots/app-02-chat.png` | ✓ |
| スクリーンショット3 ナレッジ管理 | `assets/screenshots/app-03-knowledge.png` | ✓ |
| スクリーンショット4 モデル選択 | `assets/screenshots/app-04-models.png` | ✓ |
| MSIXパッケージ内ロゴ（150/44/50px） | CIが `store-logo-300.png` から自動生成 | ✓ |

追加推奨（任意）: 1枚以上のスクリーンショットに**日本語のキャプション**を重ねる（ストア内で内容が伝わりクリック率が上がる）。

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
| データ収集の開示 | Web検索トグル（既定OFF・任意）のみ外部送信あり → 掲載文・審査メモに明記済み |
| ローカルデータ | ナレッジ・履歴は `data\knowledge.db`（SQLite）にPC内保存。アンインストール時に削除確認あり |

---

## 11. AIポリシー対応

Microsoft Store Policies（v7.19）は**生成AIが作る動的コンテンツもポリシー準拠を要求**する。本アプリの態勢:

| 要件 | 本アプリの状況 |
|---|---|
| AI生成コンテンツのポリシー準拠 | ナレッジ登録文書のみから回答（RAG）。ナレッジ外は「該当する記載がありません」と推測しない（ハルシネーションガード・回答ガード閾値運用） |
| 有害コンテンツ | 一般知識の質問には応答しない設計（社内文書Q&A特化）。UGC・共有機能なし |
| AIであることの透明性 | 掲載文に「AIで検索」「AIモデル（Qwen3）同梱」と明記 |

**審査リスクと対策**: 審査員が一般知識の質問（「富士山の高さは？」等）をすると「該当なし」が返る。これは仕様だが、説明がないと「機能しないアプリ」と誤判定されうる → **審査メモ（§13）に使い方を必ず記載する**。

---

## 12. 技術認証チェックリスト（実測済み）

MicrosoftのEXE認証テスト項目（[MSI/EXE認証プロセス](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-certification-process)）に対する本アプリの状況:

| 要件 | 状態 |
|---|---|
| 完全オフラインのスタンドアロンインストーラ（DL不要・ダウンローダーではない） | ✓ モデル同梱2.23GB |
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

申請フォームの「認証メモ」欄にそのまま貼る想定（英語で記載）:

> This app is a fully offline internal-document Q&A tool. No account, no sign-in, and no network connection are required to evaluate it.
>
> 1. Launch "社内知恵袋" (ShineosQA) from the Start menu / desktop icon. It opens in about 2 seconds.
> 2. **Important**: The app answers only from documents registered in the "ナレッジ (Knowledge)" tab. With an empty knowledge base, any general question (e.g. "How tall is Mt. Fuji?") is answered with "該当する記載がありません" (not found in the knowledge base). This is intended anti-hallucination behavior, not a malfunction.
> 3. To evaluate Q&A: drag & drop any PDF / Word / Markdown / text file onto the Knowledge tab, wait a few seconds for indexing, then ask a question about the file's content in Japanese. Answers include citations (document name and section).
> 4. On first launch after installation, a one-time guide dialog about knowledge registration appears (can be skipped).
> 5. The web-search toggle is OFF by default. When explicitly turned ON, only the typed query is sent to DuckDuckGo.
> 6. Closing the app stops all backend/engine processes and releases localhost port 8300 — nothing stays resident.
>
> Silent install (EXE form): `ShineosQA-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`

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
| MSIXパッケージ要件（サイズ上限25GB等） | https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements |
| Microsoft Store Policies（生成AIコンテンツ規定を含む） | https://learn.microsoft.com/en-us/windows/apps/publish/store-policies |
| アプリの申請作成（Partner Center手順） | https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/create-app-submission |
