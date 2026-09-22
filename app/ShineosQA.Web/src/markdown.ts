// 最小安全Markdownレンダラ（XSS対策: まず全文エスケープ→書式適用）
const escapeHtml = (s: string): string =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

export function renderMarkdown(src: string): string {
  const lines = escapeHtml(src).split('\n');
  const out: string[] = [];
  let inList = false;
  for (const raw of lines) {
    const line = raw.trimEnd();
    // ネストした箇条書き（モデルが文書ごとに子項目をインデントする）を
    // 階層として見せる。インデント2スペース（orタブ）=1階層、最大4階層まで
    const li = line.match(/^(\s*)[-•]\s+(.*)$/);
    const h = line.match(/^(#{1,4})\s+(.*)$/);
    if (li) {
      if (!inList) { out.push('<ul>'); inList = true; }
      const depth = Math.min(4, Math.floor(li[1].replace(/\t/g, '  ').length / 2));
      out.push(`<li${depth > 0 ? ` style="margin-left:${depth * 18}px"` : ''}>${inline(li[2])}</li>`);
      continue;
    }
    if (inList) { out.push('</ul>'); inList = false; }
    if (h) out.push(`<h${h[1].length + 2}>${inline(h[2])}</h${h[1].length + 2}>`);
    else if (line === '') out.push('');
    else out.push(`<p>${inline(line)}</p>`);
  }
  if (inList) out.push('</ul>');
  return out.join('\n');
}

function inline(s: string): string {
  return s
    .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
    .replace(/【(出典[^】]*)】/g, '<span class="cite">$&</span>')
    .replace(/（(https?:\/\/[^）]+)）/g, '（<a href="$1" target="_blank" rel="noopener noreferrer">$1</a>）');
}
