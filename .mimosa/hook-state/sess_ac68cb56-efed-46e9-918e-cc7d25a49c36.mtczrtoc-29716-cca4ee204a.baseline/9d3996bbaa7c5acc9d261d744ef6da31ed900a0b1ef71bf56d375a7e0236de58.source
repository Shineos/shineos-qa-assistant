#!/usr/bin/env python3
"""社内知恵袋: Open WebUI の名称表示を調整するパッチ（冪等・v1.0.56）

背景:
- Open WebUI は WEBUI_NAME を独自名にすると自動で " (Open WebUI)" を末尾に
  付加する（env.py）。本製品ではヘッダー2行目に「powered by Open WebUI」を
  表示して帰属を明示するため、この重複する末尾付加を除去する。
- ライセンス上の帰属（attribution）は保持する: UI サブタイトル・README・
  THIRD-PARTY-NOTICES に明示済み。

使い方:
  python patch_openwebui_branding.py [open_webui パッケージのルート]
終了コード: 0 = 適用済み / 1 = 失敗
"""
import os
import shutil
import sys

MARKER = "# [Shineos patch] no auto (Open WebUI) suffix"
OLD = (
    "WEBUI_NAME = os.getenv('WEBUI_NAME', 'Open WebUI')\n"
    "if WEBUI_NAME != 'Open WebUI':\n"
    "    WEBUI_NAME += ' (Open WebUI)'\n"
)
NEW = (
    "WEBUI_NAME = os.getenv('WEBUI_NAME', 'Open WebUI')\n"
    "# [Shineos patch] no auto (Open WebUI) suffix\n"
    "# 帰属は UI サブタイトル「powered by Open WebUI」で明示するため末尾付加を無効化\n"
)


def find_env_py(explicit=None):
    if explicit:
        p = os.path.join(explicit, "env.py")
        if os.path.isfile(p):
            return p
        raise FileNotFoundError(f"not found: {p}")
    venv = os.path.dirname(os.path.dirname(os.path.abspath(sys.executable)))
    p = os.path.join(venv, "Lib", "site-packages", "open_webui", "env.py")
    if not os.path.isfile(p):
        raise FileNotFoundError(f"not found: {p}")
    return p


def main():
    explicit = sys.argv[1] if len(sys.argv) > 1 else None
    try:
        path = find_env_py(explicit)
    except FileNotFoundError as e:
        print(f"[error] {e}")
        return 1
    with open(path, encoding="utf-8") as f:
        src = f.read()
    if MARKER in src:
        print(f"[skip] already patched: {path}")
        return 0
    if OLD not in src:
        print(f"[error] anchor not found in: {path}")
        return 1
    shutil.copy2(path, path + ".shineos.bak")
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(src.replace(OLD, NEW, 1))
    print(f"[done] patched: {path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
