import { ChatView } from './chat';
import { KnowledgeView } from './knowledge';
import { SettingsView } from './settings';
import { Models } from './wizard';

const views = {
  chat: new ChatView(),
  knowledge: new KnowledgeView(),
  settings: new SettingsView(),
};

/** タブの見た目を切替（URLは変更しない） */
function activateTab(name: string) {
  document.querySelectorAll<HTMLButtonElement>('.tab').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
  document.querySelectorAll('.tabpane').forEach(p => p.classList.toggle('active', p.id === `tab-${name}`));
  const v = views[name as keyof typeof views] as { refresh?: () => Promise<void> } | undefined;
  if (v && typeof v.refresh === 'function') void v.refresh();
}

/** パスからタブ/会話を復元（リロード・戻る・直URL対応） */
function route() {
  const p = location.pathname;
  if (p.startsWith('/knowledge')) activateTab('knowledge');
  else if (p.startsWith('/settings')) activateTab('settings');
  else activateTab('chat'); // '/' と /c/{uuid}（会話の復元はChatViewが処理）
}

document.querySelectorAll<HTMLButtonElement>('.tab').forEach(btn => {
  btn.addEventListener('click', () => {
    const name = btn.dataset.tab!;
    const path = name === 'chat' ? views.chat.currentPath() : `/${name}`;
    if (location.pathname !== path) history.pushState({}, '', path);
    activateTab(name);
  });
});
window.addEventListener('popstate', route);
route();

// 初回起動ウィザード（チャットモデル未導入時）
void Models.maybeShowWizard();
