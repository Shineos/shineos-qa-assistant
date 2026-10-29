// 動作診断（GET /api/diagnostics）の表示整形。DOM非依存の純関数のみ＝vitestで契約を固定する。
// バックエンドの Diagnostics 実装は段階的に拡張されるため、stages の形は複数候補を許容する
// （{embed:{p50_ms}} / {t_embed_ms:{p50}} / フラットな embed_p50_ms など）。
import type { DiagnosticsInfo, WarmupResult } from './api';

export interface DiagRow { key: string; value: string }

const isNum = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);

/** オブジェクトから候補キーの数値を拾う。{p50_ms:12} のような入れ子も1段まで辿る */
function pickNum(obj: unknown, keys: string[], depth = 0): number | null {
  if (!obj || typeof obj !== 'object' || depth > 2) return null;
  const rec = obj as Record<string, unknown>;
  for (const k of keys) {
    const v = rec[k];
    if (isNum(v)) return v;
    if (v && typeof v === 'object') {
      const nested = pickNum(v, ['p50_ms', 'p50', 'p50Ms', 'median_ms'], depth + 1);
      if (nested !== null) return nested;
    }
  }
  return null;
}

/** ステージ別 p50(ms)。実装形状の揺れを吸収し、見つからなければ null */
export function stageP50(d: DiagnosticsInfo | null | undefined, stage: 'embed' | 'retrieve' | 'prompt'): number | null {
  if (!d) return null;
  const keys: Record<'embed' | 'retrieve' | 'prompt', string[]> = {
    // バックエンド Diagnostics.cs のステージキー（t_xxx_ms）を最優先。t_retrieve_ms は
    // 「検索+リランク+ガード」なので t_search_ms より先に見る（意味の取り違え防止）。
    embed: ['t_embed_ms', 'embed', 'embedding', 't_embed', 'embed_ms', 'embed_p50_ms', 'emb_p50_ms'],
    retrieve: ['t_retrieve_ms', 'retrieve', 'retrieval', 't_search_ms', 'search', 'retrieve_ms', 'retrieve_p50_ms'],
    prompt: ['t_prompt_ms', 'prompt', 't_prompt', 'prompt_ms', 'prompt_p50_ms'],
  };
  const fromStages = pickNum(d.stages, keys[stage]);
  if (fromStages !== null) return fromStages;
  // 段を持たずトップレベルに平坦化して返す実装にも追随する
  return pickNum(d, keys[stage]);
}

const fmtMs = (v: number | null): string => (v === null ? '—' : `${Math.round(v)} ms`);
const fmtCount = (v: number | null | undefined, unit: string): string => (isNum(v) ? `${Math.round(v)} ${unit}` : '—');

/** 診断カードの行（key/value）。d が null でも行を返し、呼び出し側のDOMを壊さない */
export function formatDiagnostics(d: DiagnosticsInfo | null | undefined): DiagRow[] {
  if (!d) return [{ key: '状態', value: '診断情報を取得できません' }];
  const eng = d.engines ?? null;
  const engineText = eng
    ? `LLM: ${eng.llm ?? '—'} / 埋め込み: ${eng.embed ?? '—'} / リランカ: ${eng.rank ?? '—'}`
    : '—';
  // エンドポイント実装の揺れ（requests/samples、cache/cache_entries、camelCase）を吸収する
  const requests = pickNum(d, ['requests', 'samples', 'total_requests', 'Samples']);
  const cache = pickNum(d, ['cache', 'cache_entries', 'cache_size', 'CachedSamples', 'cached_samples']);
  // キャッシュ応答数（cached_requests）は質問回数に併記する（ヒット率が一目で分かるように）
  const cachedRequests = pickNum(d, ['cached_requests', 'cachedRequests']);
  const requestsText = requests === null
    ? '—'
    : `${fmtCount(requests, '回')}${cachedRequests === null ? '' : `（キャッシュ応答 ${fmtCount(cachedRequests, '回')}）`}`;
  // ttft_p50_ms が無い実装では stages.t_first_token_ms で代替する
  const ttftP50 = pickNum(d, ['ttft_p50_ms', 'ttftP50Ms', 'ttfb_p50_ms', 'ttfbP50Ms'])
    ?? pickNum(d.stages, ['t_first_token_ms']);
  const ttftP95 = pickNum(d, ['ttft_p95_ms', 'ttftP95Ms', 'ttfb_p95_ms', 'ttfbP95Ms']);
  return [
    { key: '質問回数', value: requestsText },
    { key: 'キャッシュ', value: fmtCount(cache, '件') },
    { key: 'TTFT p50', value: fmtMs(ttftP50) },
    { key: 'TTFT p95', value: fmtMs(ttftP95) },
    { key: '埋め込み p50', value: fmtMs(stageP50(d, 'embed')) },
    { key: '検索 p50', value: fmtMs(stageP50(d, 'retrieve')) },
    { key: 'プロンプト p50', value: fmtMs(stageP50(d, 'prompt')) },
    { key: 'エンジン', value: engineText },
  ];
}

/** ウォームアップ結果の1行表示 */
export function formatWarmupResult(r: WarmupResult): string {
  if (!r.ok) return `ウォームアップ失敗: ${r.message ?? '不明なエラー'}`;
  return r.ms !== undefined ? `ウォームアップ完了（${(r.ms / 1000).toFixed(1)}秒）` : 'ウォームアップ完了';
}
