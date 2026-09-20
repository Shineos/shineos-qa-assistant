using System.IO.Compression;
using System.Text;
using System.Xml;
using Docnet.Core;
using Docnet.Core.Models;

namespace ShineosQA.Backend;

/// <summary>ドキュメント解析・ナレッジ取り込み。XXE対策: DtdProcessing.Ignore（v1.0.78の方針を継承）。
/// PDF抽出はPdfPig本体＋従来自製抽出器のフォールバック（T1）。拡張パック（図面）はT2〜T6の分岐のみで本体フローを変えない</summary>
public sealed class Ingest
{
    private readonly Db _db;
    private readonly LlmGateway _gw;
    private readonly Supervisor _sup;
    private readonly ChunkIndex _index;
    private readonly AppConfig _cfg;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _ingestLock = new(1, 1); // 取り込みの直列化（embedエンジン飽和防止）
    private readonly string _filesDir;

    public Ingest(Db db, LlmGateway gw, Supervisor sup, ChunkIndex index, AppConfig cfg, ILogger log)
    {
        _db = db; _gw = gw; _sup = sup; _index = index; _cfg = cfg; _log = log;
        var dataRoot = Path.IsPathRooted(cfg.DataDir) ? cfg.DataDir : Path.Combine(AppContext.BaseDirectory, cfg.DataDir);
        _filesDir = Path.Combine(dataRoot, "files");
    }

    /// <summary>元ファイル保存先（拡張パックON時のみ使用）。Programのthumb/fileエンドポイントが参照する</summary>
    public string FilesDir => _filesDir;

    public static readonly string[] SupportedExtensions = { ".md", ".txt", ".docx", ".pdf" };

    public static string ExtractText(string fileName, Stream stream)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".txt" => ReadText(stream),
            ".docx" => ExtractDocx(stream),
            ".pdf" => ExtractPdfAny(stream),
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

    /// <summary>PDF: PdfPig（ToUnicode CMap対応・日本語PDF可）を本体に、従来の自製抽出器をフォールバックに</summary>
    private static string ExtractPdfAny(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        string? modern = null;
        try { modern = PdfText.ExtractAll(bytes).Text; } catch { }
        if (!string.IsNullOrWhiteSpace(modern)) return modern;
        return ExtractPdf(new MemoryStream(bytes));
    }

    /// <summary>簡易PDFテキスト抽出（FlateDecode＋Tj/TJ演算子）。CJK CIDフォント非対応のベストエフォート。
    /// PdfPig失敗時のフォールバックとして残す（両方失敗で従来どおりのエラー）</summary>
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

