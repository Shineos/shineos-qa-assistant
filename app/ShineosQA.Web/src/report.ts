import { api } from './api';

/** AI生成コンテンツの報告窓口（Shineos公式問い合わせフォーム）。
 *  Microsoft Store ポリシー 11.16「Live Generative AI Content」対応。 */
export const REPORT_CONTACT_URL = 'https://shineos.com/contact/';

/** 問い合わせフォームの種別セレクトに自動入力する値（contact.astro の option value と一致させる） */
const REPORT_PREFILL_TYPE = 'その他のお問い合わせ';

const REASONS = ['不適切・有害な内容', '事実と異なる・誤解を招く内容', 'その他'];

function clip(s: string, n: number): string {
  return s.length > n ? s.slice(0, n) + '…' : s;
}

/** クリップボードへコピー（WebView2のループバックはSecure ContextなのでClipboard APIが使える。
 *  不可な環境向けにexecCommandフォールバックも用意） */
async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    const ok = document.execCommand('copy');
    ta.remove();
    return ok;
  }
}

/** 報告内容を問い合わせフォームへ事前入力するURL。
 *  shineos.com の contact.astro が ?type=&message= を読んでフォームに自動入力する。 */
function prefillUrl(summary: string): string {
  const q = new URLSearchParams({ type: REPORT_PREFILL_TYPE, message: summary });
  return `${REPORT_CONTACT_URL}?${q.toString()}`;
}

/** AI回答に対する問題報告ダイアログ。
 *  アプリ自身は何も送信しない（PRIVACY.mdの「外部通信なし」を維持）。
 *  「問い合わせフォームを開く」はブラウザでフォームを開き、報告内容を自動入力する。
 *  送信はユーザー自身の操作でのみ行われる。サイト側の自動入力未対応の場合に備え、
 *  クリップボードへのコピーも同時に行う（貼り付けで代替可能）。 */
export function openReportModal(answer: string, question: string): void {
  const host = document.getElementById('modal-host')!;
  host.innerHTML = '';
  const ov = document.createElement('div');
  ov.className = 'src-overlay';
  ov.innerHTML = `
    <div class="src-modal rpt-modal">
      <div class="src-head"><span>⚠️ 問題を報告</span><button class="icon-btn" data-close>✕ 閉じる</button></div>
      <div class="src-body rpt-body">
        <p class="rpt-lead">AIの回答に不適切な内容や誤りを見つけた場合は、下の<b>「問い合わせフォームを開く」</b>を押してください。報告内容が<a href="${REPORT_CONTACT_URL}" target="_blank" rel="noopener noreferrer">Shineosの問い合わせフォーム</a>に自動入力されます（社名・お名前・メールアドレスのご記入と送信はご自身で行います）。</p>
        <label class="rpt-label" for="rpt-reason">問題の種類</label>
        <select id="rpt-reason">${REASONS.map(r => `<option>${r}</option>`).join('')}</select>
        <label class="rpt-label" for="rpt-comment">詳細（任意）</label>
        <textarea id="rpt-comment" rows="3" placeholder="状況などをご記入ください（任意）"></textarea>
        <label class="rpt-label">報告内容（この内容がフォームに自動入力されます）</label>
        <div id="rpt-summary" class="rpt-summary"></div>
        <p class="rpt-note" id="rpt-note">🔒 この画面からは何も送信されません。「問い合わせフォームを開く」はブラウザでフォームを開き、分類・質問・AIの回答（抜粋）を自動入力します。フォームの送信はユーザー自身の操作でのみ行われます。</p>
        <div class="rpt-actions">
          <button type="button" class="btn-secondary" id="rpt-copy">📋 内容をコピー</button>
          <button type="button" class="primary" id="rpt-open">問い合わせフォームを開く</button>
        </div>
      </div>
    </div>`;

  const close = () => { host.innerHTML = ''; };
  ov.addEventListener('click', e => { if (e.target === ov) close(); });
  ov.querySelector('[data-close]')!.addEventListener('click', close);

  const summaryEl = ov.querySelector('#rpt-summary') as HTMLElement;
  const noteEl = ov.querySelector('#rpt-note') as HTMLElement;
  let version = '—';
  const buildSummary = (): string => {
    const reason = (ov.querySelector('#rpt-reason') as HTMLSelectElement).value;
    const comment = (ov.querySelector('#rpt-comment') as HTMLTextAreaElement).value.trim() || 'なし';
    const now = new Date();
    const pad = (n: number) => String(n).padStart(2, '0');
    const ts = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())} ${pad(now.getHours())}:${pad(now.getMinutes())}`;
    return [
      '【ShineosQA 問題報告】',
      `分類: ${reason}`,
      `アプリバージョン: ${version}`,
      `日時: ${ts}`,
      '',
      '【質問】',
      clip(question || '（質問なし）', 500),
      '',
      '【AIの回答（抜粋）】',
      clip(answer || '（回答なし）', 800),
      '',
      '【詳細】',
      comment,
    ].join('\n');
  };
  const renderSummary = () => { summaryEl.textContent = buildSummary(); };
  renderSummary();
  void api.status().then(s => { version = s.version; renderSummary(); }).catch(() => { /* バージョン不明のまま表示 */ });

  (ov.querySelector('#rpt-reason') as HTMLSelectElement).addEventListener('change', renderSummary);
  (ov.querySelector('#rpt-comment') as HTMLTextAreaElement).addEventListener('input', renderSummary);

  (ov.querySelector('#rpt-copy') as HTMLButtonElement).addEventListener('click', async ev => {
    const btn = ev.currentTarget as HTMLButtonElement;
    const ok = await copyText(summaryEl.textContent ?? '');
    btn.textContent = ok ? '✅ コピーしました' : '⚠️ コピーできません（報告内容を選択してコピーしてください）';
    if (ok) window.setTimeout(() => { btn.textContent = '📋 内容をコピー'; }, 2500);
  });

  (ov.querySelector('#rpt-open') as HTMLButtonElement).addEventListener('click', async ev => {
    const btn = ev.currentTarget as HTMLButtonElement;
    const summary = buildSummary();
    // サイト側の自動入力（?type=&message=）に対応していない場合に備え、クリップボードにも入れておく
    const copied = await copyText(summary);
    if (copied) {
      noteEl.textContent = '📋 報告内容をクリップボードにもコピーしました。フォームに自動入力されない場合は、メッセージ欄に貼り付けてご利用ください。';
    }
    btn.disabled = true;
    window.open(prefillUrl(summary), '_blank', 'noopener,noreferrer');
    btn.disabled = false;
  });

  host.appendChild(ov);
}
