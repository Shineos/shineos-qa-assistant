using System.IO.Compression;
using System.Runtime.InteropServices.WindowsRuntime;
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

    public static readonly string[] SupportedExtensions = { ".md", ".txt", ".docx", ".pdf", ".xlsx", ".csv", ".tsv", ".dxf" };
    private static readonly string[] SheetExtensions = { ".xlsx", ".csv", ".tsv" };

    public static string ExtractText(string fileName, Stream stream)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".txt" => ReadText(stream),
            ".docx" => ExtractDocx(stream),
            ".pdf" => ExtractPdfAny(stream),
            ".dwg" => throw new NotSupportedException("DWG形式は直接検索できません。CADソフトからDXF形式でエクスポートするか、PDF形式で取り込んでください (SHINE_E_UNSUPPORTED_CAD)"),
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
                // PDF: PdfPig（座標付き）→ 空なら従来抽出器にフォールバック → 画像のみならOCR救済（拡張パックON時）
                PdfExtractResult? pdf = null;
                string text = "";
                List<string>? sheetChunks = null;
                bool scannedOcr = false;
                List<(float W, float H)>? ocrPageDims = null;
                List<DxfText.DxfRun>? dxfRuns = null;
                if (ext == ".pdf")
                {
                    try { pdf = PdfText.ExtractAll(bytes); text = pdf.Text; }
                    catch { text = ""; }
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        pdf = null;
                        InvalidDataException? legacyErr = null;
                        try { text = ExtractPdf(new MemoryStream(bytes)); }
                        catch (InvalidDataException ex) { legacyErr = ex; text = ""; }
                        // スキャンPDF（画像のみ）救済: ページをレンダリング→内蔵OCRでテキスト化。
                        // 実図面検証で金賞作品図面（スキャン1枚）が取り込み不可だった問題の根本対策
                        if (string.IsNullOrWhiteSpace(text) && packOn)
                        {
                            var ocr = await OcrPdfFallbackAsync(bytes, ct);
                            if (ocr != null)
                            {
                                var firstDim = ocr.PageDims.Count > 0 ? ocr.PageDims[0] : (0f, 0f);
                                pdf = new PdfExtractResult(ocr.Text, ocr.Pages, ocr.Runs, firstDim.Item1, firstDim.Item2);
                                text = pdf.Text;
                                ocrPageDims = ocr.PageDims;
                                scannedOcr = true;
                            }
                            else if (legacyErr != null)
                                throw new InvalidDataException(legacyErr.Message + " （スキャンPDF: OCRでも読み取れる文字がありませんでした）");
                        }
                        if (string.IsNullOrWhiteSpace(text) && legacyErr != null) throw legacyErr;
                    }
                }
                else if (SheetExtensions.Contains(ext))
                {
                    // 表計算ファイル（本体標準機能）: ヘッダ＋行バッチのチャンク列（Rag.Chunk不使用）
                    sheetChunks = SheetExtract.ExtractChunks(fileName, bytes);
                }
                else if (ext == ".dxf")
                {
                    // CAD図面（DXF）: 拡張パックON時のみ。TEXT/MTEXT/ATTRIBの文字と挿入点を抽出し
                    // 表題欄抽出にそのまま渡す（DWGはクローズド形式のため対象外→明確な案内メッセージ）
                    if (!packOn)
                        throw new NotSupportedException("DXF（CAD図面）の取り込みには設定→拡張機能「図面PDF検索・Q&A」を有効にしてください (SHINE_E_EXTENSION_DISABLED)");
                    var dxf = DxfText.Extract(bytes);
                    text = dxf.Text;
                    dxfRuns = dxf.Runs;
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
                    // 表紙付きPDF対策: 表題欄を読むページ（図面シート）を選んでから抽出する
                    var sheetPage = DrawingIngest.PickSheetPage(pdf.Runs, pdf.Pages);
                    var (pw, ph) = ocrPageDims != null && ocrPageDims.Count >= sheetPage
                        ? ocrPageDims[sheetPage - 1]
                        : DrawingIngest.PageDims(pdf.Runs, sheetPage);
                    if (pw <= 0) { pw = pdf.PageWidth; ph = pdf.PageHeight; }
                    var sheetRuns = pdf.Runs.Where(r => r.Page == sheetPage).ToList();
                    meta = DrawingIngest.ExtractTitleBlock(sheetRuns, pw, ph);
                    if (meta.ZubanRaw is null && Extensions.DrawingLlmEnabled(_db))
                        meta = await TryLlmMetaAsync(meta, sheetRuns, pw, ph, pdf.Text, ct);
                    // 図面は1枚1チャンク（表題欄前置き＋全テキスト）。テキスト層ゼロはチャンク0で登録継続。
                    // OCR由来のテキストは読み取り誤差の可能性をチャンク内に明記する（回答の根拠提示に直結）
                    var fullText = scannedOcr ? pdf.Text + "\n※このテキストはOCRによる読み取りです（誤読を含む場合があります）" : pdf.Text;
                    if (fullText.Trim().Length > 0)
                        chunks.Add(DrawingIngest.BuildChunkText(meta, fullText));
                }
                else if (dxfRuns != null)
                {
                    // DXF（CAD図面）: ヒューリスティック判定を介さず図面として取り込む（DXFは図面そのもの）。
                    // 文字の挿入点（Y上向き正=PDF式と同向）から表題欄抽出を共用する
                    isDrawing = true;
                    var runs = DxfText.ToPdfRuns(dxfRuns);
                    var (pw, ph) = DrawingIngest.PageDims(runs, 1);
                    if (pw <= 0) { pw = 297; ph = 210; } // 空図面の保険（A3縦）
                    meta = DrawingIngest.ExtractTitleBlock(runs, pw, ph);
                    if (meta.ZubanRaw is null && Extensions.DrawingLlmEnabled(_db))
                        meta = await TryLlmMetaAsync(meta, runs, pw, ph, text, ct);
                    if (text.Trim().Length > 0)
                        chunks.Add(DrawingIngest.BuildChunkText(meta, text));
                }
                else if (sheetChunks != null)
                {
                    chunks = sheetChunks;
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
                if (packOn && ext == ".pdf") await TryMakeThumbnailAsync(fileId, bytes, ct); // 失敗しても取り込みを妨げない
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

    private async Task TryMakeThumbnailAsync(long fileId, byte[] pdf, CancellationToken ct)
    {
        try
        {
            byte[] pngBytes; int w, h;
            using (var lib = DocLib.Instance)
            using (var reader = lib.GetDocReader(pdf, new PageDimensions(400, 560)))
            {
                var page = reader.GetPageReader(0);
                var raw = page.GetImage(); // BGRA 32bpp 行優先
                w = (int)Math.Round((double)page.GetPageWidth());
                h = w > 0 ? raw.Length / (4 * w) : 0; // 生バイト長から高さを検証付きで導出
                if (w <= 0 || h <= 0 || w * h * 4 != raw.Length) return; // 寸法が取れない場合はサムネなし
                var rgba = new byte[raw.Length];
                for (int i = 0; i + 3 < raw.Length; i += 4)
                { rgba[i] = raw[i + 2]; rgba[i + 1] = raw[i + 1]; rgba[i + 2] = raw[i]; rgba[i + 3] = 255; }
                if (IsNearlyBlack(rgba, w, h))
                {
                    // スキャンPDFのCMYK-JPEG黒つぶし対策（実図面で発生）: WinRT描画に差し替える
                    var winrt = await RenderPageWinRtAsync(pdf, 0, 400, ct);
                    pngBytes = winrt.Png; w = winrt.W; h = winrt.H;
                }
                else
                {
                    pngBytes = Png.EncodeRgba(rgba, w, h);
                }
            }
            File.WriteAllBytes(Path.Combine(_filesDir, fileId + ".thumb.png"), pngBytes);
        }
        catch (Exception ex) { _log.Warn($"thumbnail failed: {ex.Message}"); }
    }

    private void InsertDrawingMeta(long fileId, DrawingIngest.DrawingMeta m) =>
        _db.Exec("INSERT OR REPLACE INTO drawing_meta(file_id, zuban_raw, zuban_norm, hinmei, zairyo, scale, revision, approved_at) VALUES($f,$zr,$zn,$h,$za,$s,$r,$a)",
            ("$f", fileId), ("$zr", m.ZubanRaw), ("$zn", m.ZubanNorm), ("$h", m.Hinmei), ("$za", m.Zairyo),
            ("$s", m.Scale), ("$r", m.Revision), ("$a", m.ApprovedAt));

    /// <summary>表題欄のLLM構造化（T6）。ルール抽出で図番が取れなかった図面のみ。失敗時はルール結果を維持。
    /// 出力は検証器を通し、尺度・寸法表記の誤採用（実図面で発生）を二重に防ぐ</summary>
    private async Task<DrawingIngest.DrawingMeta> TryLlmMetaAsync(DrawingIngest.DrawingMeta m,
        List<PdfTextRun> sheetRuns, float pw, float ph, string fullText, CancellationToken ct)
    {
        try
        {
            _sup.EnsureLlm();
            var region = sheetRuns.Where(r => pw > 0 && ph > 0
                && r.X > pw * 0.55f && r.Y < ph * 0.40f);
            var regionText = string.Join("\n", DrawingIngest.ToLines(region.ToList()));
            if (regionText.Length == 0) regionText = fullText[..Math.Min(400, fullText.Length)];
            // 表題欄の兆候（図番系ラベルか図番パターン）がないシートではLLMを起こさない。
            // 「尺度1:1・受検番号・氏名」しかない表題欄でLLMが尺度/寸法を図番・品名にした実図面の誤りの根本対策
            var hasSign = System.Text.RegularExpressions.Regex.IsMatch(regionText, "図番|品名|材質|材料|DWG|TITLE|MATERIAL")
                || DrawingIngest.ZubanRegex().IsMatch(regionText);
            if (!hasSign) return m;
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
            var zuban = Get("zuban");
            var hinmei = Get("hinmei");
            var zairyo = Get("zairyo");
            var revision = Get("revision");
            return m with
            {
                ZubanRaw = m.ZubanRaw ?? (DrawingIngest.IsPlausibleZuban(zuban) ? zuban!.Trim() : null),
                Hinmei = m.Hinmei ?? (DrawingIngest.IsPlausibleHinmei(hinmei) && !DrawingIngest.IsMaterialToken(hinmei) ? hinmei!.Trim() : null),
                Zairyo = m.Zairyo ?? (DrawingIngest.IsPlausibleZairyo(zairyo) ? zairyo!.Trim() : null),
                Revision = m.Revision ?? (DrawingIngest.IsPlausibleRevision(revision) ? revision!.Trim() : null),
            };
        }
        catch (Exception ex) { _log.Warn($"drawing llm structuring failed: {ex.Message}"); return m; }
    }

    public sealed record OcrPdfText(string Text, int Pages, List<PdfTextRun> Runs, List<(float W, float H)> PageDims);

    /// <summary>画像のみPDF（スキャン図面）の救済: 各ページをレンダリング→内蔵OCR（回転最良）でテキスト化。
    /// 拡張パックON時のみ呼ばれる。長文スキャン（MaxPages超）やOCR結果がほぼ空のページは救済対象外としてnull
    /// （静的: テストから直接検証できる。OCRは ja-JP言語パック必須、無ければガイド付きの例外）。
    /// レンダリングはWindows.Data.Pdf（WinRT）。Docnet/PDFiumはスキャンPDFのCMYK-JPEGを黒つぶしで
    /// 描画するため（実図面の金賞作品PDFで発生）、OCR入力には使えない</summary>
    public static async Task<OcrPdfText?> OcrPdfFallbackAsync(byte[] bytes, CancellationToken ct)
    {
        if (!Ocr.IsAvailable())
            throw new InvalidDataException("pdf: 画像のみのPDFです。スキャン図面のOCR取り込みにはWindowsの日本語言語パックが必要です");
        // レンダ解像度はエンジン上限まで使用する（表題欄などの小さな文字の認識率が解像度に直結する。
        // 従来の2200から上限2600へ引き上げ）
        var target = (int)Math.Min(2600u, Ocr.MaxImageDimension);
        // ページ数だけ先に知る必要があるため全体を1回ロードして順に描画する
        var tmp = Path.Combine(Path.GetTempPath(), "shineosqa-ocr-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(tmp);
            var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file).AsTask(ct);
            int pages = (int)pdf.PageCount;
            if (pages == 0 || pages > DrawingIngest.MaxPages) return null; // 長文スキャンは図面拡張の対象外（誠実に失敗させる）
            var pngs = new List<byte[]>();
            var dims = new List<(float W, float H)>();
            for (uint i = 0; i < pages; i++)
            {
                using var page = pdf.GetPage(i);
                using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var opts = new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = (uint)target };
                await page.RenderToStreamAsync(ms, opts).AsTask(ct);
                var size = (uint)ms.Size;
                using var reader = new Windows.Storage.Streams.DataReader(ms.GetInputStreamAt(0));
                await reader.LoadAsync(size);
                var pngBytes = new byte[size];
                reader.ReadBytes(pngBytes);
                var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(new MemoryStream(pngBytes).AsRandomAccessStream());
                pngs.Add(pngBytes);
                dims.Add((dec.PixelWidth, dec.PixelHeight));
            }
            var sb = new StringBuilder();
            var runs = new List<PdfTextRun>();
            for (int i = 0; i < pngs.Count; i++)
            {
                var best = await Ocr.RecognizeBestAsync(pngs[i]);
                sb.AppendLine(best.Text);
                var (w, h) = dims[i];
                // OCRの単語矩形（左上原点・ピクセル）をPDF式（左下原点）へ変換し、表題欄抽出を共用する
                foreach (var word in best.Words)
                    runs.Add(new PdfTextRun(word.Text, i + 1, (float)word.X, (float)(h - (word.Y + word.H)), (float)word.W, (float)word.H));
                ct.ThrowIfCancellationRequested();
            }
            var text = sb.ToString();
            // 白紙・無地ページ（OCR実測で0〜3文字程度）は救済不能。小さな表題欄のみのシートも救えるよう
            // 下限は最小限にする（実測: 画像サンプル640x320の有効テキスト18文字が救済対象）
            if (text.Trim().Length < 8) return null;
            return new OcrPdfText(text, pages, runs, dims);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>指定ページをWindows.Data.Pdf（WinRT）でPNG描画する。Docnet黒つぶし対策の共通経路
    /// （SourcePreviewの図上ハイライトでも使用）。戻り値のPNG寸法はBitmapDecoderで実測する
    /// （DestinationWidthは幅指定のみで高さは縦横比維持）</summary>
    public static async Task<(byte[] Png, int W, int H)> RenderPageWinRtAsync(byte[] pdfBytes, int pageIndex, int targetWidth, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shineosqa-pdf-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            await File.WriteAllBytesAsync(tmp, pdfBytes, ct);
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(tmp);
            var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file).AsTask(ct);
            if (pageIndex >= pdf.PageCount) throw new InvalidDataException("pdf: page index out of range");
            using var page = pdf.GetPage((uint)pageIndex);
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var opts = new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = (uint)targetWidth };
            await page.RenderToStreamAsync(ms, opts).AsTask(ct);
            var size = (uint)ms.Size;
            using var reader = new Windows.Storage.Streams.DataReader(ms.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            var png = new byte[size];
            reader.ReadBytes(png);
            var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(new MemoryStream(png).AsRandomAccessStream());
            return (png, (int)dec.PixelWidth, (int)dec.PixelHeight);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>描画結果の黒つぶし検出（疎サンプリング）。スキャンPDFのCMYK-JPEGをDocnetで描くと
    /// 全面黒になるため（実図面で発生）、サムネイルをWinRT描画へフォールバックする判定に使う</summary>
    private static bool IsNearlyBlack(byte[] rgba, int w, int h)
    {
        long sample = 0, dark = 0;
        int stride = w * 4;
        int limit = Math.Min(rgba.Length - 3, stride * h);
        for (int i = 0; i < limit; i += 4 * 97)
        {
            sample++;
            int lum = (rgba[i] + rgba[i + 1] + rgba[i + 2]) / 3;
            if (lum < 8) dark++;
        }
        return sample > 0 && dark * 20 > sample * 19; // 95%以上が黒
    }
}
