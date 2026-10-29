import { describe, expect, it } from 'vitest';
import { ANSWER_LANG_STORAGE_KEY, normalizeAnswerLang } from './chat';

// 回答言語（多言語対応）: UIのselect・端末保存値・/APIの answer_lang は
// ja/en/vi/zh/id/tl の6値のみ。不明値は既定jaへ倒す（バックエンドの正規化と同じ）。
describe('normalizeAnswerLang', () => {
  it('対応6言語はそのまま返す', () => {
    for (const lang of ['ja', 'en', 'vi', 'zh', 'id', 'tl'] as const) {
      expect(normalizeAnswerLang(lang)).toBe(lang);
    }
  });

  it('大文字・前後空白は正規化する', () => {
    expect(normalizeAnswerLang(' EN ')).toBe('en');
    expect(normalizeAnswerLang('Vi')).toBe('vi');
  });

  it('未対応値・空・null・undefinedは既定ja', () => {
    expect(normalizeAnswerLang('fr')).toBe('ja');
    expect(normalizeAnswerLang('')).toBe('ja');
    expect(normalizeAnswerLang(null)).toBe('ja');
    expect(normalizeAnswerLang(undefined)).toBe('ja');
  });

  it('端末保存キーは固定（既存ユーザーの設定を引き継ぐ）', () => {
    expect(ANSWER_LANG_STORAGE_KEY).toBe('shineosqa-answer-lang');
  });
});
