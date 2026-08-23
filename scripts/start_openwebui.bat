@echo off
rem ============================================================================
rem 社内知恵袋 - Open WebUI manual start (for debug / service stopped case)
rem Run this file as administrator if the service cannot be started.
rem ============================================================================
setlocal
set "APP_DIR=%~dp0"
set "DATA_DIR=%APP_DIR%data"
set "WEBUI_AUTH=False"
set "ENABLE_SIGNUP=False"
set "OLLAMA_BASE_URL=http://127.0.0.1:11434"
rem デフォルトモデルは「社内知恵袋」（configure_model.ps1 が作成する別名カスタムモデル。
rem ツール無効化済みで「応答なし」を防ぐ。素のモデルタグを指定しないこと）
set "DEFAULT_MODELS=社内知恵袋"
set "RAG_EMBEDDING_ENGINE=ollama"
set "RAG_EMBEDDING_MODEL=bge-m3"
rem チャンク 300: 情報密度を上げ、スモールモデルが扱いやすい単位にする
set "CHUNK_SIZE=300"
set "CHUNK_OVERLAP=30"
set "RAG_TOP_K=3"
rem embedding は直列処理（同期）: 非同期だとローカルでキューが詰まりナレッジ登録が停滞する
set "ENABLE_ASYNC_EMBEDDING=False"
rem ハイブリッド検索（BM25 + ベクトル）: 型番・規程番号・固有名詞を漏らさずヒットさせる
set "ENABLE_RAG_HYBRID_SEARCH=True"
set "HYBRID_BM25_WEIGHT=0.5"
rem Web 検索は既定 OFF（ON にすると質問内容が外部の検索サービスに送信されるため）
set "ENABLE_WEB_SEARCH=False"
set "WEB_SEARCH_ENGINE=duckduckgo"
set "BYPASS_WEB_SEARCH_EMBEDDING_AND_RETRIEVAL=True"
set "BYPASS_WEB_SEARCH_WEB_LOADER=True"
rem モデルが「回答をノートに書く」（write_note）関数呼び出しを選び、
rem チャットに回答が表示されなくなる問題を防ぐため Notes 機能を無効化
set "ENABLE_NOTES=False"

echo Starting 社内知恵袋 (Open WebUI)...
echo Open http://localhost:8080 in your browser after the server is up.
"%APP_DIR%venv\Scripts\open-webui.exe" serve --port 8080
endlocal
