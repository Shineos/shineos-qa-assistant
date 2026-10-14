import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { REPORT_CONTACT_URL, openReportModal } from './report';

const Q = '会議室の予約ルールを教えて';
const A = '会議室は利用前日までに予約してください。';

// Store ポリシー 11.16「Live Generative AI Content」の審査用件をテストで永久化する:
// 各AI回答に報告導線があり、ユーザー自身の操作でのみ問い合わせフォームに自動入力される。
describe('問題報告モーダル(Store 11.16対応)', () => {
  beforeEach(() => {
    document.body.innerHTML = '<div id="modal-host"></div>';
    vi.stubGlobal('fetch', vi.fn(async () => ({
      ok: true,
      json: async () => ({
        version: '2.2.0-test', tier: 'quick', chat_model: '', chunks: 0, ram_gb: 8,
        engines: { tier: '', chat_model: '', llm: '', embed: '', rank: '' },
      }),
    })));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    document.body.innerHTML = '';
  });

  it('モーダルが描画され、質問・回答・分類がサマリに入る', () => {
    openReportModal(A, Q);
    expect(document.querySelector('.src-overlay')).not.toBeNull();
    expect((document.getElementById('rpt-reason') as HTMLSelectElement).options.length).toBe(3);
    const summary = document.getElementById('rpt-summary')!.textContent ?? '';
    expect(summary).toContain('分類: 不適切・有害な内容');
    expect(summary).toContain(Q);
    expect(summary).toContain(A);
  });

  it('この画面からは何も送信されない旨が表示される(PRIVACY主張との整合)', () => {
    openReportModal(A, Q);
    expect(document.getElementById('rpt-note')!.textContent).toContain('何も送信されません');
  });

  it('分類を変更するとサマリの分類行が更新される', () => {
    openReportModal(A, Q);
    const sel = document.getElementById('rpt-reason') as HTMLSelectElement;
    sel.value = '事実と異なる・誤解を招く内容';
    sel.dispatchEvent(new Event('change'));
    expect(document.getElementById('rpt-summary')!.textContent).toContain('分類: 事実と異なる・誤解を招く内容');
  });

  it('「問い合わせフォームを開く」が ?type=&message= 付きでフォームURLを開く', async () => {
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText: vi.fn(async () => {}) },
      configurable: true,
    });
    const openSpy = vi.spyOn(window, 'open').mockReturnValue(null);
    openReportModal(A, Q);
    (document.getElementById('rpt-open') as HTMLButtonElement).click();
    await vi.waitFor(() => expect(openSpy).toHaveBeenCalled());
    const url = openSpy.mock.calls[0][0] as string;
    expect(url.startsWith(REPORT_CONTACT_URL)).toBe(true);
    const params = new URL(url).searchParams;
    expect(params.get('type')).toBe('その他のお問い合わせ');
    expect(params.get('message')).toContain(Q);
    expect(params.get('message')).toContain(A);
  });
});
