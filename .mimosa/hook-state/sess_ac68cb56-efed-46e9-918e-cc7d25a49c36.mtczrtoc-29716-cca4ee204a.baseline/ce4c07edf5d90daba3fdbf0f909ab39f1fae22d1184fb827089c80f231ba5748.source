#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ShineosQA: Open WebUI パッチの適用状態を自己診断する（v1.0.52）

背景: 本製品は Open WebUI 0.11.0 の不具合を補うため site-packages 内の
models.py / middleware.py にパッチを当てている。Open WebUI をバージョンアップ
するとパッチが外れ、RAG（社内文書検索）が静かに止まって幻覚の原因になる。

挙動: 両パッチの存在を検査し、結果を1行ずつ表示する。
終了コード: 0 = すべて適用済み / 1 = 未適用あり（アプリ側で警告表示に使用）

使い方: アプリ起動時（MainWindow）とスモークテストから呼ばれる。
"""
import os
import sys

MARKERS = [
    (
        os.path.join('utils', 'models.py'),
        "OLLAMA_HIDDEN_MODELS",
        'モデル一覧の最適化パッチ（ベースモデル非表示）',
    ),
    (
        os.path.join('utils', 'middleware.py'),
        "[Shineos patch] model knowledge -> metadata.files",
        'ナレッジRAG注入パッチ（検索結果の自動注入）',
    ),
    (
        os.path.join('utils', 'middleware.py'),
        "[Shineos patch] 空クエリ対策",
        '検索語フォールバックパッチ（空クエリ対策）',
    ),
    (
        os.path.join('env.py'),
        "[Shineos patch] no auto (Open WebUI) suffix",
        '名称表示パッチ（(Open WebUI) 重複付加の除去）',
    ),
]


def find_package_root(explicit=None):
    if explicit:
        p = os.path.join(explicit, 'open_webui')
        return p if os.path.isdir(p) else None
    # sys.executable が venv の python なら venv/Lib/site-packages を探す
    venv = os.path.dirname(os.path.dirname(os.path.abspath(sys.executable)))
    p = os.path.join(venv, 'Lib', 'site-packages', 'open_webui')
    return p if os.path.isdir(p) else None


def run_check(root=None):
    root = root or find_package_root()
    if not root:
        print('[ERROR] open_webui パッケージが見つかりません')
        return False
    ok = True
    for rel, marker, desc in MARKERS:
        path = os.path.join(root, rel)
        try:
            with open(path, encoding='utf-8') as f:
                present = marker in f.read()
        except OSError:
            present = False
        print(f"[{'OK' if present else 'NG'}] {desc}: {rel}")
        if not present:
            ok = False
    return ok


def main():
    explicit = sys.argv[1] if len(sys.argv) > 1 else None
    ok = run_check(explicit)
    print('RESULT: ' + ('HEALTHY' if ok else 'BROKEN'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