    /// <summary>ファイル取り込み: 解析→（拡張パックON時）元ファイル保存・図面分岐→チャンク→埋め込み→索引更新</summary>
    public async Task<long> IngestFileAsync(string fileName, Stream content, CancellationToken ct)
    {
        await _ingestLock.WaitAsync(ct);
        try
        {
            // 元ファイル保存と抽出の両方で使うため、ストリームを1回だけ読み込む
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            var bytes = ms.ToArray();
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            bool packOn = Extensions.IsEnabled(_db, Extensions.DrawingId);

            _db.Exec("INSERT INTO files(name, status) VALUES($n,'parsing')", ("$n", fileName));
            long fileId = _db.LastInsertId();
            try
            {
                // PDF: PdfPig（座標付き）→ 空なら従来抽出器にフォールバック
                PdfExtractResult? pdf = null;
                string text;
                if (ext == ".pdf")
                {
                    try { pdf = PdfText.ExtractAll(bytes); text = pdf.Text; }
                    catch { text = ""; }
                    if (string.IsNullOrWhiteSpace(text)) { pdf = null; text = ExtractPdf(new MemoryStream(bytes)); }
                }
                else
                {
                    text = ExtractText(fileName, new MemoryStream(bytes));
                }

                // 拡張パック: 元ファイルを保存（「開く」・サムネイル・データ資産化の前提。失敗しても取り込みは続行）
                if (packOn) SaveOriginal(fileId, ext, bytes);

                var chunks = new List<string>();
                bool isDrawing = false;
                DrawingIngest.DrawingMeta meta = new(null, null, null, null, null, null);
                if (packOn && ext == ".pdf" && pdf != null && DrawingIngest.LooksLikeDrawing(fileName, pdf.Pages, pdf.Text))
                {
                    isDrawing = true;
                    meta = DrawingIngest.ExtractTitleBlock(pdf.Runs, pdf.PageWidth, pdf.PageHeight);
                    if (meta.ZubanRaw is null && Extensions.DrawingLlmEnabled(_db))
                        meta = await TryLlmMetaAsync(meta, pdf, ct);
                    // 図面は1枚1チャンク（表題欄前置き＋全テキスト）。テキスト層ゼロはチャンク0で登録継続
                    if (pdf.Text.Trim().Length > 0)
                        chunks.Add(DrawingIngest.BuildChunkText(meta, pdf.Text));
                }
                else if (text.Trim().Length > 0)
                {
                    chunks = Rag.Chunk(text);
                }
                if (chunks.Count == 0 && !isDrawing) throw new InvalidDataException("no text content");

                _db.Exec("UPDATE files SET status='embedding', kind=$k WHERE file_id=$i",
                    ("$k", isDrawing ? "drawing" : "doc"), ("$i", fileId));
                if (isDrawing) InsertDrawingMeta(fileId, meta);

                if (chunks.Count > 0)
                {
                    var embeddings = new List<float[]>();
                    _sup.StartEmbedAndRank(); // 前回クラッシュ・tier変更後のStopAll等で止まっていたら自己修復
                    foreach (var batch in chunks.Chunk(16))
                        embeddings.AddRange(await _gw.EmbedAsync(_cfg.EnginePortEmb, batch, ct));

                    for (int seq = 0; seq < chunks.Count; seq++)
                        _db.InsertChunk(fileId, seq, chunks[seq], ChunkIndex.FloatsToBytes(embeddings[seq]));
                    _index.AddRange(fileName, fileId, chunks.Select((t, seq) => (seq, t, embeddings[seq])));
                }
                _db.Exec("UPDATE files SET status='ready', chunk_count=$c WHERE file_id=$i", ("$c", chunks.Count), ("$i", fileId));
                if (packOn && ext == ".pdf") TryMakeThumbnail(fileId, bytes); // 失敗しても取り込みを妨げない
                _log.Info($"ingested {fileName}: {chunks.Count} chunks{(isDrawing ? " (drawing)" : "")}");
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

    /// <summary>削除時に元ファイル・サムネイルを掃除（ProgramのDELETEから呼ばれる）</summary>
    public void DeleteStoredFiles(long fileId)
    {
        try
        {
            if (!Directory.Exists(_filesDir)) return;
            foreach (var f in Directory.EnumerateFiles(_filesDir, $"{fileId}.*"))
                File.Delete(f);
        }
        catch (Exception ex) { _log.Warn($"delete stored files failed: {ex.Message}"); }
    }

    // ---- 拡張パック内部 ----

    private void SaveOriginal(long fileId, string ext, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(_filesDir);
            File.WriteAllBytes(Path.Combine(_filesDir, fileId + ext), bytes);
        }
        catch (Exception ex) { _log.Warn($"save original failed: {ex.Message}"); }
    }

    private void TryMakeThumbnail(long fileId, byte[] pdf)
    {
        try
        {
            using var lib = DocLib.Instance;
            using var reader = lib.GetDocReader(pdf, new PageDimensions(400, 560));
            var page = reader.GetPageReader(0);
            var raw = page.GetImage(); // BGRA 32bpp 行優先
            int w = (int)Math.Round((double)page.GetPageWidth());
            int h = w > 0 ? raw.Length / (4 * w) : 0; // 生バイト長から高さを検証付きで導出
            if (w <= 0 || h <= 0 || w * h * 4 != raw.Length) return; // 寸法が取れない場合はサムネなし
            var rgba = new byte[raw.Length];
            for (int i = 0; i + 3 < raw.Length; i += 4)
            { rgba[i] = raw[i + 2]; rgba[i + 1] = raw[i + 1]; rgba[i + 2] = raw[i]; rgba[i + 3] = 255; }
            File.WriteAllBytes(Path.Combine(_filesDir, fileId + ".thumb.png"), Png.EncodeRgba(rgba, w, h));
        }
        catch (Exception ex) { _log.Warn($"thumbnail failed: {ex.Message}"); }
    }

    private void InsertDrawingMeta(long fileId, DrawingIngest.DrawingMeta m) =>
        _db.Exec("INSERT OR REPLACE INTO drawing_meta(file_id, zuban_raw, zuban_norm, hinmei, zairyo, scale, revision, approved_at) VALUES($f,$zr,$zn,$h,$za,$s,$r,$a)",
            ("$f", fileId), ("$zr", m.ZubanRaw), ("$zn", m.ZubanNorm), ("$h", m.Hinmei), ("$za", m.Zairyo),
            ("$s", m.Scale), ("$r", m.Revision), ("$a", m.ApprovedAt));

    /// <summary>表題欄のLLM構造化（T6）。ルール抽出で図番が取れなかった図面のみ。失敗時はルール結果を維持</summary>
    private async Task<DrawingIngest.DrawingMeta> TryLlmMetaAsync(DrawingIngest.DrawingMeta m, PdfExtractResult pdf, CancellationToken ct)
    {
        try
        {
            _sup.EnsureLlm();
            var region = pdf.Runs.Where(r => pdf.PageWidth > 0 && pdf.PageHeight > 0
                && r.X > pdf.PageWidth * 0.55f && r.Y < pdf.PageHeight * 0.40f);
            var regionText = string.Join("\n", DrawingIngest.ToLines(region.ToList()));
            if (regionText.Length == 0) regionText = pdf.Text[..Math.Min(400, pdf.Text.Length)];
            var prompt = "以下は図面の表題欄付近から抽出したテキストである。図番(zuban)・品名(hinmei)・材質(zairyo)・改訂(revision)を読み取り、"
                + "JSON「{\"zuban\":..,\"hinmei\":..,\"zairyo\":..,\"revision\":..}」のみを返せ（不明はnull、推測しない）。テキスト:\n" + regionText;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var resp = await _gw.ChatOnceAsync(_cfg.EnginePortLlm, new[] { ("user", prompt) }, 0, 200, cts.Token);
            var start = resp.IndexOf('{');
            var end = resp.LastIndexOf('}');
            if (start < 0 || end <= start) return m;
            using var doc = System.Text.Json.JsonDocument.Parse(resp[start..(end + 1)]);
            string? Get(string k) =>
                doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            return m with
            {
                ZubanRaw = m.ZubanRaw ?? Get("zuban"),
                Hinmei = m.Hinmei ?? Get("hinmei"),
                Zairyo = m.Zairyo ?? Get("zairyo"),
                Revision = m.Revision ?? Get("revision"),
            };
        }
        catch (Exception ex) { _log.Warn($"drawing llm structuring failed: {ex.Message}"); return m; }
    }
}
