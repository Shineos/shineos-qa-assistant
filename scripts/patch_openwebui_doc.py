#!/usr/bin/env python3
"""社内知恵袋: /doc コマンド（資料作成パイプライン）のパッチ（冪等）

やること:
1. tools/doc_pipeline.py を venv の site-packages へ doc_pipeline.py としてコピー
2. v1.0.66-67 の middleware.py 注入を除去（.shineosdoc.bak から復元）
3. main.py の process_chat に /doc トリガーの LLM バイパス処理を注入
   - 案内文を OpenAI 互換 SSE に変換して process_chat_response へ渡すため、
     最終応答に LLM を使わない（復唱省略・脚色の構造的解消）

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

MW_MARKER = "# [Shineos patch] document creation (/資料)"
MW_BACKUP_SUFFIX = ".shineosdoc.bak"

MAIN_MARKER = "# [Shineos patch] document creation (/doc)"
# process_chat の先頭（payload 処理の前）に注入する。
# payload 後は user 文が RAG テンプレートで置き換えられるため、生の質問文が
# 残っているこの位置で判定する必要がある（v1.0.68 実機検証）。
MAIN_ANCHOR = (
    "    async def process_chat(request, form_data, user, metadata, model, tasks=None):\n"
    "        try:\n"
    "            form_data, metadata, events = await process_chat_payload(request, form_data, user, metadata, model)\n"
)
MAIN_INJECT = (
    "    async def process_chat(request, form_data, user, metadata, model, tasks=None):\n"
    "        try:\n"
    "            # [Shineos patch] document creation (/doc): LLM bypass\n"
    "            try:\n"
    "                from doc_pipeline import maybe_build_doc_response as _doc_mbr\n"
    "                _doc_response = await _doc_mbr(request, form_data, user, metadata, model)\n"
    "                if _doc_response is not None:\n"
    "                    _doc_ctx = await build_chat_response_context(\n"
    "                        request, form_data, user, model, metadata, tasks, []\n"
    "                    )\n"
    "                    return await process_chat_response(_doc_response, _doc_ctx)\n"
    "            except Exception:\n"
    "                log.exception('[Shineos] /doc bypass failed; falling back to normal chat')\n"
    "\n"
    "            form_data, metadata, events = await process_chat_payload(request, form_data, user, metadata, model)\n"
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


def find_openwebui_file(rel):
    p = os.path.join(site_packages_dir(), "open_webui", rel.replace("/", os.sep))
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


def unpatch_middleware():
    """v1.0.66-67 の middleware 注入を除去（バックアップから復元）"""
    mw = find_openwebui_file("utils/middleware.py")
    with open(mw, encoding="utf-8") as f:
        src = f.read()
    if MW_MARKER not in src:
        print("[skip] middleware has no legacy /doc injection")
        return 0
    bak = mw + MW_BACKUP_SUFFIX
    if os.path.isfile(bak):
        shutil.copy2(bak, mw)
        print(f"[done] middleware restored from backup (legacy injection removed): {mw}")
        return 0
    print("[error] legacy injection found but backup missing: " + bak)
    return 1


def patch_main():
    mainpy = find_openwebui_file("main.py")
    bak = mainpy + ".shineosdoc.bak"
    with open(mainpy, encoding="utf-8") as f:
        src = f.read()
    if MAIN_MARKER in src:
        # 注入位置を変更した古いパッチがある場合は一度外して作り直す
        if os.path.isfile(bak):
            shutil.copy2(bak, mainpy)
            with open(mainpy, encoding="utf-8") as f:
                src = f.read()
            print("[fix] re-applying patch at updated position")
        else:
            print(f"[skip] already patched: {mainpy}")
            return 0
    if MAIN_ANCHOR not in src:
        print("[error] anchor not found in main.py")
        print("        Open WebUI のバージョンが変わった可能性があります。MAIN_ANCHOR を見直してください。")
        return 1
    shutil.copy2(mainpy, bak)
    Path(mainpy).write_text(src.replace(MAIN_ANCHOR, MAIN_INJECT, 1), encoding="utf-8", newline="")
    print(f"[done] patched: {mainpy}")
    return 0


def main():
    worst = 0
    worst = max(worst, copy_pipeline_module())
    worst = max(worst, unpatch_middleware())
    try:
        mainpy = find_openwebui_file("main.py")
    except FileNotFoundError as e:
        print(f"[error] {e}")
        return 1
    print(f"target: {mainpy}")
    worst = max(worst, patch_main())
    return worst


if __name__ == "__main__":
    sys.exit(main())
