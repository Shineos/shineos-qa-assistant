# プライバシーポリシー（Privacy Policy）

- **制定日**: 2026-08-28
- **最終改定日**: 2026-08-31
- **適用対象**: 社内知恵袋（ShineosQA）Windows アプリケーション（以下「本製品」）

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
| **インストール時のみ** | 必要なコンポーネントを公式ソースからダウンロードします。Ollama（`github.com`）、Python 3.12（`nuget.org`）、AIモデル qwen2.5 / bge-m3（Ollama公式レジストリ `registry.ollama.ai`）、Open WebUI・Pythonライブラリ（`pypi.org` / `download.pytorch.org`）。ダウンロード完了後は不要になります |
| **通常利用時（既定）** | **外部通信なし**。AI処理・文書検索（RAG）・チャットはすべてお使いのPC内（`127.0.0.1`）で完結します。バージョンアップの確認も無効化されているため、外部への問い合わせは発生しません |
| **Web検索 ON 時（任意・既定OFF）** | チャット入力欄のWeb検索ボタンを**お客様が明示的にONにした場合のみ**、入力した質問文が外部の検索サービス（DuckDuckGo・APIキー不要）へ送信され、検索結果のページを取得します。トグルはチャットごとの選択で、初回起動時ガイドでも注意を表示します。**社内情報に関する質問の際は OFF のまま**にしてください |

## 4. データの保存先

すべてのデータはお使いのPC内にのみ保存されます。

