# -*- coding: utf-8 -*-
"""
purge_orphan_vectors.py - ナレッジから削除した文書の検索索引（ベクトル）残骸を掃除する保守ツール

背景（既知問題・v1.0.60時点）:
  Open WebUI 0.11 では、ナレッジからファイルを削除しても検索索引（ベクトル）の一部が
  残ることがあり、削除済み文書の内容が回答され続けることがある。

使い方（管理者権限で実行。実行前に ShineosQA サービスを停止すること）:
  1. 管理者 PowerShell で sc stop ShineosQA
  2. venv の python で本スクリプトを実行
       "（インストール先）\\venv\\Scripts\\python.exe" purge_orphan_vectors.py --needles "KZ-,検証用文書"
  3. sc start ShineosQA
  --needles には「消したはずの文書に含まれる語」をカンマ区切りで指定する。
  指定した語を含むチャンクだけを削除するため、残りのナレッジは影響を受けない。
  ベクトルDBの場所（data\\vector_db）はスクリプトの設置場所から自動検出するため、
  インストール先が既定と異なっていてもそのまま動作する。
"""
import argparse
import chromadb
import json
import os
import sys


def default_vector_db_path():
    """ベクトルDBの場所を自動検出する。
    本ツールは {インストール先}\\scripts に同梱されるため、
    親ディレクトリの data\\vector_db を既定とする。
    見つからない場合（開発環境など）は既定のインストール先へフォールバックする。"""
    app_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    p = os.path.join(app_dir, 'data', 'vector_db')
    if os.path.isdir(p):
        return p
    return r'C:\Program Files\ShineosQA\data\vector_db'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--path', default=None, help='ベクトルDBの場所（未指定なら自動検出）')
    ap.add_argument('--kid', default=None, help='対象コレクション（ナレッジid）。未指定なら全コレクション走査')
    ap.add_argument('--needles', required=True, help='カンマ区切りの削除対象語（文書内文字列）')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()
    if not args.path:
        args.path = default_vector_db_path()

    needles = [n.strip() for n in args.needles.split(',') if n.strip()]
    client = chromadb.PersistentClient(path=args.path)
    if args.kid:
        collections = [client.get_collection(args.kid)]
    else:
        collections = list(client.list_collections())

    total_deleted = 0
    for c in collections:
        ids = set()
        for needle in needles:
            try:
                got = c.get(where_document={'$contains': needle}, include=[])
                got_ids = got.get('ids') or []
            except Exception as e:
                print('WARN: %s の走査に失敗: %s' % (c.name, e))
                got_ids = []
            for i in got_ids:
                ids.add(i)
        if not ids:
            continue
        idl = sorted(ids)
        if args.dry_run:
            print('%s: 削除候補 %d 件（dry-run）' % (c.name, len(idl)))
        else:
            for i in range(0, len(idl), 200):
                c.delete(ids=idl[i:i + 200])
            print('%s: 削除 %d 件（残 %d）' % (c.name, len(idl), c.count()))
        total_deleted += len(idl)
    print('合計削除（候補）: %d 件' % total_deleted)
    print('完了後は sc start ShineosQA でサービスを起動してください。')


if __name__ == '__main__':
    main()
