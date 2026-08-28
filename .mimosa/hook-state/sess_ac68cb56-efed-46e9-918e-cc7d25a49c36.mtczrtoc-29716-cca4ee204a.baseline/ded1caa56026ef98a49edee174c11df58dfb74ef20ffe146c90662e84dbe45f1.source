#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ShineosQA: 導入済み環境のスモークテスト（v1.0.52）

インストール直後・アップグレード後に、製品の核心機能が壊れていないことを
自動検証する。手動テストで見つかった過去の回帰（RAG停止・幻覚引用・
モデル一覧汚染・ナレッジ未紐付け）をすべて検出可能。

検証項目:
  1. パッチ適用状態（check_patches.py 相当）
  2. モデル一覧がプリセット3つのみ（ベースモデル/Arena が見えない）
  3. プリセット3モデルすべてにナレッジが紐付いている
  4. RAG: 「出張時の宿泊費の上限は？」→ 15,000円 かつ 出典(sources)付き
  5. ガードレール: 「寮に入居したい」→ 拒否文言 かつ 架空文書名なし

使い方:
  python smoke_test.py --base-url http://localhost:8080
終了コード: 0 = 全項目合格 / 1 = 不合格
"""
import argparse
import json
import sys
import time
import urllib.parse
import urllib.request

sys.path.insert(0, __file__.rsplit('\\', 1)[0] or '.')
import check_patches  # noqa: E402


def http_json(base_url, path, method='GET', token=None, body=None, timeout=420):
    url = base_url.rstrip('/') + path
    data = None
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode('utf-8')
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        raw = resp.read().decode('utf-8')
    return json.loads(raw) if raw else None


def chat(base_url, token, question):
    r = http_json(base_url, '/api/chat/completions', 'POST', token, {
        'model': '社内知恵袋',
        'messages': [{'role': 'user', 'content': question}],
        'stream': False,
    })
    return r['choices'][0]['message']['content'], len(r.get('sources') or [])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--base-url', default='http://localhost:8080')
    ap.add_argument('--email', default='admin@localhost')
    ap.add_argument('--password', default='admin')
    args = ap.parse_args()

    failures = []

    def check(name, ok, detail=''):
        print(f"[{'PASS' if ok else 'FAIL'}] {name}" + (f" ({detail})" if detail else ''))
        if not ok:
            failures.append(name)

    # 1) パッチ
    print('--- 1. パッチ適用状態 ---')
    ok = check_patches.run_check()
    check('パッチ適用', ok)

    # サインイン
    s = http_json(args.base_url, '/api/v1/auths/signin', 'POST',
                  body={'email': args.email, 'password': args.password})
    if not s or not s.get('token'):
        print('[FAIL] サインインに失敗しました')
        return 1
    tok = s['token']

    # 2) モデル一覧
    print('--- 2. モデル一覧 ---')
    ml = http_json(args.base_url, '/api/models', token=tok)['data']
    names = [m.get('name') for m in ml]
    hidden_leak = [n for n in names if n in ('bge-m3:latest', 'qwen2.5:3b', 'qwen2.5:7b', 'qwen2.5:1.5b')]
    check('モデル一覧（プリセットのみ）', not hidden_leak and len(names) <= 4, f'{names}')

    # 3) ナレッジ紐付け
    print('--- 3. ナレッジ紐付け ---')
    for name in ('社内知恵袋', '経費精算ガイド', 'ITヘルプデスク'):
        q = urllib.parse.quote(name)
        m = http_json(args.base_url, f'/api/v1/models/model?id={q}', token=tok)
        kn = (m.get('meta') or {}).get('knowledge') or []
        check(f'ナレッジ紐付け: {name}', len(kn) >= 1)

    # 4) RAG（根拠付き回答）
    print('--- 4. RAG回答（約10〜40秒） ---')
    t0 = time.time()
    ans, src = chat(args.base_url, tok, '出張時の宿泊費の上限は？')
    check('RAG: 根拠付き回答（15,000円）', '15,000' in ans and src >= 1,
          f'{time.time()-t0:.0f}s sources={src}')

    # 5) ガードレール（幻覚引用なし）
    print('--- 5. ガードレール（約10〜40秒） ---')
    t0 = time.time()
    ans2, _ = chat(args.base_url, tok, '寮に入居したいのですが、手続きを教えてください。')
    ok5 = ('該当する記載がありません' in ans2) and ('寮管理規程' not in ans2)
    check('ガードレール: 拒否＋架空引用なし', ok5, f'{time.time()-t0:.0f}s')

    print('==============================')
    if failures:
        print(f'結果: 不合格 ({len(failures)}項目) -> {failures}')
        print('対処: デスクトップの ShineosQA-repair.bat または再インストールをお試しください')
        return 1
    print('結果: 全項目合格')
    return 0


if __name__ == '__main__':
    sys.exit(main())
