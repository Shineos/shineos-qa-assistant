# Privacy Policy / プライバシーポリシー

> **日本語サマリー**: 社内知恵袋（ShineosQA）は完全ローカル実行のツールであり、**利用状況の収集・外部送信（テレメトリー）は一切ありません**。インストール時にのみ、各コンポーネントを公式ソースからダウンロードします。任意機能のWeb検索（既定OFF）を有効化した場合のみ、検索クエリが外部（DuckDuckGo）へ送信されます。すべてのデータ（ナレッジ・チャット履歴・ログ）はお使いのPC内にのみ保存されます。

This is the privacy policy of **ShineosQA (社内知恵袋)**, last updated 2026-08-28.

## Data collection

**None.** ShineosQA does not collect, transmit, or share any personal data, usage statistics, or telemetry. There are no analytics, no crash reporters, and no phone-home mechanisms in this software.

## Network access

| When | What happens |
|---|---|
| **Installation only** | Components are downloaded from official sources: `github.com` (Ollama, Inno Setup dependencies), `pypi.org` / `nuget.org` (Open WebUI, Python packages), `python.org` (Python), and the official Ollama model registry (`registry.ollama.ai`) for Qwen2.5 / bge-m3 models |
| **Runtime (default)** | **No outbound connections.** All AI processing, document search (RAG), and chat run entirely on `localhost` (default port 8080) on your own PC |
| **Optional web search (OFF by default)** | If — and only if — the user explicitly enables the web-search toggle, the entered query is sent to the configured search engine (DuckDuckGo). The toggle resets per chat and a warning is shown in the first-run guide |

## Where your data is stored

All user data remains on the local machine and is never uploaded:

- Knowledge documents & uploaded files: `C:\Program Files\ShineosQA\data\`
- Chat history & settings: `C:\Program Files\ShineosQA\data\webui.db`
- Application log: `%APPDATA%\ShineosQA\app.log`

Uninstalling the application removes these directories (a confirmation dialog offers to keep knowledge documents for upgrades).

## Third-party components

This tool bundles or downloads open-source components (Ollama, Open WebUI, Python, Qwen2.5, bge-m3, NSSM). Their licenses and sources are listed in [vendor/THIRD-PARTY-NOTICES.txt](vendor/THIRD-PARTY-NOTICES.txt). Their default telemetry/update-check features (where present) are disabled in this distribution (`ENABLE_VERSION_UPDATE_CHECK=False`).

## Contact

Privacy inquiries: https://shineos.com/contact/
