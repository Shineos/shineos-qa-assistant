#!/usr/bin/env python3
"""社内知恵袋: /資料 コマンド（資料作成パイプライン）のパッチ（冪等）

やること:
1. tools/doc_pipeline.py を venv の site-packages へ doc_pipeline.py としてコピー
   （open_webui プロセス内から import できるようにする）
2. utils/middleware.py の process_chat_payload に /資料 トリガーの横取り処理を注入
   - 通常の Q&A 経路（ナレッジ RAG 注入・拒否システムプロンプト）の前に処理する
   - 例外時は通常チャットへフォールバック

パスはすべて実行中の Python（venv）とこのスクリプト自身の位置から決定し、
外部入力（引数・環境変数）をパス構築に使わない。

使い方:
  python patch_openwebui_doc.py

終了コード: 0 = 適用済み（または適用完了）/ 1 = 失敗
"""
import os
import shutil
import sys
from pathlib import Path

DOC_MARKER = "# [Shineos patch] document creation (/資料)"

# process_chat_payload 内のアンカー（一意）。
# extra_params 生成後・ナレッジ注入前の位置に入るため、
# 資料モードでは Q&A の RAG 注入も拒否プロンプトも適用されない。
DOC_ANCHOR = "    events = []\n    sources = []\n"

DOC_INJECT = (
    "    events = []\n"
    "    sources = []\n"
    "    # [Shineos patch] document creation (/資料)\n"
    "    try:\n"
    "        _doc_pipeline = None\n"
    "        try:\n"
    "            from doc_pipeline import handle_document_request as _doc_pipeline_fn\n"
    "            _doc_pipeline = _doc_pipeline_fn\n"
    "        except Exception:\n"
    "            _doc_pipeline = None\n"
    "        if _doc_pipeline is not None:\n"
    "            _doc_topic = ''\n"
    "            for _dm in reversed(form_data.get('messages', [])):\n"
    "                if isinstance(_dm, dict) and _dm.get('role') == 'user':\n"
    "                    _dc = _dm.get('content')\n"
    "                    if isinstance(_dc, str):\n"
    "                        _doc_topic = _dc.strip()\n"
    "                    break\n"
    "            if _doc_topic.startswith('/資料'):\n"
    "                form_data = await _doc_pipeline(\n"
    "                    request, form_data, extra_params, user, _doc_topic[4:].strip())\n"
    "                return form_data, metadata, events\n"
    "    except Exception:\n"
    "        log.exception('[Shineos] document pipeline failed; falling back to normal chat')\n"
)


def site_packages_dir():
    """実行中の venv の site-packages（インストーラは必ず venv の python で起動する）"""
    venv = os.path.dirname(os.path.dirname(os.path.abspath(sys.executable)))
    return os.path.join(venv, "Lib", "site-packages")


def find_tools_dir():
    """doc_pipeline.py のコピー元（このスクリプトと同じ階層構造の tools/）"""
    here = os.path.dirname(os.path.abspath(__file__))
    for cand in (os.path.join(os.path.dirname(here), "tools"), os.path.join(here, "tools")):
        if os.path.isfile(os.path.join(cand, "doc_pipeline.py")):
            return cand
    return None


def find_middleware():
    p = os.path.join(site_packages_dir(), "open_webui", "utils", "middleware.py")
    if not os.path.isfile(p):
        raise FileNotFoundError(f"not found: {p}")
    return p


def copy_pipeline_module():
    src_dir = find_tools_dir()
    if not src_dir:
        print("[error] doc_pipeline.py not found in tools/")
        return 1
    src = os.path.join(src_dir, "doc_pipeline.py")
    dst = os.path.join(site_packages_dir(), "doc_pipeline.py")
    changed = True
    if os.path.isfile(dst):
        with open(src, encoding="utf-8") as f:
            a = f.read()
        with open(dst, encoding="utf-8") as f:
            b = f.read()
        changed = a != b
    if changed:
        shutil.copyfile(src, dst)
        print(f"[done] copied doc_pipeline.py -> {dst}")
    else:
        print(f"[skip] doc_pipeline.py already up to date: {dst}")
    return 0


def patch_middleware():
    mw = find_middleware()
    with open(mw, encoding="utf-8") as f:
        src = f.read()
    if DOC_MARKER in src:
        print(f"[skip] already patched: {mw}")
        return 0
    if DOC_ANCHOR not in src:
        print("[error] anchor not found in middleware.py")
        print("        Open WebUI のバージョンが変わった可能性があります。DOC_ANCHOR を見直してください。")
        return 1
    shutil.copy2(mw, mw + ".shineosdoc.bak")
    Path(mw).write_text(src.replace(DOC_ANCHOR, DOC_INJECT, 1), encoding="utf-8", newline="")
    print(f"[done] patched: {mw}")
    return 0


def main():
    worst = 0
    worst = max(worst, copy_pipeline_module())
    try:
        mw = find_middleware()
    except FileNotFoundError as e:
        print(f"[error] {e}")
        return 1
    print(f"target: {mw}")
    worst = max(worst, patch_middleware())
    return worst


if __name__ == "__main__":
    sys.exit(main())
