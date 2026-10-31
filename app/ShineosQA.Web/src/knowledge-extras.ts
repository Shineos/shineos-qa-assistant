// ナレッジ画面の追加セクション（FAQ管理・図面台帳・実績集計）。
// 既存のナレッジ一覧（knowledge.ts）は変更せず、その下に折りたたみセクションとして置く。
import { api, type DrawingRow, type FaqCandidate, type FaqEntry, type JissekiColumnsResult, type JissekiRoleMap, type JissekiSummary, type KnowledgeFile } from './api';

const esc = (s: string): string =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

/** FAQの「別の言い方」入力（カンマ・読点・改行区切り）を配列にする。空要素は除く（API契約: variants） */
export function splitFaqVariants(raw: string): string[] {
  return raw.split(/[,、\n]/).map(s => s.trim()).filter(s => s.length > 0);
}

const JISSEKI_ROLE_LABELS: Record<string, string> = {
  zuban: '図番', mitumori: '見積', genka: '原価', nouki: '納期', other: 'その他',
};

/** 実績表の列の役割 → 日本語ラベル。未知の役割はそのまま表示する */
export function jissekiRoleLabel(role: string): string {
  return JISSEKI_ROLE_LABELS[role] ?? role;
}

const JISSEKI_METRIC_LABELS: Record<string, string> = { mitumori: '見積', genka: '原価' };
const JISSEKI_AGG_LABELS: Record<string, string> = { sum: '合計', avg: '平均', count: '件数' };

/** 集計結果の見出し（例: 「見積の合計」） */
export function jissekiSummaryLabel(metric: string, aggregate: string): string {
  return `${JISSEKI_METRIC_LABELS[metric] ?? metric}の${JISSEKI_AGG_LABELS[aggregate] ?? aggregate}`;
}

/** 集計結果の値表示。金額は3桁区切り＋単位、件数は「N 件」 */
export function formatJissekiValue(s: Pick<JissekiSummary, 'aggregate' | 'value' | 'unit'>): string {
  const n = Number.isFinite(s.value) ? s.value.toLocaleString('ja-JP') : String(s.value);
  if (s.aggregate === 'count') return `${n} 件`;
  return `${n}${s.unit || '円'}`;
}

/** 列役割の編集ドロップダウンの選択肢（値=APIのrole、other=役割なし） */
export const JISSEKI_ROLE_OPTIONS: { value: string; label: string }[] = [
  { value: 'other', label: 'その他' },
  { value: 'zuban', label: '図番' },
  { value: 'mitumori', label: '見積' },
  { value: 'genka', label: '原価' },
  { value: 'nouki', label: '納期' },
];

/** 編集済みの列役割からAPIの上書きマッピングを作る。図番列は必須（実績辞書のキー）。
 * 同じ列に複数役割・図番未指定の場合はエラー文を返す */
export function buildJissekiRoleMap(roles: { column_index: number; role: string }[]): { map?: JissekiRoleMap; error?: string } {
  const pick = (role: string): number => {
    const hit = roles.find(r => r.role === role);
    return hit ? hit.column_index : -1;
  };
  const map: JissekiRoleMap = { zuban: pick('zuban'), mitumori: pick('mitumori'), genka: pick('genka'), nouki: pick('nouki') };
  if (map.zuban < 0) return { error: '図番列を指定してください（実績辞書は図番をキーにします）' };
  const used = [map.zuban, map.mitumori, map.genka, map.nouki].filter(i => i >= 0);
  if (used.length !== new Set(used).size) return { error: '同じ列に複数の役割は割り当てられません' };
  return { map };
}

/** 回答（最大max文字）を1行に丸めて表示用にする。全文はtitle属性で見せる */
function truncate(s: string, max: number): string {
  const t = s.replace(/\s+/g, ' ').trim();
  return t.length > max ? t.slice(0, max) + '…' : t;
}

/** 保存操作の間、ブロック内のボタンをまとめて無効化/復帰する */
function btnToggle(scope: HTMLElement | null, disabled: boolean): void {
  scope?.querySelectorAll<HTMLButtonElement>('button').forEach(b => { b.disabled = disabled; });
}

export class KnowledgeExtras {
  private jissekiFiles: KnowledgeFile[] = [];
  private selectedJissekiId: number | null = null;
  private lastJissekiIds = '';
  private drawingQuery = '';
  private drawingTimer: ReturnType<typeof setTimeout> | null = null;
  private jisSeq = 0; // 実績の集計・列取得の古い応答で表示を上書きしないための連番

