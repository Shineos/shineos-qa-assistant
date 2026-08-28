#!/usr/bin/env python3
"""社内知恵袋: Open WebUI のモデル一覧から特定モデルを除外するパッチ（冪等）

背景:
- Open WebUI 0.11.0 の非表示機能（meta.hidden）はワークスペース（カスタム）モデルのみ対応で、
  Ollama のベースモデル（bge-m3:latest 等）は一覧から外す方法が無い。
- そこで /api/models の生成元である get_all_models() の末尾に、
  環境変数 OLLAMA_HIDDEN_MODELS（セミコロン区切り）で指定したモデル ID を除外する
  最小限のコードを挿入する。
- チャット送信時のモデル解決に使う request.app.state.MODELS キャッシュは
  挿入箇所より前で更新されるため、既存チャット・ナレッジ処理には影響しない（表示のみ除外）。

使い方:
  python patch_openwebui_models.py [open_webui パッケージのルート]
  省略時は sys.executable から venv の site-packages を自動検出する。

終了コード: 0 = 適用済み（または適用完了）/ 1 = 失敗
"""
import os
import shutil
import sys

MARKER = "# [Shineos patch] OLLAMA_HIDDEN_MODELS"
IMPORT_ANCHOR = "import copy\n"
IMPORT_LINE = "import os\n"
# get_all_models() 末尾の「キャッシュ更新 → return」のユニークな文脈に挿入する
ANCHOR = (
    "    else:\n"
    "        request.app.state.MODELS = models_dict\n"
    "\n"
    "    return models\n"
)
FILTER_CODE = (
    "    # [Shineos patch] OLLAMA_HIDDEN_MODELS（セミコロン区切り）で指定したモデルを一覧から除外\n"
    "    # （bge-m3:latest は埋め込み用・qwen2.5:3b はカスタムモデルの土台。チャット一覧には表示しない）\n"
    "    _hidden = [m for m in os.getenv('OLLAMA_HIDDEN_MODELS', '').split(';') if m]\n"
    "    if _hidden:\n"
    "        models = [m for m in models if m.get('id') not in _hidden]\n"
)


def find_models_py(explicit=None):
    if explicit:
        p = os.path.join(explicit, "utils", "models.py")
        if os.path.isfile(p):
            return p
        raise FileNotFoundError(f"not found: {p}")
    venv = os.path.dirname(os.path.dirname(os.path.abspath(sys.executable)))
    p = os.path.join(venv, "Lib", "site-packages", "open_webui", "utils", "models.py")
    if not os.path.isfile(p):
        raise FileNotFoundError(f"not found: {p}")
    return p


def patch(path):
    with open(path, encoding="utf-8") as f:
        src = f.read()
    if MARKER in src:
        print(f"[skip] already patched: {path}")
        return 0
    if ANCHOR not in src:
        print(f"[error] anchor not found in {path}: {ANCHOR!r}")
        return 1

    if not any(line.strip() == "import os" for line in src.splitlines()[:30]):
        if IMPORT_ANCHOR not in src:
            print("[error] import anchor not found")
            return 1
        src = src.replace(IMPORT_ANCHOR, IMPORT_ANCHOR + IMPORT_LINE, 1)

    before_return = (
        "    else:\n"
        "        request.app.state.MODELS = models_dict\n"
        "\n"
    )
    src = src.replace(ANCHOR, before_return + FILTER_CODE + "    return models\n", 1)

    bak = path + ".shineos.bak"
    if not os.path.exists(bak):
        shutil.copy2(path, bak)
        print(f"[backup] {bak}")

    with open(path, "w", encoding="utf-8") as f:
        f.write(src)
    print(f"[ok] patched: {path}")
    return 0


def main():
    explicit = sys.argv[1] if len(sys.argv) > 1 else None
    try:
        path = find_models_py(explicit)
    except FileNotFoundError as e:
        print(f"[error] {e}")
        return 1
    return patch(path)


if __name__ == "__main__":
    sys.exit(main())
