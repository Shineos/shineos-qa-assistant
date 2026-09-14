import { api, type ModelEntry } from './api';

/// 初回起動ウィザード＋モデル管理（Store対応: 実行ファイル同梱・モデルはアプリ内DL）

export class Models {
  /** 初回起動時にチャットモデルが無ければウィザードを表示 */
  static async maybeShowWizard(): Promise<void> {
    try {
      const { needs_wizard } = await api.models();
      if (needs_wizard) Models.showWizard();
    } catch { /* サーバ停止時は無視 */ }
  }

  private static showWizard() {
    const ov = document.createElement('div');
    ov.className = 'wizard-overlay';
    ov.innerHTML = `
      <div class="wizard">
        <h2>ようこそ「社内知恵袋」へ</h2>
        <p>利用開始にあたり、AIモデルの初回ダウンロードが必要です。<br/>
           まずはクイック（1.7B）をおすすめします。標準（4B）はあとからチャット画面のモデルボタンや設定から追加できます。</p>
        <div class="wizard-choices">
          <button data-id="chat-quick" class="wizard-choice wizard-default">
            <b>クイック（既定・推奨）</b><span>Qwen3-1.7B・約1.1GB・高速（8GB以上）</span>
          </button>
          <button data-id="chat-standard" class="wizard-choice">
            <b>標準（高精度）</b><span>Qwen3-4B・約2.3GB・16GB以上推奨（あとから追加でもOK）</span>
          </button>
        </div>
        <div class="wizard-progress" hidden>
          <div id="wiz-label">準備中…</div>
          <div class="pbar"><div id="wiz-bar" class="pbar-fill"></div></div>
          <div id="wiz-pct" class="muted"></div>
        </div>
      </div>`;
    document.body.appendChild(ov);

    const label = ov.querySelector('#wiz-label') as HTMLElement;
    const bar = ov.querySelector('#wiz-bar') as HTMLElement;
    const pct = ov.querySelector('#wiz-pct') as HTMLElement;
    const prog = ov.querySelector('.wizard-progress') as HTMLElement;

    ov.querySelectorAll<HTMLButtonElement>('.wizard-choice').forEach(btn => {
      btn.addEventListener('click', async () => {
        ov.querySelectorAll('.wizard-choice').forEach(b => (b as HTMLButtonElement).disabled = true);
        prog.hidden = false;
        const chatId = btn.dataset.id!;
        // 必須の埋め込みモデル→選択したチャットモデルの順にDL
        const { models } = await api.models();
        const targets = [
          models.find(m => m.kind === 'embed' && !m.installed),
          models.find(m => m.id === chatId && !m.installed),
          models.find(m => m.kind === 'rerank' && !m.installed),
        ].filter(Boolean) as ModelEntry[];
        try {
          for (const m of targets) {
            label.textContent = `${m.name} をダウンロード中…`;
            void api.installModel(m.id).catch(err => console.error(err));
            await Models.poll(m, label, bar, pct);
          }
          label.textContent = '完了しました。画面を再読み込みします…';
          bar.style.width = '100%';
          // 選択した階級を既定として永続（オート判定で未導入モデルが選ばれるのを防ぐ）
          try { await api.saveSettings({ tier: chatId === 'chat-quick' ? 'quick' : 'standard' }); } catch { /* 停止時は無視 */ }
          setTimeout(() => location.reload(), 1200);
        } catch (ex) {
          label.textContent = `エラー: ${(ex as Error).message}。再読み込みして再試行してください。`;
        }
      });
    });
  }

