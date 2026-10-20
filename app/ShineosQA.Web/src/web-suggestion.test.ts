import { describe, expect, it } from 'vitest';
import { shouldShowWebSuggestion } from './chat';

/// 拒否回答への「Webで検索する」チップ表示条件（v2.4.1）
describe('shouldShowWebSuggestion', () => {
  it('refusal + suggestフラグ + Web OFF で表示', () => {
    expect(shouldShowWebSuggestion('refusal', true, false)).toBe(true);
  });
  it('Web検索ON中は表示しない（すでに検索済み）', () => {
    expect(shouldShowWebSuggestion('refusal', true, true)).toBe(false);
  });
  it('answer/clarify には出さない', () => {
    expect(shouldShowWebSuggestion('answer', true, false)).toBe(false);
    expect(shouldShowWebSuggestion('clarify', true, false)).toBe(false);
  });
  it('旧バックエンド（suggest_web無し）では非表示', () => {
    expect(shouldShowWebSuggestion('refusal', undefined, false)).toBe(false);
    expect(shouldShowWebSuggestion('refusal', false, false)).toBe(false);
  });
  it('kind無し（更に旧）でも非表示', () => {
    expect(shouldShowWebSuggestion(null, true, false)).toBe(false);
  });
});