  constructor(private readonly host: HTMLElement) {
    host.innerHTML = `
      <details class="k-section" id="faq-section">
        <summary>よくある質問（FAQ）管理<span class="k-count" id="faq-count"></span></summary>
        <div class="k-section-body">
          <div class="faq-form">
            <input type="text" id="faq-question" class="k-input" placeholder="質問（例: 有給休暇は何日前までに申請しますか）" autocomplete="off" />
            <textarea id="faq-answer" class="k-input" rows="2" placeholder="回答（登録した内容がそのまま即答に使われます）"></textarea>
            <input type="text" id="faq-variants" class="k-input" placeholder="別の言い方（任意・カンマ区切り）" autocomplete="off" />
            <div class="faq-form-row">
              <button type="button" id="faq-add" class="primary small">FAQを追加</button>
              <span id="faq-status" class="muted"></span>
            </div>
          </div>
          <div id="faq-list"></div>
          <div class="faq-cand-head">👎フィードバックからの候補<span class="k-count" id="faq-cand-count"></span></div>
          <div class="muted">👍「役に立った」/👎「改善してほしい」の評価から、質問×回答の候補が自動で表示されます。回答を修正して採用すると、次からその質問にはLLMを使わず即答します。</div>
          <div id="faq-cand-list"></div>
          <div id="faq-cand-status" class="muted"></div>
        </div>
      </details>
      <details class="k-section" id="drawings-section">
        <summary>図面台帳<span class="k-count" id="drawings-count"></span></summary>
        <div class="k-section-body">
          <div class="kfilter"><input type="search" id="drawing-search" placeholder="図番・品名・材質・改訂で検索" autocomplete="off" /></div>
          <div id="drawings-table-host"></div>
        </div>
      </details>
      <details class="k-section" id="jisseki-section">
        <summary>実績集計（見積・原価）</summary>
        <div class="k-section-body">
          <div class="jis-controls">
            <label class="jis-field"><span>実績表</span><select id="jis-file"></select></label>
            <label class="jis-field"><span>指標</span><select id="jis-metric"><option value="mitumori">見積</option><option value="genka">原価</option></select></label>
            <label class="jis-field"><span>集計</span><select id="jis-agg"><option value="sum">合計</option><option value="avg">平均</option><option value="count">件数</option></select></label>
            <label class="jis-field"><span>図番</span><input type="text" id="jis-zuban" placeholder="任意（例: ST-1042A）" autocomplete="off" /></label>
            <button type="button" id="jis-run" class="primary small">集計</button>
          </div>
          <div id="jis-columns" class="jis-columns muted">実績表を選ぶと列を自動検出します</div>
          <div id="jis-save-row" hidden>
            <button type="button" id="jis-save" class="primary small">この列設定で実績辞書を作り直す</button>
            <span id="jis-save-status" class="muted"></span>
          </div>
          <div id="jis-result" class="jis-result" hidden></div>
          <div id="jis-note" class="muted"></div>
        </div>
      </details>`;
    this.wire();
  }

  /** 要素の取得（このクラスが生成したDOM内に限定する） */
  private q<T extends HTMLElement>(selector: string): T {
    const el = this.host.querySelector(selector);
    if (!el) throw new Error(`knowledge-extras: ${selector} が見つかりません`);
    return el as T;
  }