  private static async poll(m: ModelEntry, label: HTMLElement, bar: HTMLElement, pct: HTMLElement): Promise<void> {
    for (;;) {
      await new Promise(r => setTimeout(r, 800));
      const p = await api.modelProgress();
      if (p.currentId === m.id || p.state === 'idle') {
        if (p.state === 'downloading' && p.total > 0) {
          const per = Math.min(100, Math.round((p.bytes / p.total) * 100));
          bar.style.width = `${per}%`;
          pct.textContent = `${(p.bytes / 1024 / 1024).toFixed(0)} / ${(p.total / 1024 / 1024).toFixed(0)} MB（${per}%）`;
        } else if (p.state === 'verifying') {
          label.textContent = `${m.name} の整合性を検証中…`;
        } else if (p.state === 'done' || p.state === 'idle') {
          return;
        } else if (p.state === 'error') {
          throw new Error(p.error ?? 'ダウンロード失敗');
        }
      }
    }
  }

  /** 設定タブ: モデル一覧（導入状況・追加DL・削除）— 他画面と同一のカード/行デザイン */
  static async renderSettings(): Promise<HTMLElement> {
    const box = document.createElement('div');
    box.innerHTML = '<div class="settings-card"><h3>AIモデル</h3><div id="models-list"></div></div>';
    const list = box.querySelector('#models-list') as HTMLElement;
    const { models } = await api.models();
    for (const m of models) {
      const row = document.createElement('div');
      row.className = 'source model-item';
      const info = document.createElement('div');
      info.className = 'setting-text';
      info.innerHTML = `<b>${esc(m.name)}</b> <span class="muted">${esc(m.license)}・${(m.sizeBytes / 1024 / 1024 / 1024).toFixed(2)}GB</span>`;
      const st = document.createElement('span');
      st.className = (m.installed ? 'st-ready' : 'muted') + ' model-badge';
      st.textContent = m.installed ? '導入済み' : (m.required ? '未導入（必須）' : '未導入');
      const act = document.createElement('div');
      act.className = 'model-actions';
      if (!m.installed) {
        const btn = document.createElement('button');
        btn.className = 'primary small dl-btn';
        btn.innerHTML = '<span class="pct">ダウンロード</span>';
        btn.addEventListener('click', async () => {
          btn.disabled = true;
          // DL中はグルグル（スピナー）＋進捗%を表示
          btn.innerHTML = '<span class="spinner mini"></span><span class="pct">0%</span>';
          try {
            void api.installModel(m.id).catch(err => console.error(err));
            for (;;) {
              await new Promise(r => setTimeout(r, 800));
              const p = await api.modelProgress();
              if (p.currentId === m.id || p.state === 'idle') {
                const pct = btn.querySelector('.pct') as HTMLElement | null;
                if (p.state === 'downloading' && p.total > 0) {
                  const per = Math.min(100, Math.round((p.bytes / p.total) * 100));
                  if (pct) pct.textContent = `${per}%`;
                  btn.title = `${(p.bytes / 1024 / 1024).toFixed(0)} / ${(p.total / 1024 / 1024).toFixed(0)} MB`;
                } else if (p.state === 'verifying') {
                  if (pct) pct.textContent = '検証中';
                } else if (p.state === 'done' || p.state === 'idle') {
                  break;
                } else if (p.state === 'error') {
                  throw new Error(p.error ?? 'ダウンロード失敗');
                }
              }
            }
            await Models.renderInto(list);
          } catch (ex) {
            btn.disabled = false;
            btn.innerHTML = `<span class="pct">再試行</span>`;
            btn.title = `失敗: ${(ex as Error).message}`;
          }
        });
        act.appendChild(btn);
      } else if (!m.required) {
        const btn = document.createElement('button');
        btn.className = 'icon-btn';
        btn.textContent = '×';
        btn.title = '削除';
        btn.addEventListener('click', async () => { await api.deleteModel(m.id); await Models.renderInto(list); });
        act.appendChild(btn);
      }
      row.append(info, st, act);
      list.appendChild(row);
    }
    return box;
  }

  static async renderInto(container: HTMLElement) {
    container.innerHTML = '';
    const el = await Models.renderSettings();
    while (el.firstChild) container.appendChild(el.firstChild);
  }
}

function esc(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
