import { describe, expect, it } from 'vitest';
import { shouldAppendAssistantBubble } from './chat';

// clarify は clarify イベントが選択肢カードを描画済み。done で空のアシスタント吹き出しを
// 追加していた不具合（v2.6.2 で実害）の再発防止。
describe('shouldAppendAssistantBubble', () => {
  it('clarify は吹き出しを足さない（カード描画済み）', () => {
    expect(shouldAppendAssistantBubble('clarify', '')).toBe(false);
    expect(shouldAppendAssistantBubble('clarify', 'CLARIFY:ST-1042A')).toBe(false);
  });

  it('本文が空なら clarify 以外でもカードを作らない', () => {
    expect(shouldAppendAssistantBubble('refusal', '')).toBe(false);
    expect(shouldAppendAssistantBubble(undefined, '   \n')).toBe(false);
  });

  it('本文があれば通常回答・拒否回答とも追加する', () => {
    expect(shouldAppendAssistantBubble('answer', '回答本文')).toBe(true);
    expect(shouldAppendAssistantBubble('refusal', '該当する記載がありません')).toBe(true);
    expect(shouldAppendAssistantBubble(undefined, '旧バックエンドの回答')).toBe(true);
  });
});
