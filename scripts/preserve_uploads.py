#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ShineosQA: アップグレード時に画面から追加された文書を退避する（v1.0.52）

背景: 再インストール（アップグレード）では Open WebUI のデータ（DB・ベクトル）は
初期化される。{app}\\knowledge に置いた文書は自動再登録されるが、画面のナレッジ
メニューから追加した文書は data\\uploads 配下にしか存在しないため消えてしまう。

挙動: 旧 data\\webui.db の file テーブルを読み、各ファイルを実体（uploads/）から
{app}\\knowledge\\アップグレード退避\\ へコピーする。コピー後、setup_knowledge が
自動で再登録するため、ナレッジは次バージョンでも引き継がれる。

使い方: register_service.ps1 から data バックアップの前に呼ばれる。
終了コード: 0 = 成功（退避対象なし含む）/ 1 = 失敗（処理続行可能・警告のみ）
"""
import json
import os
import re
import shutil
import sqlite3
import sys

INVALID = r'[\\/:*?"<>|]'


def safe_name(name: str) -> str:
    return re.sub(INVALID, '_', (name or '').strip()) or 'unnamed'


def main():
    if len(sys.argv) < 2:
        print('usage: preserve_uploads.py <AppDir>')
        return 1
    app_dir = sys.argv[1]
    data_dir = os.path.join(app_dir, 'data')
    db_path = os.path.join(data_dir, 'webui.db')
    if not os.path.isfile(db_path):
        print(f'[skip] no previous data db: {db_path}')
        return 0

    out_dir = os.path.join(app_dir, 'knowledge', 'アップグレード退避')
    os.makedirs(out_dir, exist_ok=True)

    copied = 0
    try:
        con = sqlite3.connect(f'file:{db_path}?mode=ro', uri=True)
        cur = con.cursor()
        cur.execute('SELECT filename, path, hash FROM file')
        for filename, path, _hash in cur.fetchall():
            src = None
            if path:
                cand = path if os.path.isabs(path) else os.path.join(data_dir, path)
                if os.path.isfile(cand):
                    src = cand
            if not src:
                # 既定の配置パターン uploads/<path or id_filename> は path 列で判明する。
                # path が無い古い構成では uploads 配下を filename の末尾一致で探す
                up = os.path.join(data_dir, 'uploads')
                if os.path.isdir(up):
                    for f in os.listdir(up):
                        if safe_name(filename) and safe_name(filename) in f:
                            src = os.path.join(up, f)
                            break
            if not src:
                continue
            dst = os.path.join(out_dir, safe_name(filename))
            try:
                if not os.path.isfile(dst):
                    shutil.copy2(src, dst)
                    copied += 1
            except Exception as e:
                print(f'[warn] copy failed: {filename}: {e}')
        con.close()
    except Exception as e:
        print(f'[warn] db read failed: {e}')
        return 1
    print(f'[done] preserved files: {copied} -> {out_dir}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
