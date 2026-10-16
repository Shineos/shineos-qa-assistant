import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { onSuccessfulAnswer, shouldAskAt } from './review-prompt';

// Store評価導線の節度設計: しきい値(5回)到達で1度、以後20回ごと。
// 「あとで」=30日非表示、「✕」=恒久オフ。押しすぎないことがランキング改善の前提
describe('shouldAskAt', () => {
  it('最初の表示は累計5回、以後20回ごと', () => {
    expect(shouldAskAt(1)).toBe(false);
    expect(shouldAskAt(4)).toBe(false);
    expect(shouldAskAt(5)).toBe(true);
    expect(shouldAskAt(6)).toBe(false);
    expect(shouldAskAt(24)).toBe(false);
    expect(shouldAskAt(25)).toBe(true);
  });
});

describe('onSuccessfulAnswer (localStorage state machine)', () => {
  beforeEach(() => {
    // バナーは #chat-form の直前に挿入されるため、最小のチャット画面DOMを用意する
    document.body.innerHTML = '<div id="app"><div></div><form id="chat-form"></form></div>';
    localStorage.clear();
  });

  it('5回到達でバナーが表示される', () => {
    for (let i = 0; i < 5; i++) onSuccessfulAnswer();
    expect(document.getElementById('review-banner')).not.toBeNull();
  });

  it('4回では表示されない', () => {
    for (let i = 0; i < 4; i++) onSuccessfulAnswer();
    expect(document.getElementById('review-banner')).toBeNull();
  });

  it('「あとで」は30日間非表示(期限切れでは再表示)', () => {
    for (let i = 0; i < 5; i++) onSuccessfulAnswer();
    (document.querySelector('.rvp-later') as HTMLButtonElement).click();
    expect(document.getElementById('review-banner')).toBeNull();
    // 30日以内は何回回答しても出ない
    for (let i = 0; i < 30; i++) onSuccessfulAnswer();
    expect(document.getElementById('review-banner')).toBeNull();
    // 30日経過後は次のしきい値で再表示する
    const s = localStorage.getItem('rvp_state')!;
    localStorage.setItem('rvp_state', s.replace(/later:.*/, `later:${new Date(Date.now() - 31 * 86400000).toISOString()}`));
    for (let i = 0; i < 20; i++) onSuccessfulAnswer();
    expect(document.getElementById('review-banner')).not.toBeNull();
  });

  it('「✕」で恒久オフ', () => {
    for (let i = 0; i < 5; i++) onSuccessfulAnswer();
    (document.querySelector('.rvp-never') as HTMLButtonElement).click();
    for (let i = 0; i < 100; i++) onSuccessfulAnswer();
    expect(document.getElementById('review-banner')).toBeNull();
  });

  it('「Storeで評価」でStoreスキームを開き二度と表示しない', () => {
    let opened: string | null = null;
    vi_stubOpen((u) => { opened = u; });
    for (let i = 0; i < 5; i++) onSuccessfulAnswer();
    (document.querySelector('.rvp-open') as HTMLButtonElement).click();
    expect(opened).toBe('ms-windows-store://review/?productid=XP8C6NSGDR1N9F');
    for (let i = 0; i < 100; i++) onSuccessfulAnswer();
    expect(document.getElementById('review-banner')).toBeNull();
  });
});

// window.open のスタブ（jsdomは未実装）。viimport を使わず簡易に差し替える
let originalOpen: typeof window.open | null = null;
function vi_stubOpen(fn: (url: string) => void) {
  originalOpen = window.open;
  window.open = ((url?: string | URL) => { fn(String(url)); return null; }) as typeof window.open;
}

afterEach(() => {
  if (originalOpen) { window.open = originalOpen; originalOpen = null; }
  localStorage.clear();
  document.getElementById('review-banner')?.remove();
});
