// バックエンドREST/SSEクライアント（OpenAPI契約はフェーズ1で型生成に置換予定）

export interface SourceInfo { file: string; snippet: string; text?: string; kind?: string; url?: string | null; file_id?: number; fileId?: number; zuban?: string | null; hinmei?: string | null; revision?: string | null; is_drawing?: boolean; isDrawing?: boolean }
export interface ChatSummary { uuid: string; id: number; title: string; updated_at: string; archived?: number; bookmarked?: number }
export interface ChatMessage { id?: number; role: string; content: string; image?: string | null; sources_json?: string | null; kind?: string | null; created_at?: string }
export interface ChatDetail { id: number; title: string; archived?: number; bookmarked?: number; messages: ChatMessage[] }
export interface KnowledgeFile { file_id: number; name: string; status: string; error?: string | null; chunk_count: number; added_at: string; kind?: string; zuban_raw?: string | null; hinmei?: string | null; zairyo?: string | null; revision?: string | null; hit_count?: number; enabled?: number; total_chars?: number }
export interface StatusInfo {
  version: string; tier: string; chat_model: string; chunks: number; ram_gb: number;
  engines: { tier: string; chat_model: string; llm: string; embed: string; rank: string };
}
export interface Settings { web_search: boolean; tier: string; idle_unload_minutes: number; bg_friendly: boolean; extensions?: { drawing?: boolean } }
export interface OcrResult { text: string; zubans: { raw: string; norm: string }[]; vision?: { zuban?: string | null; hinmei?: string | null; zairyo?: string | null; revision?: string | null; shape?: string | null } | null }

/// 回答言語（多言語対応）。ja（既定）以外はLLMへの回答言語指示に使われる。
export type AnswerLang = 'ja' | 'en' | 'vi' | 'zh' | 'id' | 'tl';

/// 現場の正解（FAQ）: GET /api/faq。承認済みの質問はLLMを使わず即答される（DifyのAnnotation相当）
export interface FaqEntry { id: number; question: string; answer: string; sources?: unknown[]; hits?: number }
export interface FaqInput { question: string; answer: string; variants?: string[]; status?: string; tags?: string }

/// 図面台帳（GET /api/drawings）: drawing_meta の一覧。承認日・尺度は未抽出の場合 null
export interface DrawingRow {
  file_id: number; file: string;
  zuban?: string | null; hinmei?: string | null; zairyo?: string | null;
  scale?: string | null; revision?: string | null; approved_at?: string | null;
}

/// 実績表の列マッピング（GET /api/jisseki/columns）。role は zuban/mitumori/genka/nouki/other
export interface JissekiColumn { column_name: string; role: string; column_index: number }
export interface JissekiColumnsResult { file_id: number; file: string; manual?: boolean; items: JissekiColumn[] }
/** 列マッピングの手動上書き（PUT /api/jisseki/{id}/columns）。-1=その役割の列なし。zuban必須 */
export interface JissekiRoleMap { zuban: number; mitumori: number; genka: number; nouki: number }
/// 実績の集計結果（GET /api/jisseki/summary）。count=集計対象件数、unit=金額単位（件数は空）
export interface JissekiSummary { metric: string; aggregate: string; count: number; value: number; unit: string }

/// FAQ候補（GET /api/faq/candidates）。👎フィードバックから自動生成され、採用するとFAQ（即答）になる
export interface FaqCandidate {
  id: number; question: string; answer: string;
  comment?: string | null; corrected_answer?: string | null;
  source_message_id?: number | null; rating?: number; status: string; created_at: string;
}

/// 動作診断（GET /api/diagnostics）。バックエンドの実装進行に合わせて
/// 追加フィールドを許容する（未知キーは無視して表示側で正規化する）。
/// stages は実装により「stage名→{p50_ms,p95_ms}」「t_xxx_ms→数値」等の揺れがあり得るため
/// 型は緩く持ち、src/diagnostics-format.ts で正規化する。
export interface DiagnosticsInfo {
  version?: string;
  tier?: string;
  chunks?: number;
  engines?: { tier?: string; chat_model?: string; llm?: string; embed?: string; rank?: string } | null;
  cache?: number | null;
  requests?: number | null;
  ttft_p50_ms?: number | null;
  ttft_p95_ms?: number | null;
  stages?: Record<string, unknown> | null;
  [key: string]: unknown;
}

