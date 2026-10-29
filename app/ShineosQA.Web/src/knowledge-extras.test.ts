import { describe, expect, it } from 'vitest';
import { formatJissekiValue, jissekiRoleLabel, jissekiSummaryLabel, splitFaqVariants } from './knowledge-extras';

// FAQの「別の言い方」入力: APIの variants は文字列配列なので、画面の1行入力を分割する
describe('splitFaqVariants', () => {
  it('カンマ・読点・改行で分割し、空要素は除く', () => {
    expect(splitFaqVariants('有給休暇, 休暇申請、年末年始\n夏季休暇')).toEqual([
      '有給休暇', '休暇申請', '年末年始', '夏季休暇',
    ]);
  });

  it('空文字・空白のみは空配列', () => {
    expect(splitFaqVariants('')).toEqual([]);
    expect(splitFaqVariants(' , 、\n ')).toEqual([]);
  });
});

describe('jissekiRoleLabel', () => {
  it('既知の役割は日本語ラベルへ', () => {
    expect(jissekiRoleLabel('zuban')).toBe('図番');
    expect(jissekiRoleLabel('mitumori')).toBe('見積');
    expect(jissekiRoleLabel('genka')).toBe('原価');
    expect(jissekiRoleLabel('nouki')).toBe('納期');
    expect(jissekiRoleLabel('other')).toBe('その他');
  });

  it('未知の役割はそのまま表示する', () => {
    expect(jissekiRoleLabel('custom')).toBe('custom');
  });
});

describe('jissekiSummaryLabel', () => {
  it('指標と集計の組み合わせを日本語にする', () => {
    expect(jissekiSummaryLabel('mitumori', 'sum')).toBe('見積の合計');
    expect(jissekiSummaryLabel('genka', 'avg')).toBe('原価の平均');
    expect(jissekiSummaryLabel('mitumori', 'count')).toBe('見積の件数');
  });
});

describe('formatJissekiValue', () => {
  it('金額は3桁区切り＋単位', () => {
    expect(formatJissekiValue({ aggregate: 'sum', value: 150000, unit: '円' })).toBe('150,000円');
    expect(formatJissekiValue({ aggregate: 'avg', value: 9800, unit: '円' })).toBe('9,800円');
  });

  it('件数は「N 件」', () => {
    expect(formatJissekiValue({ aggregate: 'count', value: 3, unit: '' })).toBe('3 件');
  });

  it('単位が無い金額は円として表示する（旧バックエンド互換）', () => {
    expect(formatJissekiValue({ aggregate: 'sum', value: 500, unit: '' })).toBe('500円');
  });
});
