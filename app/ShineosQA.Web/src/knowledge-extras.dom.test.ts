import { afterEach, describe, expect, it, vi } from 'vitest';
import { KnowledgeExtras } from './knowledge-extras';

/** fetchの戻り値を最小限のJSON Responseで作る（api.ts の json() が resp.json() を呼ぶ） */
function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function mount(): { host: HTMLElement; extras: KnowledgeExtras } {
  const host = document.createElement('div');
  document.body.appendChild(host);
  return { host, extras: new KnowledgeExtras(host) };
}

afterEach(() => {
  vi.unstubAllGlobals();
  document.body.innerHTML = '';
});

// 追加セクションのDOM組み立て（ID/セレクタ）とAPI呼び出しをまとめて確認する。
// 個々の純関数は knowledge-extras.test.ts で検証済み。
describe('KnowledgeExtras（DOM）', () => {
  it('公開FAQの一覧と図面台帳の表を描画する', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith('/api/faq')) {
        return jsonResponse({ items: [{ id: 7, question: '有給休暇の申請期限は？', answer: '3日前までです。', hits: 12 }] });
      }
      if (url.startsWith('/api/drawings')) {
        return jsonResponse({
          items: [{
            file_id: 3, file: 'ST-1042A.pdf', zuban: 'ST-1042A', hinmei: 'ブラケット',
            zairyo: 'SUS304', scale: '1:2', revision: 'B', approved_at: '2026-04-01',
          }],
        });
      }
      return jsonResponse({ items: [] });
    });
    vi.stubGlobal('fetch', fetchMock);

    const { host, extras } = mount();
    await extras.refresh();

    expect(fetchMock).toHaveBeenCalledWith('/api/faq?status=published');
    expect(host.querySelectorAll('#faq-list tbody tr')).toHaveLength(1);
    expect(host.querySelector('#faq-list')?.textContent).toContain('有給休暇の申請期限は？');
    expect(host.querySelector('#faq-count')?.textContent).toBe('（1件）');
    expect(host.querySelectorAll('.drawings-table tbody tr')).toHaveLength(1);
    expect(host.querySelector('.drawings-table')?.textContent).toContain('SUS304');
    expect(host.querySelector('.drawings-table')?.textContent).toContain('ブラケット');
  });

  it('実績表を選ぶと列を自動検出し、見積合計を表示する', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.startsWith('/api/jisseki/columns')) {
        return jsonResponse({
          items: [
            { column_name: '図番', role: 'zuban', column_index: 0 },
            { column_name: '見積金額', role: 'mitumori', column_index: 1 },
          ],
        });
      }
      if (url.startsWith('/api/jisseki/summary')) {
        return jsonResponse({ metric: 'mitumori', aggregate: 'sum', count: 3, value: 150000, unit: '円' });
      }
      return jsonResponse({ items: [] });
    });
    vi.stubGlobal('fetch', fetchMock);

    const { host, extras } = mount();
    extras.setFiles([{
      file_id: 9, name: 'jisseki.xlsx', status: 'ready', chunk_count: 3, added_at: '2026-10-01 10:00:00',
      kind: 'sheet_jisseki',
    }]);
    const fileSelect = host.querySelector('#jis-file') as HTMLSelectElement;
    expect(fileSelect.value).toBe('9');

    fileSelect.dispatchEvent(new Event('change'));

    await vi.waitFor(() => {
      expect(host.querySelector('#jis-columns')?.textContent).toContain('検出した列');
    });
    await vi.waitFor(() => {
      expect(host.querySelector('#jis-result')?.textContent).toContain('150,000円');
    });
    expect(host.querySelector('#jis-result')?.textContent).toContain('見積の合計');
    expect(fetchMock).toHaveBeenCalledWith('/api/jisseki/summary?file_id=9&metric=mitumori&aggregate=sum');
  });

  it('実績表が無いときは集計操作を無効化し、案内を表示する', () => {
    const { host, extras } = mount();
    extras.setFiles([]);
    expect((host.querySelector('#jis-file') as HTMLSelectElement).disabled).toBe(true);
    expect((host.querySelector('#jis-run') as HTMLButtonElement).disabled).toBe(true);
    expect(host.querySelector('#jis-note')?.textContent).toContain('実績表');
  });
});
