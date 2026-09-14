using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ShineosQA.Backend;

/// <summary>ドキュメント解析・ナレッジ取り込み。XXE対策: DtdProcessing.Ignore（v1.0.78の方針を継承）</summary>
public sealed class Ingest
{
    private readonly Db _db;
    private readonly LlmGateway _gw;
    private readonly Supervisor _sup;
    private readonly ChunkIndex _index;
    private readonly AppConfig _cfg;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _ingestLock = new(1, 1); // 取り込みの直列化（embedエンジン飽和防止）

    public Ingest(Db db, LlmGateway gw, Supervisor sup, ChunkIndex index, AppConfig cfg, ILogger log)
    { _db = db; _gw = gw; _sup = sup; _index = index; _cfg = cfg; _log = log; }

    public static readonly string[] SupportedExtensions = { ".md", ".txt", ".docx", ".pdf" };

    public static string ExtractText(string fileName, Stream stream)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".txt" => ReadText(stream),
            ".docx" => ExtractDocx(stream),
            ".pdf" => ExtractPdf(stream),
            _ => throw new NotSupportedException($"unsupported file type: {ext} (SHINE_E_DOC_PARSE_FAILED)")
        };
    }

    private static string ReadText(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        var enc = new UTF8Encoding(false);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return enc.GetString(bytes, 3, bytes.Length - 3);
        // UTF-16 BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return enc.GetString(bytes);
    }

    private static string ExtractDocx(Stream s)
    {
        using var zip = new ZipArchive(s, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("docx: word/document.xml not found");
        using var es = entry.Open();
        var sb = new StringBuilder();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }; // XXE対策
        using var reader = XmlReader.Create(es, settings);
        bool inText = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "w:t") inText = true;
                else if (reader.Name == "w:p" || reader.Name == "w:br") sb.Append('\n');
            }
            else if (reader.NodeType == XmlNodeType.Text && inText) sb.Append(reader.Value);
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "w:t") inText = false;
        }
        var text = sb.ToString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("docx: no text extracted");
        return text;
    }

    /// <summary>簡易PDFテキスト抽出（FlateDecode＋Tj/TJ演算子）。CJK CIDフォント非対応のベストエフォート</summary>
    private static string ExtractPdf(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        var sb = new StringBuilder();
        int i = 0;
        while (i < bytes.Length - 5)
        {
            // "stream" ... "endstream" の間を FlateDecode として展開を試みる
            if (bytes[i] == 's' && bytes[i + 1] == 't' && bytes[i + 2] == 'r' && bytes[i + 3] == 'e' && bytes[i + 4] == 'a' && bytes[i + 5] == 'm')
            {
                int start = i + 6;
                if (start < bytes.Length && bytes[start] == '\r') start++;
                if (start < bytes.Length && bytes[start] == '\n') start++;
                int end = FindEndStream(bytes, start);
                if (end > start)
                {
                    var inflated = TryInflate(bytes, start, end - start) ?? bytes[start..end]; // 非圧縮ストリームも直接扱う
                    ExtractTextOperators(inflated, sb);
                    i = end + 9;
                    continue;
                }
            }
            i++;
        }
        var text = sb.ToString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("pdf: no extractable text (scanned or CID font PDF may be unsupported)");
        return text;
    }

    private static int FindEndStream(byte[] b, int from)
    {
        for (int i = from; i < b.Length - 9; i++)
            if (b[i] == 'e' && b[i + 1] == 'n' && b[i + 2] == 'd' && b[i + 3] == 's' && b[i + 4] == 't' && b[i + 5] == 'r' && b[i + 6] == 'e' && b[i + 7] == 'a' && b[i + 8] == 'm')
                return i;
        return -1;
    }

    private static byte[]? TryInflate(byte[] b, int start, int len)
    {
        // zlibヘッダ(2バイト)付きと生deflateの両方を試す
        foreach (var skip in new[] { 2, 0 })
        {
            try
            {
                using var ms = new MemoryStream(b, start + skip, Math.Max(0, len - skip));
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                ds.CopyTo(outMs);
                if (outMs.Length > 0) return outMs.ToArray();
            }
            catch { }
        }
        return null;
    }

    private static void ExtractTextOperators(byte[] content, StringBuilder sb)
    {
        int i = 0;
        while (i < content.Length)
        {
            if (content[i] == '(')
            {
                int depth = 1; i++;
                var s = new StringBuilder();
                while (i < content.Length && depth > 0)
                {
                    char c = (char)content[i];
                    if (c == '\\') { i++; if (i < content.Length) s.Append((char)content[i]); }
                    else if (c == '(') { depth++; s.Append(c); }
                    else if (c == ')') { depth--; if (depth > 0) s.Append(c); }
                    else if (c != '\n' && c != '\r') s.Append(c);
                    i++;
                }
                sb.Append(s);
            }
            else if (content[i] == 'T' && i + 1 < content.Length && content[i + 1] == 'd')
            { sb.Append('\n'); i += 2; }
            else i++;
        }
    }

    /// <summary>ファイル取り込み: 解析→チャンク→埋め込み→索引更新（ステータス遷移をDBに記録）</summary>
    public async Task<long> IngestFileAsync(string fileName, Stream content, CancellationToken ct)
    {
        await _ingestLock.WaitAsync(ct);
        try
        {
            _db.Exec("INSERT INTO files(name, status) VALUES($n,'parsing')", ("$n", fileName));
            long fileId = _db.LastInsertId();
            try
            {
                var text = ExtractText(fileName, content);
                var chunks = Rag.Chunk(text);
                if (chunks.Count == 0) throw new InvalidDataException("no text content");
                _db.Exec("UPDATE files SET status='embedding' WHERE file_id=$i", ("$i", fileId));

                var embeddings = new List<float[]>();
                _sup.StartEmbedAndRank(); // 前回クラッシュ・tier変更後のStopAll等で止まっていたら自己修復
                foreach (var batch in chunks.Chunk(16))
                    embeddings.AddRange(await _gw.EmbedAsync(_cfg.EnginePortEmb, batch, ct));

                for (int seq = 0; seq < chunks.Count; seq++)
                    _db.InsertChunk(fileId, seq, chunks[seq], ChunkIndex.FloatsToBytes(embeddings[seq]));
                _db.Exec("UPDATE files SET status='ready', chunk_count=$c WHERE file_id=$i", ("$c", chunks.Count), ("$i", fileId));
                _index.AddRange(fileName, fileId, chunks.Select((t, seq) => (seq, t, embeddings[seq])));
                _log.Info($"ingested {fileName}: {chunks.Count} chunks");
                return fileId;
            }
            catch (Exception ex)
            {
                _db.Exec("UPDATE files SET status='error', error=$e WHERE file_id=$i", ("$e", ex.Message), ("$i", fileId));
                throw;
            }
        }
        finally { _ingestLock.Release(); }
    }
}
