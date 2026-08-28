#!/usr/bin/env python3
"""社内知恵袋: モデルのナレッジを常に RAG 注入するパッチ（冪等）

背景（Open WebUI 0.11.0 実機検証・v1.0.47）:
- モデル（ワークスペース）に紐付けたナレッジの自動検索は legacy
  function_calling のみ動作するが、middleware が knowledge_files を
  form_data['files'] に追加する一方、RAG 処理（chat_completion_files_handler）
  は body['metadata']['files'] だけを読むため、注入が一度も発火しない。
- ネイティブツール経路（knowledge_search 等）は小規模モデルがツール呼び出しを
  誤選択しやすく「応答なし／ファイルが見つからない」の原因になるため、
  本製品はツール無効化（builtinTools 全 false）+ legacy 注入を採る。

挙動:
- legacy 分岐の末尾で knowledge_files を metadata['files'] にも複写する。
- {id} のみの UI 保存形式を retrieval 側の collection 分岐が解釈できる
  {id, name, type:'collection'} に正規化する。

使い方:
  python patch_openwebui_rag.py [open_webui パッケージのルート]
  省略時は sys.executable から venv の site-packages を自動検出する。

終了コード: 0 = 適用済み（または適用完了）/ 1 = 失敗
"""
import os
import shutil
import sys

MARKER = "# [Shineos patch] model knowledge -> metadata.files"
MARKER2 = "# [Shineos patch] files handler diagnostics"
OLD_BUGGY_TAIL = "form_data['metadata'] = _meta"
ANCHOR = (
    "        files = form_data.get('files', [])\n"
    "        files.extend(knowledge_files)\n"
    "        form_data['files'] = files\n"
)
INJECT_CODE = (
    "        files = form_data.get('files', [])\n"
    "        files.extend(knowledge_files)\n"
    "        form_data['files'] = files\n"
    "        # [Shineos patch] model knowledge -> metadata.files（常にRAG注入させる）\n"
    "        # ※ metadata は process_chat_payload の引数（main.py が form_data.files の\n"
    "        #   参照で初期化するため、UIの無添付チャットでは list が差し替わり反映されない。\n"
    "        #   metadata 側へ明示的に積む必要がある: v1.0.47 実機検証）\n"
    "        _kn_files = []\n"
    "        for _item in knowledge_files:\n"
    "            if _item.get('legacy') or _item.get('collection_names') or _item.get('collection_name'):\n"
    "                _kn_files.append(_item)\n"
    "            elif _item.get('id'):\n"
    "                _kn_files.append({'id': _item.get('id'), 'name': _item.get('name'), 'type': 'collection'})\n"
    "        metadata['files'] = (metadata.get('files') or []) + _kn_files\n"
    "        log.debug('[Shineos] legacy knowledge branch ran: kn_files=%s metadata_files=%s', len(_kn_files), len(metadata.get('files') or []))\n"
)
ANCHOR2 = "    if files := body.get('metadata', {}).get('files', None):"
INJECT2 = (
    "    if files := body.get('metadata', {}).get('files', None):\n"
    "        # [Shineos patch] files handler: type 正規化（ナレッジ由来の {id} のみの項目を\n"
    "        #   collection として解釈させる。後段で差し替えられるケースへの二重防波堤）\n"
    "        for _si, _sit in enumerate(files):\n"
    "            if isinstance(_sit, dict) and _sit.get('id') and not _sit.get('type'):\n"
    "                files[_si] = {'id': _sit.get('id'), 'name': _sit.get('name'), 'type': 'collection'}\n"
    "        # [Shineos patch] files handler diagnostics\n"
    "        log.debug('[Shineos] files handler: n_files=%s', len(files))\n"
)
# 検索語フォールバックの修正（v1.0.48）:
# ENABLE_RETRIEVAL_QUERY_GENERATION=False のとき generate_queries が例外になり、
# フォールバックの get_last_user_message(body['messages']) が 0.11.0 の処理済み
# メッセージから空を返すため、RAG 検索が空クエリになる（実機検証）。
# metadata.user_message（main.py が格納）へ確実にフォールバックさせる。
ANCHOR3 = (
    "        if len(queries) == 0:\n"
    "        queries = [get_last_user_message(body['messages']) or '']\n"
)
INJECT3 = (
    "        if len(queries) == 0 or not any(isinstance(_q, str) and _q.strip() for _q in queries):\n"
    "            # [Shineos patch] 空クエリ対策: metadata.user_message へフォールバック\n"
    "            _um = (body.get('metadata') or {}).get('user_message')\n"
    "            if not _um:\n"
    "                _um = get_last_user_message(body.get('messages') or [])\n"
    "            if isinstance(_um, dict):\n"
    "                _um = _um.get('content')\n"
    "            if isinstance(_um, list):\n"
    "                _um = ' '.join(str(_b.get('text', '')) for _b in _um if isinstance(_b, dict))\n"
    "            if _um and isinstance(_um, str) and _um.strip():\n"
    "                queries = [_um.strip()]\n"
    "            log.debug('[Shineos] query fallback used: user_message=%s', bool(_um))\n"
)

