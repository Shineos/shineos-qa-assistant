import { describe, expect, it } from 'vitest';
import { formatDiagnostics, formatWarmupResult, stageP50 } from './diagnostics-format';

// 動作診断カード（v2.7）の表示契約。バックエンドが stages の形を変えても
// 表示側の正規化で吸収できることを固定する。
describe('formatDiagnostics', () => {
  it('質問回数/キャッシュ/TTFT/ステージp50/エンジン状態を整形する', () => {
    const rows = formatDiagnostics({
      version: '2.7.0', tier: 'quick', chunks: 12, cache: 4, requests: 9,
      ttft_p50_ms: 1234.6, ttft_p95_ms: 2500,
      engines: { llm: 'running(pid=1)', embed: 'running(pid=2)', rank: 'stopped' },
      stages: { embed: { p50_ms: 21 }, retrieve: { p50_ms: 35 }, prompt: { p50_ms: 47 } },
    });
    const map = new Map(rows.map(r => [r.key, r.value]));
    expect(map.get('質問回数')).toBe('9 回');
    expect(map.get('キャッシュ')).toBe('4 件');
    expect(map.get('TTFT p50')).toBe('1235 ms'); // 四捨五入
    expect(map.get('TTFT p95')).toBe('2500 ms');
    expect(map.get('埋め込み p50')).toBe('21 ms');
    expect(map.get('検索 p50')).toBe('35 ms');
    expect(map.get('プロンプト p50')).toBe('47 ms');
    expect(map.get('エンジン')).toContain('LLM: running(pid=1)');
    expect(map.get('エンジン')).toContain('リランカ: stopped');
  });

  it('stages や engines が無くてもクラッシュせず「—」を返す', () => {
    const rows = formatDiagnostics({ requests: 1 });
    const map = new Map(rows.map(r => [r.key, r.value]));
    expect(map.get('TTFT p50')).toBe('—');
    expect(map.get('埋め込み p50')).toBe('—');
    expect(map.get('エンジン')).toBe('—');
  });

  it('requests/samples・ttft の実装揺れを吸収する', () => {
    const rows = formatDiagnostics({ samples: 3, cache_entries: 2, stages: { t_first_token_ms: { p50: 800 } } });
    const map = new Map(rows.map(r => [r.key, r.value]));
    expect(map.get('質問回数')).toBe('3 回');
    expect(map.get('キャッシュ')).toBe('2 件');
    expect(map.get('TTFT p50')).toBe('800 ms');
  });

  it('camelCase/PascalCase で返る実装（ttfbP50Ms等）にも追随する', () => {
    const rows = formatDiagnostics({ Samples: 5, CachedSamples: 1, ttfbP50Ms: 500, ttfbP95Ms: 900 });
    const map = new Map(rows.map(r => [r.key, r.value]));
    expect(map.get('質問回数')).toBe('5 回');
    expect(map.get('キャッシュ')).toBe('1 件');
    expect(map.get('TTFT p50')).toBe('500 ms');
    expect(map.get('TTFT p95')).toBe('900 ms');
  });

  it('バックエンド契約どおりの cached_requests を質問回数に併記する', () => {
    const rows = formatDiagnostics({ requests: 9, cached_requests: 4, cache: 2 });
    const map = new Map(rows.map(r => [r.key, r.value]));
    expect(map.get('質問回数')).toBe('9 回（キャッシュ応答 4 回）');
    expect(map.get('キャッシュ')).toBe('2 件');
  });

  it('取得失敗（null）はカード内の1行に留める', () => {
    expect(formatDiagnostics(null)).toEqual([{ key: '状態', value: '診断情報を取得できません' }]);
  });
});

describe('stageP50（stages形状の揺れ吸収）', () => {
  it('t_xxx_ms 形式の p50 を拾う（retrieve は search より優先）', () => {
    const d = { stages: { t_embed_ms: { p50: 12 }, t_search_ms: { p50: 99 }, t_retrieve_ms: { p50: 34 }, t_prompt_ms: { p50: 56 } } };
    expect(stageP50(d, 'embed')).toBe(12);
    expect(stageP50(d, 'retrieve')).toBe(34); // t_search_ms(99) ではなく t_retrieve_ms
    expect(stageP50(d, 'prompt')).toBe(56);
  });

  it('トップレベル平坦化（embed_p50_ms）にも追随する', () => {
    const d = { embed_p50_ms: 7, retrieve_p50_ms: 8, prompt_p50_ms: 9 };
    expect(stageP50(d, 'embed')).toBe(7);
    expect(stageP50(d, 'retrieve')).toBe(8);
    expect(stageP50(d, 'prompt')).toBe(9);
  });

  it('該当なしは null（表示は「—」）', () => {
    expect(stageP50({}, 'embed')).toBeNull();
    expect(stageP50(null, 'embed')).toBeNull();
  });
});

describe('formatWarmupResult', () => {
  it('成功は完了メッセージ（msがあれば秒付き）', () => {
    expect(formatWarmupResult({ ok: true, ms: 12345 })).toBe('ウォームアップ完了（12.3秒）');
    expect(formatWarmupResult({ ok: true })).toBe('ウォームアップ完了');
  });

  it('失敗は理由を含む（理由なしでも壊れない）', () => {
    expect(formatWarmupResult({ ok: false, message: 'エンジン起動失敗' })).toBe('ウォームアップ失敗: エンジン起動失敗');
    expect(formatWarmupResult({ ok: false })).toContain('不明なエラー');
  });
});
