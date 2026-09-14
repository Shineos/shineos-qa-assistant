# プライバシーポリシー（Privacy Policy）

- **制定日**: 2026-08-28
- **最終改定日**: 2026-09-14（v2アーキテクチャ対応版）
- **適用対象**: 社内知恵袋（ShineosQA）v2 Windows アプリケーション（以下「本製品」）

---

## 1. はじめに

本製品は、社内規定・業務マニュアルをナレッジ化し、社内Q&Aを**お使いのパソコン内だけで完結**させることを目的としたツールです。

本製品の基本方針は次のとおりです。

- **利用状況の収集・外部送信（テレメトリ）は一切ありません**
- アナリティクス・クラッシュレポート・自動更新のための通信はありません
- 登録した社内文書・チャット履歴・検索データは、**すべてお使いのPC内にのみ保存**されます

本ポリシーは、本製品がいつ・どのような通信を行うか、どのデータをどこに保存するかを、お客様が判断できるよう正確に記載することを目的としています。

## 2. 収集する情報

本製品は、**個人情報・利用情報のいずれも収集しません**。

- 氏名・メールアドレス・IPアドレス等の個人情報を収集・送信しません
- 利用統計・操作ログの外部送信（テレメトリ）はありません
- 本製品がやり取りするデータは、お客様が登録した社内文書と、チャットで入力した質問・回答（業務データ）のみであり、これらは外部へ送信されません

## 3. 外部通信の内訳

本製品の通信は、下表のとおりです。**既定の状態では外部への通信は発生しません**。

| タイミング | 通信内容 |
|---|---|
| **インストール時** | インストーラはAIモデル・推論エンジンを**すべて同梱**しており、インストール中のダウンロードはありません（完全オフラインでインストール可能） |
| **追加モデルの取得時（任意）** | 初期モデル（1.7B・埋め込み・リランカ）は同梱済みです。お客様が**画面から標準（4B）・高品質（30B）モデルの追加を明示的に行った場合のみ**、モデルファイルを `huggingface.co`（モデル配布元）からダウンロードし、SHA256検証後に保存します。質問や文書の内容は送信しません |
| **通常利用時（既定）** | **外部通信なし**。AI処理・文書検索（RAG）・チャットはすべてお使いのPC内（`127.0.0.1`）で完結します。バージョンアップの確認も行わないため、外部への問い合わせは発生しません |
| **Web検索 ON 時（任意・既定OFF）** | チャット入力欄のWeb検索ボタンを**お客様が明示的にONにした場合のみ**、入力した質問文が外部の検索サービス（DuckDuckGo・APIキー不要）へ送信され、検索結果のページを取得します。トグルはチャットごとの選択で、初回起動時ガイドでも注意を表示します。**社内情報に関する質問の際は OFF のまま**にしてください |

## 4. データの保存先

すべてのデータはお使いのPC内にのみ保存されます。既定のインストール先は
`%LOCALAPPDATA%\Programs\ShineosQA`（ユーザーフォルダ内・管理者権限不要）です。