# PPTX読込パッチ（v1.0.61）: unstructured 非同梱のため python-pptx の PptxLoader を直接使う
PPTX_MARKER = "# [Shineos patch] offline pptx loader (python-pptx)"
PPTX_ANCHOR = (
    "            elif file_content_type in [\n"
    "                'application/vnd.ms-powerpoint',\n"
    "                'application/vnd.openxmlformats-officedocument.presentationml.presentation',\n"
    "            ] or file_ext in ['ppt', 'pptx']:\n"
    "                try:\n"
    "                    from langchain_community.document_loaders import UnstructuredPowerPointLoader\n"
    "\n"
    "                    loader = UnstructuredPowerPointLoader(file_path)\n"
    "                except ImportError:\n"
    "                    log.warning(\n"
    "                        \"The 'unstructured' package is not installed. \"\n"
    "                        'Falling back to python-pptx for PowerPoint file loading. '\n"
    "                        'Install unstructured for better results: pip install unstructured'\n"
    "                    )\n"
    "                    loader = PptxLoader(file_path)"
)
PPTX_NEW = (
    "            elif file_content_type in [\n"
    "                'application/vnd.ms-powerpoint',\n"
    "                'application/vnd.openxmlformats-officedocument.presentationml.presentation',\n"
    "            ] or file_ext in ['ppt', 'pptx']:\n"
    "                # [Shineos patch] offline pptx loader (python-pptx)\n"
    "                # unstructured は同梱しないため、python-pptx ベースの PptxLoader を直接使う\n"
    "                loader = PptxLoader(file_path)"
)

# ナレッジ登録のエラーメッセージパッチ（v1.0.61）: 画像などテキスト抽出できない形式を
# 英語の技術的メッセージではなく、日本語でわかりやすく拒否する
KMSG_MARKER = "# [Shineos patch] knowledge: friendly rejection for non-text files"
KMSG_ANCHOR = (
    "    # Add content to the vector database\n"
    "    try:\n"
    "        await process_file(\n"
    "            request,\n"
    "            ProcessFileForm(file_id=form_data.file_id, collection_name=id),\n"
    "            user=user,\n"
    "            db=db,\n"
    "        )\n"
    "    except Exception as e:\n"
    "        raise HTTPException(\n"
    "            status_code=status.HTTP_400_BAD_REQUEST,\n"
    "            detail=str(e),\n"
    "        )"
)
KMSG_NEW = (
    "    # [Shineos patch] knowledge: friendly rejection for non-text files\n"
    "    # 画像などテキスト抽出できない形式は、わかりやすい日本語で拒否する\n"
    "    _lower = (file.filename or '').lower()\n"
    "    if _lower.endswith(('.png', '.jpg', '.jpeg', '.gif', '.bmp', '.webp', '.tif', '.tiff', '.svg', '.ico', '.heic')):\n"
    "        raise HTTPException(\n"
    "            status_code=status.HTTP_400_BAD_REQUEST,\n"
    "            detail='画像ファイルはナレッジに登録できません（テキストが含まれていないため）。テキストの含まれる PDF・Word・Markdown などを登録してください。',\n"
    "        )\n"
    "    try:\n"
    "        await process_file(\n"
    "            request,\n"
    "            ProcessFileForm(file_id=form_data.file_id, collection_name=id),\n"
    "            user=user,\n"
    "            db=db,\n"
    "        )\n"
    "    except Exception as e:\n"
    "        raise HTTPException(\n"
    "            status_code=status.HTTP_400_BAD_REQUEST,\n"
    "            detail='ナレッジに登録できませんでした（テキストが抽出できない形式の可能性があります）: ' + str(e),\n"
    "        )"
)


