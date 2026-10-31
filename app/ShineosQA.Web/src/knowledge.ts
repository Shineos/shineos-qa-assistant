import { api, type KnowledgeFile } from './api';
import { KnowledgeExtras } from './knowledge-extras';

const statusLabel: Record<string, string> = {
  pending: '待機中', parsing: '解析中', embedding: '埋め込み中', ready: '利用可能', error: 'エラー',
};

/** ファイル種別アイコン（Dify風一覧のファイル名列）。文書種別の共通アイコン */
const SVG_DOC = `<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z"/><path d="M14 2v4a2 2 0 0 0 2 2h4"/></svg>`;

const ACCEPT = '.pdf,.docx,.md,.txt,.xlsx,.csv,.tsv';

export class KnowledgeView {
  private packDrawing = false; // 拡張パック（図面）: OFFの間は列・フィルタを表示しない（本体UI不変の要求）
  private filterText = '';
  // Dify風一覧（v2.7.0）: ソート状態（字数・検索回数・登録日時でヘッダークリック切替）と選択行
  private sortKey: 'added_at' | 'total_chars' | 'hit_count' = 'added_at';
  private sortDir: 1 | -1 = -1; // 既定は新しい順
  private selected = new Set<number>();
  // ナレッジ一覧の下に置く追加セクション（FAQ管理・図面台帳・実績集計）
  private extras: KnowledgeExtras;

