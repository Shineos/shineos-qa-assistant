# 社内知恵袋（ShineosQA）

**Languages**: [日本語](README.md) | [English](README-EN.md)

**社内規定・業務マニュアルをナレッジ化し、社内Q&Aをパソコン内だけで安全に実現する Windows アプリ**

[Shineos Inc.](https://shineos.com) が提供する社内Q&Aツールです。Ollama + Open WebUI によるローカルAI環境を、技術者でない方でもインストールした瞬間から使えるようにパッケージ化しました。社内資料を外部に送信しない**完全オフライン運用**が特徴です。

[![Latest Release](https://img.shields.io/github/v/release/Shineos/shineos-qa-assistant?sort=semver&label=Latest%20Release)](https://github.com/Shineos/shineos-qa-assistant/releases/latest)

## できること

| 機能 | 内容 |
|------|------|
| 社内Q&A（RAG） | ナレッジに登録した社内規定・業務マニュアルから、根拠（文書名・該当箇所）付きで回答 |
| ナレッジ自動登録 | インストール時にフォルダを指定するだけ。インストール後は画面からドラッグ＆ドロップで追加 |
| ハイブリッド検索 | BM25（キーワード一致）+ ベクトル検索（意味一致）の併用で、型番・規程番号・固有名詞を正確にヒット |
| 用途別プリセット | 「経費精算ガイド」「ITヘルプデスク」など用途別ボットを選択するだけ |
| ハルシネーション抑制 | ナレッジに回答が無い場合は推測せず「該当する記載がありません」と回答 |
| 応答速度の自動チューニング | モデル常駐の最適化・起動時ウォームアップで、質問から回答までの待ち時間を最小化 |
| 他アプリへの配慮 | AI応答中でも他のアプリを優先（CPU優先度制御・メモリ自動調整） |
| 完全オフライン運用 | 社内資料を外部に送信しない（機密資料の利用に最適） |
| 自動起動 | PC再起動後も Windowsサービスとして自動起動 |
| Web検索（既定 OFF） | ON にすると最新情報を参照（DuckDuckGo・APIキー不要）。**質問内容が外部に送信されるため社内情報の質問時は OFF に** |
| ファイル生成 | チャットから PDF・PowerPoint・Word を作成（オプション） |

## 動作環境

- Windows 10 / 11（64bit）
- メモリ 8GB 以上（16GB 以上推奨）
- 空き容量 15GB 以上
- インストール時のみインターネット接続（Ollama本体1.5GB＋AIモデル約3.4GBなど、**合計約6GB**のダウンロード。所要約30〜90分）
- ※ 回答まで数秒〜20秒ほどかかります（完全社内処理のため）。社内規定・業務マニュアルのQ&Aに特化しており、一般知識の質問には「該当なし」とお答えします

## ダウンロードとインストール

[Releases](https://github.com/Shineos/shineos-qa-assistant/releases/latest) ページから最新の `ShineosQA-Setup-<version>.exe` をダウンロードし、**ダブルクリックするだけでインストール**できます。

1. インストーラの案内に沿ってAIモデルを選択
   - **qwen2.5:3b（推奨・高速）**: 約1.9GB・8GBメモリ機でも快適
   - **qwen2.5:7b（高品質）**: 約4.7GB・16GB以上のメモリ推奨
   - **qwen2.5:1.5b（軽量）**: 約1GB・8GB機に最適・最速
2. 社内文書（ナレッジ）フォルダを指定（省略可。後から画面で追加できます）
3. 完了後、デスクトップの「社内知恵袋」をダブルクリックするとアプリ画面が開きます（URL入力不要・閉じるとサービスも停止）

※ 現在はテスト証明書での署名のため、SmartScreenが「認識されないアプリ」と表示することがあります。その場合は「**詳細情報**」→「**実行**」を選択してください。

## 使い方

1. 画面下の入力欄に「経費精算の手順は？」のように入力して送信
2. 回答には根拠（文書名・該当箇所）が付きます
3. 資料の追加は画面左の「ナレッジ」メニューからドラッグ＆ドロップでいつでも可能

詳しくは **[ユーザーマニュアル](docs/user-guide.md)** を参照してください。

## 画面ギャラリー

### 使用例（動画）

質問してから出典付きの回答が返るまでの実際の流れです（実際のアプリ画面を録画・約2.5倍速）。

![使用例: 質問から出典付き回答まで](assets/videos/app-usage.gif)

### アプリ画面

| 画面 | 内容 |
|------|------|
| [![メイン画面](assets/screenshots/app-01-main.png)](assets/screenshots/app-01-main.png) | **メイン画面**。デスクトップのアイコンから起動するとこの画面が開きます（URL入力不要） |
| [![出典付き回答](assets/screenshots/app-02-chat.png)](assets/screenshots/app-02-chat.png) | **出典付きの回答**。「1泊あたり15,000円(税込)」と引用元文書（QA_list.md）が一緒に表示されます |
| [![ナレッジ管理](assets/screenshots/app-03-knowledge.png)](assets/screenshots/app-03-knowledge.png) | **ナレッジ管理**。社内文書（PDF・Markdown）の登録状況を確認・追加できます |
| [![モデル一覧](assets/screenshots/app-04-models.png)](assets/screenshots/app-04-models.png) | **用途別プリセット**。「経費精算ガイド」「ITヘルプデスク」など目的に合わせて選択できます |

## 提供形態とサポート

- **本体は無償**（MIT License・自己責任でのご利用）
- **導入支援・保守サポートは有償**: 導入の代行、社内文書の登録支援、障害対応は [https://shineos.com/contact/](https://shineos.com/contact/) まで
- 本ツールは オープンソース **Open WebUI** を内部エンジンとして利用しています（UIヘッダー2行目の「powered by Open WebUI」は帰属の明示です）

## ドキュメント

| ドキュメント | 内容 |
|------|------|
| [ユーザーマニュアル](docs/user-guide.md) | 日常の使い方・ナレッジ追加・トラブル対処 |
| [技術資料](docs/technical-notes.md) | メモリ/速度チューニング、RAGの仕組み、アップグレード、スモークテスト等の技術詳細 |
| [構築ドキュメント](docs/build.md) | ビルド・テスト・リリース手順（開発者向け） |
| [変更履歴](CHANGELOG.md) | バージョンごとの変更内容 |
| [コード署名ポリシー](CODE_SIGNING.md) | 署名対象・ビルド/署名フロー・チーム役割（SignPath Foundation 要件） |
| [プライバシーポリシー](PRIVACY.md) | データ収集なし・通信の内訳・データ保存先 |
| [README-EN.md](README-EN.md) | English documentation |

## ライセンス

本プロジェクトのコードは MIT License です。同梱・導入するコンポーネントのライセンスは [vendor/THIRD-PARTY-NOTICES.txt](vendor/THIRD-PARTY-NOTICES.txt) を参照してください。

## 問い合わせ

- 不具合・導入支援・カスタマイズのご相談: [https://shineos.com/contact/](https://shineos.com/contact/)
- 社内Q&A・ナレッジ運用に関するご相談もお気軽にどうぞ