def find_openwebui_file(rel, explicit=None):
    if explicit:
        p = os.path.join(explicit, rel)
        if os.path.isfile(p):
            return p
        raise FileNotFoundError(f"not found: {p}")
    venv = os.path.dirname(os.path.dirname(os.path.abspath(sys.executable)))
    p = os.path.join(venv, "Lib", "site-packages", "open_webui", rel.replace('/', os.sep))
    if not os.path.isfile(p):
        raise FileNotFoundError(f"not found: {p}")
    return p


def apply_patch(path, marker, anchor, inject):
    """共通適用処理: バックアップ → 置換 → Path.write_text で書き込み"""
    with open(path, encoding="utf-8") as f:
        src = f.read()
    if marker in src:
        print(f"[skip] already patched ({marker[:40]}...): {path}")
        return 0
    if anchor not in src:
        print(f"[error] anchor not found in: {path}")
        print("        Open WebUI のバージョンが変わった可能性があります。ANCHOR を見直してください。")
        return 1
    shutil.copy2(path, path + ".shineos.bak")
    from pathlib import Path
    Path(path).write_text(src.replace(anchor, inject, 1), encoding="utf-8", newline="")
    print(f"[done] patched: {path}")
    return 0


def patch(path):
    with open(path, encoding="utf-8") as f:
        src = f.read()
    if MARKER in src:
        if OLD_BUGGY_TAIL in src and os.path.isfile(path + ".shineos.bak"):
            # 旧バージョンのパッチ（誤った辞書へ書くもの）が入っている場合は
            # バックアップから復元して付け直す
            print("[fix] replacing outdated shineos patch with corrected version")
            shutil.copy2(path + ".shineos.bak", path)
            with open(path, encoding="utf-8") as f:
                src = f.read()
        elif (
            "legacy knowledge branch ran" not in src
            or "query fallback used" not in src
        ) and os.path.isfile(path + ".shineos.bak"):
            # 旧適用分（診断 or 検索語フォールバック修正なし）も作り直す
            print("[fix] re-applying patch to add diagnostics")
            shutil.copy2(path + ".shineos.bak", path)
            with open(path, encoding="utf-8") as f:
                src = f.read()
        else:
            print(f"[skip] already patched: {path}")
            return 0
    if ANCHOR not in src:
        print(f"[error] anchor not found in: {path}")
        print("        Open WebUI のバージョンが変わった可能性があります。ANCHOR を見直してください。")
        return 1
    shutil.copy2(path, path + ".shineos.bak")
    patched = src.replace(ANCHOR, INJECT_CODE, 1)
    # ハンドラ側: type 正規化 + 診断ログ
    if ANCHOR2 in patched:
        patched = patched.replace(ANCHOR2, INJECT2, 1)
    # 検索語フォールバック修正（空クエリ対策）
    if ANCHOR3 in patched:
        patched = patched.replace(ANCHOR3, INJECT3, 1)
    from pathlib import Path
    Path(path).write_text(patched, encoding="utf-8", newline="")
    print(f"[done] patched: {path}")
    return 0


def main():
    explicit = sys.argv[1] if len(sys.argv) > 1 else None
    worst = 0
    print(f"target: {find_openwebui_file('utils/middleware.py', explicit)}")
    worst = max(worst, patch(find_openwebui_file('utils/middleware.py', explicit)))

    # PPTX読込パッチ（v1.0.61）: python-pptx の PptxLoader を直接使う
    try:
        pptx_path = find_openwebui_file("retrieval/loaders/main.py", explicit)
    except FileNotFoundError as e:
        print(f"[error] {e}")
        worst = 1
    else:
        print(f"target: {pptx_path}")
        worst = max(worst, apply_patch(pptx_path, PPTX_MARKER, PPTX_ANCHOR, PPTX_NEW))

    # ナレッジ登録の日本語エラーパッチ（v1.0.61）
    try:
        kn_path = find_openwebui_file("routers/knowledge.py", explicit)
    except FileNotFoundError as e:
        print(f"[error] {e}")
        worst = 1
    else:
        print(f"target: {kn_path}")
        worst = max(worst, apply_patch(kn_path, KMSG_MARKER, KMSG_ANCHOR, KMSG_NEW))

    return worst


if __name__ == "__main__":
    sys.exit(main())
