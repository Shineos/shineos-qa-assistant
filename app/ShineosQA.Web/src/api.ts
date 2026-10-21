// バックエンドREST/SSEクライアント（OpenAPI契約はフェーズ1で型生成に置換予定）

export interface SourceInfo { file: string; snippet: string; text?: string; kind?: string; url?: string | null; file_id?: number; fileId?: number; zuban?: string | null; hinmei?: string | null; revision?: string | null; is_drawing?: boolean; isDrawing?: boolean }
export interface ChatSummary { uuid: string; id: number; title: string; updated_at: string; archived?: number; bookmarked?: number }
export interface ChatMessage { id?: number; role: string; content: string; image?: string | null; sources_json?: string | null; kind?: string | null; created_at?: string }
export interface ChatDetail { id: number; title: string; archived?: number; bookmarked?: number; messages: ChatMessage[] }
export interface KnowledgeFile { file_id: number; name: string; status: string; error?: string | null; chunk_count: number; added_at: string; kind?: string; zuban_raw?: string | null; hinmei?: string | null; zairyo?: string | null; revision?: string | null }
export interface StatusInfo {
  version: string; tier: string; chat_model: string; chunks: number; ram_gb: number;
  engines: { tier: string; chat_model: string; llm: string; embed: string; rank: string };
}
export interface Settings { web_search: boolean; tier: string; idle_unload_minutes: number; bg_friendly: boolean; extensions?: { drawing?: boolean } }
export interface OcrResult { text: string; zubans: { raw: string; norm: string }[]; vision?: { zuban?: string | null; hinmei?: string | null; zairyo?: string | null; revision?: string | null; shape?: string | null } | null }

async function json<T>(resp: Response): Promise<T> {
  if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
  return resp.json() as Promise<T>;
}

export const api = {
  status: () => fetch('/api/status').then(r => json<StatusInfo>(r)),
  getSettings: () => fetch('/api/settings').then(r => json<Settings>(r)),
  saveSettings: (patch: Record<string, unknown>) =>
    fetch('/api/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(patch) }),
  chats: (archived?: boolean, q?: string, bookmarked?: boolean) => {
    const ps = new URLSearchParams();
    if (archived) ps.set('archived', '1');
    if (bookmarked) ps.set('bookmarked', '1');
    if (q) ps.set('q', q);
    const qs = ps.toString();
    return fetch(`/api/chats${qs ? '?' + qs : ''}`).then(r => json<ChatSummary[]>(r));
  },
  newChat: () => fetch('/api/chats', { method: 'POST' }).then(r => json<{ uuid: string }>(r)),
  chat: (uuid: string) => fetch(`/api/chats/${uuid}`).then(r => json<ChatDetail>(r)),
  archiveChat: (uuid: string, archived: boolean) =>
    fetch(`/api/chats/${uuid}/archive`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ archived }) }),
  bookmarkChat: (uuid: string, bookmarked: boolean) =>
    fetch(`/api/chats/${uuid}/bookmark`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ bookmarked }) }),
  renameChat: (uuid: string, title: string) =>
    fetch(`/api/chats/${uuid}/rename`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ title }) }),
  deleteChat: (uuid: string) => fetch(`/api/chats/${uuid}`, { method: 'DELETE' }),
  messageImageUrl: (messageId: number) => `/api/messages/${messageId}/image`,
  knowledge: (q?: string) => fetch(`/api/knowledge${q ? `?q=${encodeURIComponent(q)}` : ''}`).then(r => json<KnowledgeFile[]>(r)),
  thumbUrl: (id: number) => `/api/knowledge/${id}/thumb`,
  fileUrl: (id: number) => `/api/knowledge/${id}/file`,
  ocr: (image: File) => {
    const fd = new FormData();
    fd.append('image', image, image.name || 'capture.png');
    return fetch('/api/ocr', { method: 'POST', body: fd }).then(r => json<OcrResult>(r));
  },
  upload: (files: File[]) => {
    const fd = new FormData();
    for (const f of files) fd.append('files', f, f.name);
    return fetch('/api/knowledge', { method: 'POST', body: fd })
      .then(r => json<{ results: { name: string; ok: boolean; file_id?: number; message?: string; error?: string; suggest_vision?: boolean }[] }>(r));
  },
  importFolder: (path: string) =>
    fetch('/api/knowledge/import', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path }) })
      .then(r => json<{ imported: number }>(r)),
  deleteKnowledge: (id: number) => fetch(`/api/knowledge/${id}`, { method: 'DELETE' }),
  retryKnowledge: (id: number) =>
    fetch(`/api/knowledge/${id}/retry`, { method: 'POST' })
      .then(r => r.json().then(j => ({ ok: (r.ok && j.ok !== false) as boolean, message: j.message as string | undefined })))
      .catch(() => ({ ok: false as boolean, message: '通信エラー' })),
  models: () => fetch('/api/models').then(r => json<{ models: ModelEntry[]; needs_wizard: boolean }>(r)),
  installModel: (id: string) =>
    fetch('/api/models/install', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id }) }),
  modelProgress: () => fetch('/api/models/progress').then(r => json<{ state: string; currentId?: string; bytes: number; total: number; error?: string }>(r)),
  deleteModel: (id: string) =>
    fetch('/api/models/delete', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id }) }),
  importModel: (path: string) =>
    fetch('/api/models/import', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path }) }),
};

