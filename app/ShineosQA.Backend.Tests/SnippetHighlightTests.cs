using Xunit;
namespace ShineosQA.Backend.Tests;

/// <summary>スニペットは元テキストの完全な部分文字列でなければならない:
/// 出典モーダルの該当箇所ハイライト（body.indexOf(snippet)）の前提（区切り文字落ちで不発した実バグ）</summary>
public class SnippetHighlightTests
{
    [Fact]
    public void Snippet_IsContiguousSubstringOfSource()
    {
        var lines = Enumerable.Range(1, 30).Select(i => $"行{i} これは項目{i}の説明文です。本文が続きます。").ToList();
        var text = string.Join("\n", lines);
        var toks = Rag.Tokenize("項目5の説明文").ToHashSet();
        var snip = Rag.Snippet(text, toks, 240);
        Assert.True(text.Contains(snip), "スニペットが元テキストの部分文字列になっていない");
        Assert.True(snip.Contains("項目5"));
    }

    [Fact]
    public void Snippet_MultiLineChunk_KeepsHighlightable()
    {
        // 図面チャンク（寸法行が改行で並ぶ・文末の句点が無い）でのハイライト前提を検証
        var text = "【図面】図番: D24\n投 影 法 尺度 1 : 1\n受検番号\n普通公差4JIS B 0403-CT8\nJIS B 0405-m\nØ160\nRc1/16\n";
        var toks = Rag.Tokenize("普通公差のJIS規格").ToHashSet();
        var snip = Rag.Snippet(text, toks, 240);
        Assert.True(text.Contains(snip));
        Assert.Contains("0403", snip);
        Assert.Contains("0405", snip);
    }
}