  private wire(): void {
    this.q<HTMLButtonElement>('#faq-add').addEventListener('click', () => void this.addFaq());
    this.q<HTMLInputElement>('#faq-question').addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); void this.addFaq(); }
    });
    // 図面検索: 入力のたびに叩かないよう300msデバウンス（ナレッジ一覧の検索と同じ操作感）
    const search = this.q<HTMLInputElement>('#drawing-search');
    search.addEventListener('input', () => {
      if (this.drawingTimer !== null) clearTimeout(this.drawingTimer);
      this.drawingTimer = setTimeout(() => {
        this.drawingTimer = null;
        this.drawingQuery = search.value.trim();
        void this.loadDrawings();
      }, 300);
    });
    // 実績集計: 実績表の選択・指標・集計の変更で自動再集計。図番はEnter/ボタンで確定
    this.q<HTMLSelectElement>('#jis-file').addEventListener('change', (e) => {
      this.selectedJissekiId = Number((e.target as HTMLSelectElement).value) || null;
      void this.refreshJisseki();
    });
    for (const selector of ['#jis-metric', '#jis-agg']) {
      this.q<HTMLSelectElement>(selector).addEventListener('change', () => void this.runSummary());
    }
    this.q<HTMLInputElement>('#jis-zuban').addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); void this.runSummary(); }
    });
    this.q<HTMLButtonElement>('#jis-run').addEventListener('click', () => void this.runSummary());
    this.q<HTMLButtonElement>('#jis-save').addEventListener('click', () => void this.saveJissekiColumns());
    // セクションを開いた時点で列検出＋既定集計を実行（選択済みファイルがある場合）
    this.q<HTMLDetailsElement>('#jisseki-section').addEventListener('toggle', (e) => {
      if ((e.target as HTMLDetailsElement).open && this.selectedJissekiId !== null) void this.refreshJisseki();
    });
  }

  /** ナレッジタブ表示時にFAQ・図面台帳・候補を再取得する（ローカルDBのみ） */
  async refresh(): Promise<void> {
    await Promise.all([this.loadFaq(), this.loadDrawings(), this.loadFaqCandidates()]);
  }

  /** ナレッジ一覧の更新を実績集計の選択肢へ反映する（実績表のみ） */
  setFiles(files: KnowledgeFile[]): void {
    this.jissekiFiles = files.filter(f => f.kind === 'sheet_jisseki' && f.status === 'ready');
    const ids = this.jissekiFiles.map(f => f.file_id).join(',');
    const note = this.q<HTMLElement>('#jis-note');
    if (ids === this.lastJissekiIds && this.q<HTMLSelectElement>('#jis-file').options.length > 0) return;
    this.lastJissekiIds = ids;
    const sel = this.q<HTMLSelectElement>('#jis-file');
    const prev = this.selectedJissekiId;
    sel.innerHTML = this.jissekiFiles.length > 0
      ? this.jissekiFiles.map(f => `<option value="${f.file_id}">${esc(f.name)}</option>`).join('')
      : '<option value="">（実績表なし）</option>';
    const empty = this.jissekiFiles.length === 0;
    sel.disabled = empty;
    this.q<HTMLButtonElement>('#jis-run').disabled = empty;
    if (empty) {
      this.selectedJissekiId = null;
      note.textContent = '実績表（Excel / CSV）をナレッジに取り込むと、列の自動検出と見積・原価の集計ができます。';
      this.q<HTMLElement>('#jis-columns').textContent = '実績表を選ぶと列を自動検出します';
      this.q<HTMLElement>('#jis-result').hidden = true;
      return;
    }
    if (prev !== null && this.jissekiFiles.some(f => f.file_id === prev)) sel.value = String(prev);
    else this.selectedJissekiId = Number(sel.value) || null;
    note.textContent = '';
  }

  /** ナレッジ一覧の「実」ボタンから呼ぶ: 実績集計セクションを開いて対象ファイルを選択する */
  focusJissekiFile(fileId: number): void {
    const details = this.q<HTMLDetailsElement>('#jisseki-section');
    details.open = true;
    const sel = this.q<HTMLSelectElement>('#jis-file');
    if ([...sel.options].some(o => o.value === String(fileId))) {
      sel.value = String(fileId);
      this.selectedJissekiId = fileId;
    }
    details.scrollIntoView({ block: 'start' });
    if (this.selectedJissekiId !== null) void this.refreshJisseki();
  }

  // ---- FAQ管理（GET/POST/DELETE /api/faq） ----

  private async loadFaq(): Promise<void> {
    const list = this.q<HTMLElement>('#faq-list');
    try {
      const { items } = await api.faq('published');
      this.renderFaq(items ?? []);
    } catch {
      this.q<HTMLElement>('#faq-count').textContent = '';
      list.innerHTML = '<div class="muted">FAQを取得できませんでした（バックエンド未対応の可能性があります）</div>';
    }
  }

  private renderFaq(items: FaqEntry[]): void {
    this.q<HTMLElement>('#faq-count').textContent = `（${items.length}件）`;
    const list = this.q<HTMLElement>('#faq-list');
    if (items.length === 0) {
      list.innerHTML = '<div class="muted">公開中のFAQはありません。よくある質問を登録すると、LLMを使わずに即答します。</div>';
      return;
    }
    list.innerHTML = `<table class="table faq-table">
      <thead><tr><th>質問</th><th>回答</th><th>参照</th><th></th></tr></thead>
      <tbody>${items.map(it => `<tr>
        <td class="faq-q">${esc(it.question)}</td>
        <td class="faq-a" title="${esc(it.answer)}">${esc(truncate(it.answer, 120))}</td>
        <td>${it.hits ?? 0}</td>
        <td><button type="button" class="icon-btn" data-del="${it.id}" title="このFAQを削除">×</button></td>
      </tr>`).join('')}</tbody></table>`;
    list.querySelectorAll<HTMLButtonElement>('[data-del]').forEach(btn => {
      btn.addEventListener('click', () => void this.deleteFaq(Number(btn.dataset.del)));
    });
  }

  private async addFaq(): Promise<void> {
    const questionEl = this.q<HTMLInputElement>('#faq-question');
    const answerEl = this.q<HTMLTextAreaElement>('#faq-answer');
    const variantsEl = this.q<HTMLInputElement>('#faq-variants');
    const status = this.q<HTMLElement>('#faq-status');
    const btn = this.q<HTMLButtonElement>('#faq-add');
    const question = questionEl.value.trim();
    const answer = answerEl.value.trim();
    if (!question || !answer) { status.textContent = '質問と回答を入力してください'; return; }
    const variants = splitFaqVariants(variantsEl.value);
    btn.disabled = true;
    status.textContent = '保存中…';
    try {
      const r = await api.addFaq({ question, answer, variants: variants.length > 0 ? variants : undefined });
      if (!r.ok) { status.textContent = `保存できませんでした: ${r.error ?? '入力内容を確認してください'}`; return; }
      questionEl.value = '';
      answerEl.value = '';
      variantsEl.value = '';
      status.textContent = 'FAQを追加しました（公開済み）';
      await this.loadFaq();
    } catch (ex) {
      status.textContent = `保存できませんでした: ${(ex as Error).message}`;
    } finally {
      btn.disabled = false;
    }
  }

  private async deleteFaq(id: number): Promise<void> {
    if (!Number.isFinite(id)) return;
    const status = this.q<HTMLElement>('#faq-status');
    try {
      await api.deleteFaq(id);
      status.textContent = 'FAQを削除しました';
      await this.loadFaq();
    } catch (ex) {
      status.textContent = `削除できませんでした: ${(ex as Error).message}`;
    }
  }

  // ---- FAQ候補（👎フィードバック → 編集採用） ----

  private async loadFaqCandidates(): Promise<void> {
    const host = this.q<HTMLElement>('#faq-cand-list');
    try {
      const { items } = await api.faqCandidates('pending');
      this.renderFaqCandidates(items ?? []);
    } catch {
      this.q<HTMLElement>('#faq-cand-count').textContent = '';
      host.innerHTML = '<div class="muted">候補を取得できませんでした（バックエンド未対応の可能性があります）</div>';
    }
  }

  private renderFaqCandidates(items: FaqCandidate[]): void {
    this.q<HTMLElement>('#faq-cand-count').textContent = `（${items.length}件）`;
    const host = this.q<HTMLElement>('#faq-cand-list');
    if (items.length === 0) {
      host.innerHTML = '<div class="muted">処理待ちの候補はありません。</div>';
      return;
    }
    host.innerHTML = items.map(c => `<div class="faq-cand" data-id="${c.id}">
      <div class="faq-cand-meta muted">${c.rating === 1 ? '👍 役に立った回答' : '👎 改善してほしい'}・${esc(c.created_at)}${c.comment ? `・コメント: ${esc(truncate(c.comment, 80))}` : ''}</div>
      <input type="text" class="k-input faq-cand-q" value="${esc(c.question)}" aria-label="質問" />
      <textarea class="k-input faq-cand-a" rows="2" aria-label="回答">${esc(c.corrected_answer || c.answer)}</textarea>
      <div class="faq-form-row">
        <button type="button" class="primary small" data-adopt="${c.id}">FAQとして採用</button>
        <button type="button" class="small" data-dismiss="${c.id}">候補を削除</button>
      </div>
    </div>`).join('');
    host.querySelectorAll<HTMLButtonElement>('[data-adopt]').forEach(btn => {
      btn.addEventListener('click', () => void this.adoptFaqCandidate(Number(btn.dataset.adopt)));
    });
    host.querySelectorAll<HTMLButtonElement>('[data-dismiss]').forEach(btn => {
      btn.addEventListener('click', () => void this.deleteFaqCandidate(Number(btn.dataset.dismiss)));
    });
  }

  private async adoptFaqCandidate(id: number): Promise<void> {
    const block = this.q<HTMLElement>('#faq-cand-list').querySelector<HTMLElement>(`.faq-cand[data-id="${id}"]`);
    const status = this.q<HTMLElement>('#faq-cand-status');
    const question = block?.querySelector<HTMLInputElement>('.faq-cand-q')?.value.trim() ?? '';
    const answer = block?.querySelector<HTMLTextAreaElement>('.faq-cand-a')?.value.trim() ?? '';
    if (!question || !answer) { status.textContent = '質問と回答を入力してから採用してください'; return; }
    btnToggle(block, true);
    try {
      const r = await api.adoptFaqCandidate(id, { question, answer });
      if (!r.ok) { status.textContent = `採用できませんでした: ${r.error ?? '入力内容を確認してください'}`; return; }
      status.textContent = 'FAQとして採用しました（この質問には次から即答します）';
      await Promise.all([this.loadFaqCandidates(), this.loadFaq()]);
    } catch (ex) {
      status.textContent = `採用できませんでした: ${(ex as Error).message}`;
    } finally {
      btnToggle(block, false);
    }
  }

  private async deleteFaqCandidate(id: number): Promise<void> {
    const status = this.q<HTMLElement>('#faq-cand-status');
    try {
      await api.deleteFaqCandidate(id);
      status.textContent = '候補を削除しました';
      await this.loadFaqCandidates();
    } catch (ex) {
      status.textContent = `削除できませんでした: ${(ex as Error).message}`;
    }
  }

  // ---- 図面台帳（GET /api/drawings） ----

  private async loadDrawings(): Promise<void> {
    const host = this.q<HTMLElement>('#drawings-table-host');
    try {
      const { items } = await api.drawings(this.drawingQuery || undefined);
      this.renderDrawings(items ?? []);
    } catch {
      this.q<HTMLElement>('#drawings-count').textContent = '';
      host.innerHTML = '<div class="muted">図面台帳を取得できませんでした（図面拡張機能が無効の可能性があります）</div>';
    }
  }

  private renderDrawings(items: DrawingRow[]): void {
    this.q<HTMLElement>('#drawings-count').textContent = `（${items.length}件）`;
    const host = this.q<HTMLElement>('#drawings-table-host');
    if (items.length === 0) {
      host.innerHTML = `<div class="muted">${this.drawingQuery
        ? '条件に一致する図面はありません'
        : '図面が登録されていません（図面拡張機能ONで図面PDFを取り込むと表示されます）'}</div>`;
      return;
    }
    host.innerHTML = `<div class="table-scroll"><table class="table drawings-table">
      <thead><tr><th>図番</th><th>品名</th><th>材質</th><th>改訂</th><th>尺度</th><th>承認日</th><th>ファイル</th></tr></thead>
      <tbody>${items.map(d => `<tr>
        <td class="mono">${esc(d.zuban ?? '—')}</td>
        <td>${esc(d.hinmei ?? '—')}</td>
        <td>${esc(d.zairyo ?? '—')}</td>
        <td>${esc(d.revision ?? '—')}</td>
        <td>${esc(d.scale ?? '—')}</td>
        <td>${esc(d.approved_at ?? '—')}</td>
        <td><button type="button" class="link-btn" data-fid="${d.file_id}" title="元ファイルを開く">${esc(d.file)}</button></td>
      </tr>`).join('')}</tbody></table></div>`;
    // ファイル名クリック: 既存のナレッジと同一の元ファイル配信（/api/knowledge/{id}/file）で開く
    host.querySelectorAll<HTMLButtonElement>('[data-fid]').forEach(btn => {
      btn.addEventListener('click', () => window.open(api.fileUrl(Number(btn.dataset.fid)), '_blank', 'noopener'));
    });
  }

  // ---- 実績集計（GET /api/jisseki/columns・/api/jisseki/summary） ----

  private async refreshJisseki(): Promise<void> {
    const fileId = this.selectedJissekiId;
    if (fileId === null) return;
    const colsHost = this.q<HTMLElement>('#jis-columns');
    const seq = ++this.jisSeq;
    colsHost.textContent = '列を検出中…';
    this.q<HTMLElement>('#jis-save-row').hidden = true;
    try {
      const result = await api.jissekiColumns(fileId);
      if (seq !== this.jisSeq) return;
      this.renderJissekiColumns(result);
    } catch (ex) {
      if (seq !== this.jisSeq) return;
      colsHost.textContent = `列を取得できませんでした: ${(ex as Error).message}`;
    }
    await this.runSummary();
  }

  /** 列役割の編集UI。自動検出/手動上書きの別を表示し、ドロップダウンで役割を変えて保存できる */
  private renderJissekiColumns(result: JissekiColumnsResult): void {
    const host = this.q<HTMLElement>('#jis-columns');
    const items = result.items ?? [];
    if (items.length === 0) {
      host.textContent = '列を自動検出できませんでした（ヘッダー行のある実績表か確認してください）';
      this.q<HTMLElement>('#jis-save-row').hidden = true;
      return;
    }
    const badge = result.manual ? '<span class="jis-manual">手動設定済み</span>' : '<span class="muted">（自動検出）</span>';
    host.innerHTML = `<div class="jis-cols-edit"><span class="jis-cols-label">列の役割 ${badge}:</span>`
      + items.map(c => `<span class="jis-col" title="列 ${c.column_index + 1}"><b>${esc(c.column_name)}</b>`
        + `<select class="jis-role" data-idx="${c.column_index}" aria-label="${esc(c.column_name)}の役割">`
        + JISSEKI_ROLE_OPTIONS.map(o =>
            `<option value="${o.value}"${o.value === (c.role || 'other') ? ' selected' : ''}>${o.label}</option>`).join('')
        + `</select></span>`).join('')
      + '</div>';
    this.q<HTMLElement>('#jis-save-row').hidden = false;
  }

  /** 列役割の手動上書きを保存する。成功したら実績辞書が作り直されるため列表示と集計を再取得する */
  private async saveJissekiColumns(): Promise<void> {
    const fileId = this.selectedJissekiId;
    if (fileId === null) return;
    const status = this.q<HTMLElement>('#jis-save-status');
    const roles = [...this.q<HTMLElement>('#jis-columns').querySelectorAll<HTMLSelectElement>('.jis-role')]
      .map(sel => ({ column_index: Number(sel.dataset.idx), role: sel.value }));
    const { map, error } = buildJissekiRoleMap(roles);
    if (!map) { status.textContent = error ?? '入力内容を確認してください'; return; }
    btnToggle(this.q<HTMLElement>('#jis-save-row'), true);
    status.textContent = '再作成中…';
    try {
      const r = await api.jissekiSaveColumns(fileId, map);
      if (!r.ok) { status.textContent = `保存できませんでした: ${r.error ?? '入力内容を確認してください'}`; return; }
      status.textContent = `列設定を保存し、実績辞書を作り直しました（${r.rows ?? 0}件）`;
      await this.refreshJisseki();
    } catch (ex) {
      status.textContent = `保存できませんでした: ${(ex as Error).message}`;
    } finally {
      btnToggle(this.q<HTMLElement>('#jis-save-row'), false);
    }
  }

  private async runSummary(): Promise<void> {
    const fileId = this.selectedJissekiId;
    if (fileId === null) return;
    const metric = this.q<HTMLSelectElement>('#jis-metric').value as 'mitumori' | 'genka';
    const aggregate = this.q<HTMLSelectElement>('#jis-agg').value as 'sum' | 'avg' | 'count';
    const zuban = this.q<HTMLInputElement>('#jis-zuban').value.trim();
    const result = this.q<HTMLElement>('#jis-result');
    result.hidden = false;
    result.innerHTML = '<span class="muted">集計中…</span>';
    const seq = ++this.jisSeq;
    try {
      const s = await api.jissekiSummary({ file_id: fileId, metric, aggregate, zuban: zuban || undefined });
      if (seq !== this.jisSeq) return;
      const label = jissekiSummaryLabel(s.metric, s.aggregate);
      const detail = zuban ? `図番 ${zuban}・${s.count}件` : `${s.count}件`;
      result.innerHTML = `<span class="jis-result-label">${esc(label)}</span>`
        + `<b>${esc(formatJissekiValue(s))}</b>`
        + `<span class="muted">（${esc(detail)}から集計）</span>`;
    } catch (ex) {
      if (seq !== this.jisSeq) return;
      result.innerHTML = `<span class="muted">集計できませんでした: ${esc((ex as Error).message)}</span>`;
    }
  }
}