/// POST /api/warmup の結果。失敗時はバックエンドが 503 + { code, message } を返す。
export interface WarmupResult { ok: boolean; message?: string; ms?: number }

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
  toggleKnowledge: (id: number) =>
    fetch(`/api/knowledge/${id}/toggle`, { method: 'POST' }).then(r => json<{ enabled: number }>(r)),
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
  diagnostics: () => fetch('/api/diagnostics').then(r => json<DiagnosticsInfo>(r)),
  warmup: () => warmup(),
  /// ---- 現場の正解（FAQ）管理 ----
  /// status を省略すると全ステータス。UIは公開済み（published）のみを一覧する。
  faq: (status = 'published') =>
    fetch(`/api/faq?status=${encodeURIComponent(status)}`).then(r => json<{ items: FaqEntry[] }>(r)),
  /// POST /api/faq。入力エラー（400）は本文の error を返し、例外にしない（UIで表示するため）
  addFaq: async (body: FaqInput): Promise<{ ok: boolean; id?: number; error?: string }> => {
    const resp = await fetch('/api/faq', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
    });
    let parsed: { ok?: boolean; id?: number; error?: string } = {};
    try { parsed = await resp.json() as typeof parsed; } catch { /* 本文なし */ }
    if (!resp.ok) return { ok: false, error: parsed.error ?? `HTTP ${resp.status}` };
    return { ok: parsed.ok !== false, id: parsed.id, error: parsed.error };
  },
  deleteFaq: (id: number) => fetch(`/api/faq/${id}`, { method: 'DELETE' }),
  /// ---- FAQ候補（👎フィードバックからの自動生成 → 編集採用） ----
  faqCandidates: (status = 'pending') =>
    fetch(`/api/faq/candidates?status=${encodeURIComponent(status)}`).then(r => json<{ items: FaqCandidate[] }>(r)),
  /// POST /api/faq/candidates/{id}/adopt: 回答を編集してFAQ（即答）として採用する。
  /// 入力エラー（400）は本文の error を返し、例外にしない（UIで表示するため）
  adoptFaqCandidate: async (id: number, body: { question?: string; answer?: string }): Promise<{ ok: boolean; faq_id?: number; error?: string }> => {
    const resp = await fetch(`/api/faq/candidates/${id}/adopt`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
    });
    let parsed: { ok?: boolean; faq_id?: number; error?: string } = {};
    try { parsed = await resp.json() as typeof parsed; } catch { /* 本文なし */ }
    if (!resp.ok) return { ok: false, error: parsed.error ?? `HTTP ${resp.status}` };
    return { ok: parsed.ok !== false, faq_id: parsed.faq_id, error: parsed.error };
  },
  deleteFaqCandidate: (id: number) => fetch(`/api/faq/candidates/${id}`, { method: 'DELETE' }),
  /// ---- 図面台帳 ----
  drawings: (q?: string) =>
    fetch(`/api/drawings${q ? `?q=${encodeURIComponent(q)}` : ''}`).then(r => json<{ items: DrawingRow[] }>(r)),
  /// ---- 実績表（列の自動検出・手動上書き・見積/原価の集計） ----
  jissekiColumns: (fileId: number) =>
    fetch(`/api/jisseki/columns?file_id=${fileId}`).then(r => json<JissekiColumnsResult>(r)),
  /// PUT /api/jisseki/{id}/columns: 列役割の手動上書き。元ファイルを再パースし実績辞書を作り直す。
  /// 入力エラー（400）は本文の error を返し、例外にしない（UIで表示するため）
  jissekiSaveColumns: async (fileId: number, roles: JissekiRoleMap): Promise<{ ok: boolean; rows?: number; error?: string }> => {
    const resp = await fetch(`/api/jisseki/${fileId}/columns`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(roles),
    });
    let parsed: { ok?: boolean; rows?: number; error?: string } = {};
    try { parsed = await resp.json() as typeof parsed; } catch { /* 本文なし */ }
    if (!resp.ok) return { ok: false, error: parsed.error ?? `HTTP ${resp.status}` };
    return { ok: parsed.ok !== false, rows: parsed.rows, error: parsed.error };
  },
  jissekiSummary: (p: { file_id: number; metric: 'mitumori' | 'genka'; aggregate: 'sum' | 'avg' | 'count'; zuban?: string }) => {
    const ps = new URLSearchParams({ file_id: String(p.file_id), metric: p.metric, aggregate: p.aggregate });
    if (p.zuban) ps.set('zuban', p.zuban);
    return fetch(`/api/jisseki/summary?${ps.toString()}`).then(r => json<JissekiSummary>(r));
  },
  /// POST /api/feedback: 回答への👍/👎・修正回答をローカル保存する（外部送信なし）。
  /// candidate_saved/candidate_reason はFAQ候補生成の結果（トースト文言の分岐に使用・v2.7.0）
  feedback: (body: { message_id?: number; chat_id?: number; rating: 1 | -1; comment?: string; corrected_answer?: string }) =>
    fetch('/api/feedback', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
    }).then(r => json<{ ok: boolean; id?: number; candidate_saved?: boolean; candidate_reason?: string }>(r)),
};

