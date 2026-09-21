using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ShineosQA.Backend;

/// <summary>表計算ファイル（.xlsx/.csv/.tsv）の取り込み解析（拡張パック「Excel・CSV取り込み」）。
/// xlsxはOpenXMLの最小解析（sharedStrings＋worksheets。docxと同じZipArchive+XmlReaderの依存ゼロ方針）。
/// 既知限界（v1）: Excelの日付セルはシリアル値のまま出力、数式は再計算値でなく保存値、.xls（旧バイナリ）は非対応</summary>
public static class SheetExtract
{
    public const int MaxRows = 20000;      // 1ファイルの最大データ行（メモリ・埋め込みコスト上限）
    public const int RowsPerChunk = 30;    // チャンクあたりのデータ行数（ヘッダ行は毎チャンクに複製）
    private const int MaxSheets = 20;

    static SheetExtract() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // shift_jis用

    /// <summary>拡張子で振り分けてチャンク列を生成。1チャンク=ヘッダ＋30行。出力例:
    /// 【表】品名リスト.csv / シート: Sheet1 / 1〜30行目\n品名 | 材質 | 数量\nブラケット | SS400 | 10 ...</summary>
    public static List<string> ExtractChunks(string fileName, byte[] bytes)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var sheets = ext switch
        {
            ".xlsx" => ParseXlsx(bytes),
            ".csv" => new[] { (Path.GetFileNameWithoutExtension(fileName), ParseCsv(bytes, ',')) },
            ".tsv" => new[] { (Path.GetFileNameWithoutExtension(fileName), ParseCsv(bytes, '\t')) },
            _ => throw new NotSupportedException($"unsupported sheet type: {ext}"),
        };
        var chunks = new List<string>();
        foreach (var (name, rows) in sheets)
        {
            if (rows.Length == 0) continue;
            var header = string.Join(" | ", rows[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"列{i + 1}" : h.Trim()));
            var data = rows.Skip(1).ToList();
            // ヘッダのみ（データ0行）でも列名で検索できるよう1チャンクを出す
            for (int start = 0; start < Math.Max(data.Count, 1); start += RowsPerChunk)
            {
                var end = Math.Min(start + RowsPerChunk, Math.Max(data.Count, 1));
                var sb = new StringBuilder();
                sb.Append($"【表】{Path.GetFileName(fileName)} / シート: {name} / {start + 2}〜{end + 1}行目\n");
                sb.Append(header).Append('\n');
                for (int i = start; i < end && i < data.Count; i++)
                    sb.Append(string.Join(" | ", data[i])).Append('\n');
                chunks.Add(sb.ToString().TrimEnd());
                if (chunks.Count * (long)RowsPerChunk >= MaxRows) break;
            }
            if (chunks.Count >= 700) break; // チャンク数上限（埋め込みコスト上限・20000行相当）
        }
        return chunks;
    }

    /// <summary>CSV/TSV解析（クォート・エスケープ済みカンマ・CRLF対応）。UTF-8 → 失敗時 Shift-JIS（日本語CSVの実勢）</summary>
    public static string[][] ParseCsv(byte[] bytes, char sep)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.GetEncoding("shift_jis").GetString(bytes); }
        var rows = new List<string[]>();
        var cur = new List<string>();
        var field = new StringBuilder();
        bool inQuote = false, fieldQuoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuote)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuote = false;
                }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) { inQuote = true; fieldQuoted = true; }
            else if (c == sep) { cur.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                cur.Add(field.ToString()); field.Clear();
                if (cur.Any(v => v.Length > 0)) rows.Add(cur.ToArray());
                cur.Clear();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || fieldQuoted || cur.Count > 0)
        {
            cur.Add(field.ToString());
            if (cur.Any(v => v.Length > 0)) rows.Add(cur.ToArray());
        }
        if (rows.Count > MaxRows) rows = rows.Take(MaxRows).ToList();
        return rows.ToArray();
    }

    /// <summary>xlsx解析。sharedStrings＋workbook（シート名）＋rels（rId→実体）＋worksheetsを
    /// ZipArchive+XmlReaderで最小解析する。壊れた構成は例外（呼び出し側で取り込みエラー扱い）</summary>
    public static (string SheetName, string[][] Rows)[] ParseXlsx(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sst)
            using (var r = XmlReader.Create(sst.Open()))
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "si")
                    {
                        var s = new StringBuilder();
                        while (r.Read() && !(r.NodeType == XmlNodeType.EndElement && r.LocalName == "si"))
                            if (r.NodeType == XmlNodeType.Element && r.LocalName == "t" && r.Read()) s.Append(r.Value);
                        shared.Add(s.ToString());
                    }

        var sheetNames = new List<(string Name, string Rid)>();
        if (zip.GetEntry("xl/workbook.xml") is { } wb)
            using (var r = XmlReader.Create(wb.Open()))
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet")
                    {
                        var name = r.GetAttribute("name") ?? "Sheet";
                        var rid = r.GetAttribute("r:id") ?? "";
                        sheetNames.Add((name, rid));
                    }

        var rels = new Dictionary<string, string>();
        if (zip.GetEntry("xl/_rels/workbook.xml.rels") is { } rel)
            using (var r = XmlReader.Create(rel.Open()))
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship")
                    {
                        var id = r.GetAttribute("Id") ?? "";
                        var target = r.GetAttribute("Target") ?? "";
                        rels[id] = target.StartsWith("/", StringComparison.Ordinal) ? target[1..] : "xl/" + target;
                    }

        var outSheets = new List<(string, string[][])>();
        var entries = zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)
                                             && e.FullName.EndsWith(".xml", StringComparison.Ordinal)).ToList();
        for (int s = 0; s < entries.Count && s < MaxSheets; s++)
        {
            var entry = entries[s];
            var name = sheetNames.FirstOrDefault(sn => rels.TryGetValue(sn.Rid, out var t) &&
                       ("xl/" + t).Equals(entry.FullName, StringComparison.OrdinalIgnoreCase)).Name;
            if (name == null) name = sheetNames.Count > s ? sheetNames[s].Name : $"Sheet{s + 1}";
            var rows = ReadSheetRows(entry, shared);
            if (rows.Length > 0) outSheets.Add((name, rows));
        }
        return outSheets.ToArray();
    }

    private static string[][] ReadSheetRows(ZipArchiveEntry entry, List<string> shared)
    {
        var rows = new List<string[]>();
        using var r = XmlReader.Create(entry.Open());
        var curRow = new SortedDictionary<int, string>();
        string? curType = null, curRef = null;
        var curVal = new StringBuilder();
        bool inCell = false;
        int seqCol = 0;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                if (r.LocalName == "row")
                {
                    FlushRow(rows, curRow);
                    curRow.Clear();
                    seqCol = 0;
                }
                else if (r.LocalName == "c")
                {
                    curType = r.GetAttribute("t");
                    curRef = r.GetAttribute("r");
                    curVal.Clear();
                    inCell = true;
                    // 列位置: r属性（例: B3）から。無い場合は出現順
                    var col = -1;
                    if (curRef is not null)
                    {
                        int idx = 0;
                        foreach (var ch in curRef)
                        {
                            if (ch is >= 'A' and <= 'Z') idx = idx * 26 + (ch - 'A' + 1);
                            else if (ch is >= 'a' and <= 'z') idx = idx * 26 + (ch - 'a' + 1);
                            else break;
                        }
                        col = idx - 1;
                    }
                    seqCol = col >= 0 ? col : seqCol;
                }
                else if (inCell && (r.LocalName == "v" || r.LocalName == "t") && !r.IsEmptyElement)
                {
                    if (r.Read() && r.NodeType == XmlNodeType.Text) curVal.Append(r.Value);
                }
            }
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "c")
            {
                inCell = false;
                var v = curVal.ToString();
                if (curType == "s" && int.TryParse(v, out var si)) v = si < shared.Count ? shared[si] : "";
                curRow[seqCol] = v;
                seqCol++;
            }
            // 行の確定は次の row 開始時（直前の curRow を書き出す）とドキュメント末の FlushRow のみ。
            // row 終了時にも書き出すと同じ行が二重登録される
        }
        FlushRow(rows, curRow);
        var width = rows.Count > 0 ? rows.Max(x => x.Length) : 0;
        return rows.Select(x => x.Concat(Enumerable.Repeat("", width)).Take(width).ToArray()).ToArray();
    }

    private static void FlushRow(List<string[]> rows, SortedDictionary<int, string> cur)
    {
        if (cur.Count == 0) return;
        var arr = new string[cur.Keys.Max() + 1];
        for (int i = 0; i < arr.Length; i++) arr[i] = "";
        foreach (var kv in cur) arr[kv.Key] = kv.Value;
        if (arr.Any(v => v.Length > 0)) rows.Add(arr);
    }
}
