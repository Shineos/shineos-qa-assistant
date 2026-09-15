import { api, type KnowledgeFile } from './api';

const statusLabel: Record<string, string> = {
  pending: '待機中', parsing: '解析中', embedding: '埋め込み中', ready: '利用可能', error: 'エラー',
};

export class KnowledgeView {
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

  async refresh() {
    const files: KnowledgeFile[] = await api.knowledge();
    const tbody = document.getElementById('files-body')!;
    tbody.innerHTML = '';
    for (const f of files) {
      const tr = document.createElement('tr');
      const status = f.status === 'error' ? `エラー（${f.error ?? ''}）` : statusLabel[f.status] ?? f.status;
      tr.innerHTML = `<td>${esc(f.name)}</td><td class="st-${f.status}">${esc(status)}</td><td>${f.chunk_count}</td><td>${esc(f.added_at ?? '')}</td>`;
      const td = document.createElement('td');
      const del = document.createElement('button');
      del.className = 'icon-btn';
      del.textContent = '×';
      del.addEventListener('click', async () => { await api.deleteKnowledge(f.file_id); await this.refresh(); });
      td.appendChild(del);
      tr.appendChild(td);
      tbody.appendChild(tr);
    }
    if (files.length === 0) {
      tbody.innerHTML = '<tr><td colspan="5" class="muted">登録されたファイルはありません</td></tr>';
    }
  }
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