/// AIエンジンのウォームアップ（POST /api/warmup）。失敗しても例外にせず
/// { ok:false, message } を返し、設定画面が壊れないようにする。
export async function warmup(): Promise<WarmupResult> {
  try {
    const resp = await fetch('/api/warmup', { method: 'POST' });
    let body: { ok?: boolean; message?: string; code?: string; ms?: number } | null = null;
    try { body = await resp.json() as { ok?: boolean; message?: string; code?: string; ms?: number }; } catch { /* 本文なし */ }
    if (!resp.ok) {
      return { ok: false, message: body?.message ?? body?.code ?? `HTTP ${resp.status}` };
    }
    return { ok: body?.ok !== false, message: body?.message, ms: typeof body?.ms === 'number' ? body.ms : undefined };
  } catch (ex) {
    return { ok: false, message: (ex as Error).message };
  }
}

export interface ModelEntry {
  id: string; name: string; file: string; kind: string; sizeBytes: number; license: string;
  installed: boolean; required: boolean; minRamGb: number;
  corrupted?: boolean; // 起動時SHA検証で破損判定済み（再ダウンロードで修復可）
}

/// POST /api/chat をSSE受信する。イベント: meta / model / web / delta / done / error
/// （作成モード v2.5.0 追加: phase / outline / section_start / section_done）
export async function streamChat(
  body: { chat_uuid?: string; message: string; web_search?: boolean; model?: string; capture_image?: string; summary_file_id?: number; answer_lang?: AnswerLang; draft?: { refs?: number[]; template?: string; seed?: { question: string; answer: string } } },
  handlers: {
    meta?: (d: { chat_id: number; chat_uuid: string }) => void;
    model?: (d: { tier: string; model_file: string }) => void;
    web?: (d: { results: { title: string; url: string; snippet: string }[]; error?: string | null }) => void;
    /// partial=true は「検索直後・リランク前の先行refs」、false は確定refs（v2.7）
    refs?: (d: { files: string[]; web: string[]; partial?: boolean }) => void;
    /// 処理段階の一行表示（v2.7.0・ZCode方式）: 質問解釈/検索/文書選択/根拠確定/生成/検証
    stage?: (d: { text: string }) => void;
    patch?: (content: string) => void;
    delta?: (content: string) => void;
    phase?: (d: { text: string }) => void;
    outline?: (d: { title: string; sections: { Title: string; Goal: string }[] }) => void;
    section_start?: (d: { index: number; title: string }) => void;
    section_done?: (d: { index: number }) => void;
    done?: (d: { cached: boolean; kind?: string | null; guard?: string | null; suggest_web?: boolean; template?: string; auto_inferred?: boolean; title?: string; sources?: SourceInfo[]; ttfb_ms?: number; ms?: number; route?: string; confidence?: number; instant?: boolean; message_id?: number }) => void;
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
          case 'stage': handlers.stage?.(parsed); break;
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
