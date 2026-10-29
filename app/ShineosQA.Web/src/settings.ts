import { api, type StatusInfo, type DiagnosticsInfo } from './api';
import { Models } from './wizard';
import { REPORT_CONTACT_URL } from './report';
import { formatDiagnostics, formatWarmupResult } from './diagnostics-format';

export class SettingsView {
  private busy = false; // 初期ロードとタブ切替の二重refreshによるDOM競合（detachされた方に描く）を防ぐ

  constructor() {
    void this.refresh();
  }

  async refresh() {
    if (this.busy) return;
    this.busy = true;
    try {
      await this.refreshInner();
    } finally {
      this.busy = false;
    }
  }

  private async refreshInner() {
    const [status, settings] = await Promise.all([api.status(), api.getSettings()]);
    const body = document.getElementById('settings-body')!;
    body.innerHTML = `
      <div class="settings-card">
        <h3>検索</h3>
        <div class="setting-row">
          <div class="setting-text">
            <b>Web検索</b>
            <span>ONにすると質問内容が外部の検索サービス（DuckDuckGo）へ送信されます。社内情報の質問時はOFFのままご利用ください。</span>
          </div>
          <label class="switch"><input type="checkbox" id="set-web" ${settings.web_search ? 'checked' : ''} /><span class="slider"></span></label>
        </div>
      </div>
      <div class="settings-card">
        <h3>拡張機能</h3>
        <div class="setting-row">
          <div class="setting-text">
            <b>図面PDF検索・Q&amp;A（製造業向け）</b>
            <span>図面PDFの取り込み時に表題欄（図番・品名・材質・改訂）を自動で読み取り、図番・品名での検索を可能にします。無効にしても取り込んだ図面データは残ります。</span>
          </div>
          <label class="switch"><input type="checkbox" id="set-ext-drawing" ${settings.extensions?.drawing ? 'checked' : ''} /><span class="slider"></span></label>
        </div>
      </div>
      <div class="settings-card">
        <h3>AIエンジン</h3>
        <div class="setting-row">
          <div class="setting-text"><b>モデル階級</b><span>変更はAIエンジンの再起動を伴います（数秒〜十数秒）</span></div>
          <select id="set-tier">
            <option value="auto" ${settings.tier === 'auto' ? 'selected' : ''}>自動（RAMで判定）</option>
            <option value="standard" ${settings.tier === 'standard' ? 'selected' : ''}>標準（Qwen3-4B・16GB以上推奨）</option>
            <option value="quick" ${settings.tier === 'quick' ? 'selected' : ''}>クイック（Qwen3-1.7B・8GB機向）</option>
          </select>
        </div>
        <div class="setting-row">
          <div class="setting-text">
            <b>他アプリ優先モード</b>
            <span>AI処理を低優先度で実行し、他のアプリの操作を優先します（回答がやや遅くなります）。長時間使わない間はAIのメモリも自動的に解放します。</span>
          </div>
          <label class="switch"><input type="checkbox" id="set-bg" ${settings.bg_friendly ? 'checked' : ''} /><span class="slider"></span></label>
        </div>
        <div class="setting-row">
          <div class="setting-text"><b>アイドル停止までの時間</b><span>この時間AIを使わないとエンジンを停止して省電力化します（無効で常駐＝放置後の初回質問もすぐ答えます）</span></div>
          <select id="set-idle">
            <option value="30" ${settings.idle_unload_minutes === 30 ? 'selected' : ''}>30分</option>
            <option value="60" ${settings.idle_unload_minutes === 60 ? 'selected' : ''}>60分</option>
            <option value="120" ${settings.idle_unload_minutes === 120 ? 'selected' : ''}>120分</option>
            <option value="0" ${settings.idle_unload_minutes === 0 ? 'selected' : ''}>無効（常駐）</option>
          </select>
        </div>
      </div>
      <div id="models-host"></div>
      <div class="settings-card">
        <h3>システム状態</h3>
        <div id="status-rows" class="status-rows"></div>
      </div>
      <div class="settings-card">
        <h3>動作診断</h3>
        <div id="diag-rows" class="status-rows">
          <div class="status-row"><span>状態</span><b>読み込み中…</b></div>
        </div>
        <div class="setting-row">
          <div class="setting-text">
            <b>ウォームアップ</b>
            <span>AIモデルを今すぐメモリへ読み込みます。しばらく使っていない後の初回回答が速くなります（数十秒かかることがあります）。</span>
          </div>
          <button type="button" class="primary small" id="diag-warmup">ウォームアップ</button>
        </div>
        <div id="diag-note" class="muted" hidden></div>
      </div>
      <div class="settings-card">
        <h3>サポート</h3>
        <div class="setting-row">
          <div class="setting-text">
            <b>問題を報告</b>
            <span>AIの回答に不適切な内容や誤りを見つけた場合は、チャット画面の各回答の下にある「問題を報告」から報告できます（報告内容は問い合わせフォームに自動入力されます。社名・お名前・メールアドレスのご記入と送信はご自身で行います）。</span>
          </div>
          <button type="button" class="primary small" id="set-report">問い合わせフォーム</button>
        </div>
      </div>`;
    document.getElementById('set-web')!.addEventListener('change', async (e) => {
      await api.saveSettings({ web_search: (e.target as HTMLInputElement).checked });
      await this.refreshStatus(status);
    });
    document.getElementById('set-tier')!.addEventListener('change', async (e) => {
      await api.saveSettings({ tier: (e.target as HTMLSelectElement).value });
      await this.refresh();
    });
    document.getElementById('set-bg')!.addEventListener('change', async (e) => {
      await api.saveSettings({ bg_friendly: (e.target as HTMLInputElement).checked });
    });
    document.getElementById('set-idle')!.addEventListener('change', async (e) => {
      await api.saveSettings({ idle_unload_minutes: Number((e.target as HTMLSelectElement).value) });
    });
    // 拡張パック（図面検索・Excel/CSV）: 即時反映。取り込み済み図面件数をカード内に表示
    const extDrawingCount = await api.knowledge().then(fs => fs.filter(f => f.kind === 'drawing').length).catch(() => 0);
    const extText = document.querySelector('#settings-body .settings-card:nth-child(2) .setting-text span') as HTMLElement | null;
    if (extText) extText.textContent += `（取り込み済み図面: ${extDrawingCount}件）`;
    document.getElementById('set-ext-drawing')!.addEventListener('change', async (e) => {
      await api.saveSettings({ extensions: { drawing: (e.target as HTMLInputElement).checked } });
    });
    document.getElementById('set-report')!.addEventListener('click', () => {
      window.open(REPORT_CONTACT_URL, '_blank', 'noopener,noreferrer');
    });
    // 動作診断（v2.7）: 取得は非同期・失敗してもカード内だけに留める（設定画面全体を壊さない）
    document.getElementById('diag-warmup')!.addEventListener('click', () => { void this.warmup(); });
    void this.loadDiagnostics();
    // AIモデル管理セクション（初回DL・追加・削除）をカード内へ描画
    const modelsHost = document.getElementById('models-host')!;
    modelsHost.innerHTML = '';
    const rendered = await Models.renderSettings();
    while (rendered.firstChild) modelsHost.appendChild(rendered.firstChild);
    await this.refreshStatus(status);
  }