| データ | 保存先 |
|---|---|
| 登録した社内文書（ナレッジ） | `C:\Program Files\ShineosQA\knowledge\` |
| チャット履歴・設定 | `C:\Program Files\ShineosQA\data\webui.db` |
| 検索用ベクトルデータベース | `C:\Program Files\ShineosQA\data\vector_db\` |
| アップロードしたファイル | `C:\Program Files\ShineosQA\data\uploads\` |
| チャットから生成した資料（PDF/PPTX/Word） | `C:\Program Files\ShineosQA\data\mcpo_output\`（資料作成ツールの一部は `%USERPROFILE%\shineos-qa-out\`、環境変数 `FILEGEN_OUT` で変更可） |
| 動作ログ（インストール・実行・検証） | `C:\Program Files\ShineosQA\logs\` |
| アプリのログ・初回起動フラグ・WebView2ブラウザデータ（キャッシュ等） | `%APPDATA%\ShineosQA\` |
| AIモデル本体（qwen2.5・bge-m3） | Ollama のモデル保存先（システムアカウントプロファイル内を含む） |

※ インストール先を変更した場合は、`C:\Program Files\ShineosQA` の部分が選択したフォルダになります。

## 5. 第三者提供

本製品は、**収集したデータを第三者へ提供・販売することはありません**。

唯一の例外は、上記 3 のとおり、お客様がWeb検索をONにした場合に、**入力した質問文**が検索サービス（DuckDuckGo）へ送信される点です。これは本製品のデータではなく、お客様が明示的に実行した検索操作の一部です。

## 6. データの保持と削除

- チャット履歴・ナレッジは、お客様が削除するまでPC内に保持されます（Open WebUI の管理画面から削除可能）
- **アンインストール時**: 「ナレッジ（社内文書・検索データ）を残すか」「Ollama 本体とAIモデル（数GB）を削除するか」を確認します。「すべて削除」を選んだ場合は、モデル・ログ・検索データを含めて掃除します
- **アップグレード（バージョン更新）時**: 旧データは `data.backup-<日付>` に1世代バックアップされ、社内文書は引き継がれます
- 削除した文書の検索索引が残る場合は、同梱の保守ツール（`purge_orphan_vectors.py`）で掃除できます

## 7. セキュリティについて

- 本製品のサービス（Open WebUI・Ollama）は**ローカルホスト（`127.0.0.1`）のみで待ち受け**、外部ネットワークからのアクセスはできません
- インストールには管理者権限が必要です
- Web検索をOFFにした状態では、本製品はインターネットに一切接続しません

## 8. ポリシーの改定

本ポリシーは、機能の変更や法令の改正に応じて改定することがあります。改定時は本ページの「最終改定日」を更新し、重要な変更がある場合は製品のリリースノート等でお知らせします。

## 9. お問い合わせ

プライバシー・データ取り扱いに関するお問い合わせは、下記までお願いいたします。

- Shineos Inc.: [https://shineos.com/contact/](https://shineos.com/contact/)

---

# Privacy Policy (English)

- **Effective date**: 2026-08-28
- **Last updated**: 2026-08-31
- **Applies to**: 社内知恵袋 (ShineosQA) Windows application ("the Product")

## 1. Overview

The Product is a fully local company Q&A tool that turns internal documents into a searchable knowledge base. Its core principle: **no data collection, no telemetry, no external transmission.** All processing runs on your own PC.

## 2. Data collection

**None.** The Product does not collect, transmit, or share any personal data, usage statistics, or telemetry. There are no analytics, no crash reporters, and no phone-home mechanisms. The only data involved is the internal documents you register and the questions/answers you enter in chat — and that data never leaves your PC.

## 3. Network access

| When | What happens |
|---|---|
| **Installation only** | Components are downloaded from official sources: Ollama (`github.com`), Python 3.12 (`nuget.org`), AI models qwen2.5 / bge-m3 (official Ollama registry, `registry.ollama.ai`), Open WebUI and Python libraries (`pypi.org` / `download.pytorch.org`). No network access is needed afterwards. |
| **Normal use (default)** | **No outbound connections.** AI processing, document search (RAG), and chat all run on `localhost` (`127.0.0.1`) on your PC. Version-update checks are disabled. |
| **Optional web search (OFF by default)** | Only if you explicitly enable the per-chat web-search toggle, your question text is sent to an external search service (DuckDuckGo, no API key required) and result pages are fetched. The toggle is per chat and a warning is shown in the first-run guide. Keep it OFF when asking about internal information. |

## 4. Where your data is stored

All data stays on your PC:

- Registered documents (knowledge): `C:\Program Files\ShineosQA\knowledge\`
- Chat history & settings: `C:\Program Files\ShineosQA\data\webui.db`
- Search vector database: `C:\Program Files\ShineosQA\data\vector_db\`
- Uploaded files: `C:\Program Files\ShineosQA\data\uploads\`
- Generated documents (PDF/PPTX/Word): `C:\Program Files\ShineosQA\data\mcpo_output\` (some tools use `%USERPROFILE%\shineos-qa-out\`, configurable via `FILEGEN_OUT`)
- Logs: `C:\Program Files\ShineosQA\logs\` and `%APPDATA%\ShineosQA\`
- AI models (qwen2.5 / bge-m3): inside Ollama's model directory (which may include the system-profile location)

## 5. Third-party sharing

None. The Product never shares or sells your data. The only exception is the optional web search described above: when you enable it, the question you type is sent to the search service (DuckDuckGo) as part of that explicit search operation.

## 6. Retention and deletion

- Chat history and knowledge remain on your PC until you delete them (removable from the Open WebUI admin screen).
- **Uninstall**: you are asked whether to keep the knowledge documents and whether to delete Ollama and the AI models (several GB). Choosing full deletion also cleans models, logs, and search data.
- **Upgrade**: old data is backed up to `data.backup-<date>` (one generation) and your documents are carried over.
- A maintenance tool (`purge_orphan_vectors.py`) is provided to clean leftover search-index entries after document deletion.

## 7. Security

- The Product's services (Open WebUI, Ollama) listen **only on localhost (`127.0.0.1`)** and are not reachable from the network.
- Administrator rights are required to install.
- With web search OFF, the Product makes no internet connection at all.

## 8. Policy updates

This policy may be updated in response to feature changes or legal requirements. Updates are reflected in the "Last updated" date, and significant changes are announced in release notes.

## 9. Contact

Privacy inquiries: [https://shineos.com/contact/](https://shineos.com/contact/)
