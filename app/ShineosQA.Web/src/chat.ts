import { api, streamChat, type SourceInfo, type ChatSummary, type ModelEntry } from './api';
import { renderMarkdown } from './markdown';

const esc = (s: string): string =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

// 絵文字は環境により表示が崩れるためインラインSVGで統一
const svgWrap = (path: string) =>
  `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${path}</svg>`;
const SVG_CLIP = svgWrap('<path d="m21.44 11.05-9.19 9.19a6 6 0 0 1-8.49-8.49l8.57-8.57A4 4 0 1 1 18 8.84l-8.59 8.57a2 2 0 0 1-2.83-2.83l8.49-8.48"/>');
const SVG_GLOBE = svgWrap('<circle cx="12" cy="12" r="10"/><path d="M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20"/><path d="M2 12h20"/>');
const SVG_DOC = svgWrap('<path d="M14.5 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7.5L14.5 2z"/><path d="M14 2v6h6"/>');
const SVG_ZAP = svgWrap('<polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2"/>');
// 標準モデルのアイコン: 同心円だけの図形は「読み込み中スピナー」と紛らわしいので
// 十字線付きの照準（crosshair）型にして区別させる
const SVG_TARGET = svgWrap('<circle cx="12" cy="12" r="7"/><circle cx="12" cy="12" r="1.5"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3"/>');
const SVG_TROPHY = svgWrap('<path d="M6 9H4.5a2.5 2.5 0 0 1 0-5H6"/><path d="M18 9h1.5a2.5 2.5 0 0 0 0-5H18"/><path d="M4 22h16"/><path d="M10 14.66V17c0 .55-.47.98-.97 1.21C7.85 18.75 7 20.24 7 22"/><path d="M14 14.66V17c0 .55.47.98.97 1.21C16.15 18.75 17 20.24 17 22"/><path d="M18 2H6v7a6 6 0 0 0 12 0V2Z"/>');
// メッセージカード時刻横のコピーアイコン（コピー完了時はチェックに差し替え）
const SVG_COPY = svgWrap('<rect x="9" y="9" width="13" height="13" rx="2" ry="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/>');
const SVG_CHECK = svgWrap('<polyline points="20 6 9 17 4 12"/>');
const SVG_ARCHIVE = svgWrap('<rect x="2" y="3" width="20" height="5" rx="1"/><path d="M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8"/><path d="M10 12h4"/>');
const SVG_ARCHIVE_RESTORE = svgWrap('<rect x="2" y="3" width="20" height="5" rx="1"/><path d="M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8"/><path d="M10 12h4"/><path d="m9 16 3-3 3 3"/>');

/** キャプチャ画像を data URL 化して送信Payloadに含める（サーバ側でローカル保存し過去チャットでも表示） */
function fileToDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(r.result as string);
    r.onerror = () => reject(r.error);
    r.readAsDataURL(file);
  });
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

/** モデル階級 → 表示情報（送信フォームのセレクター用） */
const MODEL_CHOICES = [
  { id: 'chat-quick', tier: 'quick', label: '⚡ クイック 1.7B', desc: '高速・低負荷（8GB以上）', icon: SVG_ZAP },
  { id: 'chat-standard', tier: 'standard', label: '🎯 標準 4B', desc: '高精度（16GB以上推奨）', icon: SVG_TARGET },
  { id: 'chat-quality', tier: 'quality', label: '🏆 高品質 30B', desc: '最高精度・実験的（16GB以上・初回読込遅め）', icon: SVG_TROPHY },
] as const;

function fmtTime(d: Date): string {
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}
function fmtTimeSec(d: Date): string {
  return `${fmtTime(d)}:${String(d.getSeconds()).padStart(2, '0')}`;
}
function fmtFull(d: Date): string {
  return `${d.getFullYear()}年${String(d.getMonth() + 1).padStart(2, '0')}月${String(d.getDate()).padStart(2, '0')}日 ${fmtTimeSec(d)}`;
}
/// サーバの created_at（UTC "YYYY-MM-DD HH:MM:SS"）をローカル時刻へ
function parseServerTime(s?: string): Date | null {
  if (!s) return null;
  const d = new Date(s.replace(' ', 'T') + 'Z');
  return isNaN(d.getTime()) ? null : d;
}
function domainOf(u: string): string {
  try { return new URL(u).hostname.replace(/^www\./, ''); } catch { return u; }
}

