import { api, type KnowledgeFile } from './api';

const statusLabel: Record<string, string> = {
  pending: '待機中', parsing: '解析中', embedding: '埋め込み中', ready: '利用可能', error: 'エラー',
};

const ACCEPT = '.pdf,.docx,.md,.txt,.xlsx,.csv,.tsv';

export class KnowledgeView {
  private packDrawing = false; // 拡張パック（図面）: OFFの間は列・フィルタを表示しない（本体UI不変の要求）
  private filterText = '';

  constructor() {
    const dz = document.getElementById('dropzone')!;
    const fi = document.getElementById('file-input') as HTMLInputElement;
    dz.addEventListener('dragover', (e) => { e.preventDefault(); dz.classList.add('over'); });
    dz.addEventListener('dragleave', () => dz.classList.remove('over'));
    dz.addEventListener('drop', (e) => {
      e.preventDefault();
      dz.classList.remove('over');
      void this.upload([...e.dataTransfer!.files]);
    });
    fi.addEventListener('change', () => { void this.upload([...fi.files!]); fi.value = ''; });
    const kf = document.getElementById('knowledge-filter') as HTMLInputElement | null;
    if (kf) kf.addEventListener('input', () => { this.filterText = kf.value.trim(); void this.refresh(); });
    void this.refresh();
  }

  private async upload(files: File[]) {
    if (files.length === 0) return;
    const st = document.getElementById('ingest-status')!;
    st.textContent = `${files.length}件を取り込み中…（解析とベクトル化に時間がかかります）`;
    try {
      const r = await api.upload(files);
      const ok = r.results.filter(x => x.ok).length;
      const errs = r.results.filter(x => !x.ok).map(x => `${x.name}: ${x.message ?? x.error ?? ''}`);
      st.textContent = `${ok}/${r.results.length}件を登録しました` + (errs.length ? ` ／ 失敗: ${errs.join(', ')}` : '');
    } catch (ex) {
      st.textContent = `アップロード失敗: ${(ex as Error).message}`;
    }
    await this.refresh();
  }

  /** タブ表示時・操作後に毎回呼ばれる。拡張パック（図面）の状態もここで再反映する */
  async refresh() {
    try {
      const s = await api.getSettings();
      this.packDrawing = !!s.extensions?.drawing;
    } catch { /* 設定取得失敗時は現状維持で一覧のみ更新 */ }
    const fi = document.getElementById('file-input') as HTMLInputElement;
    fi.accept = ACCEPT + (this.packDrawing ? ',.dxf' : '');
    const dz = document.getElementById('dropzone')!;
    const tn = dz.firstChild;
    if (tn && tn.nodeType === Node.TEXT_NODE) {
      tn.textContent = this.packDrawing
        ? 'ここに PDF / Word / Excel / CSV / Markdown / テキスト / DXF（CAD図面） をドラッグ＆ドロップ '
        : 'ここに PDF / Word / Excel / CSV / Markdown / テキスト をドラッグ＆ドロップ ';
    }
    const kf = document.getElementById('knowledge-filter') as HTMLInputElement | null;
    if (kf) kf.hidden = !this.packDrawing;

    const files: KnowledgeFile[] = await api.knowledge(this.filterText || undefined);
    const tbody = document.getElementById('files-body')!;
    const head = document.getElementById('files-head')!;
    // 図面列の表示は拡張パックの状態に従う（OFF時は従来の5列のまま）
    const drawingCol = this.packDrawing && files.some(f => f.kind === 'drawing');
    head.innerHTML = `<th>ファイル</th>${drawingCol ? '<th>種別/図番</th>' : ''}<th>状態</th><th>チャンク数</th><th>登録日時</th><th></th>`;
    tbody.innerHTML = '';
    const cols = drawingCol ? 6 : 5;
    for (const f of files) {
      const tr = document.createElement('tr');
      const status = f.status === 'error' ? `エラー（${f.error ?? ''}）` : statusLabel[f.status] ?? f.status;
      const isDraw = drawingCol && f.kind === 'drawing';
      let cells = `<td>${isDraw ? `<img class="file-thumb" src="${api.thumbUrl(f.file_id)}" alt="" onerror="this.remove()">` : ''}${esc(f.name)}</td>`;
      if (drawingCol) {
        cells += isDraw
          ? `<td><span class="badge-drawing">図面</span> ${esc(f.zuban_raw ?? '図番不明')}${f.hinmei ? ` <span class="muted">${esc(f.hinmei)}</span>` : ''}</td>`
          : `<td><span class="muted">文書</span></td>`;
      }
      cells += `<td class="st-${f.status}">${esc(status)}</td><td>${f.chunk_count}</td><td>${esc(f.added_at ?? '')}</td>`;
      tr.innerHTML = cells;
      const td = document.createElement('td');
      // 取り込み失敗は原因を取り除いた後にワンクリックで再試行できる（元ファイルが保存されている場合）
      if (f.status === 'error') {
        const retry = document.createElement('button');
        retry.className = 'icon-btn';
        retry.textContent = '↻';
        retry.title = '再試行';
        retry.addEventListener('click', async () => {
          retry.disabled = true;
          const r = await api.retryKnowledge(f.file_id).catch(() => ({ ok: false, message: '通信エラー' }));
          if (r.ok === false) {
            const st = tr.querySelector('.st-error') as HTMLElement | null;
            if (st) st.title = `再試行できません: ${r.message ?? ''}`;
          }
          await this.refresh();
        });
        td.appendChild(retry);
      }
      const del = document.createElement('button');
      del.className = 'icon-btn';
      del.textContent = '×';
      del.addEventListener('click', async () => { await api.deleteKnowledge(f.file_id); await this.refresh(); });
      td.appendChild(del);
      tr.appendChild(td);
      tbody.appendChild(tr);
    }
    if (files.length === 0) {
      tbody.innerHTML = `<tr><td colspan="${cols}" class="muted">登録されたファイルはありません</td></tr>`;
    }
  }
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
