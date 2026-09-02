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
  4. RAG: 「出張時の宿泊費の上限は？」→ 15,000円 かつ 出典(sources)付き（180秒以内）
  5. ガードレール: 「寮に入居したい」→ 拒否文言 かつ 架空文書名なし
  5b. ナレッジ網羅性: 「PCの貸出申請は？」→ 窓口3階（QA_list 未登録回帰の検知・v1.0.75）
  6. 埋め込み: bge-m3 が5秒以内に応答する（v1.0.74）
  7. 資料作成: /pdf・/pptx・/docx（--quick でスキップ可能）
  8. 代表質問セット（--eval 時のみ）: 手順/可否/固有名詞/複合質問の対象外混入（v1.0.74）

使い方:
  python smoke_test.py --base-url http://localhost:8080
  python smoke_test.py --eval --quick   # 代表質問込み・資料作成スキップ
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
    # SSRF対策: ローカルの社内知恵袋 API のみに接続する
    u = urllib.parse.urlparse(url)
    if u.scheme != 'http' or u.hostname not in ('127.0.0.1', 'localhost'):
        raise ValueError('blocked non-local url: %r' % url)
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


def chat_stream(base_url, token, question):
    """ストリーミングで回答し、初回トークンまでの時間（TTFT）と完了時間を返す（v1.0.74）"""
    url = base_url.rstrip('/') + '/api/chat/completions'
    u = urllib.parse.urlparse(url)
    if u.scheme != 'http' or u.hostname not in ('127.0.0.1', 'localhost'):
        raise ValueError('blocked non-local url: %r' % url)
    body = json.dumps({
        'model': '社内知恵袋',
        'messages': [{'role': 'user', 'content': question}],
        'stream': True,
    }, ensure_ascii=False).encode('utf-8')
    req = urllib.request.Request(url, data=body, method='POST', headers={
        'Content-Type': 'application/json',
        'Authorization': 'Bearer ' + token,
    })
    t0 = time.time()
    ttft = None
    text = ''
    with urllib.request.urlopen(req, timeout=420) as resp:
        for raw in resp:
            line = raw.decode('utf-8').strip()
            if not line.startswith('data:') or line == 'data: [DONE]':
                continue
            try:
                chunk = json.loads(line[5:].strip())
            except Exception:
                continue
            delta = (chunk.get('choices') or [{}])[0].get('delta') or {}
            content = delta.get('content') or ''
            if content:
                if ttft is None:
                    ttft = time.time() - t0
                text += content
    return text, ttft, time.time() - t0