/** 出典ビューア: 該当箇所をハイライトして文書全体（チャンク）を表示 */
function openSourceModal(src: SourceInfo) {
  const host = document.getElementById('modal-host')!;
  host.innerHTML = '';
  const ov = document.createElement('div');
  ov.className = 'src-overlay';
  const body = src.text && src.text.length > 0 ? src.text : src.snippet;
  const needle = src.snippet.slice(0, Math.min(80, src.snippet.length));
  let highlighted: string;
  const idx = body.indexOf(needle);
  if (idx >= 0) {
    highlighted = esc(body.slice(0, idx)) + '<mark>' + esc(body.slice(idx, idx + src.snippet.length)) + '</mark>' + esc(body.slice(idx + src.snippet.length));
  } else {
    highlighted = esc(body) + (src.snippet ? '<hr><b>該当箇所:</b> ' + esc(src.snippet) : '');
  }
  ov.innerHTML = `
    <div class="src-modal">
      <div class="src-head"><span class="src-file">${SVG_DOC} ${esc(src.file)}</span><button class="icon-btn" data-close>✕ 閉じる</button></div>
      <div class="src-body">${highlighted}</div>
    </div>`;
  ov.addEventListener('click', e => { if (e.target === ov) host.innerHTML = ''; });
  ov.querySelector('[data-close]')!.addEventListener('click', () => { host.innerHTML = ''; });
  host.appendChild(ov);
}

function sourceElement(s: SourceInfo): HTMLElement {
  // 図面フラグ・ファイルIDは保存時期により camelCase/fileId と snake_case/file_id が混在する（旧チャット互換）
  const fid = s.file_id ?? s.fileId;
  const drawing = s.is_drawing ?? s.isDrawing;
  const row = document.createElement('div');
  row.className = 'source' + (s.kind === 'web' ? ' web-source' : '');
  row.tabIndex = 0;
  if (s.kind === 'web' && s.url) {
    // Web検索出典: タイトル＋URL＋プレビュー（クリックで新しいタブで開く）
    row.innerHTML = `<span class="src-ic">${SVG_GLOBE}</span><div class="src-main"><b>${esc(s.file)}</b>
      <div class="source-url">${esc(s.url)}</div>
      <div class="source-snippet">${esc(s.snippet.slice(0, 110))}…</div></div>`;
    row.addEventListener('click', () => window.open(s.url!, '_blank', 'noopener,noreferrer'));
  } else if (drawing && fid) {
    // 図面出典（拡張パック）: 図番＋品名＋改訂を表示。クリックで**元ファイル**を別タブに開く
    // （PDF図面は画像・線を含むオリジナルそのもの。テキスト抽出チャンクは別物）。
    // 図番が抽出できない図面（スキャン図面・DXF等）はファイル名を見出しにして【図面】種別だけは示す
    const rev = s.revision ? `・改訂${esc(s.revision)}` : '';
    const heading = s.zuban
      ? `<b>【図】${esc(s.zuban)}</b>` + (s.hinmei ? `<span class="muted">（${esc(s.hinmei)}${rev}）</span>` : '')
      : `<b>【図面】</b><span class="muted">${esc(s.file)}</span>`;
    row.innerHTML = `<span class="src-ic">${SVG_DOC}</span><div class="src-main">${heading}` +
      `<div class="source-snippet">${esc(s.snippet.slice(0, 90))}…</div></div>`;
    row.title = 'クリックで元ファイルを開く';
    row.addEventListener('click', () => window.open(api.fileUrl(fid), '_blank'));
  } else {
    row.innerHTML = `<span class="src-ic">${SVG_DOC}</span><div class="src-main"><b>${esc(s.file)}</b><div class="source-snippet">${esc(s.snippet.slice(0, 90))}…</div></div>`;
    row.addEventListener('click', () => openSourceModal(s));
  }
  return row;
}

export class ChatView {
  private chatUuid = '';
  private sending = false;
  private webSearch = false;
  private selTier = '';
  private modelsById = new Map<string, ModelEntry>();
  private modelMenu: HTMLElement | null = null;
  private packDrawing = false;               // 拡張パック（図面）: キャプチャ入力の表示条件
  private pendingCapture: { objectUrl: string; file: File } | null = null;
  private pendingZuban = '';
  private showArchived = false;              // サイドバー一覧のアーカイブビュー切替