  /** 動作診断カード: GET /api/diagnostics を非同期取得。失敗してもカード内に留める */
  private async loadDiagnostics(): Promise<void> {
    const host = document.getElementById('diag-rows');
    if (!host) return; // 設定画面が再描画済み（別タブ遷移等）なら何もしない
    try {
      const d = await api.diagnostics();
      this.renderDiagnostics(d);
    } catch {
      host.innerHTML = '<div class="status-row"><span>状態</span><b>診断情報を取得できません</b></div>';
    }
  }

  private renderDiagnostics(d: DiagnosticsInfo | null): void {
    const host = document.getElementById('diag-rows');
    if (!host) return;
    const rows = formatDiagnostics(d);
    host.innerHTML = rows
      .map(r => `<div class="status-row"><span>${esc(r.key)}</span><b>${esc(r.value)}</b></div>`)
      .join('');
  }

  /** ウォームアップ: POST /api/warmup → 結果をカード下に1行表示し、診断値も再取得 */
  private async warmup(): Promise<void> {
    const btn = document.getElementById('diag-warmup') as HTMLButtonElement | null;
    const note = document.getElementById('diag-note');
    if (btn) { btn.disabled = true; btn.textContent = '準備中…'; }
    if (note) {
      note.hidden = false;
      note.textContent = 'ウォームアップ中…（モデルの読み込みには数十秒かかることがあります）';
    }
    try {
      const r = await api.warmup();
      if (note) note.textContent = formatWarmupResult(r);
      // 読み込み後のエンジン状態を「システム状態」と「動作診断」の両方へ反映
      if (r.ok) void this.refreshStatus(null);
      await this.loadDiagnostics();
    } catch (ex) {
      if (note) note.textContent = `ウォームアップ失敗: ${(ex as Error).message}`;
    } finally {
      if (btn) { btn.disabled = false; btn.textContent = 'ウォームアップ'; }
    }
  }

  private async refreshStatus(_prev: StatusInfo | null) {
    try {
      const s: StatusInfo = await api.status();
      const host = document.getElementById('status-rows');
      if (host) {
        const rows: [string, string][] = [
          ['バージョン', s.version],
          ['階級', `${s.tier}（${s.chat_model}）`],
          ['RAM', `${s.ram_gb}GB`],
          ['ナレッジ', `${s.chunks} チャンク`],
          ['LLMエンジン', s.engines.llm],
          ['埋め込み', s.engines.embed],
          ['リランカ', s.engines.rank],
        ];
        host.innerHTML = rows.map(([k, v]) => `<div class="status-row"><span>${k}</span><b>${esc(v)}</b></div>`).join('');
      }
    } catch (ex) {
      const host = document.getElementById('status-rows');
      if (host) host.innerHTML = `<div class="status-row"><span>状態</span><b>取得失敗: ${esc((ex as Error).message)}</b></div>`;
    }
  }
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
