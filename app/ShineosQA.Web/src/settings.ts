import { api, type StatusInfo } from './api';
import { Models } from './wizard';

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
      </div>
      <div id="models-host"></div>
      <div class="settings-card">
        <h3>システム状態</h3>
        <div id="status-rows" class="status-rows"></div>
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
    // 拡張パック（図面検索）: 即時反映。取り込み済み図面件数をカード内に表示
    const extDrawingCount = await api.knowledge().then(fs => fs.filter(f => f.kind === 'drawing').length).catch(() => 0);
    const extText = document.querySelector('#settings-body .settings-card:nth-child(2) .setting-text span') as HTMLElement | null;
    if (extText) extText.textContent += `（取り込み済み図面: ${extDrawingCount}件）`;
    document.getElementById('set-ext-drawing')!.addEventListener('change', async (e) => {
      await api.saveSettings({ extensions: { drawing: (e.target as HTMLInputElement).checked } });
    });
    // AIモデル管理セクション（初回DL・追加・削除）をカード内へ描画
    const modelsHost = document.getElementById('models-host')!;
    modelsHost.innerHTML = '';
    const rendered = await Models.renderSettings();
    while (rendered.firstChild) modelsHost.appendChild(rendered.firstChild);
    await this.refreshStatus(status);
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