  constructor() {
    const form = document.getElementById('chat-form') as HTMLFormElement;
    const input = document.getElementById('chat-input') as HTMLTextAreaElement;
    const webBtn = document.getElementById('web-btn') as HTMLButtonElement;

    form.addEventListener('submit', (e) => { e.preventDefault(); void this.send(); });
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void this.send(); }
    });
    input.addEventListener('input', () => {
      input.style.height = 'auto';
      input.style.height = Math.min(input.scrollHeight, 160) + 'px';
    });
    document.getElementById('new-chat')!.addEventListener('click', () => this.newChat());
    webBtn.addEventListener('click', () => {
      this.webSearch = !this.webSearch;
      webBtn.classList.toggle('active', this.webSearch);
    });

    // 📎 添付: ファイルをその場でナレッジ登録（ZCodeスタイル）
    const attachBtn = document.getElementById('attach-btn') as HTMLButtonElement;
    const attachInput = document.getElementById('attach-input') as HTMLInputElement;
    attachBtn.addEventListener('click', () => attachInput.click());
    attachInput.addEventListener('change', async () => {
      const files = [...attachInput.files!];
      attachInput.value = '';
      if (files.length === 0) return;
      this.setStatus(`📎 ${files.map(f => f.name).join(', ')} を登録中…（解析とベクトル化に時間がかかります）`);
      try {
        const r = await api.upload(files);
        const ok = r.results.filter(x => x.ok).length;
        const errs = r.results.filter(x => !x.ok).map(x => `${x.name}: ${x.message ?? x.error}`);
        this.setStatus(`📎 ${ok}/${r.results.length}件を登録しました。すぐに質問できます${errs.length ? ' ／ 失敗: ' + errs.join(', ') : ''}`);
      } catch (ex) {
        this.setStatus(`📎 登録失敗: ${(ex as Error).message}`);
      }
    });
    // 📸 キャプチャ（拡張パックON時のみ表示）: Win+Shift+S → Ctrl+V で図面の一部から質問
    const captureBtn = document.getElementById('capture-btn') as HTMLButtonElement;
    captureBtn.addEventListener('click', () =>
      this.setStatus('📸 Win+Shift+S で画面（図面の表題欄など）を切り取り、入力欄に Ctrl+V で貼り付けてください'));
    input.addEventListener('paste', (e) => this.onPaste(e));

    // モデルセレクター: 現在の階級を表示し、未導入モデルはその場でダウンロード可能
    document.getElementById('model-btn')!.addEventListener('click', () => void this.toggleModelMenu());
    void this.initModelPicker();
    document.addEventListener('click', e => {
      if (this.modelMenu && !(e.target as HTMLElement).closest('.model-wrap')) this.closeModelMenu();
    });

    // URLルーティング: /c/{uuid} で開く（リロード・共有で会話を復元）
    window.addEventListener('popstate', () => this.routeFromUrl());
    this.routeFromUrl();

    void this.refresh();
  }

  /** タブ表示時に呼ばれる（main.ts activateTab）。拡張パックの状態もここで再反映する
   *  （設定画面でのトグル直後にチャットへ戻ってもキャプチャボタンが即座に切替わる） */
  async refresh() {
    const captureBtn = document.getElementById('capture-btn') as HTMLButtonElement;
    const webBtn = document.getElementById('web-btn') as HTMLButtonElement;
    try {
      const s = await api.getSettings();
      this.webSearch = s.web_search;
      webBtn.classList.toggle('active', this.webSearch);
      this.packDrawing = !!s.extensions?.drawing;
      captureBtn.hidden = !this.packDrawing;
    } catch { /* 設定取得失敗時は現状維持 */ }
  }
  /** 現在のチャットのURLパス（タブ復帰用） */
  currentPath(): string {
    return this.chatUuid ? `/c/${this.chatUuid}` : '/';
  }

  private routeFromUrl() {
    const m = location.pathname.match(/^\/c\/([0-9a-zA-Z]+)/);
    if (m) void this.openChat(m[1]);
    else { this.chatUuid = ''; this.clearMessages(); }
    void this.refreshList();
  }

  private nav(uuid: string) {
    this.chatUuid = uuid;
    if (location.pathname !== `/c/${uuid}`) history.pushState({}, '', uuid ? `/c/${uuid}` : '/');
  }

  private async refreshList(selectUuid = '') {
    const chats: ChatSummary[] = await api.chats(this.showArchived);
    const list = document.getElementById('chat-list')!;
    list.innerHTML = '';
    // アーカイブ切替ヘッダー（通常一覧の最上部にのみ出す）
    if (!this.showArchived) {
      const archivedToggle = document.createElement('button');
      archivedToggle.className = 'archive-toggle';
      archivedToggle.innerHTML = `${SVG_ARCHIVE} アーカイブ済みを表示`;
      archivedToggle.addEventListener('click', () => { this.showArchived = true; void this.refreshList(selectUuid); });
      list.appendChild(archivedToggle);
    } else {
      const backToggle = document.createElement('button');
      backToggle.className = 'archive-toggle';
      backToggle.innerHTML = `${SVG_ARCHIVE_RESTORE} 通常のチャットに戻る`;
      backToggle.addEventListener('click', () => { this.showArchived = false; void this.refreshList(selectUuid); });
      list.appendChild(backToggle);
    }
    for (const c of chats) {
      const el = document.createElement('div');
      el.className = 'chat-item' + (c.uuid === (selectUuid || this.chatUuid) ? ' active' : '');
      const title = document.createElement('span');
      title.textContent = c.title;
      title.addEventListener('click', () => void this.openChat(c.uuid));
      // アーカイブ ⇄ 復元（データは消さずに一覧の出し入れだけを行う）
      const arc = document.createElement('button');
      arc.className = 'icon-btn';
      arc.innerHTML = this.showArchived ? SVG_ARCHIVE_RESTORE : SVG_ARCHIVE;
      arc.title = this.showArchived ? 'アーカイブから戻す' : 'アーカイブ';
      arc.addEventListener('click', async (e) => {
        e.stopPropagation();
        await api.archiveChat(c.uuid, !this.showArchived);
        await this.refreshList(selectUuid);
      });
      const del = document.createElement('button');
      del.className = 'icon-btn';
      del.textContent = '×';
      del.title = '削除';
      del.addEventListener('click', async (e) => {
        e.stopPropagation();
        await api.deleteChat(c.uuid);
        if (c.uuid === this.chatUuid) { this.chatUuid = ''; history.pushState({}, '', '/'); this.clearMessages(); }
        await this.refreshList();
      });
      el.append(title, arc, del);
      list.appendChild(el);
    }
  }

  private newChat() {
    this.chatUuid = '';
    history.pushState({}, '', '/');
    this.clearMessages();
    void this.refreshList();
    (document.getElementById('chat-input') as HTMLTextAreaElement).focus();
  }

  clearMessages() {
    const m = document.getElementById('messages')!;
    m.innerHTML = `<div class="empty"><div class="empty-icon">💬</div>
      <h2>社内規定・業務マニュアルについて質問してください</h2>
      <p>回答には出典（文書名・該当箇所）が付きます。ナレッジにない質問には「該当する記載がありません」と回答します。</p></div>`;
  }

  private async openChat(uuid: string) {
    this.nav(uuid);
    let detail;
    try { detail = await api.chat(uuid); } catch { this.clearMessages(); return; }
    const m = document.getElementById('messages')!;
    m.innerHTML = '';
    if (detail.messages.length === 0) { this.clearMessages(); }
    for (const msg of detail.messages) {
      let sources: SourceInfo[] = [];
      try { sources = msg.sources_json ? JSON.parse(msg.sources_json) : []; } catch { /* ignore */ }
      const t = parseServerTime(msg.created_at);
      // 過去チャットでもキャプチャ画像を表示する（ローカル保存された画像をサーバから配信）
      const imageUrl = msg.image && msg.id ? api.messageImageUrl(msg.id) : undefined;
      this.appendMessage(msg.role, msg.content, sources, t ?? undefined, imageUrl);
    }
    void this.refreshList(uuid);
    m.scrollTop = m.scrollHeight;
  }

  private appendMessage(role: string, content: string, sources: SourceInfo[] = [], time?: Date, imageUrl?: string): HTMLElement {
    const m = document.getElementById('messages')!;
    const empty = m.querySelector('.empty');
    if (empty) empty.remove();
    const wrap = document.createElement('div');
    wrap.className = 'msg ' + (role === 'user' ? 'user' : 'assistant');
    const card = document.createElement('div');
    card.className = 'msg-card';
    const body = document.createElement('div');
    body.className = 'msg-body';
    body.innerHTML = renderMarkdown(content);
    card.appendChild(body);
    if (imageUrl) {
      const img = document.createElement('img');
      img.className = 'msg-image';
      img.src = imageUrl;
      img.alt = 'キャプチャ画像';
      card.appendChild(img);
    }
    if (sources.length > 0) {
      const src = document.createElement('details');
      src.className = 'sources';
      src.innerHTML = `<summary>${SVG_CLIP} 出典 (${sources.length})</summary>`;
      for (const s of sources) src.appendChild(sourceElement(s));
      card.appendChild(src);
    }
    wrap.appendChild(card);
    this.appendTimeRow(wrap, time, content);
    m.appendChild(wrap);
    m.scrollTop = m.scrollHeight;
    return card;
  }

  // ---- キャプチャ入力（拡張パック・図面）: ペースト画像 → OCR → 確認チップ → 図番前置きで送信 ----

  private onPaste(e: ClipboardEvent) {
    if (!this.packDrawing) return;
    const items = e.clipboardData?.items ?? [];
    for (const it of items) {
      if (it.type.startsWith('image/')) {
        const file = it.getAsFile();
        if (file) { e.preventDefault(); void this.handleCapture(file); }
        return;
      }
    }
  }

  private async handleCapture(file: File) {
    this.clearCapture(true);
    const objectUrl = URL.createObjectURL(file);
    this.pendingCapture = { objectUrl, file };
    const host = document.getElementById('capture-host')!;
    host.innerHTML = `<div class="chip"><img class="chip-thumb" src="${objectUrl}" alt=""><span class="chip-text">画像を読み取り中…</span><button type="button" class="icon-btn" title="キャンセル">✕</button></div>`;
    host.querySelector('.icon-btn')!.addEventListener('click', () => this.clearCapture(true));
    try {
      const r = await api.ocr(file);
      this.pendingZuban = r.zubans[0]?.raw ?? '';
      const textEl = host.querySelector('.chip-text')!;
      if (this.pendingZuban) {
        // 確認チップ: OCR結果を1タップで修正できるようにする（読み間違いを致命傷にしない設計）
        textEl.textContent = '';
        const inp = document.createElement('input');
        inp.type = 'text';
        inp.className = 'chip-input';
        inp.value = this.pendingZuban;
        inp.title = '図番（修正できます）';
        inp.addEventListener('keydown', (ev) => {
          if (ev.key === 'Enter') { ev.preventDefault(); (document.getElementById('chat-input') as HTMLTextAreaElement).focus(); }
        });
        const lbl = document.createElement('span');
        lbl.textContent = 'の図面について質問';
        textEl.append(inp, lbl);
        inp.focus();
        inp.select();
      } else {
        textEl.textContent = '図番を読み取れませんでした（画像は質問に添付されます）';
      }
    } catch (ex) {
      const textEl = host.querySelector('.chip-text');
      if (textEl) textEl.textContent = `読み取り失敗: ${(ex as Error).message}`;
    }
  }

  private clearCapture(revoke: boolean) {
    if (this.pendingCapture && revoke) URL.revokeObjectURL(this.pendingCapture.objectUrl);
    this.pendingCapture = null;
    this.pendingZuban = '';
    const host = document.getElementById('capture-host');
    if (host) host.innerHTML = '';
  }

  /** 時刻行（カードの外・下）。時刻の横にコピーアイコン: rawTextはユーザーは入力テキスト、
   *  AI回答はMarkdownソース（DB保存内容と同一）をそのままクリップボードへ */
  private appendTimeRow(wrap: HTMLElement, time: Date | undefined, rawText: string): void {
    const row = document.createElement('div');
    row.className = 'msg-time';
    if (time) {
      const tm = document.createElement('span');
      tm.textContent = fmtTime(time);
      tm.title = fmtFull(time);
      row.appendChild(tm);
    }
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'copy-btn';
    btn.title = 'コピー';
    btn.setAttribute('aria-label', 'メッセージをコピー');
    btn.innerHTML = SVG_COPY;
    btn.addEventListener('click', async () => {
      if (!(await copyText(rawText))) return;
      btn.innerHTML = SVG_CHECK;
      btn.classList.add('copied');
      btn.title = 'コピーしました';
      window.setTimeout(() => {
        btn.innerHTML = SVG_COPY;
        btn.classList.remove('copied');
        btn.title = 'コピー';
      }, 1500);
    });
    row.appendChild(btn);
    wrap.appendChild(row);
  }

  /** 思考中インジケータ（カードなし・回答開始でカード表示に切替）。
   *  refs: 今参照している資料/サイトの1行表示（ZCode方式） */
  private createThinking(): { setPhase: (t: string) => void; setRefs: (kb: string[], web: string[]) => void; remove: () => void; el: HTMLElement } {
    const m = document.getElementById('messages')!;
    const empty = m.querySelector('.empty');
    if (empty) empty.remove();
    const wrap = document.createElement('div');
    wrap.className = 'msg assistant thinking-block';
    wrap.innerHTML = `<div class="thinking">
        <div class="thinking-line"><span class="spinner"></span><span class="phase">考え中…</span><span class="elapsed"></span></div>
        <div class="thinking-refs" hidden></div>
      </div>`;
    m.appendChild(wrap);
    m.scrollTop = m.scrollHeight;
    const refsEl = wrap.querySelector('.thinking-refs') as HTMLElement;
    let kbRefs: string[] = [], webRefs: string[] = [];
    const renderRefs = () => {
      const lines: string[] = [];
      if (kbRefs.length > 0) lines.push(`📄 ${kbRefs.join('、')}`);
      if (webRefs.length > 0) lines.push(`🌐 ${webRefs.join('、')}`);
      refsEl.innerHTML = lines.map(l => `<div>${esc(l)}</div>`).join('');
      refsEl.hidden = lines.length === 0;
    };
    const t0 = Date.now();
    const timer = window.setInterval(() => {
      const el = wrap.querySelector('.elapsed') as HTMLElement;
      if (el) el.textContent = Math.round((Date.now() - t0) / 1000) + '秒';
    }, 1000);
    return {
      el: wrap,
      setPhase: (t: string) => { (wrap.querySelector('.phase') as HTMLElement).textContent = t; },
      setRefs: (kb, web) => { kbRefs = kb; webRefs = web; renderRefs(); },
      remove: () => { window.clearInterval(timer); wrap.remove(); },
    };
  }

  // ---- モデルセレクター（送信フォーム） ----

  private async initModelPicker(): Promise<void> {
    try {
      const [st, { models }] = await Promise.all([api.status(), api.models()]);
      this.selTier = st.engines.tier;
      this.modelsById = new Map(models.map(m => [m.id, m]));
      this.renderModelBtn();
    } catch { /* サーバ停止時は無視 */ }
  }

  private renderModelBtn(): void {
    const label = document.getElementById('model-btn-label');
    if (!label) return;
    const c = MODEL_CHOICES.find(x => x.tier === this.selTier);
    label.textContent = c ? c.label.replace(/^(⚡|🎯|🏆) /, '') : 'モデル';
    const btn = document.getElementById('model-btn');
    if (!btn) return;
    // ボタン内のアイコンも選択階級に合わせる（index.htmlの初期値は⚡）
    const svg = btn.querySelector('svg');
    if (svg && c) svg.outerHTML = c.icon;
    btn.title = c
      ? `回答に使うAIモデル: ${c.label}（クリックで変更）`
      : '回答に使うAIモデルを選択';
  }

  private closeModelMenu(): void {
    this.modelMenu?.remove();
    this.modelMenu = null;
  }

  private async toggleModelMenu(): Promise<void> {
    if (this.modelMenu) { this.closeModelMenu(); return; }
    // 開くたびに最新状態を取得（設定タブや他画面での変化を反映）
    try {
      const [st, { models }] = await Promise.all([api.status(), api.models()]);
      this.selTier = st.engines.tier;
      this.modelsById = new Map(models.map(m => [m.id, m]));
    } catch { /* 取得失敗時は既知の情報で表示 */ }
    this.renderModelBtn();

    const wrap = document.querySelector('.model-wrap') as HTMLElement;
    const menu = document.createElement('div');
    menu.className = 'model-menu';
    for (const c of MODEL_CHOICES) {
      const m = this.modelsById.get(c.id);
      const installed = m?.installed ?? false;
      const corrupted = installed && (m as { corrupted?: boolean } | undefined)?.corrupted === true;
      const active = this.selTier === c.tier && installed && !corrupted;
      const opt = document.createElement('div');
      opt.className = 'model-opt' + (active ? ' active' : '');
      opt.innerHTML = `
        <span class="model-ic">${c.icon}</span>
        <div class="model-txt"><b>${esc(c.label)}</b><span class="muted">${esc(c.desc)}${m ? '・' + (m.sizeBytes / 1024 / 1024 / 1024).toFixed(1) + 'GB' : ''}</span></div>
        <span class="model-state"></span>`;
      const state = opt.querySelector('.model-state') as HTMLElement;
      if (!installed) state.innerHTML = '<span class="badge-undl">未DL</span>';
      if (corrupted) {
        // 破損検出: 再ダウンロードで修復できる（バックエンドが破損ファイルを置き換える）
        state.innerHTML = '<span class="badge-corrupt">破損</span>';
        state.insertAdjacentHTML('beforeend', '<button type="button" class="primary small dl-btn"><span class="pct">再ダウンロード</span></button>');
        const btn = state.querySelector('button') as HTMLButtonElement;
        btn.addEventListener('click', async ev => {
          ev.stopPropagation();
          if (btn.disabled) return;
          btn.disabled = true;
          btn.innerHTML = '<span class="spinner mini"></span><span class="pct">0%</span>';
          try {
            void api.installModel(c.id).catch(err => console.error(err));
            await this.pollModelDownload(c.id, btn, opt);
            this.selTier = c.tier;
            this.renderModelBtn();
            this.closeModelMenu();
            void api.saveSettings({ tier: c.tier });
            this.setStatus(`${c.label} を再ダウンロードして修復しました。次の質問から使用します`);
          } catch (ex) {
            btn.disabled = false;
            btn.innerHTML = '<span class="pct">再試行</span>';
            this.setStatus(`モデルの再ダウンロードに失敗しました: ${(ex as Error).message}`);
          }
        });
      } else if (active) {
        state.insertAdjacentHTML('beforeend', '<span class="badge-inuse">使用中</span>');
      } else if (installed) {
        // 導入済み: 行のどこでもクリックで即切り替え（ボタンなし）
        opt.classList.add('selectable');
        opt.addEventListener('click', () => {
          this.selTier = c.tier;
          this.renderModelBtn();
          this.closeModelMenu();
          void api.saveSettings({ tier: c.tier });
          this.setStatus(`回答モデルを ${c.label} に切り替えました（モデル読込中…）`);
        });
      } else {
        // 未DL: クリック前にスピナーは表示しない（テキストのみ）。クリックで無効化してスピナー＋進捗に切替
        state.insertAdjacentHTML('beforeend', '<button type="button" class="primary small dl-btn"><span class="pct">ダウンロード</span></button>');
        const btn = state.querySelector('button') as HTMLButtonElement;
        btn.addEventListener('click', async ev => {
          ev.stopPropagation();
          if (btn.disabled) return;
          btn.disabled = true;
          btn.innerHTML = '<span class="spinner mini"></span><span class="pct">0%</span>';
          try {
            void api.installModel(c.id).catch(err => console.error(err));
            await this.pollModelDownload(c.id, btn, opt);
            this.selTier = c.tier;
            this.renderModelBtn();
            this.closeModelMenu();
            void api.saveSettings({ tier: c.tier }); // DL完了時点でエンジン読込開始
            this.setStatus(`${c.label} のダウンロードが完了しました。次の質問から使用します`);
          } catch (ex) {
            btn.disabled = false;
            btn.innerHTML = '<span class="pct">再試行</span>';
            this.setStatus(`モデルのダウンロードに失敗しました: ${(ex as Error).message}`);
          }
        });
      }
      menu.appendChild(opt);
    }
    wrap.appendChild(menu);
    this.modelMenu = menu;
  }

  /** モデルDLの進捗をスピナー内の%表示に反映（ウィザードと同じ進捗APIを利用） */
  private async pollModelDownload(id: string, btn: HTMLButtonElement, opt: HTMLElement): Promise<void> {
    const txt = opt.querySelector('.model-txt .muted') as HTMLElement;
    const pct = () => btn.querySelector('.pct') as HTMLElement | null;
    for (;;) {
      await new Promise(r => setTimeout(r, 800));
      const p = await api.modelProgress();
      if (p.currentId === id || p.state === 'idle') {
        if (p.state === 'downloading' && p.total > 0) {
          const per = Math.min(100, Math.round((p.bytes / p.total) * 100));
          if (pct()) pct()!.textContent = `${per}%`;
          if (txt) txt.textContent = `${(p.bytes / 1024 / 1024).toFixed(0)} / ${(p.total / 1024 / 1024).toFixed(0)} MB（${per}%）`;
        } else if (p.state === 'verifying') {
          if (pct()) pct()!.textContent = '検証中';
        } else if (p.state === 'done' || p.state === 'idle') {
          return;
        } else if (p.state === 'error') {
          throw new Error(p.error ?? 'ダウンロード失敗');
        }
      }
    }
  }

  private setStatus(text: string, show = true) {
    const el = document.getElementById('chat-status') as HTMLElement;
    el.textContent = text;
    el.hidden = !show || !text;
  }

  private async send() {
    if (this.sending) return;
    const input = document.getElementById('chat-input') as HTMLTextAreaElement;
    const text = input.value.trim();
    if (!text) return;
    input.value = '';
    input.style.height = 'auto';
    // キャプチャ（拡張パック）: 確認チップの図番（修正可）を質問文に前置きする
    const chipInput = document.querySelector('#capture-host .chip-input') as HTMLInputElement | null;
    const zuban = this.pendingZuban ? (chipInput?.value.trim() || this.pendingZuban) : '';
    const captureFile = this.pendingCapture?.file;
    const captureUrl = this.pendingCapture?.objectUrl;
    const message = zuban ? `（図番: ${zuban}）${text}` : text;
    this.clearCapture(false);
    this.sending = true;
    (document.getElementById('send-btn') as HTMLButtonElement).disabled = true;
    this.setStatus('', false); // 前回質問の残置ステータスを消す
    this.appendMessage('user', message, [], new Date(), captureUrl);
    const thinking = this.createThinking();
    let acc = '';
    let liveCard: HTMLElement | null = null;
    let liveBody: HTMLElement | null = null;
    const t0 = performance.now();

    try {
      // キャプチャ画像をローカル保存用に送る（data URL。サーバ側で data/files/captures/ に保存し過去チャットでも表示）
      const captureImage = captureFile ? await fileToDataUrl(captureFile) : undefined;
      await streamChat(
        { chat_uuid: this.chatUuid || undefined, message, web_search: this.webSearch, model: this.selTier || undefined, capture_image: captureImage },
        {
          meta: (d) => {
            if (!this.chatUuid && d.chat_uuid) { this.chatUuid = d.chat_uuid; history.replaceState({}, '', `/c/${this.chatUuid}`); }
            thinking.setPhase('ナレッジを検索中…');
          },
          model: (d) => {
            const label = d.tier === 'standard' ? '標準 4B' : 'クイック 1.7B';
            thinking.setPhase(`🔄 ${label} へ切り替え中…（初回はモデルの読込に時間がかかります）`);
          },
          web: (d) => {
            if (d.error) {
              thinking.setPhase('⚠️ Web検索に失敗（ナレッジのみで回答）…');
              this.setStatus('⚠️ Web検索に失敗しました。ナレッジのみで回答します。');
            } else {
              thinking.setPhase('🌐 Web検索を実行中…');
              const domains = [...new Set(d.results.map(r => domainOf(r.url)))].slice(0, 3);
              if (d.results.length > 3) domains.push(`ほか${d.results.length - 3}件`);
              thinking.setRefs([], domains);
            }
          },
          refs: (d) => {
            thinking.setPhase('回答を生成中…');
            const domains = [...new Set((d.web ?? []).map(u => domainOf(u)))].slice(0, 3);
            thinking.setRefs(d.files ?? [], domains);
          },
          patch: (content) => {
            // 出典行正規化などの最終版本文で差し替え
            acc = content;
            if (liveBody) liveBody.innerHTML = renderMarkdown(acc);
          },
          delta: (c) => {
            if (!liveCard) {
              thinking.remove();
              const wrap = document.createElement('div');
              wrap.className = 'msg assistant';
              liveCard = document.createElement('div');
              liveCard.className = 'msg-card';
              liveBody = document.createElement('div');
              liveBody.className = 'msg-body';
              liveCard.appendChild(liveBody);
              wrap.appendChild(liveCard);
              document.getElementById('messages')!.appendChild(wrap);
            }
            acc += c;
            liveBody!.innerHTML = renderMarkdown(acc);
            const m = document.getElementById('messages')!;
            m.scrollTop = m.scrollHeight;
            this.setStatus('', false);
          },
          done: (d) => {
            if (liveCard && liveBody) {
              if (d.sources && d.sources.length > 0) {
                const src = document.createElement('details');
                src.className = 'sources';
                src.innerHTML = `<summary>${SVG_CLIP} 出典 (${d.sources.length})${d.cached ? '・キャッシュ' : ''}</summary>`;
                for (const s of d.sources) src.appendChild(sourceElement(s));
                liveCard.appendChild(src);
              }
              // 時刻はカードの外（下）に追加。コピーアイコンは acc（patch適用後の最終Markdown）をコピー対象にする
              this.appendTimeRow(liveCard.parentElement!, new Date(), acc);
            } else {
              thinking.remove();
              this.appendMessage('assistant', acc || '', d.sources ?? [], new Date());
            }
            const secs = ((performance.now() - t0) / 1000).toFixed(1);
            this.setStatus(`⏱ ${secs}s${d.cached ? '（キャッシュから即答）' : ''}${d.guard ? '（該当なし）' : ''}`);
            void this.refreshList(this.chatUuid);
          },
          error: (e) => {
            thinking.remove();
            this.appendMessage('assistant', `⚠️ ${e.message}`, [], new Date());
          },
        },
      );
    } catch (ex) {
      thinking.remove();
      this.appendMessage('assistant', `⚠️ 接続エラー: ${(ex as Error).message}`, [], new Date());
    } finally {
      thinking.remove();
      this.sending = false;
      (document.getElementById('send-btn') as HTMLButtonElement).disabled = false;
      input.focus();
    }
  }
}