  constructor() {
    this.extras = new KnowledgeExtras(document.getElementById('knowledge-extras')!);
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
      // 取り込みはバックグラウンドで進行するため、登録中も一覧を2秒ごとに再描画して
      // 各ファイルの状態（解析中→ベクトル化中→利用可能）を可視化する
      const poll = setInterval(() => { void this.refreshListOnly(); }, 2000);
      try {
        const r = await api.upload(files);
        const ok = r.results.filter(x => x.ok).length;
        const errs = r.results.filter(x => !x.ok).map(x => `${x.name}: ${x.message ?? x.error ?? ''}`);
        st.textContent = `${ok}/${r.results.length}件を登録しました` + (errs.length ? ` ／ 失敗: ${errs.join(', ')}` : '');
        // スキャン図面を視覚モデル未導入で取り込んだ場合はVL導入案内を出す（v2.4.0）
        // 一度閉じたら恒久非表示（removal、同一環境で繰り返し案内しない）
        if (r.results.some(x => x.suggest_vision) && localStorage.getItem('vision-hint-dismissed') !== '1') {
          this.showVisionHint();
        }
      } finally {
        clearInterval(poll);
      }
    } catch (ex) {
      st.textContent = `アップロード失敗: ${(ex as Error).message}`;
    }
    await this.refresh();
  }

  /** スキャン図面取り込み後の視覚モデル（VL）導入案内バナー */
  private showVisionHint() {
    if (document.getElementById('vision-hint')) return;
    const dz = document.getElementById('dropzone')!;
    const bar = document.createElement('div');
    bar.id = 'vision-hint';
    bar.className = 'vision-hint';
    bar.innerHTML = `<span>スキャン図面を内蔵OCRで読み込みました。視覚モデル（約2.1GB）を導入すると注記・寸法の読み取り精度が向上します。</span>`;
    const btn = document.createElement('button');
    btn.className = 'primary small';
    btn.textContent = '設定を開く';
    btn.addEventListener('click', () => {
      localStorage.setItem('vision-hint-dismissed', '1');
      document.querySelector<HTMLButtonElement>('button.tab[data-tab="settings"]')?.click();
    });
    const close = document.createElement('button');
    close.className = 'icon-btn';
    close.textContent = '×';
    close.title = '閉じる（今後表示しない）';
    close.addEventListener('click', () => {
      localStorage.setItem('vision-hint-dismissed', '1');
      bar.remove();
    });
    bar.append(btn, close);
    dz.parentNode!.insertBefore(bar, dz);
  }

  /** 一覧のみ再取得（設定再取得・拡張パック再評価を行わない軽量版。取り込み中の進捗表示用） */
  private async refreshListOnly() {
    try {
      const files: KnowledgeFile[] = await api.knowledge(this.filterText || undefined);
      this.renderFiles(files);
      this.extras.setFiles(files);
    } catch { /* 取得失敗時は無視（次のポーリングで再取得） */ }
  }

  private renderFiles(files: KnowledgeFile[]) {
    // Dify風一覧（v2.7.0）: チェック / # / ファイル名 / 種別 / 字数 / 検索回数 / 登録日時 /
    // ステータス / アクション（トグル+…メニュー）。ソートはヘッダークリックで字数・検索回数・
    // 登録日時を切替（クライアントソート）。選択行は一括削除バーで操作する
    const tbody = document.getElementById('files-body')!;
    const head = document.getElementById('files-head')!;
    // ソート（既定のfile_id DESC＝追加順を kept: added_atソートで再現するため added_at 文字列比較）
    const sorted = [...files].sort((a, b) => {
      const av = this.sortKey === 'added_at' ? (a.added_at ?? '') : (this.sortKey === 'total_chars' ? (a.total_chars ?? 0) : (a.hit_count ?? 0));
      const bv = this.sortKey === 'added_at' ? (b.added_at ?? '') : (this.sortKey === 'total_chars' ? (b.total_chars ?? 0) : (b.hit_count ?? 0));
      if (av < bv) return -1 * this.sortDir;
      if (av > bv) return 1 * this.sortDir;
      return 0;
    });
    // ヘッダー（ソート可能列は矢印付き・クリックで切替）
    const arrow = (k: typeof this.sortKey) => this.sortKey === k ? (this.sortDir === 1 ? ' ↑' : ' ↓') : '';
    const th = (k: string, label: string) => `<th class="sortable" data-sort="${k}">${label}${arrow(k as typeof this.sortKey)}</th>`;
    head.innerHTML = `<th class="col-check"><input type="checkbox" id="sel-all" aria-label="全選択"></th><th class="col-idx">#</th><th>ファイル名</th><th>種別</th>${th('total_chars', '字数')}${th('hit_count', '検索回数')}${th('added_at', '登録日時')}<th>ステータス</th><th>アクション</th>`;
    tbody.innerHTML = '';
    const cols = 9;
    for (let i = 0; i < sorted.length; i++) {
      const f = sorted[i];
      const tr = document.createElement('tr');
      tr.className = f.enabled === 0 ? 'row-disabled' : '';
      const status = f.status === 'error' ? `エラー（${f.error ?? ''}）` : statusLabel[f.status] ?? f.status;
      const badge = f.kind === 'drawing'
        ? '<span class="badge-kind kind-drawing">図面</span>'
        : f.kind === 'sheet_jisseki'
          ? '<span class="badge-kind kind-jisseki">実績表</span>'
          : '<span class="badge-kind kind-doc">文書</span>';
      const zuban = f.kind === 'drawing' && f.zuban_raw ? ` <span class="muted">${esc(f.zuban_raw)}</span>` : '';
      const nameCell = f.kind === 'drawing'
        ? `<img class="file-thumb" data-fid="${f.file_id}" src="${api.thumbUrl(f.file_id)}" alt="" onerror="this.remove()"> ${esc(f.name)}${zuban}`
        : `<span class="file-ico">${SVG_DOC}</span> ${esc(f.name)}`;
      tr.innerHTML = `<td class="col-check"><input type="checkbox" class="row-sel" data-fid="${f.file_id}" ${this.selected.has(f.file_id) ? 'checked' : ''}></td>`
        + `<td class="col-idx">${i + 1}</td>`
        + `<td class="col-name">${nameCell}</td>`
        + `<td>${badge}</td>`
        + `<td class="num">${(f.total_chars ?? 0).toLocaleString('ja-JP')}</td>`
        + `<td class="num">${f.hit_count ?? 0}</td>`
        + `<td class="muted">${esc(f.added_at ?? '')}</td>`
        + `<td class="st-${f.status}"><span class="dot dot-${f.status}"></span>${esc(status)}</td>`
        + `<td class="col-actions"><span class="actions-inline"></span></td>`;
      // ステータスセル内のテキストノード（dot span の後）を整形
      const stCell = tr.querySelector(`.st-${f.status}`) as HTMLElement;
      const stText = status;
      stCell.innerHTML = `<span class="dot dot-${f.status}"></span><span>${esc(stText)}</span>`;
      // トグルスイッチ（enabled切替: 0=検索対象から一時除外・索引を即時再ロード）
      const actionsCell = tr.querySelector('.actions-inline') as HTMLElement;
      const toggle = document.createElement('label');
      toggle.className = 'kswitch';
      toggle.title = f.enabled === 0 ? '無効（検索対象外）→ 有効にする' : '有効 → 検索対象から外す';
      toggle.innerHTML = `<input type="checkbox" ${f.enabled === 0 ? '' : 'checked'} aria-label="この文書を検索対象に含める">`;
      const tglInput = toggle.querySelector('input')!;
      tglInput.addEventListener('change', async () => {
        try { await api.toggleKnowledge(f.file_id); } catch { /* 失敗時は再取得で元に戻す */ }
        await this.refresh();
      });
      actionsCell.appendChild(toggle);
      // 「…」メニュー（既存の個別アクションを集約）
      const more = document.createElement('button');
      more.className = 'icon-btn';
      more.textContent = '…';
      more.title = '操作メニュー';
      more.addEventListener('click', (e) => { e.stopPropagation(); this.openRowMenu(f, e.target as HTMLElement, () => this.refresh()); });
      actionsCell.appendChild(more);
      tbody.appendChild(tr);
    }
    // 行チェックボックス（一括削除用）
    tbody.querySelectorAll<HTMLInputElement>('.row-sel').forEach(cb => {
      cb.addEventListener('change', () => {
        const fid = Number(cb.getAttribute('data-fid'));
        if (cb.checked) this.selected.add(fid); else this.selected.delete(fid);
        this.renderBulkBar();
      });
    });
    const selAll = head.querySelector('#sel-all') as HTMLInputElement | null;
    if (selAll) selAll.addEventListener('change', () => {
      for (const f of sorted) { if (selAll.checked) this.selected.add(f.file_id); else this.selected.delete(f.file_id); }
      this.renderFiles(files);
    });
    // ソートヘッダー
    head.querySelectorAll<HTMLTableCellElement>('th.sortable').forEach(h => {
      h.addEventListener('click', () => {
        const k = h.getAttribute('data-sort') as typeof this.sortKey;
        if (this.sortKey === k) this.sortDir = (this.sortDir === 1 ? -1 : 1) as 1 | -1;
        else { this.sortKey = k; this.sortDir = k === 'added_at' ? -1 : -1; }
        this.renderFiles(files);
      });
    });
    if (sorted.length === 0) {
      tbody.innerHTML = `<tr><td colspan="${cols}" class="muted">登録されたファイルはありません</td></tr>`;
    }
    this.renderBulkBar();
    // 図面のサムネイルをクリックすると元ファイルを開く
    tbody.querySelectorAll('.file-thumb').forEach(img => {
      (img as HTMLElement).style.cursor = 'zoom-in';
      img.addEventListener('click', () => {
        const fid = img.getAttribute('data-fid');
        if (fid) window.open(api.fileUrl(Number(fid)), '_blank');
      });
    });
  }

  /** 行の「…」メニュー: 既存の個別アクション（要約/実績集計/再試行/ダウンロード/削除）をドロップダウンに集約 */
  private openRowMenu(f: KnowledgeFile, anchor: HTMLElement, refresh: () => Promise<void>) {
    this.closeRowMenu();
    const menu = document.createElement('div');
    menu.className = 'row-menu';
    const items: HTMLButtonElement[] = [];
    if (f.status === 'ready' && f.chunk_count > 0) {
      const sum = document.createElement('button');
      sum.textContent = '文書を要約';
      sum.addEventListener('click', () => {
        this.closeRowMenu();
        document.querySelector<HTMLButtonElement>('button.tab[data-tab="chat"]')?.click();
        document.dispatchEvent(new CustomEvent('shineosqa:summarize', { detail: { fileId: f.file_id, name: f.name } }));
      });
      items.push(sum);
    }
    if (f.status === 'error') {
      const retry = document.createElement('button');
      retry.textContent = '再試行';
      retry.addEventListener('click', async () => { this.closeRowMenu(); await api.retryKnowledge(f.file_id).catch(() => null); await refresh(); });
      items.push(retry);
    }
    if (f.kind === 'sheet_jisseki' && f.status === 'ready') {
      const jis = document.createElement('button');
      jis.textContent = '実績集計';
      jis.addEventListener('click', () => { this.closeRowMenu(); this.extras.focusJissekiFile(f.file_id); });
      items.push(jis);
    }
    const dl = document.createElement('button');
    dl.textContent = '元ファイルを開く';
    dl.addEventListener('click', () => { this.closeRowMenu(); window.open(api.fileUrl(f.file_id), '_blank'); });
    items.push(dl);
    const del = document.createElement('button');
    del.className = 'danger';
    del.textContent = '削除';
    del.addEventListener('click', async () => { this.closeRowMenu(); this.selected.delete(f.file_id); await api.deleteKnowledge(f.file_id); await refresh(); });
    items.push(del);
    menu.append(...items);
    const cell = anchor.parentElement!;
    cell.style.position = 'relative';
    cell.appendChild(menu);
    setTimeout(() => {
      const closer = (ev: MouseEvent) => { if (!menu.contains(ev.target as Node)) this.closeRowMenu(); };
      document.addEventListener('click', closer, { once: true });
    }, 0);
  }

  private closeRowMenu() {
    document.querySelectorAll('.row-menu').forEach(m => m.remove());
  }

  /** 一括削除バー: チェックした行があるときだけ表示 */
  private renderBulkBar() {
    const host = document.getElementById('bulk-bar')!;
    if (this.selected.size === 0) { host.innerHTML = ''; host.hidden = true; return; }
    host.hidden = false;
    host.innerHTML = `<span>${this.selected.size}件を選択中</span>`;
    const del = document.createElement('button');
    del.className = 'danger-btn';
    del.textContent = '選択した項目を削除';
    del.addEventListener('click', async () => {
      const ids = [...this.selected];
      for (const id of ids) await api.deleteKnowledge(id).catch(() => null);
      this.selected.clear();
      await this.refresh();
    });
    host.appendChild(del);
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
    try {
      const files: KnowledgeFile[] = await api.knowledge(this.filterText || undefined);
      this.renderFiles(files);
      this.extras.setFiles(files);
    } catch { /* 取得失敗時は現状維持 */ }
    // FAQ・図面台帳はナレッジタブを表示したときだけ取得する（起動時の余分な通信を避ける）
    if (document.getElementById('tab-knowledge')?.classList.contains('active')) {
      await this.extras.refresh();
    }
  }
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