export interface ModelEntry {
  id: string; name: string; file: string; kind: string; sizeBytes: number; license: string;
  installed: boolean; required: boolean; minRamGb: number;
  corrupted?: boolean; // 起動時SHA検証で破損判定済み（再ダウンロードで修復可）
}

/// POST /api/chat をSSE受信する。イベント: meta / model / web / delta / done / error
/// （作成モード v2.5.0 追加: phase / outline / section_start / section_done）
export async function streamChat(
  body: { chat_uuid?: string; message: string; web_search?: boolean; model?: string; capture_image?: string; summary_file_id?: number; draft?: { refs?: number[]; template?: string; seed?: { question: string; answer: string } } },
  handlers: {
    meta?: (d: { chat_id: number; chat_uuid: string }) => void;
    model?: (d: { tier: string; model_file: string }) => void;
    web?: (d: { results: { title: string; url: string; snippet: string }[]; error?: string | null }) => void;
    refs?: (d: { files: string[]; web: string[] }) => void;
    patch?: (content: string) => void;
    delta?: (content: string) => void;
    phase?: (d: { text: string }) => void;
    outline?: (d: { title: string; sections: { Title: string; Goal: string }[] }) => void;
    section_start?: (d: { index: number; title: string }) => void;
    section_done?: (d: { index: number }) => void;
    done?: (d: { cached: boolean; kind?: string | null; guard?: string | null; suggest_web?: boolean; template?: string; auto_inferred?: boolean; title?: string; sources?: SourceInfo[]; ttfb_ms?: number; ms?: number }) => void;
    clarify?: (d: { zuban: string; message: string; options: { file_id: number; file: string; zuban: string; revision: string | null; hinmei: string | null }[] }) => void;
    error?: (d: { code: string; message: string }) => void;
  },
  signal?: AbortSignal,
): Promise<void> {
  const resp = await fetch('/api/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
    signal,
  });
  if (!resp.ok || !resp.body) throw new Error(`HTTP ${resp.status}`);
  const reader = resp.body.getReader();
  const decoder = new TextDecoder();
  let buf = '';
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buf += decoder.decode(value, { stream: true });
    let idx: number;
    while ((idx = buf.indexOf('\n\n')) >= 0) {
      const frame = buf.slice(0, idx);
      buf = buf.slice(idx + 2);
      let ev = '';
      let data = '';
      for (const line of frame.split('\n')) {
        if (line.startsWith('event: ')) ev = line.slice(7).trim();
        else if (line.startsWith('data: ')) data += line.slice(6);
      }
      if (!ev || !data) continue;
      try {
        const parsed = JSON.parse(data);
        switch (ev) {
          case 'meta': handlers.meta?.(parsed); break;
          case 'model': handlers.model?.(parsed); break;
          case 'web': handlers.web?.(parsed); break;
          case 'refs': handlers.refs?.(parsed); break;
          case 'patch': handlers.patch?.(parsed.content ?? ''); break;
          case 'delta': handlers.delta?.(parsed.content ?? ''); break;
          case 'phase': handlers.phase?.(parsed); break;
          case 'outline': handlers.outline?.(parsed); break;
          case 'section_start': handlers.section_start?.(parsed); break;
          case 'section_done': handlers.section_done?.(parsed); break;
          case 'done': handlers.done?.(parsed); break;
          case 'clarify': handlers.clarify?.(parsed); break;
          case 'error': handlers.error?.(parsed); break;
        }
      } catch { /* 不完全フレームは無視 */ }
    }
  }
}

/// テキストをUTF-8 BOM付きの.mdファイルとしてダウンロードする（作成モード v2.5.0。
/// BOM付きでExcel/メモ帳で開いた際の文字化けを防ぐ）
export function downloadMarkdown(text: string, filename: string): void {
  const blob = new Blob(['\uFEFF' + text], { type: 'text/markdown;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 5000);
}

/// 下書きMarkdownをWord(.docx)に変換してダウンロードする（v2.5.4・ボタン押下時のみ）
export async function downloadDocx(title: string, markdown: string): Promise<void> {
  const resp = await fetch('/api/export/docx', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title, markdown }),
  });
  if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
  const blob = await resp.blob();
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = (title.replace(/[\/:*?"<>|]/g, '_').slice(0, 40) || 'draft') + '.docx';
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 5000);
}
