import { describe, expect, it } from 'vitest';
import { renderMarkdown } from './markdown';

describe('renderMarkdown', () => {
  it('書式適用前にHTMLをエスケープする(XSS安全)', () => {
    const html = renderMarkdown('<script>alert(1)</script>');
    expect(html).not.toContain('<script>');
    expect(html).toContain('&lt;script&gt;');
  });

  it('ネストした箇条書きを階層どおりに表示する', () => {
    const html = renderMarkdown('- 文書A\n  - 項目1\n  - 項目2');
    expect(html).toContain('<ul>');
    expect(html).toContain('<li>文書A</li>');
    expect(html).toContain('<li style="margin-left:18px">項目1</li>');
  });

  it('見出しは2段深いレベルで出力される(#→h3, ##→h4)', () => {
    expect(renderMarkdown('# 大見出し')).toContain('<h3>大見出し</h3>');
    expect(renderMarkdown('## 仕様')).toContain('<h4>仕様</h4>');
  });

  it('太字と【出典】ハイライトを適用する', () => {
    const html = renderMarkdown('**重要**【出典1】');
    expect(html).toContain('<strong>重要</strong>');
    expect(html).toContain('<span class="cite">【出典1】</span>');
  });

  it('全角かっこ内のURLをリンクにする(target=_blank)', () => {
    const html = renderMarkdown('詳細（https://example.com/x）参照');
    expect(html).toContain('<a href="https://example.com/x" target="_blank" rel="noopener noreferrer">');
  });
});
