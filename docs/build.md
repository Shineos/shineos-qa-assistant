# 社内知恵袋 — 構築ドキュメント

> **⚠ 本書は v1（Ollama + Open WebUI 版）向けのアーカイブです。** 現行の v2 のビルド手順は [v2開発ガイド](dev-v2.md) を参照してください。

> 作成: 2026-08-20 | Shineos Inc.
> ステータス: 実装完了（Windows実機検証は未実施 — §8 参照）
> 関連: [Zenn記事（ベースとなる技術構成）](https://zenn.dev/shineos/articles/local-llm-rag-web-search-with-ollama)

---

## 1. 目的・方針

Shineos Inc. が Zenn で公開した「Ollama + Open WebUI」によるローカルRAG環境をベースに、**社内規定・業務マニュアルをナレッジ化して社内Q&Aを実現するツール**として構築する。

社内の利用者は、インストール後にナレッジ登録済みのQ&A環境をそのまま利用できる。問い合わせ先は https://shineos.com/contact/ 。

### 製品の約束（ユーザーへの約束事）

| 約束 | 実現方法 |
|------|----------|
| ダブルクリックでインストール完了 | Inno Setup 製インストーラ（管理者権限自動要求） |
| 初期設定なし・インストール後すぐ利用 | `WEBUI_AUTH=False` でログイン不要（新規DB限定の公式機能。初回起動前にNSSM環境変数で設定） |
| PC再起動後も自動起動 | NSSM で Windowsサービス登録（自動起動ON） |
| 完全オフラインで利用可能 | チャット・RAG（文書アップロードQA）はインストール完了後、一切インターネット不要。Open WebUI は既定で外部通信ゼロ |
| 外部検索も利用可能（オプション） | 内蔵 DuckDuckGo 検索（APIキー不要）を初回起動時から利用可能に。SearXNG への切替も設定可能（§3.2） |

### スコープ外（v1ではやらない）

- SearXNG 本体の同梱（WindowsではDocker必須のため。設定案内のみ §3.2）
- 自社RAGエンジンの実装（Open WebUI 内蔵RAGで代替）
- コード署名（SmartScreen警告は案内で対応 §10）
- GitHub CI、自動ビルド

---

## 2. 製品概要

### 2.1 インストール処理フロー

```
[ShineosQA-Setup-1.0.0.exe（ダブルクリック）]
    │
    ├─ ① 環境チェック（Windows 10/11 64bit・ポート8080空き・RAM検出）
    ├─ ② AIモデル選択ページ（既定: qwen2.5:3b / 軽量: qwen2.5:1.5b）
    ├─ ③ Python 3.12 を {app}\python にサイレント導入（PATH汚染なし）
    ├─ ④ Ollama を公式インストーラでサイレント導入（サービス不在時はNSSMフォールバック）
    ├─ ⑤ AIモデルダウンロード（qwen2.5:3b=1.9GB + bge-m3=274MB）
    ├─ ⑥ venv 作成 → torch(CPU) → open-webui をインストール
    ├─ ⑦ NSSM でサービス「ShineosQA」登録（環境変数注入・自動起動ON）
    ├─ ⑧ サービス起動 → /health ポーリング（最大180秒）
    └─ ⑨ 完了画面＋デスクトップに「はじめに.txt」とショートカット
            │
            ▼
    [ブラウザで http://localhost:8080 → ログイン不要でチャット開始]
```

- ③〜⑥は**キャンセル可能な進捗ページ**（進捗バー＋ステップ表示）で実行。完了まで約20〜60分（Ollama本体1.5GB＋モデル1.9GBなど総DL約4GBが大半）
- 全ステップの詳細ログは `{app}\install.log` に記録
- 各ステップは冪等（再実行時はスキップ/上書き）

### 2.2 利用モード

| モード | 動作 | 既定 |
|--------|------|------|
| 完全オフライン | チャット＋RAG（文書アップロードQA）。外部通信ゼロ | **既定動作** |
| Web検索（DuckDuckGo） | チャット入力欄のWeb検索ボタンONで利用。APIキー不要 | 機能は有効化済み・ボタンは既定OFF |
| SearXNG 切替 | サービス環境変数で切替（§3.2） | 切替手順のみ提供 |

### 2.3 環境変数（サービス注入分）

サービス「ShineosQA」には NSSM の `AppEnvironmentExtra` で以下を注入する（**システム環境変数は一切変更しない** → setx不使用・アンインストールが完全）：

| 変数 | 値 | 理由 |
|------|-----|------|
| `DATA_DIR` | `{app}\data` | 未設定だとデータがパッケージ内に入るため必須 |
| `WEBUI_SECRET_KEY` | ランダム64文字（インストール毎に生成） | 認証セッション固定化・再現性 |
| `WEBUI_AUTH` | `False` | ログイン不要（新規DB限定。再インストール時はdataを初期化） |
| `ENABLE_SIGNUP` | `False` | アカウント登録画面を出さない |
| `OLLAMA_BASE_URL` | `http://127.0.0.1:11434` | ローカルOllamaを明示 |
| `RAG_EMBEDDING_ENGINE` | `ollama` | 埋め込みをOllamaに切替（torch実行回避） |
| `RAG_EMBEDDING_MODEL` | `bge-m3` | ローカル埋め込みモデル |
| `ENABLE_WEB_SEARCH` | `True` | DuckDuckGo検索を利用可能に |
| `WEB_SEARCH_ENGINE` | `duckduckgo` | APIキー不要のプロバイダ |

---

## 3. モデル選定

### 3.1 選定結果

| | **既定: qwen2.5:3b** | 高品質: qwen2.5:7b | 軽量: qwen2.5:1.5b |
|---|---|---|---|
| サイズ | 1.9GB | 4.7GB | 1.0GB |
| 8GB機 | 快適 | 非推奨 | 快適 |
| 16GB以上 | 快適 | 快適 | — |
| ライセンス | Apache 2.0（商用可・無償・同梱可） | 同左 | 同左 |
| 特徴 | CPU専用PCで1秒前後の高速応答・日本語実用レベル | 日本語品質が高い | 最軽量・さらに高速 |

選定根拠:
1. **業務利用のライセンス**: qwen2.5系・qwen3.5系は Apache 2.0（Ollamaレジストリ・HuggingFaceで確認）。商用利用・再配布・同梱が無償で可能
2. **CPU専用PCでの実測**: qwen2.5:3b は「日本の首都は？」に0.7〜1.6秒で正答。qwen3:4b（思考型）は思考モードがOllamaのテンプレートで無効化できず、回答まで30秒以上〜数百トークンの思考が続き**採用不可**。qwen3.5:4b は Open WebUI のモデル設定 API で `think:false` を適用すれば思考なしで回答することを実機で確認（reasoning_len: 0、約7.5t/s）したが、**選択肢をシンプルに保つため v1.0.31 で上級オプションを廃止し3択に整理**（2026-08-23）
3. **日本語・性能**: qwen2.5系は小規模でも日本語QAの実用下限を満たす。業務利用は「文書検索→引用付き回答」の抽出型RAG QAを主用途と想定

### 3.2 検索エンジンの切替（SearXNG）

DuckDuckGo はレート制限（1時間あたりのリクエスト上限）があり、大量利用には不向き。SearXNG に切替える場合（自己運用のSearXNGインスタンスが必要。WindowsではDocker必須のため同梱しない）:

1. `{app}` をエクスプローラで開く
2. 管理者PowerShellで:
   ```
   cd "C:\Program Files\ShineosQA"
   .\tools\nssm.exe set ShineosQA AppEnvironmentExtra "DATA_DIR=C:\Program Files\ShineosQA\data" "WEBUI_AUTH=False" "OLLAMA_BASE_URL=http://127.0.0.1:11434" "RAG_EMBEDDING_ENGINE=ollama" "RAG_EMBEDDING_MODEL=bge-m3" "ENABLE_WEB_SEARCH=True" "WEB_SEARCH_ENGINE=searxng" "SEARXNG_QUERY_URL=http://127.0.0.1:8888/search?q=<query>"
   ```
3. `.\tools\nssm.exe restart ShineosQA`

---

## 4. 技術前提（検証済みの事実・2026-08-20時点）

公式ドキュメント・ソースコードで確認済み。**元の提案文書から修正した点を含む**:

| 項目 | 事実 | 元文書からの修正 |
|------|------|------------------|
| Open WebUI v0.11.0 | pip導入は Python 3.11/3.12 のみ（3.13不可） | — |
| torch | 実質ハード依存（Windows既定pipでCUDA版2.5GB超）。**CPU版を先に導入すれば回避**（`pip install torch --index-url https://download.pytorch.org/whl/cpu`） | サイズ対策を追加 |
| WEBUI_AUTH | 現行でも有効。**新規DB（ユーザー0）限定**。フロントエンドがsignin APIを呼ぶ既知の不具合あり（未解決・要因はCookie残存） | 「初期設定後に変更不可」→「新規DB限定」に修正 |
| ポート | `open-webui serve` は `PORT` 環境変数が効かず **`--port 8080` フラグ必須** | **3000→8080に修正**（3000はDockerマッピング） |
| DATA_DIR | 未設定だと**パッケージ内**にデータが入る（Windowsでは必須設定） | 必須設定として追加 |
| ヘルスチェック | `GET /health`（認証不要）→ 起動確認に使用 | — |
| RAG | 内蔵RAG＋`RAG_EMBEDDING_ENGINE=ollama`でOllama埋め込みに切替可 → torch実行回避・自前RAGエンジン不要 | 自社RAGエンジン実装を廃止 |
| Web検索 | `ENABLE_WEB_SEARCH` / `WEB_SEARCH_ENGINE` 環境変数が存在（ソース確認）。DuckDuckGoはAPIキー不要 | 「DuckDuckGo API」表記を修正（公式APIは存在しない） |
| qwen2.5 / qwen3.5 | `qwen2.5:3b`=1.9GB / `qwen2.5:1.5b`=1.0GB / `qwen2.5:7b`=4.7GB がOllamaライブラリに存在。**Apache 2.0**。qwen3:4b は思考モード無効化不能のため不採用、qwen3.5:4b は think:false 適用で採用（v1.0.30） | モデルをqwen3系→qwen2.5系に変更（v1.0.24）、qwen3.5:4b を上級オプションとして追加（v1.0.30） |
| ツール無効化 | Open WebUI はモデルに builtin ツール（write_note 等 34個）を提供する。**モデルがツール呼び出しを選ぶとチャットにテキスト回答が残らず「応答なし」になる**（2026-08-22 実機検証）。同名（id=base_model_id）のモデル設定は 0.10.x でマージされず無効化が効かないため、**別名カスタムモデル「社内知恵袋」**（base_model_id=実モデル）として builtinTools 全無効 + capabilities.tools=false + think:false を適用する | v1.0.30 で対応。DEFAULT_MODELS=社内知恵袋 |
| Ollama | `OllamaSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` 有効（winget実績）。公式DL URL 有効。MIT | — |
| NSSM | 2.24・パブリックドメイン。`AppEnvironmentExtra KEY=VALUE` でサービスに環境変数注入可 | setx方式を廃止（環境変数汚染ゼロ） |

---

## 5. ファイル構成

```
shineos-qa-assistant/                      # 本リポジトリ（Shineos/shineos-qa-assistant）
├── README.md                          # 概要・方針・利用モード・ライセンス
├── LICENSE                            # MIT（株式会社シャイオス）
├── docs/
│   ├── build.md                       ← 本ドキュメント
│   └── user-guide.md                  # ユーザー向けマニュアル（配布物に同梱）
├── installer/
│   └── installer.iss                  # Inno Setup 6.7.3 スクリプト（本体）
├── scripts/
│   ├── preflight.ps1                  # OS/ポート/RAMチェック → preflight.ini
│   ├── setup_python.ps1               # Python 3.12 → {app}\python
│   ├── setup_ollama.ps1               # Ollama導入＋サービス確認/フォールバック
│   ├── setup_openwebui.ps1            # -Mode models: モデルDL / -Mode app: venv+torch+open-webui
│   ├── register_service.ps1           # NSSM登録（環境変数込み）・自動起動・開始
│   ├── wait_ready.ps1                 # /health ポーリング
│   └── start_openwebui.bat            # 手動起動用（デバッグ・サービス停止時の代替）
├── assets/
│   └── app.ico                        # インストーラ/ショートカット用アイコン
└── vendor/
    ├── nssm.exe                       # NSSM 2.24 win64（公式nssm.cc・パブリックドメイン）
    ├── THIRD-PARTY-NOTICES.txt        # 同梱・導入コンポーネントのライセンス一覧
    └── README.md                      # ベンダーバイナリの出典・更新手順
```

### インストール後の配置（`{app}` = `C:\Program Files\ShineosQA`）

```
{app}\
├── python\                      # Python 3.12（自己完結・PATH汚染なし）
├── venv\                        # open-webui 実行環境（torch CPU）
├── data\                        # Open WebUI データ（DB・アップロード文書）
├── logs\                        # openwebui.log / ollama.log / install.log
├── tools\nssm.exe               # サービス管理用（移動・削除しないこと）
├── start_openwebui.bat          # 手動起動用
└── assets\app.ico
```

---

## 6. ビルド手順（Windows）

### 6.1 前提

- Windows 10/11（64bit）の開発機
- [Inno Setup 6.7.3](https://jrsoftware.org/isinfo.php) 導入済み
  （`winget install --id=JRSoftware.InnoSetup -e` でも可）
- ⚠️ **Inno Setup 7系は不使用**: 7系は API が変更され（`Add` が1引数化など）、かつ
  ISCC が「Non-commercial use only」と表示するため商用利用にライセンスが必要になる
  可能性がある。6系（6.7.3）は商用利用が無償で、本スクリプトは6系APIで書かれている

### 6.2 コンパイル

```
# コマンドライン（Inno Setup 付属の ISCC.exe）
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\installer.iss
```

または GUI の Inno Setup で `installer\installer.iss` を開いて「Compile」。

出力: `shineos-qa-assistant\dist\ShineosQA-Setup-1.0.0.exe`

### 6.3 ビルド前チェックリスト

- [ ] `installer.iss` の `#define AppVersion` / `OutputBaseFilename` が新しいバージョンになっている
- [ ] 必要に応じて `#define OpenWebuiVersion` を最新安定版に更新（§7.2）
- [ ] `vendor\nssm.exe` が存在する（更新手順は vendor/README.md）
- [ ] Windows 10/11（64bit）のクリーン環境でテスト（§8）

---

## 7. 更新・保守

### 7.1 バージョン更新（ユーザー向け）

バージョンはインストーラの `AppVersion` と `OutputBaseFilename` で管理。配布時は GitHub Releases 等に
### 7.1 バージョン更新（リリース手順）

1. `installer.iss` の `#define MyAppVersion`（および `OutputBaseFilename`）を更新
2. main に push し、`git tag v<新バージョン> && git push origin v<新バージョン>`
3. GitHub Actions が自動ビルドし、GitHub Releases に `ShineosQA-Setup-<version>.exe` を公開する（§10.1）
4. README のバージョンバッジ（shields.io）は自動更新される。ユーザーガイドのバージョン表記は必要に応じて更新

### 7.2 open-webui バージョンの更新

`installer.iss` の `#define OpenWebuiVersion` を更新する（`setup_openwebui.ps1` は .iss から `-OpenWebuiVersion` 引数で受け取るため、.iss の更新で反映される。スクリプト内の既定値も念のため同期）。

```
pip index versions open-webui   # 最新安定版の確認（要Windows/Python）
```

更新時は以下を必ず確認する:
- WEBUI_AUTH の動作が変わっていないか（新規DB限定の制約が続いているか）
- 必要Pythonバージョン（3.11/3.12）が変わっていないか

### 7.3 Python バージョンの更新

`installer.iss` の `#define PythonVersion` を更新する（`setup_python.ps1` は `-Version` 引数で受け取る。スクリプト内の既定値も念のため同期）。
Python は **python.org の NUGet パッケージ（zip・ポータブル）** で導入する:
`https://www.nuget.org/api/v2/package/python/<version>`
（MSIインストーラは使わない。アンインストール/再インストールを繰り返しても
「バンドル登録の残りで何も展開されない」問題が発生しないため）

### 7.4 モデルの追加・変更

- モデル選択ページの選択肢は `installer.iss` の [Code]（`ModelPage.Add`）と `setup_openwebui.ps1` の `-Model` 引数で管理
- 追加時はメモリ試算（§3.1）とモデルライセンスを確認すること

---

## 8. テストチェックリスト（Windows実機）

> ⚠️ 本ドキュメント作成時点（2026-08-20）は macOS 上での実装・静的検証のみ実施済み。
> **以下は Windows 実機での検証必須項目**。リリース前に全て通過させること。

### テスト環境

| 項目 | 仕様 |
|------|------|
| OS | Windows 11 Pro（22H2以降）× 2台以上（8GB RAM機・16GB RAM機） |
| ストレージ | 空き容量15GB以上（モデル3.4GB＋venv約2GB＋Python等） |
| ネットワーク | インストール時のみ（Ollama本体1.5GB＋モデル3.4GB等、総DL約6GB） |

### 検証項目

**T1. インストール正常系（8GB機・16GB機 各1台以上）**
- [ ] exeダブルクリック → UACの管理者権限要求が出る
- [ ] モデル選択ページでRAMが正しく表示される（検出値±2GB）
- [ ] 既定（qwen2.5:3b）でインストールがエラーなく完了（20〜60分）
- [ ] 完了画面が表示され、デスクトップに「社内知恵袋」ショートカットと「ShineosQA-はじめに.txt」が作成される
- [ ] ショートカットでブラウザが開き、**ログイン画面なしで**チャット画面が表示される
- [ ] モデル「qwen2.5:3b」が選択でき、日本語の応答が返る

**T2. RAG機能**
- [ ] チャットの「+」→ PDFアップロード → 内容に関する日本語の質問 → 文書に基づく回答と引用が返る
- [ ] アップロード中もチャットが動作する（埋め込みがOllama経由で動いている）

**T3. 自動起動（再起動テスト）**
- [ ] PC再起動後、サービス「ShineosQA」が自動起動し、http://localhost:8080 にアクセスできる
- [ ] ※ Ollama が起動していない場合の症状確認（サービス「Ollama」または「ShineosOllama」の状態を確認。手動起動 `sc start Ollama` で復旧することを確認）

**T4. Web検索（DuckDuckGo）**
- [ ] チャットのWeb検索ボタンON → 最新ニュースの質問 → 検索結果を反映した回答が返る（レート制限時は空回答になる既知の制約を確認）

**T5. 完全オフライン動作**
- [ ] ネットワーク切断状態でPC起動 → チャット・RAGが正常動作（Web検索ボタンOFFのまま）

**T6. アンインストール**
- [ ] 設定 → アプリ → 社内知恵袋 → アンインストール
- [ ] サービス「ShineosQA」が停止・削除されている（`sc query ShineosQA` がエラー）
- [ ] `C:\Program Files\ShineosQA` が完全に削除されている
- [ ] システム環境変数に残留物がない（setx不使用のため）
- [ ] デスクトップのショートカット・はじめに.txt が削除されている
- [ ] ※ ユーザーの `.ollama` モデルは残す仕様（削除しない）

**T7. 再インストール・失敗復旧**
- [ ] インストール後に再実行 → 冪等に完了し、data が初期化されログイン不要のまま動く
- [ ] モデルDL中にネットワーク切断 → エラー表示と install.log の記録 → 再実行で復旧

### 検証成功基準（元提案書の基準を踏襲）

1. インストール成功率100%（10台の異なるWindows環境）
2. 初回起動時、アカウント登録なしでチャット画面が表示される
3. RAG: PDFアップロード→質問→回答生成が正常動作
4. PC再起動後、サービスが自動起動し利用可能
5. アンインストールでサービス・ファイルが完全削除

---

## 9. 既知の制約・リスク

| リスク | 内容 | 対策 |
|--------|------|------|
| SmartScreen警告 | テスト証明書（自己署名）のため「認識されないアプリ」と表示される | README・ユーザーガイドに「詳細情報→実行」「ブロック解除」の案内を明記。本番リリースは実証明書へ切替 |
| 8GB機の性能 | qwen2.5:3b はメモリ余裕少でも高速応答（実測0.7〜1.6秒）。負荷時は1.5b（軽量）へ | モデル選択ページ・ユーザーガイドで1.5b（軽量）への切替を案内。再インストールで変更可 |
| WEBUI_AUTHの既知不具合 | ブラウザのCookie残存時にsignin APIが400になるケース（未解決） | 新規DB（毎回data初期化）＋シークレットウィンドウの案内をユーザーガイドに記載 |
| Ollamaの自動起動 | 公式サービスの再起動時挙動は環境依存。フォールバック（ShineosOllama）を用意 | T3の再起動テストで確認。問題時は `sc start Ollama` |
| DuckDuckGoのレート制限 | 大量検索で空回答（Open WebUIの既知issue） | ユーザーガイドに記載。SearXNG切替手順（§3.2）を提供 |
| インストール時間 | 20〜60分（Ollama本体1.5GB＋モデルDL2.2GBなど総DL約4GBが大半） | 進捗ページに時間表示。ダウンロード失敗時は再実行で復旧（冪等） |
| 過去の不完全なPython導入 | 旧版のTargetDir不具合やMSIバンドル登録の残りで「導入済み扱いになり何も展開されない」問題があった | v1.0.16で**ポータブル方式（NUGet zip）に切替**。インストーラ・登録が無いため構造的に解消 |
| 再インストールでデータ初期化 | {app}\data を削除するためアップロード文書は消える | ユーザーガイド・はじめに.txtに明記 |
| アンインストール後にOllama本体が残る | モデル（.ollama）はユーザーデータとして残す仕様 | ユーザーガイドに明記（完全削除は `ollama` コマンド等で手動） |

---

## 10. 公開・配布

### 10.1 配布フロー（GitHub Actions 自動リリース）

exe のビルドは **GitHub Actions が自動実行**する（`.github/workflows/release.yml`）:

1. バージョンタグを push（例: `git tag v1.0.0 && git push origin v1.0.0`）
2. ワークフローが Windows ランナーで Inno Setup 6.7.3 を導入 → `ISCC.exe installer\installer.iss` でビルド
3. 成功すると **GitHub Releases に `ShineosQA-Setup-<version>.exe` が自動添付**される

手動ビルド（workflow_dispatch）でビルドのみ行いアーティファクト確認も可能。

配布ページ（Releases）に以下を明記する:
- 社内Q&Aツールの説明・問い合わせ先（https://shineos.com/contact/）
- 動作要件（Windows 10/11 64bit・8GB RAM以上・空き15GB・インストール時にネット接続）

### 10.2 コード署名（SignPath.io）

exe はビルド後に **SignPath.io でコード署名**され、署名済みファイルが Releases に公開される。

**署名方式**: SignPath REST API への**直接リクエスト**（`scripts/sign_with_signpath.ps1`）。
GitHubコネクタ（Trusted Build System）方式は組織へのコネクタ設定が必須だが、
それはAPIから設定できずコンソール専用（プラン制限の可能性もある）のため、直接方式にした
（2026-08-21 実証済み: osslsigncode で署名者・ダイジェスト一致を確認）。

**必要な設定**（リポジトリ Settings > Secrets and variables > Actions）:

| 種別 | 名前 | 値 |
|------|------|-----|
| Secret | `SIGNPATH_ORG_ID` | SignPath 組織ID |
| Secret | `SIGNPATH_API_TOKEN` | SignPath プロジェクトの API トークン |
| Variable | `SIGNPATH_PROJECT_SLUG` | `shineos-qa-assistant`（APIで作成済み・設定済み） |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | `ShineosQA-SigningPolicy`（コンソールで作成済み） |

**SignPath側の設定状況（2026-08-21）**:
- プロジェクト `社内知恵袋`（slug: `shineos-qa-assistant`）— APIで作成済み
- アーティファクト設定 `initial`（既定・有効）: `ShineosQA-Setup-*.exe` を Authenticode 署名
- 自己署名テスト証明書（`Shineos Inc.`・RSA4096・2029年まで）— コンソールで作成済み
- 署名ポリシー `ShineosQA-SigningPolicy` — コンソールで作成済み（Submitter: 管理者）

> **⚠️ v1.0.32（リポジトリ名変更）時の注意**:
> インストーラのファイル名が `ShineosLocalAI-Setup-*.exe` → `ShineosQA-Setup-*.exe` に変わったため、
> **SignPath プロジェクトのアーティファクト設定（ファイル名パターン）と GitHub Variables
> （`SIGNPATH_PROJECT_SLUG` / `SIGNPATH_SIGNING_POLICY_SLUG`）を新名前に更新するまで、
> 署名が `ProcessingFailed` になり GitHub Release が作成されません**（2026-08-23 実機確認）。
> SignPath コンソール → プロジェクト設定 → Artifact Configuration でパターンを
> `ShineosQA-Setup-*.exe` に変更し、GitHub リポジトリ設定 → Secrets and variables → Actions で
> スラッグを確認・更新したうえで、workflow_dispatch またはタグ push で再実行してください。

**署名フロー**（workflow 内の `sign_with_signpath.ps1`）:
1. `ISCC.exe` でビルド（未署名）
2. `POST /SigningRequests/SubmitWithArtifact` で署名リクエスト送信
3. 完了までポーリング（最大600秒・マルウェアスキャン含む）
4. `GET .../SignedArtifact` で署名済みexeを取得 → 未署名exeと差し替え → Releases にアップロード

**注意**:
- **テスト証明書（自己署名）はOS・ブラウザから信頼されない**（SmartScreenは「発行元不明」表示）。
  パイプライン検証用。一般公開リリースには**実コード署名証明書**が必要
- API トークンをチャット等で共有した場合は、SignPath コンソールで**ローテーション**を推奨
- マルウェアスキャンが各署名で実行されるため、完了まで数十秒〜数分かかる

### 10.3 実コード署名証明書への移行（本番リリース用）

SmartScreenで「発行元: Shineos Inc.」と認識されるようにする手順。

**選択肢A: SignPath Foundation のOSS無料プログラム（費用ゼロ・推奨）**

- SignPathはオープンソースプロジェクト向けに**無料のコード署名を提供**し、**証明書もプロジェクトごとに提供**している
  （https://www.signpath.io/solutions/open-source-community 、200以上のOSSプロジェクトが利用中）
- 申請: 同ページの「Join the community」または info@signpath.io にプロジェクト情報（リポジトリ・ライセンス・用途）を送付 → 審査
- 注意: 審査があり承認まで時間がかかる場合がある。本リポジトリは公開・MITライセンスで形式上の資格はある
- 承認後: 提供された証明書で署名ポリシーを作成 → 変数切替 → リリース（ステップ4〜6と同じ）

**選択肢B: 実コード署名証明書の購入（有料）**

**ユーザー（コンソール・CA）のみで実施可能なステップ**（証明書操作はAPI未公開のため）:

1. SignPathコンソール → **Certificates → Add certificate → CSR（certificate signing request）を発行**
   （秘密鍵はSignPathのHSM上に生成される。CSRファイルをダウンロード）
2. コード署名証明書をCAから購入（OV: 年額2〜4万円目安 / EV: 年額4〜8万円目安。DigiCert・SSL.com・Sectigo等。
   会社情報の確認が必要で発行に1〜5日程度）
3. 発行された証明書を SignPath コンソール → **Certificates → Import** でアップロード

**証明書ができた後のステップ（APIで実施可能・自動化）**:

4. 実証明書のslugを確認 → 署名ポリシー `release-signing` を作成（Submitterは既存ポリシーと同一）
5. GitHub Variables の `SIGNPATH_SIGNING_POLICY_SLUG` を `release-signing` に切替
6. タグpush → 署名済みexeを osslsigncode で検証（発行元がCAのものになっていることを確認）

**推奨**: OV証明書で十分（発行元表示＋SmartScreenの評判向上）。EVは評判の初期獲得に有利だが費用が高い。

---

## 11. ライセンス

| コンポーネント | ライセンス | 備考 |
|---------------|-----------|------|
| 本プロジェクトのコード（.iss/.ps1/.bat等） | MIT | — |
| Ollama | MIT | インストーラが自動導入 |
| Open WebUI | BSD-3-Clause | pipで導入 |
| qwen2.5（3b/1.5b） | Apache 2.0 | 商用・再配布可 |
| bge-m3 | Apache 2.0 | 同上 |
| NSSM | パブリックドメイン | vendor/nssm.exe として同梱 |

詳細は `shineos-qa-assistant/vendor/THIRD-PARTY-NOTICES.txt` を参照。