def embed_latency():
    """ローカル Ollama の埋め込み遅延を計測する（bge-m3・CPU実測で約0.1秒）"""
    url = 'http://127.0.0.1:11434/api/embed'
    body = json.dumps({'model': 'bge-m3', 'input': '出張時の宿泊費の上限は？'}, ensure_ascii=False).encode('utf-8')
    req = urllib.request.Request(url, data=body, method='POST',
                                 headers={'Content-Type': 'application/json'})
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=60) as resp:
        data = json.loads(resp.read().decode('utf-8'))
    return time.time() - t0, len(data.get('embeddings') or [])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--base-url', default='http://localhost:8080')
    ap.add_argument('--email', default='admin@localhost')
    ap.add_argument('--password', default='admin')
    ap.add_argument('--eval', action='store_true',
                    help='代表質問セット（手順/可否/固有名詞/複合）も実行する（約1〜2分追加）')
    ap.add_argument('--quick', action='store_true',
                    help='資料作成テスト（各1〜2分）をスキップする')
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
    dur4 = time.time() - t0
    # 遅延上限（v1.0.74）: 致命的な回帰（RAG注入停止・モデル不読込等）のみ検出する緩めの閾値
    check('RAG: 根拠付き回答（15,000円）', '15,000' in ans and src >= 1 and dur4 < 180,
          f'{dur4:.0f}s sources={src}')

    # 5) ガードレール（幻覚引用なし）
    # v1.0.63 以降の拒否文言は「対象外」に統一されている（旧文言も許容する）
    print('--- 5. ガードレール（約10〜40秒） ---')
    t0 = time.time()
    ans2, _ = chat(args.base_url, tok, '寮に入居したいのですが、手続きを教えてください。')
    ok5 = (('該当する記載がありません' in ans2) or ('対象外' in ans2)) and ('寮管理規程' not in ans2)
    check('ガードレール: 拒否＋架空引用なし', ok5, f'{time.time()-t0:.0f}s')

    # 5b) ナレッジ網羅性（v1.0.75）: QA_list 固有の固有名詞（窓口の3階）で検査する。
    # サブフォルダ追加時にルートの QA_list.md が未登録になる回帰を
    # 新規インストール時に検知するための既定チェック
    print('--- 5b. ナレッジ網羅性（約10〜40秒） ---')
    t0 = time.time()
    ans_qa, _ = chat(args.base_url, tok, 'PCの貸出申請はどうすればいいですか？')
    check('ナレッジ網羅性: QA_list（窓口3階）', '3階' in ans_qa, f'{time.time()-t0:.0f}s')

    # 6) 埋め込み遅延（v1.0.74）: bge-m3 は CPU 実測で約0.1秒。5秒超はモデル不読込等の異常
    print('--- 6. 埋め込み遅延 ---')
    try:
        dt, n_emb = embed_latency()
        check('埋め込み（5秒以内）', dt < 5.0 and n_emb == 1, f'{dt:.2f}s')
    except Exception as e:
        check('埋め込み（5秒以内）', False, str(e)[:80])

    if not args.quick:
        # 7) 資料作成（/pdf・/pptx・/docx コマンド: 目次→ナレッジ検索→資料生成。各約1〜2分）
        print('--- 7. 資料作成 PDF（約1〜2分） ---')
        t0 = time.time()
        ans3, _ = chat(args.base_url, tok, '/pdf 経費精算の手順について')
        ok6 = ('doc_' in ans3 and 'pdf' in ans3.lower() and '作成' in ans3)
        check('資料作成: PDFリンク付き回答', ok6, f'{time.time()-t0:.0f}s')

        print('--- 7b. 資料作成 PPTX（約1〜2分） ---')
        t0 = time.time()
        ans4, _ = chat(args.base_url, tok, '/pptx 経費精算の手順について')
        ok6b = 'doc_' in ans4 and '.pptx' in ans4.lower()
        check('資料作成: PPTXリンク付き回答', ok6b, f'{time.time()-t0:.0f}s')

        print('--- 7c. 資料作成 DOCX（約1〜2分） ---')
        t0 = time.time()
        ans5, _ = chat(args.base_url, tok, '/docx 経費精算の手順について')
        ok6c = 'doc_' in ans5 and '.docx' in ans5.lower()
        check('資料作成: DOCXリンク付き回答', ok6c, f'{time.time()-t0:.0f}s')

    if args.eval:
        # 8) 代表質問セット（--eval・v1.0.74）: 回帰検知のための固定ケース
        print('--- 8. 代表質問セット（約2〜4分） ---')
        for label, q, must, must_not, desc, trials in [
            # (ラベル, 質問, 必須語, 禁止語, 説明, 試行回数)
            # 試行回数 >1 は3Bモデルの回答揺らぎを吸収するための明示的設計（v1.0.74）
            ('手順: 経費精算の手順は？', '経費精算の手順は？',
             ['経費精算システム'], [], '経費精算システムにログインする手順を回答する', 1),
            ('可否: 在宅勤務はできますか？', '在宅勤務はできますか？',
             ['在宅勤務', '承認'], [], '可否質問は最初に明言して根拠（所属長の承認）を添える', 1),
            ('固有名詞: PCの貸出申請は？', 'PCの貸出申請はどうすればいいですか？',
             ['3階'], [], '固有名詞（窓口の場所）を正確に回答する', 1),
            ('複合: 宿泊費＋手順（対象外混入なし）', '出張の宿泊費の上限と、経費精算の手順を教えてください',
             ['15,000', '経費精算システム'], ['対象外'], '両方答えられる複合質問では対象外文言を出力しない（v1.0.73）', 1),
            # 新規ナレッジ（knowledge/社内規程/）のシナリオ
            ('数値: 国内出張の日当', '国内出張の日当はいくらですか？',
             ['1,500'], [], '数値を正確に回答する', 1),
            ('数値: 海外出張の日当', '海外出張の日当はいくらですか？',
             ['3,000'], [], '数値を正確に回答する', 1),
            ('条件付き可否: タクシー', 'タクシーは使えますか？',
             ['22時'], [], '条件（22時以降の帰宅）を明記して回答する', 1),
            ('期間: 忌引き', '父母が亡くなった場合、忌引きは何日ですか？',
             ['7'], [], '期間を正確に回答する', 1),
            ('ポリシー: パスワード', 'パスワードは何文字以上にしないといけませんか？',
             ['12'], [], '文字数条件を正確に回答する', 1),
            ('可否: USBメモリ', 'USBメモリは使えますか？',
             ['暗号化'], [], '条件（暗号化製品のみ許可）を明記して回答する', 2),
        ]:
            ok = False
            detail = ''
            for trial in range(trials):
                t0 = time.time()
                ans, ttft, total = chat_stream(args.base_url, tok, q)
                passed = all(m in ans for m in must) and all(m not in ans for m in must_not) and total < 120
                detail = f'TTFT {ttft:.1f}s / 完了 {total:.0f}s' + ('' if trials == 1 else f'（試行{trial + 1}/{trials}）')
                if passed:
                    ok = True
                    break
            check(label, ok, detail)

    print('==============================')
    if failures:
        print(f'結果: 不合格 ({len(failures)}項目) -> {failures}')
        print('対処: デスクトップの ShineosQA-repair または再インストールをお試しください')
        return 1
    print('結果: 全項目合格')
    return 0


if __name__ == '__main__':
    sys.exit(main())