| データ | 保存先 |
|---|---|
| ナレッジ（文書テキスト・検索索引・ベクトル）・チャット履歴・設定 | `{インストール先}\data\knowledge.db`（SQLite単一ファイル） |
| AIモデル本体（GGUF） | `{インストール先}\models\`（初期モデルは同梱・追加モデルもここに保存） |
| 動作ログ（バックエンド・エンジン） | `{インストール先}\data\logs\` |
| アプリのログ・初回起動フラグ・WebView2ブラウザデータ（キャッシュ等） | `%APPDATA%\ShineosQA\` |

※ v1（旧バージョン）と異なり、`C:\Program Files` へのインストール・Windowsサービスの常駐・Ollama/Open WebUI の利用は行いません。

## 5. 第三者提供

本製品は、**収集したデータを第三者へ提供・販売することはありません**。

唯一の例外は、上記 3 のとおり、お客様がWeb検索をONにした場合に、**入力した質問文**が検索サービス（DuckDuckGo）へ送信される点です。これは本製品のデータではなく、お客様が明示的に実行した検索操作の一部です。

## 6. データの保持と削除

- チャット履歴・ナレッジは、お客様が削除するまでPC内に保持されます（アプリ画面の履歴・ナレッジ管理から削除可能）
- **アンインストール時**: アプリ本体・エンジン・同梱モデルは完全に削除します。ナレッジとチャット履歴（`data` フォルダ）を削除するか残すかは確認のうえ処理します（サイレントアンインストールでは残します）
- **アップグレード（バージョン更新）時**: 設定・ナレッジ・追加DL済みモデルはそのまま引き継がれます
- アプリを閉じると、AIエンジン・バックエンドは完全に停止しメモリを解放します（バックグラウンドでの常駐はありません）

## 7. セキュリティについて

- 本製品のバックエンドは**ローカルホスト（`127.0.0.1:8300`）のみで待ち受け**、外部ネットワークからのアクセスはできません
- インストールに管理者権限は不要です（ユーザー単位でインストールされます）
- Web検索をOFFにした状態では、本製品はインターネットに一切接続しません

## 8. ポリシーの改定

本ポリシーは、機能の変更や法令の改正に応じて改定することがあります。改定時は本ページの「最終改定日」を更新し、重要な変更がある場合は製品のリリースノート等でお知らせします。

## 9. お問い合わせ

プライバシー・データ取り扱いに関するお問い合わせは、下記までお願いいたします。

- Shineos Inc.: [https://shineos.com/contact/](https://shineos.com/contact/)

---

# Privacy Policy (English)

- **Effective date**: 2026-08-28
- **Last updated**: 2026-09-14 (v2 architecture)
- **Applies to**: 社内知恵袋 (ShineosQA) v2 Windows application ("the Product")

## 1. Overview

The Product is a fully local company Q&A tool that turns internal documents into a searchable knowledge base. Its core principle: **no data collection, no telemetry, no external transmission.** All processing runs on your own PC.

## 2. Data collection

**None.** The Product does not collect, transmit, or share any personal data, usage statistics, or telemetry. There are no analytics, no crash reporters, and no phone-home mechanisms. The only data involved is the internal documents you register and the questions/answers you enter in chat — and that data never leaves your PC.

## 3. Network access

| When | What happens |
|---|---|
| **Installation** | The installer bundles the AI models and inference engine — **no downloads during installation** (fully offline install). |
| **Optional extra models** | The starter models are bundled. Only if you explicitly choose to add the 4B or 30B model from the app is the model file downloaded from `huggingface.co` (verified by SHA256). No questions or documents are ever sent. |
| **Normal use (default)** | **No outbound connections.** AI processing, document search (RAG), and chat all run on `localhost` (`127.0.0.1`) on your PC. No version-update checks are made. |
| **Optional web search (OFF by default)** | Only if you explicitly enable the per-chat web-search toggle, your question text is sent to an external search service (DuckDuckGo, no API key required) and result pages are fetched. Keep it OFF when asking about internal information. |

## 4. Where your data is stored

All data stays on your PC. The default install location is `%LOCALAPPDATA%\Programs\ShineosQA` (per-user, no admin rights):

- Knowledge (document text, search index, vectors), chat history & settings: `{install dir}\data\knowledge.db` (a single SQLite file)
- AI models (GGUF): `{install dir}\models\`
- Logs: `{install dir}\data\logs\`
- App log, first-run flag, WebView2 browser data: `%APPDATA%\ShineosQA\`

Unlike the old v1, the Product no longer installs into `C:\Program Files`, runs Windows services, or uses Ollama/Open WebUI.

## 5. Third-party sharing

None. The Product never shares or sells your data. The only exception is the optional web search described above: when you enable it, the question you type is sent to the search service (DuckDuckGo) as part of that explicit search operation.

## 6. Retention and deletion

- Chat history and knowledge remain on your PC until you delete them (from the app's history and knowledge screens).
- **Uninstall**: the application, engine and bundled models are fully removed. You are asked whether to also delete the knowledge and chat history (`data` folder); silent uninstalls keep it.
- **Upgrade**: settings, knowledge, and downloaded models carry over.
- Closing the app fully stops the AI engine and backend and releases all memory (nothing runs in the background).

## 7. Security

- The backend listens **only on localhost (`127.0.0.1:8300`)** and is not reachable from the network.
- No administrator rights are required to install (per-user install).
- With web search OFF, the Product makes no internet connection at all.

## 8. Policy updates

This policy may be updated in response to feature changes or legal requirements. Updates are reflected in the "Last updated" date, and significant changes are announced in release notes.

## 9. Contact

Privacy inquiries: [https://shineos.com/contact/](https://shineos.com/contact/)
