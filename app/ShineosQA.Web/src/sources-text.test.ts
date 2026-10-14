import { describe, expect, it } from 'vitest';
import { buildSourcesText } from './chat';

// 回答コピーの出典ブロック(画面の「出典」Detailsと同じ情報をテキスト化したもの)の契約。
// 図面は【図】図番(品名・改訂)/【図面】ファイル名、WebはURL併記 — v2.2.0統合ラインで固定。
describe('buildSourcesText', () => {
  it('通常文書はファイル名+該当箇所(空白1行化)', () => {
    const out = buildSourcesText([
      { file: '経費規程.pdf', snippet: '出張旅費 は\n交通費を　含む', kind: 'kb' },
    ]);
    expect(out).toBe('──── 出典（該当箇所） ────\n1. **経費規程.pdf**\n   出張旅費 は 交通費を 含む');
  });

  it('スニペットは110字で切り詰める', () => {
    const long = 'あ'.repeat(200);
    const out = buildSourcesText([{ file: 'a.pdf', snippet: long }]);
    expect(out).toContain('あ'.repeat(110) + '…');
    expect(out).not.toContain('あ'.repeat(111));
  });

  it('図面は【図】図番（品名・改訂）の見出しになる', () => {
    const out = buildSourcesText([
      { file: 'd.pdf', snippet: '表題欄', is_drawing: true, zuban: 'ST-1042A', hinmei: 'サポートブラケット', revision: 'B' },
    ]);
    expect(out).toContain('1. **【図】ST-1042A（サポートブラケット・改訂B）**');
  });

  it('図番が無い図面は【図面】ファイル名になる', () => {
    const out = buildSourcesText([{ file: 'scan.pdf', snippet: '', is_drawing: true }]);
    expect(out).toContain('1. **【図面】scan.pdf**');
  });

  it('Web出典はタイトル+URL併記', () => {
    const out = buildSourcesText([
      { file: 'ニュース', snippet: '概要', kind: 'web', url: 'https://example.com/a' },
    ]);
    expect(out).toContain('1. **ニュース**  https://example.com/a');
  });
});
