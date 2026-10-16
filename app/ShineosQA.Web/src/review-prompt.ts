// Microsoft Store評価導線。Storeのランキングは評価スコア×直近レビューで決まるため、
// 成功体験（出典付きで正常に回答できた後）にだけ控えめに評価を促す。
// 節度の設計: 出典付き正常回答の累計が最初5回到達時に1度だけ表示、以後20回ごと。
// 「あとで」で30日非表示、「✕」で恒久オフ。リンクはms-windows-storeプロトコル
// （ラッパーMainWindowV2がShellExecuteでStoreアプリを開く）

const KEY_COUNT = 'rvp_ok_count';
const KEY_STATE = 'rvp_state'; // '' | 'never' | 'later:<ISO日時>'
const STORE_REVIEW_URL = 'ms-windows-store://review/?productid=XP8C6NSGDR1N9F';
const FIRST_ASK = 5;
const RE_ASK_INTERVAL = 20;
const LATER_DAYS = 30;

/** 成功回答の累計カウントが表示対象か（しきい値5・以後20回ごと） */
export function shouldAskAt(count: number): boolean {
  if (count < FIRST_ASK) return false;
  if (count === FIRST_ASK) return true;
  return (count - FIRST_ASK) % RE_ASK_INTERVAL === 0;
}

function readState(): string { return localStorage.getItem(KEY_STATE) ?? ''; }

function isSuppressed(): boolean {
  const s = readState();
  if (s === 'never') return true;
  if (s.startsWith('later:')) {
    const until = Date.parse(s.slice(6));
    if (!Number.isNaN(until) && Date.now() < until) return true;
  }
  return false;
}

/** 出典付きの正常回答が完了するたびにchat.tsから呼ぶ。表示条件を満たしたらバナーを出す */
export function onSuccessfulAnswer(): void {
  if (isSuppressed()) return;
  const n = (parseInt(localStorage.getItem(KEY_COUNT) ?? '0', 10) || 0) + 1;
  localStorage.setItem(KEY_COUNT, String(n));
  if (shouldAskAt(n)) showBanner();
}

function setState(s: string): void { localStorage.setItem(KEY_STATE, s); }

function showBanner(): void {
  if (document.getElementById('review-banner')) return;
  const form = document.getElementById('chat-form');
  if (!form || !form.parentElement) return;
  const bar = document.createElement('div');
  bar.id = 'review-banner';
  bar.innerHTML =
    `<span class="rvp-text">👍 このアプリがお役に立っていますか？よろしければ評価をお願いします</span>` +
    `<button type="button" class="rvp-open">⭐ Microsoft Storeで評価する</button>` +
    `<button type="button" class="rvp-later" title="30日後に再表示">あとで</button>` +
    `<button type="button" class="rvp-never" title="今後表示しない">✕</button>`;
  bar.querySelector('.rvp-open')!.addEventListener('click', () => {
    setState('never'); // 評価画面を開いたら二度と出さない
    bar.remove();
    window.open(STORE_REVIEW_URL, '_blank');
  });
  bar.querySelector('.rvp-later')!.addEventListener('click', () => {
    setState(`later:${new Date(Date.now() + LATER_DAYS * 86400000).toISOString()}`);
    bar.remove();
  });
  bar.querySelector('.rvp-never')!.addEventListener('click', () => {
    setState('never');
    bar.remove();
  });
  form.parentElement.insertBefore(bar, form);
}
