using System.Text;
using System.Text.Json;

namespace ShineosQA.Backend;

/// <summary>チャットオーケストレーション（実測済みv2-fastフロー）:
/// 回答キャッシュ → Web検索(任意) → ハイブリッドtop8 → リランクtop2（ガード-2.0）→
/// スニペット → 痩身プロンプト → SSEストリーム → 履歴保存 → キャッシュ登録</summary>
public sealed class ChatFlow
{
    private readonly AppConfig _cfg;
    private readonly Db _db;
    private readonly Supervisor _sup;
    private readonly LlmGateway _gw;

    /// <summary>エージェント経路のツール定義（Qwen3ツール呼び出し・実測: 1応答で複数ツールを並列出力）。
    /// jisseki_lookup の値は実績情報として既に注入するため、計画呼び出しでは検索対象の決定にのみ使う</summary>
    private const string AgentToolsJson =
        "[{\"type\":\"function\",\"function\":{\"name\":\"knowledge_search\",\"description\":\"社内ナレッジ(図面・規定・マニュアル)を検索し、関連する文書の断片を返す\",\"parameters\":{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"検索語(日本語)\"}},\"required\":[\"query\"]}}}," +
        "{\"type\":\"function\",\"function\":{\"name\":\"jisseki_lookup\",\"description\":\"図番の過去見積額・原価・納期を実績表から検索する\",\"parameters\":{\"type\":\"object\",\"properties\":{\"zuban\":{\"type\":\"string\",\"description\":\"図番 例: ST-1042A\"}},\"required\":[\"zuban\"]}}}]";
    private readonly ChunkIndex _index;
    private readonly WebSearch _web;
    private readonly ILogger _log;
    private readonly Diagnostics _diag;
    private readonly FaqStore _faq;
    private readonly object _cacheLock = new();
    private List<CacheEntry> _cache = new();

    private sealed record CacheEntry(string Question, string Answer, string SourcesJson, float[] Emb, string Model, long Id);

    public ChatFlow(AppConfig cfg, Db db, Supervisor sup, LlmGateway gw, ChunkIndex index, WebSearch web, ILogger log, Diagnostics diag)
    {
        _cfg = cfg; _db = db; _sup = sup; _gw = gw; _index = index; _web = web; _log = log; _diag = diag;
        _faq = new FaqStore(db, log);
        LoadCache();
        // プロンプト仕様変更時は旧プロンプトで生成した回答キャッシュを無効化（誤回答の恒久化防止）
        if (_db.GetSetting("prompt_version", "") != Rag.PromptVersion)
        {
            ClearCache();
            _db.SetSetting("prompt_version", Rag.PromptVersion);
        }
    }

    private void LoadCache()
    {
        lock (_cacheLock)
            _cache = _db.Query("SELECT id, question, answer, sources_json, emb, model FROM answer_cache ORDER BY id DESC LIMIT 2000")
                .Select(r => new CacheEntry((string)r["question"]!, (string)r["answer"]!, (string?)r["sources_json"] ?? "[]",
                    ChunkIndex.BytesToFloats((byte[])r["emb"]!), (string?)r["model"] ?? "", (long)r["id"]!)).ToList();
    }

    /// <summary>回答キャッシュ全消去（プロンプト変更後の再生成・開発用）</summary>
    public void ClearCache()
    {
        _db.Exec("DELETE FROM answer_cache");
        lock (_cacheLock) _cache.Clear();
    }

    /// <summary>メモリ上の回答キャッシュ件数（GET /api/diagnostics の cache 表示用）</summary>
    public int CacheCount { get { lock (_cacheLock) return _cache.Count; } }

    /// <summary>公開FAQ（現場の正解）の管理API用アクセサ</summary>
    public FaqStore Faq => _faq;

    // ---- 計測ヘルパー（Phase 0.1） ----

    /// <summary>ステージ経過msの差分。マーク未設定(0)や未計測(-1)は0に落とす（負値・巨大値を出さない）</summary>
    private static long Diff(long mark, long baseMark) =>
        mark > 0 && baseMark > 0 ? Math.Max(0, mark - baseMark) : 0;

    /// <summary>done の stages フィールド。キーは /api/diagnostics の stages と同一。</summary>
    private static object Stages(long firstTokenMs, long totalMs, long tEmbed, long tSearch, long tRetrieve, long tPrompt) => new
    {
        t_embed_ms = tEmbed,
        t_search_ms = Diff(tSearch, tEmbed),
        t_retrieve_ms = Diff(tRetrieve, tEmbed),
        t_prompt_ms = Diff(tPrompt, tRetrieve),
        t_first_token_ms = firstTokenMs,
        t_total_ms = totalMs,
    };

    public sealed class SourceInfo
    {
        public string File { get; set; } = "";
        public string Snippet { get; set; } = "";
        public string Text { get; set; } = "";
        public string Kind { get; set; } = "kb";   // kb=社内ナレッジ / web=Web検索
        public string? Url { get; set; }           // Web検索のみ
        // 拡張パック（図面）: Kindはkbのまま、図面メタデータを別フィールドで運ぶ
        // （出典行の正規化 NormalizeSourcesLine が Kind=="kb" 前提のため、種別フラグを分ける）
        // JsonPropertyName必須: CamelCaseポリシーだと fileId/isDrawing になりUI側（file_id/is_drawing）と
        // 不一致し、図面出典の【図面】表示が一度も発火しなかった（出典表示不具合の根本原因）
        [System.Text.Json.Serialization.JsonPropertyName("file_id")]
        public long? FileId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("is_drawing")]
        public bool IsDrawing { get; set; }        // drawing_meta に存在する図面ファイル（出典UIの【図面】表示に使用）
        public string? Zuban { get; set; }
        public string? Hinmei { get; set; }
        public string? Revision { get; set; }
    }

    // SSE/永続化JSONは camelCase に統一（他エンドポイントのASP.NET既定と同一契約）
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>類義語・表記ゆれで通常のtop2では根拠が漏れやすい質問。列挙質問ほど広げず3件に留める。</summary>
    private static readonly System.Text.RegularExpressions.Regex SynonymBroadRegex = new(
        @"駐車場|パーキング|マイカー|自分の車|自家用車|ミーティングルーム|有給|テレワーク|在宅勤務|ホテル|宿泊",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>章間重複抑制用の要点メモ（改行を詰めて80字に切り詰める）</summary>
    private static string TruncateForMemo(string s)
    {
        var flat = s.Replace("\r", "").Replace("\n", " ").Trim();
        return Truncate(flat, 80);
    }

    /// <summary>会話文脈から独立した検索クエリをLLMに生成させる（Web検索フォローアップ改善・A案）。
    /// 例: 「今日の天気は」→「こちらは神奈川県ですよ」→「神奈川県 天気 予報」</summary>
    private async Task<string> GenerateSearchQueryAsync(
        List<(string role, string content)> history, string currentMessage, CancellationToken ct)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("以下の会話の文脈を踏まえ、ユーザーが今知りたいことを表す検索クエリを1行で出力してください。");
            sb.Append("検索クエリ以外の説明は不要です。\n\n");
            sb.Append("--- 会話 ---\n");
            foreach (var (role, content) in history)
                sb.Append($"{(role == "user" ? "ユーザー" : "アシスタント")}: {content}\n");
            sb.Append($"ユーザー: {currentMessage}\n");
            sb.Append("--- 検索クエリ: ");

            var result = new System.Text.StringBuilder();
            await _gw.ChatStreamAsync(_cfg.EnginePortLlm,
                new List<(string, string)> { ("system", sb.ToString()) },
                0.0, 60, delta =>
                {
                    result.Append(delta);
                    return Task.CompletedTask;
                }, ct);
            var query = result.ToString().Trim().Trim('"', '「', '」', '\n', '\r');
            // 生成失敗・空・長すぎる場合はフォールバック（前の質問+現在）
            if (string.IsNullOrEmpty(query) || query.Length > 100)
            {
                var prevUser = history.LastOrDefault(h => h.role == "user").content ?? "";
                return $"{prevUser} {currentMessage}".Trim();
            }
            return query;
        }
        catch (Exception ex)
        {
            _log.Warn($"search query generation failed: {ex.Message}");
            var prev = history.LastOrDefault(h => h.role == "user").content ?? "";
            return $"{prev} {currentMessage}".Trim();
        }
    }

    /// <summary>図面メタデータの取得（clarify検出用）。レコードが無い（図面でない/拡張OFF）ならnull</summary>
    private (string? Zuban, string? Revision, string? Hinmei)? DrawingMetaOfFile(long fileId)
    {
        try
        {
            var rows = _db.Query("SELECT zuban_raw, hinmei, revision FROM drawing_meta WHERE file_id=$i", ("$i", fileId));
            if (rows.Count == 0) return null;
            return (rows[0]["zuban_raw"]?.ToString(), rows[0]["revision"]?.ToString(), rows[0]["hinmei"]?.ToString());
        }
        catch { return null; }
    }

    /// <summary>指示語（前方照応）を含むか: 「それ/この/その/前の/さっき/上記/さよう」等</summary>
    private static bool RegexHasAnaphora(string s) =>
        s.Contains("それ") || s.Contains("この") || s.Contains("その") || s.Contains("前の") ||
        s.Contains("さっき") || s.Contains("上記") || s.Contains("こちら") || s.Contains("同じ");

    /// <summary>図面ファイルの出典にメタデータを付与（拡張パックOFFなら無変換）。クエリは最大2件なので都度問い合わせで十分</summary>
    private SourceInfo EnrichDrawing(SourceInfo s, long fileId)
    {
        try
        {
            var rows = _db.Query("SELECT zuban_raw, hinmei, revision FROM drawing_meta WHERE file_id=$i", ("$i", fileId));
            if (rows.Count == 0) return s;
            s.FileId = fileId;
            s.IsDrawing = true; // 図番が未抽出（null）の図面でも【図面】出典として表示できるように種別だけは立てる
            s.Zuban = rows[0]["zuban_raw"] as string;
            s.Hinmei = rows[0]["hinmei"] as string;
            s.Revision = rows[0]["revision"] as string;
        }
        catch { /* drawing_meta未作成環境（旧DB起動直下等）では無変換 */ }
        return s;
    }

    /// <summary>出典プレビュー用スニペット。語彙一致で特定できない場合（日英クロスリンガル等）は
    /// リランカーでチャンク内の該当窓を特定する（ハイライトが全文に掛かるのを防ぐ）</summary>
    private async Task<string> SnippetForSourceAsync(string text, IReadOnlySet<string> qTokens, string query, CancellationToken ct)
    {
        var s = Rag.Snippet(text, qTokens, 120);
        if (s != text) return s; // 語彙照準OK
        if (text.Length <= 120) return s;
        // フォールバック: 120字窓（60字ステップ）をリランンクして該当位置を特定
        var windows = new List<string>();
        for (int i = 0; i < text.Length; i += 60)
            windows.Add(text.Substring(i, Math.Min(120, text.Length - i)));
        try
        {
            var rr = await _gw.RerankAsync(_cfg.EnginePortRank, query, windows, 1, ct);
            if (rr.Count > 0) return windows[rr[0].Index];
        }
        catch { /* リランカ不可なら全文のまま */ }
        return s;
    }

    /// <summary>回答末尾の「出典：…」行を、実際に与えた社内文書のリストに正規化（UI出典パネルと完全一致）</summary>
    private static string NormalizeSourcesLine(string text, List<SourceInfo> sources)
    {
        var kbFiles = sources.Where(s => s.Kind == "kb").Select(s => s.File).Distinct().ToList();
        if (kbFiles.Count == 0) return text;
        var canonical = "出典：" + string.Join("、", kbFiles);
        var regex = new System.Text.RegularExpressions.Regex(@"出典[：:][^\n]*");
        if (regex.IsMatch(text)) return regex.Replace(text, canonical, 1);
        return text.TrimEnd() + "\n\n" + canonical;
    }

    // ---- SSEヘルパー ----
    /// <summary>キャプチャ画像の保存（拡張パック）。data/files/captures/ 配下にPNGとして残し、
    /// 直近のユーザーメッセージにパスを紐付ける（過去チャットでの再表示用。ローカルPC外へは送信しない）</summary>
    private void SaveCaptureImage(long chatId, string dataUrl)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            if (!dataUrl.StartsWith("data:image/", StringComparison.Ordinal) || comma < 0) return;
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            if (bytes.Length == 0 || bytes.Length > 20 * 1024 * 1024) return;
            var dataRoot = Path.IsPathRooted(_cfg.DataDir) ? _cfg.DataDir : Path.Combine(AppContext.BaseDirectory, _cfg.DataDir);
            var dir = Path.Combine(dataRoot, "files", "captures");
            Directory.CreateDirectory(dir);
            var rel = $"files/captures/{Guid.NewGuid():N}.png";
            File.WriteAllBytes(Path.Combine(dataRoot, rel.Replace('/', Path.DirectorySeparatorChar)), bytes);
            // 直前にINSERTしたユーザーメッセージへ紐付ける
            _db.Exec("UPDATE messages SET image=$p WHERE id=(SELECT MAX(id) FROM messages WHERE chat_id=$c AND role='user')",
                ("$p", rel), ("$c", chatId));
        }
        catch (Exception ex) { _log.Warn($"capture image save failed: {ex.Message}"); }
    }

    private static async Task Sse(HttpContext ctx, string ev, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes($"event: {ev}\ndata: {json}\n\n");
        await ctx.Response.Body.WriteAsync(bytes);
        await ctx.Response.Body.FlushAsync();
    }

    // チャット処理の直列化（2026-10-23実機E2E）: llama-serverは単一インスタンスのため質問が
    // 競合すると両方の応答が遅延・劣化する（prompt評価が通常の数倍に伸長を実測）。
    // 2件目以降はphaseイベントで順番待ちを通知してから待つ。using宣言でメソッド末尾まで保持し、
    // 要約/作成モードの早期return経路でも確実に解放する
    private static readonly SemaphoreSlim ChatGate = new(1, 1);

    private static async Task<IDisposable> EnterChatGateAsync(HttpContext ctx, CancellationToken ct)
    {
        if (await ChatGate.WaitAsync(0, ct)) return new ChatGateReleaser();
        await Sse(ctx, "phase", new { text = "他の質問を処理中です。順番にお答えします…" });
        // 待ち時間が長いとクライアント/中間層のボディタイムアウトで接続が切れるため
        // 3秒ごとにSSEコメント(ping)を送り、10秒ごとに経過秒を通知する（2026-10-23実測）
        var waits = 0;
        while (!await ChatGate.WaitAsync(3000, ct))
        {
            waits++;
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(": ping\n\n"));
            await ctx.Response.Body.FlushAsync();
            if (waits % 3 == 0)
                await Sse(ctx, "phase", new { text = $"他の質問を処理中です… {waits * 3}秒経過" });
        }
        return new ChatGateReleaser();
    }

    private sealed class ChatGateReleaser : IDisposable
    {
        public void Dispose() => ChatGate.Release();
    }

    /// <summary>全量常駐モードの閾値: チャンク合計がこの文字数以下なら検索/リランクをスキップし
    /// 全文をプロンプトへ注入する（2026-10-23・Dify FAQボット式の全量コンテキスト設計）</summary>
    private const int FullContextMaxChars = 3000;

    /// <summary>この文字数以下のチャンクはスニペットせず全文注入する（240字窓の末尾行切断対策）</summary>
    private const int SnippetSmallChunkChars = 300;

    /// <summary>全量コンテキストのチャンク読み込み（Phase 1.2修正・テスト可能なよう切り出し）。
    /// 旧実装は c.seq を取らず Seq=0 固定で合成していたため、隣接chunk連結が全チャンクに
    /// 同じ seq1 を付加し、出典本文の重複を生んでいた。status='ready' のみを対象にする
    /// （索引 ChunkIndex.LoadFrom と母集団を揃える）。</summary>
    internal static List<ChunkIndex.HybridHit> LoadFullContextChunks(Db db)
    {
        var list = new List<ChunkIndex.HybridHit>();
        foreach (var row in db.Query(
            "SELECT c.file_id AS fid, f.name AS name, c.seq AS seq, c.text AS text, c.emb AS emb " +
            "FROM chunks c JOIN files f ON f.file_id = c.file_id WHERE f.status='ready' AND f.enabled=1 ORDER BY c.file_id, c.seq"))
        {
            var fid = Convert.ToInt64(row["fid"] ?? 0L);
            var seq = Convert.ToInt32(row["seq"] ?? 0);
            var text = Convert.ToString(row["text"]) ?? "";
            list.Add(new ChunkIndex.HybridHit
            {
                Rec = new ChunkIndex.Rec(fid, fid, Convert.ToString(row["name"]) ?? "?", seq, text,
                    ChunkIndex.BytesToFloats((byte[])row["emb"]), new HashSet<string>()),
            });
        }
        return list;
    }

    /// <summary>出典本文の組み立て（Phase 1.2修正・テスト可能なよう切り出し）。
    /// 通常RAGでは隣接チャンク(seq+1)を連結してチャンク境界の分断を防ぐが、
    /// 全量モードは全文注入（全チャンクが chosen に入る）ため連結すると同じ文が二重に入る。</summary>
    internal static string SourceTextFor(bool fullContext, ChunkIndex.Rec rec, string? nextText) =>
        fullContext || nextText is null ? rec.Text : rec.Text + "\n" + nextText;

    /// <summary>FAQ即答。LLM・埋め込み・検索を一切通さない（目標 &lt;50ms）。
    /// 保存済みの回答をそのまま返し、出典JSONもそのまま渡す。</summary>
    private async Task AnswerInstantAsync(HttpContext ctx, long chatId, string answer, string sourcesJson,
        string route, double confidence, System.Diagnostics.Stopwatch sw)
    {
        object sources;
        try { sources = JsonSerializer.Deserialize<object>(sourcesJson) ?? Array.Empty<object>(); }
        catch { sources = Array.Empty<object>(); }

        _db.Exec("INSERT INTO messages(chat_id, role, content, sources_json, kind) VALUES($c,'assistant',$m,$s,'answer')",
            ("$c", chatId), ("$m", answer), ("$s", sourcesJson));
        var assistantMessageId = _db.LastInsertId();
        _db.Exec("UPDATE chats SET updated_at=datetime('now') WHERE id=$c", ("$c", chatId));

        var ms = sw.ElapsedMilliseconds;
        await Sse(ctx, "delta", new { content = answer });
        await Sse(ctx, "done", new
        {
            cached = true, instant = true, kind = "answer", route, confidence, message_id = assistantMessageId,
            stages = Stages(-1, ms, 0, 0, 0, 0), sources, ms,
        });
        _diag.Record(new Diagnostics.Sample(route, "answer", true, -1, 0, 0, 0, 0, 0, ms));
    }

    /// <summary>決定論コネクタ即答（図面台帳・実績）。LLMの要約を挟まず、DBの値をそのまま返す。</summary>
    private async Task AnswerDeterministicAsync(HttpContext ctx, long chatId, string answer,
        List<SourceInfo> sources, string route, double confidence, System.Diagnostics.Stopwatch sw)
    {
        var sourcesJson = JsonSerializer.Serialize(sources, JsonOpts);
        _db.Exec("INSERT INTO messages(chat_id, role, content, sources_json, kind) VALUES($c,'assistant',$m,$s,'answer')",
            ("$c", chatId), ("$m", answer), ("$s", sourcesJson));
        var assistantMessageId = _db.LastInsertId();
        _db.Exec("UPDATE chats SET updated_at=datetime('now') WHERE id=$c", ("$c", chatId));

        var ms = sw.ElapsedMilliseconds;
        await Sse(ctx, "delta", new { content = answer });
        await Sse(ctx, "done", new
        {
            cached = false, instant = true, kind = "answer", route, confidence, message_id = assistantMessageId,
            stages = Stages(-1, ms, 0, 0, 0, 0), sources, ms,
        });
        _diag.Record(new Diagnostics.Sample(route, "answer", false, -1, 0, 0, 0, 0, 0, ms));
    }

    public async Task RunAsync(HttpContext ctx)
    {
        using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
        var root = body.RootElement;
        string chatUuid = root.TryGetProperty("chat_uuid", out var cu) && cu.ValueKind == JsonValueKind.String ? cu.GetString()! : "";
        var message = root.GetProperty("message").GetString() ?? throw new BadHttpRequestException("message required");
        // キャプチャ画像（拡張パック）: data URL（data:image/png;base64,...）で受け取りローカルに保存して
        // メッセージに紐付ける。画像は完全オフラインのローカルPC内に留まる（S3等への送信は無い）
        string? captureImage = root.TryGetProperty("capture_image", out var ci) && ci.ValueKind == JsonValueKind.String
            ? ci.GetString() : null;
        bool webOn = root.TryGetProperty("web_search", out var w) && (w.ValueKind == JsonValueKind.True || w.ValueKind == JsonValueKind.False)
            ? w.GetBoolean() : _cfg.WebSearch;
        // 送信フォームからのモデル指定（quick/standard/quality）。未指定なら現在の階級を使う
        string modelSel = root.TryGetProperty("model", out var mm) && mm.ValueKind == JsonValueKind.String
            ? mm.GetString()! switch
            {
                "quick" or "chat-quick" => "quick",
                "standard" or "chat-standard" => "standard",
                "quality" or "chat-quality" => "quality",
                _ => "",
            }
            : "";
        // 回答言語（多言語対応・既定ja）。ja以外は回答キャッシュを使わない（言語混在防止）
        string answerLang = root.TryGetProperty("answer_lang", out var al) && al.ValueKind == JsonValueKind.String
            ? al.GetString()!.Trim().ToLowerInvariant() : "ja";
        if (answerLang is not ("ja" or "en" or "vi" or "zh" or "id" or "tl")) answerLang = "ja";
        bool nonJa = answerLang != "ja";

        ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.StatusCode = 200;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // チャット（履歴）の確定: uuidで解決し、無ければ新規作成
        long chatId = chatUuid.Length > 0 ? _db.ChatIdFromUuid(chatUuid) : 0;
        if (chatId == 0)
        {
            var newUuid = _db.NewChatUuid();
            chatUuid = newUuid;
            chatId = _db.ChatIdFromUuid(newUuid);
            _db.Exec("UPDATE chats SET title=$t WHERE id=$c", ("$t", ShortTitle(message)), ("$c", chatId));
        }
        await Sse(ctx, "meta", new { chat_id = chatId, chat_uuid = chatUuid });
        // 処理段階の一行表示（v2.7.0・ZCode方式）: 思考中にAIが何をしているかを見せる。
        // stageイベントを処理段階ごとに送り、UIは思考中行のphaseを一行で差し替える
        await Sse(ctx, "stage", new { text = "質問を解釈中…" });
        using var _chatGate = await EnterChatGateAsync(ctx, ctx.RequestAborted);

        // 会話コンテキスト: 同一チャットの直近やり取り（2往復・各250字）を記憶する
        var historyRows = _db.Query("SELECT role, content FROM messages WHERE chat_id=$c ORDER BY id DESC LIMIT 4", ("$c", chatId));
        historyRows.Reverse();
        var history = historyRows
            .Select(r => ((string)r["role"]! == "user" ? "user" : "assistant", Truncate((string)r["content"]!, 250)))
            .ToList();
        string prevUser = history.LastOrDefault(h => h.Item1 == "user").Item2 ?? "";

        _db.Exec("INSERT INTO messages(chat_id, role, content) VALUES($c,'user',$m)", ("$c", chatId), ("$m", message));
        if (captureImage != null) SaveCaptureImage(chatId, captureImage);

        // 分岐判定はLLM確保より先に行う（Phase 1.1）: 要約/下書きは生成直前にLLMが必要なので
        // 従来どおりここで同期的にEnsureLlmする。通常Q&Aは検索・リランクと並行して起動するため
        // ここでは確保しない（idle unload後の初回でもモデルロードを検索待ち時間に隠せる）。
        bool summaryRequested = root.TryGetProperty("summary_file_id", out var sfid) && sfid.ValueKind == JsonValueKind.Number;
        JsonElement? draftRequest = root.TryGetProperty("draft", out var draftEl) && draftEl.ValueKind == JsonValueKind.Object ? draftEl : null;
        bool autoDraftRequested = draftRequest is null && !summaryRequested && Draft.LooksLikeCreationRequest(message);
        // 要約・作成モード（v2.5.10修正）: 通常Q&A経路のStartEmbedAndRank/EnsureLlmより前に分岐するため、
        // ここでLLM自己修復を行う。idle unload（既定60分）後に作成/要約すると
        // 「接続できませんでした」network errorになる実測への対策
        if (summaryRequested || draftRequest is not null || autoDraftRequested)
        {
            try { _sup.EnsureLlm(); }
            catch (Exception ex) { _log.Warn($"draft/summary: llm ensure failed: {ex.Message}"); }
        }

        // 文書要約（v2.4.1）: ナレッジ一覧の「要約」ボタンからの確定経路。検索top-kを経ず
        // 指定ファイルの全チャンクを注入する（出典=ファイル全体・検索ミスによる幻覚源が存在しない）
        if (summaryRequested)
        {
            await SummarizeFileAsync(ctx, chatId, sfid.GetInt64(), sw);
            return;
        }

        // 下書き作成モード（v2.5.0）: draft {refs, template, seed} がある場合のみ作成パイプラインへ。
        // 通常Q&A（出典・拒否・チップ）の契約は無改変
        if (draftRequest is { } draftElement)
        {
            await DraftAsync(ctx, chatId, message, draftElement, sw);
            return;
        }
        // 意図自動判定（2026-10-23・DifyのQuestion Classifier相当）: 作成トグルを押さなくても
        // 「〜を作成してください」等の文書生成指示を検出したら作成パイプラインへ振り分ける。
        // ユーザーはモードを意識せず1つの入力箱だけで使える。方法質問（「〜作成方法を教えて」）はQ&Aのまま
        if (autoDraftRequested)
        {
            using var autoDraft = JsonDocument.Parse("{\"template\":\"auto\"}");
            _log.Info($"draft auto-detected from message intent: {Truncate(message, 40)}");
            await DraftAsync(ctx, chatId, message, autoDraft.RootElement, sw);
            return;
        }

        // Phase B: 即答ルート（DifyのAnnotation Reply / 決定論コネクタ相当）。
        // 埋め込み・検索・LLMの前に、正解が確定している質問を返す。日付感応質問は対象外。
        bool timeSensitive = Rag.TimeSensitiveQuestion().IsMatch(message);
        var intent = IntentRouter.Classify(message);

        if (!timeSensitive)
        {
            var faq = _faq.TryMatch(message);
            if (faq != null)
            {
                _log.Info($"faq hit: id={faq.Id} hits={faq.Hits} q={Truncate(message, 40)}");
                await AnswerInstantAsync(ctx, chatId, faq.Answer, faq.SourcesJson, "faq", 1.0, sw);
                return;
            }
        }
        // 図面台帳: 図番＋表題欄の属性だけの質問は、モデルを介さずDBの値で答える
        if (!timeSensitive && intent.Route == AnswerRoute.DrawingMeta
            && intent.Zuban is { Length: > 0 } zuban
            && DrawingQuery.TryAnswer(_db, message, zuban, out var drawingAnswer, out var drawingSources, out var drawingConf))
        {
            _log.Info($"drawing-meta answer: zuban={zuban} conf={drawingConf:F2}");
            await AnswerDeterministicAsync(ctx, chatId, drawingAnswer, drawingSources, "drawing", drawingConf, sw);
            return;
        }
        // 改訂比較: 同一図番の2改訂の表題欄差分を決定論的に返す
        if (!timeSensitive && intent.Route == AnswerRoute.DrawingCompare
            && intent.Zuban is { Length: > 0 } compareZuban
            && DrawingCompareQuery.TryAnswer(_db, message, compareZuban, out var compareAnswer, out var compareSources, out var compareConf))
        {
            _log.Info($"drawing-compare answer: zuban={compareZuban} conf={compareConf:F2}");
            await AnswerDeterministicAsync(ctx, chatId, compareAnswer, compareSources, "drawing-compare", compareConf, sw);
            return;
        }
        // 実績表: 図番＋見積/原価/納期の質問もDBの値で答える
        if (!timeSensitive && intent.Route == AnswerRoute.Jisseki
            && intent.Zuban is { Length: > 0 } jissekiZuban
            && JissekiQuery.TryAnswer(_db, message, jissekiZuban, out var jissekiAnswer, out var jissekiSources, out var jissekiConf))
        {
            _log.Info($"jisseki answer: zuban={jissekiZuban} conf={jissekiConf:F2}");
            await AnswerDeterministicAsync(ctx, chatId, jissekiAnswer, jissekiSources, "jisseki", jissekiConf, sw);
            return;
        }

        try
        {
            _sup.StartEmbedAndRank(); // 前回クラッシュ・tier変更後のStopAll等で止まっていたら自己修復
            // ステージ計測（Phase 0.1）: リクエスト開始からの経過msでマークする。0=未計測（早期return経路）
            long tEmbed = 0, tSearch = 0, tRetrieve = 0, tPrompt = 0;

            // 検索クエリ補強: 指示語・短い質問は直前の質問で補完（「料金はいくら？」等のfollow-up対応）
            var queryForRetrieval = message;
            if (prevUser.Length > 0 && (message.Length < 12 || RegexHasAnaphora(message)))
                queryForRetrieval = prevUser + " " + message;

            // 1) 質問埋め込み
            // 日本語質問×英語図面/規格に備え、用語の英語別名をクエリへ足す（意味は変えない。
            // 例: 「駐車場の料金」→「… parking fee」）。キーワード・プロンプトは元質問のまま。
            var qEmb = (await _gw.EmbedAsync(_cfg.EnginePortEmb, new[] { MfgTerms.ExpandQuery(queryForRetrieval) }, ctx.RequestAborted))[0];
            tEmbed = sw.ElapsedMilliseconds;

            // 2) 回答キャッシュ（cos>=0.97 → 即時）。LLM未ロードでも返せるのでEnsureLlmより前に置く
            // 日付感応質問（「今日は何日」等）は正解が日付で変わるためキャッシュから読まない。
            // 多言語回答は言語別キャッシュを持たないためキャッシュ対象外（日本語回答の混在防止）
            if (!timeSensitive && !nonJa)
            lock (_cacheLock)
            {
                // モデル別キャッシュ: 別階級で生成した回答を返さない（モデル識別列で判定）
                var hit = _cache.FirstOrDefault(c => c.Model == _cfg.ChatModelFile && Rag.Cosine(qEmb, c.Emb) >= Rag.AnswerCacheCos);
                if (hit != null)
                {
                    _db.Exec("UPDATE answer_cache SET hits=hits+1 WHERE id=$i", ("$i", hit.Id));
                    _db.Exec("UPDATE chats SET updated_at=datetime('now') WHERE id=$c", ("$c", chatId));
                    _db.Exec("INSERT INTO messages(chat_id, role, content, sources_json) VALUES($c,'assistant',$m,$s)",
                        ("$c", chatId), ("$m", hit.Answer), ("$s", hit.SourcesJson));
                    var cacheMessageId = _db.LastInsertId();
                    var cacheTotal = sw.ElapsedMilliseconds;
                    Sse(ctx, "delta", new { content = hit.Answer }).GetAwaiter().GetResult();
                    Sse(ctx, "done", new
                    {
                        cached = true, kind = "answer", route = "cache", message_id = cacheMessageId,
                        stages = Stages(cacheTotal, cacheTotal, tEmbed, tSearch, tRetrieve, tPrompt),
                        sources = JsonSerializer.Deserialize<object>(hit.SourcesJson), ms = cacheTotal,
                    }).GetAwaiter().GetResult();
                    _diag.Record(new Diagnostics.Sample("cache", "answer", true, cacheTotal, tEmbed, 0, 0, 0, 0, cacheTotal));
                    return;
                }
            }

            // 2.5) モデル指定があれば切替（embed/rankは不変・LLMのみ入れ替え）。既定階級として永続する
            if (modelSel.Length > 0 && _sup.SwitchLlmTier(modelSel))
            {
                _db.SetSetting("tier", modelSel);
                await Sse(ctx, "model", new { tier = modelSel, model_file = _cfg.ChatModelFile });
            }
            // Web検索時はクイック1.7Bだと参照内容の読み違え（「情報なし」誤判定・表の誤コピー）が
            // 起きるため（v2.1.4実測＋横浜天気の誤回答）、標準モデルが導入済みなら自動で標準を使用する。
            // ユーザーの明示指定（modelパラメータ）がある場合はそちらを優先
            if (webOn && modelSel.Length == 0 && _cfg.EffectiveTier == "quick"
                && File.Exists(Path.Combine(_cfg.ModelsDir, _cfg.StandardModel)))
            {
                if (_sup.SwitchLlmTier("standard"))
                {
                    _db.SetSetting("tier", "standard");
                    await Sse(ctx, "model", new { tier = "standard", model_file = _cfg.ChatModelFile });
                    _log.Info("web search: auto-switched to standard tier (quick misreads web references)");
                }
            }
            // LLM確保の並列化（Phase 1.1）: 従来はここで同期的に待っていたため、idle unload後の
            // 初回質問ではモデルロード（2〜5秒）がWeb検索・検索・リランクと直列になっていた。
            // Task.Runで開始し、生成直前（またはエージェント経路のLLM呼び出し直前）にawaitする。
            // 失敗はExceptionDispatchInfoで元例外を再throwし、外側のcatchによるSHINE_E_*分類を保つ。
            var llmStart = Task.Run(() =>
            {
                try { _sup.EnsureLlm(); return (Exception?)null; }
                catch (Exception ex) { return ex; }
            });
            // ローカル関数: 並列起動したEnsureLlmの完了待ち（多重呼び出し可・完了後は即時）
            async Task EnsureLlmReadyAsync()
            {
                var llmError = await llmStart.ConfigureAwait(false);
                if (llmError is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(llmError).Throw();
            }

            // 3) Web検索（任意）— 失敗・0件はSSEで可視化し、回答にも反映
            // フォローアップ質問（指示語含む・短文）では、生のメッセージではなく
            // LLMに会話文脈から独立した検索クエリを生成させる（A案）
            // 例: 「今日の天気は」→「こちらは神奈川県ですよ」→「神奈川県 天気 予報」を生成
            // 検索結果はページ本文抽出で補強（EnrichAsync）: SERPスニペットはサイト説明文で
            // 事実を含まないことが多く、そのまま注入すると作話の原因になるため
            string? webContext = null;
            List<WebSearch.WebResult>? webResults = null;
            bool webFailed = false;
            bool webContributed = false;
            if (webOn)
            {
                try
                {
                    string webQuery = message;
                    bool isFollowUp = prevUser.Length > 0 && (message.Length < 12 || RegexHasAnaphora(message));
                    if (isFollowUp)
                    {
                        webQuery = await GenerateSearchQueryAsync(history, message, ctx.RequestAborted);
                        _log.Info($"web search query rewritten: \"{message}\" -> \"{webQuery}\"");
                    }
                    webResults = await _web.SearchAsync(webQuery, 4, ctx.RequestAborted);
                    if (webResults.Count == 0) webFailed = true;
                    else
                    {
                        await _web.EnrichAsync(webResults, webQuery, ctx.RequestAborted); // ページ本文で事実を補強
                        webContext = WebSearch.ToContext(webResults);
                        webContributed = true;
                    }
                }
                catch (Exception ex) { webFailed = true; _log.Warn($"web search failed: {ex.Message}"); }
                await Sse(ctx, "web", new { results = webResults ?? new List<WebSearch.WebResult>(), error = webFailed ? "Web検索の結果を取得できませんでした（ナレッジのみで回答します）" : null });
            }

            // 4) ハイブリッド検索 top8。ナレッジ合計が小さい(≤3,000字)場合は全量常駐モード:
            // 検索・リランク・スニペット化をスキップし全文を【参照情報】へ注入する
            // （Dify FAQボットと同じ「全量コンテキスト」設計。検索ミス・スニペット切断が構造的に起きず、
            //   embed後の検索/リランク待ちも消える。KB拡大時は自動で従来のRAGへ戻る）
            var qTokens = Rag.Tokenize(queryForRetrieval).ToHashSet();
            var drawingIds = _db.Query("SELECT file_id FROM files WHERE kind='drawing'")
                .Select(r => Convert.ToInt64(r["file_id"] ?? 0L)).ToHashSet();
            string webNote = webFailed ? "\n※Web検索に失敗したため、社内ナレッジのみで判定しています。" : "";
            var docs = new List<string>();
            var sources = new List<SourceInfo>();
            List<ChunkIndex.HybridHit> chosen = new();
            bool rerankValidated = true;  // 全量モード=全文注入のため弱一致バックストップの対象外
            var rerankTop1 = double.NegativeInfinity; // リランク実行時のtop1スコア（低信頼×低Kw判定用・未実行時は∞で不発）
            long totalChars = -1;
            // 全量判定も status='ready' のみを対象にする（旧実装は embedding/error 行の文字数も数えていた）
            try
            {
                totalChars = Convert.ToInt64(_db.Scalar(
                    "SELECT COALESCE(SUM(LENGTH(c.text)),0) FROM chunks c JOIN files f ON f.file_id = c.file_id WHERE f.status='ready'"));
            }
            catch { }
            bool fullContext = totalChars > 0 && totalChars <= FullContextMaxChars;
            // 複合・列挙質問はtop2では文書横断の事実が落ちるため広めに取る。類義語質問は3件に留める。
            bool enumeration = intent.Route == AnswerRoute.MultiPart
                && !message.Contains("入社試験") && !message.Contains("選考") && !message.Contains("面接");
            bool synonymBroad = !enumeration && SynonymBroadRegex.IsMatch(message);
            bool broad = enumeration || synonymBroad;
            // 横断質問に引用された期間フレーズ（「7日」「2週間」等）。フレーズ含有チャンクを
            // SearchPhraseで直接プールに足すため、ここで1回だけ抽出する
            string? enumPhrase = enumeration ? Rag.EnumerationPhrase(message) : null;
            // 隣接チャンク連結の有無を1か所に集約（全量モードは全文注入のため連結しない・Phase 1.2修正）
            string SourceText(ChunkIndex.Rec r) => SourceTextFor(fullContext, r, fullContext ? null : _index.NextChunkText(r.FileId, r.Seq));
            string fullKb = ""; // 全量モードの社内文書全文（systemブロックへ入れる）
            List<ChunkIndex.HybridHit> hits = new(); // 全量モードでは未使用（図番あいまい性スキャンはchosenで代替）
            await Sse(ctx, "stage", new { text = "ナレッジを検索中…" });
            if (fullContext)
            {
                chosen = LoadFullContextChunks(_db);
                tSearch = sw.ElapsedMilliseconds; // 全量モードの「検索」= DBからの全チャンク読み込み
                // v2.7.0: 全文注入（8チャンク全量・プリフィル長大）から Dify式の
                // 「埋め込み類似 top4・同一文書は最大2チャンク」の選択に変更（プリフォル短縮=TTFT改善）。
                // 小規模KBでは全文書が文脈に入る品質を維持しつつ、プロンプトを約半分に圧縮する。
                // 純埋め込みだけでは同音異義の近接文書（承認→出張規程）へドリフトする実測（t44・cmp8）のため、
                // 質問の漢字キーワード（2文字以上の連続）が本文に含まれる数を僅かなボーナス（1件0.015）
                // として加算し、キーワード信号のある正解チャンクを優先する
                var qKeywords = System.Text.RegularExpressions.Regex.Matches(message, @"[一-龥ぁ-んア-ンa-zA-Z0-9]{2,}")
                    .Select(m => m.Value).Where(k => k.Length >= 2).Distinct().Take(8).ToList();
                var scored = chosen
                    .Select(h => (h, score: Rag.Cosine(qEmb, h.Rec.Emb) + 0.015f * qKeywords.Count(k => h.Rec.Text.Contains(k))))
                    .OrderByDescending(x => x.score)
                    .ToList();
                var perDoc = new Dictionary<long, int>();
                var pickedList = new List<ChunkIndex.HybridHit>();
                if (Environment.GetEnvironmentVariable("SHINEOS_FULL_INJECT") == "1")
                {
                    pickedList.AddRange(chosen); // A/B測定用: 全チャンク注入（v2.6.x相当）
                }
                else foreach (var x in scored)
                {
                    perDoc.TryGetValue(x.h.Rec.FileId, out var n);
                    if (n >= 2) continue;
                    perDoc[x.h.Rec.FileId] = n + 1;
                    pickedList.Add(x.h);
                    if (pickedList.Count >= 4) break;
                }
                chosen = pickedList;
                // top4で絞った後も docs/fullKb を必ず再構築する（systemブロック【社内文書（全文）】の
                // 唯一の参照元。代入漏れだと参照情報が空になり、モデルが全問を捏造で答える
                // （2026-10-30 cmp7で2/24に暴跌した事故の修正）
                docs.AddRange(chosen.Select(h => h.Rec.Text));
                fullKb = string.Join("\n\n", docs);
                _log.Info($"full-context mode → small-kb top{chosen.Count} selected by embedding (prompt短縮): {string.Join(",", chosen.Select(h => h.Rec.FileName).Distinct())}");
                // 先行refs（Phase 1.2）: 選択した文書を通知
                await Sse(ctx, "stage", new { text = "関連文書を選択中…" });
                await Sse(ctx, "refs", new
                {
                    files = chosen.Select(h => h.Rec.FileName).Distinct().Take(3).ToArray(),
                    web = (webResults ?? new List<WebSearch.WebResult>()).Select(wr => wr.Url).ToList(),
                    partial = true,
                });
            }
            else
            {
            hits = _index.Search(qEmb, qTokens, Rag.RerankPool);
            // 図面チャンクの判別（意図ブーストとスニペット免除で共用。files は小型テーブルなので都度照会で即時反映）
            // 図面意図ブースト: 「図面/図番」や図番パターンを含む質問では、寸法数値ノイズでキーワード一致が
            // 希薄になる図面チャンクを広めのプールから上位へ浮上させる（実図面検証cr02の根本対策）。
            // リランク以降の判断は変わらないため、通常質問への影響はこの分岐の外に出ない
            if (Rag.HasDrawingIntent(queryForRetrieval))
            {
                var pool = _index.Search(qEmb, qTokens, Rag.RerankPool * 3);
                foreach (var h in pool)
                    if (drawingIds.Contains(h.Rec.FileId)) h.Hybrid *= 1.4;
                hits = pool.OrderByDescending(h => h.Hybrid).Take(Rag.RerankPool).ToList();
            }
            tSearch = sw.ElapsedMilliseconds; // ハイブリッド検索完了
            // 日付感応質問（「今日は何日」「今何時」等）は参照情報がなくてもシステム日時から
            // 直接回答する（PCのシステム時計が根拠。ガードで「該当なし」にしない）
            if (hits.Count == 0 && string.IsNullOrEmpty(webContext) && !timeSensitive)
            {
                var refusal = "該当する記載がありません。" + webNote;
                _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'refusal')", ("$c", chatId), ("$m", refusal));
                await Sse(ctx, "delta", new { content = refusal });
                await Sse(ctx, "done", new
                {
                    cached = false, kind = "refusal", route = "refusal", guard = "empty",
                    stages = Stages(-1, sw.ElapsedMilliseconds, tEmbed, tSearch, tRetrieve, tPrompt),
                    sources = Array.Empty<object>(), suggest_web = !webOn, ms = sw.ElapsedMilliseconds,
                });
                return;
            }
            // 先行refs（Phase 1.2）: リランク前（ハイブリッド検索直後）に候補を先にUIへ通知し、
            // リランク待ち（実測~2秒）の間も「どの資料を見ているか」を表示できるようにする。
            // リランク後に partial=false の確定refsで上書きする。
            await Sse(ctx, "stage", new { text = "関連文書を選択中…" });
            await Sse(ctx, "refs", new
            {
                files = hits.Select(h => h.Rec.FileName).Distinct().Take(3).ToArray(),
                web = (webResults ?? new List<WebSearch.WebResult>()).Select(wr => wr.Url).ToList(),
                partial = true,
            });

            // 5) リランク → ガード（入力はスニペット化して高速化: 約-30%、精度は関連文中心で維持）
            // 注意: 160字への圧縮は実測で品質を落とした（Golden QA 108→101/118。タクシー22時・会議室等の
            // 事実が窓外に切れた）。240字が実測上の最適点
            var qTokForSnip = qTokens;
            docs = hits.Select(h => Rag.Snippet(h.Rec.Text, qTokForSnip, 240)).ToList();
            chosen = hits;
            rerankValidated = false; // リランクを実行しガード(GuardThreshold)を通過→弱一致バックストップの対象外
            // 複合・列挙質問（「すべて」「一覧」「それぞれ」「何日」等）は、top2に絞ると文書横断の
            // 事実が落ちるため、リランクで順位を付けず上位4件を採用する（再現率優先）。
            if (broad)
            {
                // ベクトル上位（hits）に、キーワード一致上位を足して再現率を補強する。
                // 「7日」「22時」等、ベクトルだけでは別文書に埋もれる語を拾う。
                var keywordHits = _index.SearchKeyword(qTokens, 6);
                // 列挙質問の期間フレーズ（「7日」「2週間」等）を含むチャンクを直接プールに足す。
                // 文書横断の取りこぼしは、正解チャンクがベクトル・キーワードのどちらの上位にも
                // 入らないことで起きる（実測: 「7日」横断で駐車場規程の「7日前」がプール外に落ち、
                // 回答から欠落。フレーズ直参照で構造的に防ぐ）。
                // 時刻（「22時」等）は索引側が「22:00」表記のため一致しないことがある →
                // 数字接頭辞（「22」）でも併せて直参照する（t97: 深夜割増25%の取りこぼし）
                var phraseHits = new List<ChunkIndex.HybridHit>();
                bool PhrasePriority(ChunkIndex.HybridHit h)
                {
                    if (enumPhrase is not { } p) return false;
                    if (h.Rec.Text.Contains(p, StringComparison.Ordinal)) return true;
                    // 時刻（「22時」↔「22:00」等）の表記差のみ数字接頭辞を優先対象にする
                    // （「2週間」の「2」等はほぼ全チャンクが一致し優先が退化するため対象外・t97実測）
                    if (!p.EndsWith("時") || p.Length < 2) return false;
                    var digits = p[..^1];
                    return digits.All(char.IsDigit) && h.Rec.Text.Contains(digits, StringComparison.Ordinal);
                }
                if (enumPhrase is { } phrase)
                {
                    phraseHits.AddRange(_index.SearchPhrase(phrase, 4));
                    var digits = phrase.TrimEnd('日', '週', '間', 'か', '月', 'ヶ', 'ケ', '箇', '年', '時');
                    if (digits.Length >= 1 && digits.Length < phrase.Length && digits.All(char.IsDigit))
                        phraseHits.AddRange(_index.SearchPhrase(digits, 2));
                }
                // 結合順がそのまま採択優先度（OrderByDescendingは安定ソート）:
                // ベクトル上位2件 → キーワード上位 → 残りのベクトル候補。従来の純結合＋Hybrid再ソートは
                // キーワードヒット（Hybrid=0）を常に下回らせ、実質ベクトルtop4だけが採択されていた
                // （t82: 宿泊費チャンクがキーワード最上位でも枠に入らない実測）。フレーズ含有チャンクは最優先
                chosen = hits.Take(2).Concat(keywordHits).Concat(hits.Skip(2)).Concat(phraseHits)
                    .GroupBy(h => (h.Rec.FileId, h.Rec.Seq))
                    .Select(g => g.First())
                    .OrderByDescending(PhrasePriority)
                    .Take(enumeration ? 5 : 3).ToList();
                _log.Info($"broad retrieval: {chosen.Count} chunks (multi-part, rerank skipped{(enumPhrase != null ? $", phrase=\"{enumPhrase}\"" : "")}) files={string.Join(",", chosen.Select(h => h.Rec.FileName).Distinct())}");
            }
            else if (docs.Count > 0)
            {
                // 高信頼ショートカット: ベクトル・キーワードがともに強一致ならリランク（実測~2秒）を省略して
                // ハイブリッド上位2件を採用。判定は生cos+kw（正規化前）。低スコア側は従来どおりリランク+ガード。
                var top = hits[0];
                // quick階級はprefillが支配的なので、キーワード一致が中程度でもリランクを省略してTTFTを守る。
                // standardは品質優先で従来閾値（0.40）。
                var skipKw = _cfg.EffectiveTier == "quick" ? 0.28 : Rag.RerankSkipKw;
                bool skip = top.Cos >= Rag.RerankSkipCos && top.Kw >= skipKw;
                if (skip)
                {
                    // 高信頼は文書1件のみ注入してプロンプト短縮（pp大幅減）。quickはprefill削減を優先し
                    // 閾値を少し緩める（品質ゲート18/118で確認）。それ以外はtop2。
                    var oneDoc = _cfg.EffectiveTier == "quick"
                        ? top.Cos >= 0.65 && top.Kw >= 0.30
                        : top.Cos >= 0.72 && top.Kw >= 0.35;
                    // 複数条件（「長期」「場合」「それぞれ」等）を含む質問は1件に絞らない（s07の条件欠落防止）
                    if (oneDoc && (message.Contains("長期") || message.Contains("場合") || message.Contains("それぞれ")))
                        oneDoc = false;
                    chosen = oneDoc ? hits.Take(1).ToList() : hits.Take(2).ToList();
                    _log.Info($"rerank skipped: cos={top.Cos:F2} kw={top.Kw:F2} docs={chosen.Count} file={top.Rec.FileName}");
                }
                else
                {
                    // リランカは任意モデル（未DL環境では未起動）: 起動していなければ
                    // ハイブリッド順の上位をそのまま採用する（該当なし判定はhits==0のガードが担う）
                    if (!_sup.IsRankAlive)
                    {
                        _log.Info("rerank unavailable (rank model not installed) — using hybrid order");
                        chosen = hits.Take(2).ToList();
                    }
                    else
                    {
                        var ranked = await _gw.RerankAsync(_cfg.EnginePortRank, MfgTerms.ExpandQuery(queryForRetrieval), docs, 2, ctx.RequestAborted);
                        var top1 = ranked.Count > 0 ? ranked[0].Score.ToString("F2") : "none";
                        _log.Info($"rerank: top1={top1} cos={top.Cos:F2} kw={top.Kw:F2} file={top.Rec.FileName}");
                        if (ranked.Count == 0 || ranked[0].Score < Rag.GuardThreshold)
                        {
                            // クロスリンガル救済: 外国語クエリ（かなを含まない）に限り、正規トークン一致
                            // （多言語別名↔日本語KBの語彙一致）があればリランカーの低評価に屈せず辞書から
                            // 候補を引き直して回答する。bge-rerankerはvi/id/tl×日本語ペアを正しく評価できない。
                            // 日本語クエリ（拒否質問を含む）は対象外 — KBに無い話題への救済発火を防ぐ（t77〜t80実測）
                            var crossLingual = Rag.IsForeignQuery(queryForRetrieval)
                                ? hits.Where(h => h.Canonical >= 1).Take(2).ToList()
                                : new List<ChunkIndex.HybridHit>();
                            var queryCanonicals = MfgTerms.CanonicalTokens(queryForRetrieval).ToList();
                            if (Rag.IsForeignQuery(queryForRetrieval) && queryCanonicals.Count > 0)
                            {
                                // プール内の正規一致に、辞書直参照（SearchCanonical）を足して3件まで広げる。
                                // 同点の別文書（QA+規程）で片方だけだと事実が欠ける実測（vi/zh-pw-help: 内線1234）
                                crossLingual = crossLingual
                                    .Concat(_index.SearchCanonical(queryCanonicals, 3))
                                    .GroupBy(h => (h.Rec.FileId, h.Rec.Seq))
                                    .Select(g => g.First())
                                    .Take(3).ToList();
                            }
                            if (!string.IsNullOrEmpty(webContext) || timeSensitive)
                            {
                                chosen = new(); // Webのみで回答（日付感応質問はシステム日時のみで回答）
                            }
                            else if (crossLingual.Count > 0)
                            {
                                chosen = crossLingual;
                                rerankValidated = true; // 正規トークンで語彙確認済み → 弱一致バックストップ免除
                                _log.Info($"rerank guard: cross-lingual rescue — kept {chosen.Count} canonical candidates ({string.Join(",", chosen.Select(h => h.Rec.FileName).Distinct())})");
                            }
                            else
                            {
                                var refusal = "該当する記載がありません。" + webNote;
                                _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'refusal')", ("$c", chatId), ("$m", refusal));
                                await Sse(ctx, "delta", new { content = refusal });
                                await Sse(ctx, "done", new
                                {
                                    cached = false, kind = "refusal", route = "refusal", guard = "rerank",
                                    stages = Stages(-1, sw.ElapsedMilliseconds, tEmbed, tSearch, tRetrieve, tPrompt),
                                    sources = Array.Empty<object>(), suggest_web = !webOn, ms = sw.ElapsedMilliseconds,
                                });
                                return;
                            }
                        }
                        else
                        {
                            // 高信頼（top1スコア≥RerankHighConf）なら文書1件のみ注入してプロンプト短縮（pp削減）。それ以外はtop2。
                            // ここを通った = クロスエンコーダが関連性を検証済み → 弱一致バックストップの対象外
                            rerankValidated = true;
                            rerankTop1 = ranked[0].Score;
                            var take = ranked[0].Score >= Rag.RerankHighConf ? 1 : 2;
                            // 2件目が no-hit 帯（リランカが無関連と判定）なら1件に削る。
                            // ただしクロスリンガル用の正規トークン（駐車場↔parking等）が2つ以上
                            // 一致する候補は、リランカが低くても根拠として残す（英語資料の取りこぼし防止）。
                            // ※プールが1件のときは ranked[1] が存在しない（v2.3.2で小型KBの質問が
                            //   内部エラーになる実害を実測・A/B検証中に発見）ため件数を先に見る
                            if (take == 2 && (ranked.Count < 2 || ranked[1].Score < Rag.GuardThreshold))
                            {
                                var second = ranked.Count >= 2 ? hits[ranked[1].Index] : null;
                                if (second is null || second.Canonical < 1) take = 1;
                                else _log.Info($"rerank: keeping low-score cross-lingual candidate (canonical={second.Canonical} file={second.Rec.FileName})");
                            }
                            chosen = ranked.Take(take).Select(r => hits[r.Index]).ToList();
                        }
                    }
                }
            }
            } // end else (従来RAG経路)
            tRetrieve = sw.ElapsedMilliseconds; // 検索+リランク+ガード完了（ここからプロンプト構築）
            // 回答材料が全く無い場合（Web検索0件＋タイムセンシティブ等）: 空回答の代わりに再試行を案内。
            // Web検索OFF（未試行）なのに「検索に失敗」と案内するのは誤解を招くため、文言を分岐する
            if (chosen.Count == 0 && (webResults == null || webResults.Count == 0) && string.IsNullOrEmpty(webContext))
            {
                var msg = webOn
                    ? "Web検索に失敗しました（アクセスが集中している可能性があります）。少し待ってからもう一度お試しください。"
                    : "社内ナレッジに該当する記載がありません。質問を具体的にしていただくと、より正確に回答できます。";
                var guard = webOn ? "web-failed" : "no-material";
                _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'refusal')", ("$c", chatId), ("$m", msg));
                await Sse(ctx, "delta", new { content = msg });
                await Sse(ctx, "done", new
                {
                    cached = false, kind = "refusal", route = "refusal", guard = guard,
                    stages = Stages(-1, sw.ElapsedMilliseconds, tEmbed, tSearch, tRetrieve, tPrompt),
                    sources = Array.Empty<object>(), suggest_web = !webOn, ms = sw.ElapsedMilliseconds,
                });
                return;
            }
            // 弱一致ガード（バックストップ・生成前）: kw≈0の採択を拒否する。識別の主役はリランクスコア
            // （GuardThreshold: nohit実測≤-7.8）で、RerankSkipKw=0.50により弱一致は必ずリランクを経由する。
            // 本ガードが守るのはリランカー未導入環境（ウィザード既定ではrerankを導入するが、削除した場合の
            // 最終防波堤）。0.15〜0.50帯はリランカーが無いと識別不能（リランカー推奨の根拠）。
            // 対象外（IsWeakMatch参照）: 図面質問/Web文脈/時間感応/リランク検証済み
            // chosen空（リランク拒否後にWebのみで回答する経路・時間感応）でもクラッシュしない:
            // Maxの引数を0にすると IsWeakMatch はWeb文脈/時間感応で免除されるため実質無害
            // （v2.2.0から潜在・Web検索ON×KB全棄却で初めて到達する経路。実機E2Eで発見）
            var maxKw = chosen.Count > 0 ? chosen.Max(h => h.Kw) : 0;
            if (!broad && !fullContext && Rag.IsWeakMatch(maxKw,
                    hasWebContext: !string.IsNullOrEmpty(webContext),
                    timeSensitive: timeSensitive,
                    drawingIntent: Rag.HasDrawingIntent(message),
                    rerankValidated: rerankValidated))
            {
                _log.Info($"weak-match guard: kw={maxKw:F2} < {Rag.MinAcceptKw} — 該当なし拒否へ（偽出典防止）: {Truncate(message, 40)}");
                var refusal = "該当する記載がありません。" + webNote;
                _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'refusal')", ("$c", chatId), ("$m", refusal));
                await Sse(ctx, "delta", new { content = refusal });
                await Sse(ctx, "done", new
                {
                    cached = false, kind = "refusal", route = "refusal", guard = "weak-match",
                    stages = Stages(-1, sw.ElapsedMilliseconds, tEmbed, tSearch, tRetrieve, tPrompt),
                    sources = Array.Empty<object>(), suggest_web = !webOn, ms = sw.ElapsedMilliseconds,
                });
                return;
            }
            // 低信頼×低Kwの組み合わせ拒否: リランクはno-hit帯を逃れたものの確信できないグレー帯
            // (top1<RerankHighConf)で、かつキーワード一致もほぼ無い(maxKw<MinAcceptKw)ケース。
            // 表層のみ類似の無関係チャンクで一般知識+偽出典回答ができる最後の経路を塞ぐ
            // （実測: 正解top1≥+7.3 vs 無関係≤-10.6 — グレー帯に落ちる半端な組み合わせのみ拒否）。
            // 言い換えヒット(top1≥RerankHighConf)とキーワード確認済み(maxKw≥MinAcceptKw)は通す
            // クロスリンガル正規トークン（駐車場↔parking等）が2つ以上一致する根拠がある場合は、
            // 低信頼×低kwの拒否から除外する（英語資料の正解を「該当なし」に倒さない）。
            bool canonicalEvidence = chosen.Any(h => h.Canonical >= 1);
            // 語彙アンカー免除（t22実測）: 質問の英数トークン（usb等・2文字以上）を採択チャンクが含むなら
            // 語彙的に無関係とは言えないため、低信頼×低kwの拒否を適用しない。混合トピックのチャンク
            // （USB行＋パスワード行＋送信行の1チャンク等）はリランカーがグレー帯の中途半端なスコアを付け、
            // kwも質問側の機能語ぶん分母が膨らんで閾値(0.15)を僅かに割る。富士山型の偽出典（語彙一致ゼロの
            // 日本語質問）は英数アンカーを持たないため本免除の対象外
            bool alnumAnchor = chosen.Any(h => qTokens.Any(t =>
                t.Length >= 2 && t.All(char.IsAsciiLetterOrDigit) && h.Rec.Tokens.Contains(t)));
            if (!fullContext && !canonicalEvidence && !alnumAnchor && Rag.IsLowConfidenceWeak(rerankTop1, maxKw, rerankValidated)
                && string.IsNullOrEmpty(webContext) && !timeSensitive && !Rag.HasDrawingIntent(message))
            {
                _log.Info($"low-conf guard: rerankTop1={rerankTop1:F2} kw={maxKw:F2} canon={(chosen.Count > 0 ? chosen.Max(h => h.Canonical) : 0)} — 該当なし拒否へ（偽出典防止）: {Truncate(message, 40)}");
                var refusal = "該当する記載がありません。" + webNote;
                _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'refusal')", ("$c", chatId), ("$m", refusal));
                await Sse(ctx, "delta", new { content = refusal });
                await Sse(ctx, "done", new
                {
                    cached = false, kind = "refusal", route = "refusal", guard = "low-conf",
                    stages = Stages(-1, sw.ElapsedMilliseconds, tEmbed, tSearch, tRetrieve, tPrompt),
                    sources = Array.Empty<object>(), suggest_web = !webOn, ms = sw.ElapsedMilliseconds,
                });
                return;
            }
            // 対象語リルート: リランクの意味的選択が、質問の対象語（会議室等）を字面通り含まないチャンクを
            // 採択することがある（s11実測: top1スコア3.40でも対象語欠落 → 既存の対象ガードが拒否へ倒していた）。
            // 拒否ではなく、プール内の対象語含有チャンクへ根拠を差し替える。プール全体にも無い場合は
            // 従来どおり対象ガードが拒否する（t74型: 別対象の手続きを転用する回答の防止は維持）
            var missingTargets = Rag.MissingProcedureTargets(message, chosen.Select(h => SourceText(h.Rec)));
            if (missingTargets.Count > 0 && string.IsNullOrEmpty(webContext) && !timeSensitive)
            {
                // 対象語をいくつ含むかでプールチャンクを ranking し、上位3件へ根拠を差し替える
                // （対象語すべてが1チャンクに揃うことは稀 — s11実測: 会議室/大会議室/収容人数/申請期限は
                //   3チャンクに分散。2件では事実欠落(30日)が出たため3件）
                var rerouted = hits
                    .Select(h => (Hit: h, N: Rag.ContainedProcedureTargets(message, new[] { SourceText(h.Rec) }).Count))
                    .Where(x => x.N > 0)
                    .OrderByDescending(x => x.N)
                    .Take(3).Select(x => x.Hit).ToList();
                if (rerouted.Count > 0)
                {
                    _log.Info($"target reroute: chosen lacked [{string.Join("/", missingTargets)}] — swapped to pool chunks: {string.Join(", ", rerouted.Select(h => h.Rec.FileName))}");
                    chosen = rerouted;
                }
            }
            // エージェント経路（試験的・v2.3.1）: 実績が絡む複合質問（図番+文書横断 等）を
            // 「ツール計画1回 → ツール実行 → 生成1回」の計2LLM呼び出しで捌く。Qwen3は1応答で
            // 複数ツール呼び出しを並列出力する（実測）。CPU推論では反復回数が時間に直結するため、
            // Dify式の反復ループではなくシングルショット計画を採用。
            // 発動条件: 質問内の図番が実績表(jisseki_meta)に存在 + agent_mode 有効
            List<ChunkIndex.HybridHit> agentHits = new();
            var qzn = DrawingIngest.ZubansIn(message);
            var jissekiCount = 0;
            foreach (var zn in qzn)
                jissekiCount += _db.Query("SELECT COUNT(*) AS n FROM jisseki_meta WHERE zuban_norm=$z", ("$z", zn)).Count;
            _log.Info($"agent branch check: AgentMode={_cfg.AgentMode} timeSensitive={timeSensitive} webEmpty={string.IsNullOrEmpty(webContext)} zubans=[{string.Join(",", qzn)}] jissekiCount={jissekiCount}");
            if (_cfg.AgentMode && !timeSensitive && string.IsNullOrEmpty(webContext))
            {
                foreach (var zn in qzn)
                {
                    if (_db.Query("SELECT COUNT(*) AS n FROM jisseki_meta WHERE zuban_norm=$z", ("$z", zn)).Count == 0) continue;
                    try
                    {
                        _log.Info($"agent route: plan call starting (zuban={zn})");
                        // エージェント経路は生成前にLLM（ツール計画）を呼ぶため、並列起動したEnsureLlmをここで待つ
                        await EnsureLlmReadyAsync();
                        var planMessages = JsonSerializer.Serialize(new object[]
                        {
                            new { role = "system", content = "あなたは製造業の社内Q&Aアシスタント。質問に答えるために必要な情報をツールで収集する。収集済みの情報で答えられる場合はツールを呼ばない。" },
                            new { role = "user", content = message },
                        });
                        var planResp = await _gw.ChatToolsOnceAsync(_cfg.EnginePortLlm, planMessages, AgentToolsJson, 400, ctx.RequestAborted);
                        using var planDoc = JsonDocument.Parse(planResp);
                        var msgEl = planDoc.RootElement; // ChatToolsOnceAsync はメッセージオブジェクトを返す
                        if (!msgEl.TryGetProperty("tool_calls", out var tcs)) { _log.Info("agent route: no tool_calls — fixed pipeline continues"); break; }
                        foreach (var tc in tcs.EnumerateArray())
                        {
                            // 小型モデルは弱形式のツール呼び出しを返すことがあるため、1件の異常はskip
                            try
                            {
                                if (!tc.TryGetProperty("function", out var fn)) continue;
                                if (!fn.TryGetProperty("name", out var nameEl)) continue;
                                if (nameEl.GetString() != "knowledge_search") continue; // jisseki は実績情報として既に注入済み
                                var argsStr = fn.TryGetProperty("arguments", out var a) ? a.GetString() : null;
                                using var argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsStr) ? "{}" : argsStr);
                                var q2 = argsDoc.RootElement.TryGetProperty("query", out var qq) ? qq.GetString() : null;
                                if (string.IsNullOrWhiteSpace(q2)) continue;
                                var emb2 = (await _gw.EmbedAsync(_cfg.EnginePortEmb, new[] { q2 }, ctx.RequestAborted))[0];
                                var t2 = Rag.Tokenize(q2).ToHashSet();
                                foreach (var h in _index.Search(emb2, t2, 6)) agentHits.Add(h);
                                if (agentHits.Count >= 12) break;
                            }
                            catch (Exception ex2) { _log.Warn($"agent route: tool call skipped ({ex2.Message})"); }
                        }
                        if (agentHits.Count > 0)
                        {
                            chosen = agentHits.DistinctBy(h => (h.Rec.FileId, h.Rec.Seq)).Take(4).ToList();
                            _log.Info($"agent route: plan tool_calls executed — chosen replaced with {chosen.Count} chunks");
                        }
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { _log.Warn($"agent route failed (fixed pipeline continues): {ex}"); break; }
                }
            }
            // 検索回数の記録（v2.7.0・Dify風ナレッジ一覧）: 質問の根拠として採用された文書の
            // hit_countを加算する（一覧の「検索回数」列・ヘッダークリックソートの対象）
            if (chosen.Count > 0)
            {
                var ids = string.Join(",", chosen.Select(h => h.Rec.FileId).Distinct());
                _db.Exec($"UPDATE files SET hit_count = hit_count + 1 WHERE file_id IN ({ids})");
            }
            foreach (var h in chosen)
            {
                // 全量モードでは隣接chunk連結を行わない（Phase 1.2修正: SourceTextForに集約）
                var sourceText = SourceText(h.Rec);
                // 全文注入モードでは出典スニペットのリランク救済をスキップ（レイテンシ改善2026-10-31）:
                // スニペットはUIの表示窓に過ぎず、本文は丸ごと文脈に入っている。語彙照準が外れた
                // チャンクごとにリランカー呼び出し（数百ms×4チャンク）が発生し、プリフィル開始前に
                // 負荷非依存の約2秒の固定ロス（t_prompt）になっていた実測のため、全文注入では
                // 純スニペットで即返す。通常RAGでは従来どおり照準精度を優先
                sources.Add(EnrichDrawing(new SourceInfo
                {
                    File = h.Rec.FileName,
                    // Markdown記法は出典パネルに生表示されるためプレーン化する（v2.7.0）。
                    // SnippetとTextに同一変換を適用（モーダルの該当箇所ハイライトは部分一致のため）
                    Snippet = Rag.PlainSnippet(fullContext ? Rag.Snippet(sourceText, qTokens, 120) : await SnippetForSourceAsync(sourceText, qTokens, message, ctx.RequestAborted), 120),
                    Text = Rag.PlainSnippet(sourceText, 2000),
                }, h.Rec.FileId));
            }
            // 参照確定をUIに通知（思考中の1行表示: どの資料を見ているか）。
            // 先行refs(partial=true)を確定値で上書きする。全量モードでは chosen が全チャンクのため
            // Distinct でファイル名を一意化する（通常RAGでは従来どおり全ファイル表示）
            await Sse(ctx, "stage", new { text = "回答の根拠を確定しました" });
            await Sse(ctx, "refs", new
            {
                files = chosen.Select(h => h.Rec.FileName).Distinct().ToArray(),
                web = (webResults ?? new List<WebSearch.WebResult>()).Select(wr => wr.Url).ToList(),
                partial = false,
            });

            // 同一図番（改訂違い・別ファイル）が絡む質問: 単断定・混在回答を避け、ユーザーに図面を選んでもらう
            // （UI選択式。選択後は（図番: X・改訂Y）付きで再質問され、該当図面に絞った回答になる）
            // 検出範囲: 質問に図番が明示されている場合は検索プール(hits)全体 — 同一図番の別ファイルがプールに
            // いても採択外だと気づけず、片方だけを根拠に断定するため。図番の明示がない場合は無関係な図面を
            // 巻き込まないよう採択済み(chosen)のみ。再質問済み（改訂指定あり）では発火させない
            if (!message.Contains("改訂") && !message.Contains("（図番:"))
            {
                var qZubanNorm = DrawingIngest.ZubansIn(message).FirstOrDefault();
                var ambScan = new List<(ChunkIndex.HybridHit Hit, string ZubanNorm)>();
                // 全量モードではchosen=全チャンク（検索プールの代わりに全文から走査する）
                foreach (var h in (fullContext ? chosen : (qZubanNorm != null ? hits : chosen)))
                {
                    if (!drawingIds.Contains(h.Rec.FileId)) continue;
                    var z = DrawingMetaOfFile(h.Rec.FileId)?.Zuban;
                    if (string.IsNullOrWhiteSpace(z)) continue;
                    var zn = Rag.NormalizeZuban(z);
                    if (qZubanNorm != null && zn != qZubanNorm) continue; // 質問の図番と一致するものだけ
                    ambScan.Add((h, zn));
                }
                var amb = DrawingIngest.DetectZubanAmbiguity(ambScan.Select(x => (x.Hit.Rec.FileId, (string?)x.ZubanNorm)));
                if (amb != null)
                {
                    var zubRaw = DrawingMetaOfFile(amb.Value.FileIds[0])?.Zuban ?? amb.Value.ZubanNorm;
                    _log.Info($"clarify: same zuban across multiple files ({amb.Value.ZubanNorm})");
                    var options = ambScan
                        .Where(x => amb.Value.FileIds.Contains(x.Hit.Rec.FileId))
                        .DistinctBy(x => x.Hit.Rec.FileId)
                        .Select(x => new
                        {
                            file_id = x.Hit.Rec.FileId,
                            file = x.Hit.Rec.FileName,
                            zuban = DrawingMetaOfFile(x.Hit.Rec.FileId)?.Zuban,
                            revision = DrawingMetaOfFile(x.Hit.Rec.FileId)?.Revision,
                            hinmei = DrawingMetaOfFile(x.Hit.Rec.FileId)?.Hinmei,
                        }).ToList();
                    await Sse(ctx, "clarify", new { zuban = zubRaw, message, options });
                    await Sse(ctx, "done", new
                    {
                        cached = false, kind = "clarify", route = "clarify", guard = "clarify",
                        stages = Stages(-1, sw.ElapsedMilliseconds, tEmbed, tSearch, tRetrieve, tPrompt),
                        sources = Array.Empty<object>(), ms = sw.ElapsedMilliseconds,
                    });
                    _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'clarify')", ("$c", chatId), ("$m", $"CLARIFY:{zubRaw}"));
                    return;
                }
            }

            // 対象ガード（生成前）: 「〜できますか/方法」等の手続き質問で、質問の対象語（助詞直前の漢語等）が
            // 取得文書のどれにも現れない場合、QA文書の類似手続きを別対象へ転用した回答（t74型）になる前に
            // 拒否へ倒す。Web検索時は規則①〜④が効くため適用しない。時間感応質問も対象外
            if (!broad && sources.Count > 0 && string.IsNullOrEmpty(webContext) && !timeSensitive &&
                Rag.ProcedureTargetMissing(message, chosen.Select(h => SourceText(h.Rec))))
            {
                _log.Info($"target guard: subject of procedure question not found in sources: {Truncate(message, 40)}");
                var refusal = "該当する記載がありません。" + webNote;
                _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'refusal')", ("$c", chatId), ("$m", refusal));
                await Sse(ctx, "delta", new { content = refusal });
                await Sse(ctx, "done", new
                {
                    cached = false, kind = "refusal", route = "refusal", guard = "target",
                    stages = Stages(-1, sw.ElapsedMilliseconds, tEmbed, tSearch, tRetrieve, tPrompt),
                    sources = Array.Empty<object>(), suggest_web = !webOn, ms = sw.ElapsedMilliseconds,
                });
                return;
            }

            // Web検索結果も出典として同一デザインで表示（URL＋プレビュー）
            foreach (var wr in webResults ?? new List<WebSearch.WebResult>())
                sources.Add(new SourceInfo { File = wr.Title, Snippet = wr.Snippet, Text = wr.Snippet, Kind = "web", Url = wr.Url });

            // 6) プロンプト構築: system(静的+当日付行。日付は1日単位で固定なのでキャッシュ効率を保つ) + 履歴 を前方に置き、質問ごとに変わる参照文脈は
            //    最終userメッセージに統合。llama-serverのプレフィックスキャッシュが system+履歴
            //    全体に効き、2往復目以降のpp（履歴分〜700トークン）を丸ごと削減する。
            //    文脈は隣接チャンク(seq+1)と連結してからスニペット化: 手順・条項が
            //    チャンク境界で分断されて後半ステップが答えられなくなるのを防ぐ（s04実証済み）。
            var covered = new HashSet<(long, int)>();
            var ctxDocs = new List<(string, string)>();
            // 全量常駐モード: 社内文書全文はsystemブロックへ置く（質問間で不変＝プリフィックス
            // キャッシュが効き、2問目以降のプリフィルが質問部分だけになる）。ctxPartには入れない
            if (!fullContext)
            foreach (var h in chosen)
            {
                // 直前チャンクとの連結で既に含めた続きチャンクは二重注入しない
                if (covered.Contains((h.Rec.FileId, h.Rec.Seq))) continue;
                covered.Add((h.Rec.FileId, h.Rec.Seq));
                if (_index.NextChunkText(h.Rec.FileId, h.Rec.Seq) is not null) covered.Add((h.Rec.FileId, h.Rec.Seq + 1));
                var merged = SourceText(h.Rec);
                // 図面チャンク（1枚=1チャンク・テキスト量は取り込み時に DrawingIngest.MaxTextChars で上限）は
                // スニペット化せず全文注入する: 240字の窓は寸法ノイズの間に散らばる表題欄・注記を切断し、
                // 「チャンク内に記載があるのに模型に渡らない」実図面検証cr02（JIS B 0405-m）の原因だった
                // 全量常駐モードも全文注入（スニペット化すると全量モードの趣旨＝取りこぼしゼロが損なわれる）
                if (!fullContext && !drawingIds.Contains(h.Rec.FileId))
                {
                    // 300字以下のチャンクは全文注入（240字窓だと末尾1行を切る実測: d05検査基準書の
                    // 「浸透探傷試験を追加する」が落ち、5項目中4項目の回答になった）。圧縮の旨味が
                    // 数トークンしかない小チャンクで切断リスクを負う必要はない
                    merged = broad && enumPhrase is { } ep2
                        // 列挙質問はフレーズ（「7日」等）を含む文を中心に窓を張る（トークン中心だと
                        // 目的行が窓外に切れる・t92実測）。時刻フレーズは「22:00」表記の代替中心も試す。
                        // フレーズを含まないチャンクは従来どおり
                        ? Rag.SnippetAround(merged, ep2, qTokens, 400,
                            ep2.Length >= 2 && ep2.EndsWith("時") && ep2[..^1].All(char.IsDigit) ? ep2[..^1] : null)
                        : merged.Length <= SnippetSmallChunkChars
                            ? merged
                            : broad ? Rag.Snippet(merged, qTokens, 400) : Rag.Snippet(merged, qTokens);
                }
                // 列挙質問の転写ヒント: 引用語（と時刻の数字表記）を含む行をそのまま添えて転写させる。
                // 「22時」（タクシー可）と「22:00」（深夜割増25%）のように表記の違う2事実をモデルが
                // 統合できず片方を落とす実測（t97・quick/standardの両階級）
                if (broad && enumPhrase is { } epHint)
                {
                    var altHint = epHint.Length >= 2 && epHint.EndsWith("時") && epHint[..^1].All(char.IsDigit) ? epHint[..^1] : null;
                    var hintLines = Rag.ExtractPhraseLines(merged, epHint, altHint);
                    if (hintLines.Count > 0)
                        merged += "\n【「" + epHint + "」に関係する行】\n" + string.Join("\n", hintLines);
                }
                ctxDocs.Add((h.Rec.FileName, merged));
            }
            var context = Rag.BuildContext(ctxDocs, webContext);
            // 全量常駐モード: KBは参照情報（ctxPart）へtop4で入れる（v2.7.0）。systemブロックへの全文注入は廃止
            var sysText = Rag.SystemPrompt + Rag.CurrentDateLine()
                + (fullContext && fullKb.Length > 0 ? "\n\n【社内文書（全文）】\n" + fullKb : "");
            var messages = new List<(string, string)> { ("system", sysText) };
            // Web参照が大きい場合は履歴を直近1往復に削る（ctx=2048の予算内に本文を優先して入れるため。
            // フォローアップ文脈は検索クエリ書き換え時に既に織り込み済み）
            var promptHistory = webContext != null && webContext.Length > 600 && history.Count > 2
                ? history.Skip(history.Count - 2).ToList() : history;
            messages.AddRange(promptHistory);
            // 日付感応質問にはシステム日時（時刻込み）を明示。参照情報が無い場合は【参照情報】欄を省略
            string sysInfo = timeSensitive ? "\n\n" + Rag.SystemInfoLine() : "";
            string ctxPart = context.TrimEnd().Length > 0 ? "\n\n【参照情報】\n" + context.TrimEnd() : "";
            // 複数の図面が出典の場合: 図面ごとに値を分けて答えさせる（同一図番の改訂違いや
            // 複数図面での検索競合により、1つの図面の値だけを断定する誤回答を防ぐ）
            if (chosen.Count(h => drawingIds.Contains(h.Rec.FileId)) >= 2)
                ctxPart += "\n\n※複数の図面が出典です。図面ごと（図番・改訂つき）に値を分けて回答し、1つの図面の値だけを断定しないでください。";
            // 複合・列挙質問: 広めに渡しているため、拾った文書ごとに整理させる。
            // 引用フレーズ（「7日」「22時」等）がある場合はその語を明示して抽出を促す
            // （チャンク内の関係行を取りこぼす小型モデルの揺らぎ対策・t96/t97実測）
            if (enumeration)
                ctxPart += "\n\n※複数文書を横断する質問です。"
                    + (enumPhrase is { } ep
                        ? $"「{ep}」を含む記述（「{ep}前」「{ep}以内」など{ep}を含む語"
                          + (ep.EndsWith("時") && ep.Length >= 2 ? $"、および「{ep[..^1]}:00」等の時刻表記" : "")
                          + "もすべて該当）を"
                        : "質問に直接関係する記述（日数・金額・システム名・場所などの具体的な語）を")
                    + "各文書から抜けなくすべて列挙してください（関係しない項目は書かない）。文書名ごとに箇条書きで答え、該当しない文書は無視してください。";
            // 連絡先質問（t25/t95実測）: 内線番号と窓口の場所が別QAに分かれていると、小型モデルは
            // 片方（窓口の場所）だけを答えて番号を落とす（「内線 1234」が文脈にあっても欠落した実測）。
            // 番号・場所の両方を列挙させる
            if (message.Contains("連絡先") || message.Contains("内線") || message.Contains("問い合わせ"))
                ctxPart += "\n\n※連絡先を尋ねる質問です。参照情報にある内線番号・電話番号・窓口の場所をすべて箇条書きで答えてください。";
            // 承認者質問（s07/t03実測）: 「承認は所属長。1週間を超える出張は部長の承認が必要」の条項で
            // 条件付き条項（部長）を無視し通常承認者（所属長）で答える揺らぎ。条件に合う承認者を明示させる。
            // s07は「長期出張の場合は？」と承認を省略形で聞くため「長期出張」でも発火させる
            if (message.Contains("承認") || message.Contains("長期出張"))
                ctxPart += "\n\n※承認者を尋ねる質問です。参照情報に条件別の承認者（例: 通常は所属長、1週間超は部長）がある場合は、質問の条件に合う承認者を正確に答えてください。";
            // 図面・仕様の内容質問（DrawingQa経路・d05実測）: 検査基準書の最終項目（改訂による追加試験）を
            // 改訂記載と分類して落とすため、末尾項目まで挙げさせる
            if (intent.Route == AnswerRoute.DrawingQa)
                ctxPart += "\n\n※図面・仕様に関する質問です。参照情報の該当する箇条書きは、末尾の項目まで1つ残らず挙げてください。";
            // 意図不明な極端に短い質問（例: 「テスト」）: 「テスト用サンプル」など表層一致する無関係文書が
            // 検索にヒットしても回答を試みてしまい、無関係な事実の列挙になる実測がある。短クエリのときは
            // 参照情報を外し、確認促進の応答のみにする（SystemPromptへの常時追加はquick 1.7Bが正常質問まで
            // 自己矛盾したため撤回済み）。
            // さらに応答は「回答」ではないため参照情報・出典を一切付けない（実測2026-10-16: 「テスト」→
            // 確認促進の応答に「出典：QA_list.md」が付与され、意図確認なのに出典だけ表示される混乱を生んだ）。
            // 時間感応質問（「今何時」等）は sysInfo の日時で答えられるため対象外
            bool clarifyConfirm = false; // 応答が「回答」ではなく確認促進であることを示す（kind=clarify）
            // 実績接続（v2.3.0・C1）: 質問に図番があり、実績表(.xlsx/.csv)を取り込み済みなら、
            // その図番の過去見積・原価・納期を回答文脈に追加する（「この図面の過去見積は？」に
            // 実績で答えられる = 支払対象「図面×実績の接続」）。注入テキストは validationCorpus にも
            // 加算する（FabricatedNumbers が実績の数値を捏造と誤判定しないため）
            string jissekiText = "";
            var qZubanNorms = DrawingIngest.ZubansIn(message);
            if (qZubanNorms.Count > 0)
            {
                var jissekiParts = new List<string>();
                foreach (var zn in qZubanNorms)
                    foreach (var r in _db.Query("SELECT zuban_raw, mitumori, genka, nouki FROM jisseki_meta WHERE zuban_norm=$z", ("$z", zn)))
                        jissekiParts.Add(SheetJisseki.FormatJisseki(
                            r["zuban_raw"]?.ToString(), r["mitumori"]?.ToString(), r["genka"]?.ToString(), r["nouki"]?.ToString()));
                if (jissekiParts.Count > 0)
                {
                    jissekiText = "図番の過去実績（実績表から）:\n" + string.Join("\n", jissekiParts);
                    ctxPart += "\n\n【実績情報】\n" + jissekiText;
                }
            }
            if (message.Trim().Length <= 3 && !timeSensitive)
            {
                clarifyConfirm = true;
                chosen = new();
                sources.Clear();
                ctxPart = "\n\n※質問が短すぎて意図を確認できません。知りたいことを具体的に教えてくださいとだけ返してください。";
            }
            messages.Add(("user", message + sysInfo + ctxPart + Rag.AnswerLanguageLine(answerLang)));
            tPrompt = sw.ElapsedMilliseconds; // プロンプト構築完了
            // 7) ストリーム生成。Web参照ありの回答は日付・項目の列挙が長くなるため上限を緩める
            //    （14日分の予報列挙で400トークンでは途切れる実測。prompt込みでもctx内に収まる）
            //    列挙は4〜5文書×複数項目で450では途中切断する実測（t98: 4文書目の日数に届かず欠落）のため650
            //    並列起動したLLMロードはここで待つ（失敗は元例外を再throwし外側のcatchで分類）
            await EnsureLlmReadyAsync();
            await Sse(ctx, "stage", new { text = "回答を生成中…" });
            var answer = new StringBuilder();
            var ttfb = -1L;
            await _gw.ChatStreamAsync(_cfg.EnginePortLlm, messages, 0.0, enumeration ? 650 : synonymBroad ? 350 : webContributed ? 500 : 300, async delta =>
            {
                if (ttfb < 0) ttfb = sw.ElapsedMilliseconds;
                answer.Append(delta);
                await Sse(ctx, "delta", new { content = delta });
            }, ctx.RequestAborted);

            // 8) 生成後バリデーションガード（t74の正攻法）: 回答中の数値が出典・質問のどこにも
            //    存在しない（＝捏造・取り違えの疑い）場合、修正を指示して1回だけ再生成し、
            //    成功時はSSE patchで回答を差し替える。quick 1.7B等の小型モデルの数値取り違え対策。
            //    再生成はサイレント収集（deltaを流すとクライアント側で回答が倍加するため）。
            //    timeSensitive（日付応答）は検証対象外
            var validationCorpus = string.Concat(chosen.Select(h => SourceText(h.Rec))) + "\n" + message
                + (string.IsNullOrEmpty(webContext) ? "" : "\n" + webContext)
                + (jissekiText.Length > 0 ? "\n" + jissekiText : "");
            // 注意: ここに当日付（CurrentDateLine）を足すと、回答中の日付が検証を通るようになり
            // 「金額/日付/状況」テンプレ形式の揺らぎ回答の再生成が抑制される（2026-10-30実測・d06）。
            // 再生成はテンプレ揺らぎを実質浄化しているため、日付は意図的に捏造扱いのままとする
            var validationRegenerated = false;
            if (!timeSensitive && !clarifyConfirm && (sources.Count > 0 || !string.IsNullOrEmpty(webContext)))
            {
                var badNums = Rag.FabricatedNumbers(answer.ToString(), validationCorpus);
                if (badNums.Count > 0)
                {
                    _log.Warn($"validation guard: numbers not in sources [{string.Join(", ", badNums)}] — regenerating once");
                    await Sse(ctx, "stage", new { text = "回答を検証中…（根拠を確認して答え直します）" });
                    var retryMsgs = new List<(string, string)>(messages)
                    {
                        ("assistant", answer.ToString()),
                        ("user", "先ほどの回答には参照情報に存在しない数値・事実が含まれています。推測や一般知識を使わず、【参照情報】に書かれた内容だけで答え直してください。参照情報に該当が無い場合は「該当する記載がありません」と答えてください。")
                    };
                    var retry = new StringBuilder();
                    try
                    {
                        await _gw.ChatStreamAsync(_cfg.EnginePortLlm, retryMsgs, 0.0, 300, _ => Task.CompletedTask, ctx.RequestAborted);
                        var fixedText = retry.ToString();
                        if (fixedText.Trim().Length > 0 && Rag.FabricatedNumbers(fixedText, validationCorpus).Count == 0)
                        {
                            answer.Clear().Append(fixedText);
                            validationRegenerated = true;
                            await Sse(ctx, "patch", new { content = answer.ToString() });
                            _log.Info("validation guard: answer regenerated and validated");
                        }
                        else
                        {
                            _log.Warn("validation guard: regenerated answer still invalid or empty — keeping original");
                        }
                    }
                    catch (Exception ex2)
                    {
                        _log.Warn("validation regeneration failed: " + ex2.Message);
                    }
                }
            }

            // 品質ガード（v2.7.0・残課題3項目）— 数値ガードを通った回答の「内容の質」を検査する。
            // s14型: QA集形式KBで質問に対応する見出しが無いのに、モデルが近い話題のQAを元に
            // それらしい回答を生成（副業→経費精算/在宅勤務のすり替え）。検出したら再生成せず
            // 定型の拒否へ置き換える（QA集の回答前提=見出し一致。すでに拒否している回答は触らない）。
            // 混合KBの過剰発火防止: QA形式でない文書（出張規程等）が質問の根拠を持ちうる場合
            // （質問の内容語が非QAチャンクに含まれる）は照合しない — 日当・私物USB等、QA_list以外の
            // 文書が答えを持つ質問の正解が「見出し不一致」で拒否に置換される事故の防止（2026-10-31実測）
            if (!timeSensitive && !clarifyConfirm && sources.Count > 0
                && !Rag.IsRefusal(answer.ToString())
                && !Rag.NonQaChunkGrounded(message,
                    chosen.Where(h => !Rag.IsQaFormatChunk(h.Rec.Text)).Select(h => h.Rec.Text))
                && Rag.QaHeadingMismatch(message, validationCorpus))
            {
                _log.Warn("qa heading guard: question matches no QA heading — replacing with fixed refusal");
                answer.Clear().Append("該当する記載がありません。");
                // 置換は表示にも反映する（2026-10-31実測: patch無しだとクライアントは
                // deltaで流れた元回答を表示し続け、DBだけ拒否が保存されて不一致になる）
                await Sse(ctx, "patch", new { content = answer.ToString() });
            }

            // t44型/t47型: (a)疑問詞（誰/いつ/どこ/いくら）の要点に答えていない、
            // (b)参照文脈に根拠の無い箇条書き項目を水増し列挙 —— のいずれかを検出したら
            // 1回だけサイレント再生成する。再生成は要求全体で1回まで（数値ガードで再生成済み
            // ならスキップ。レイテンシ保護）。差し替えは再生成結果が両検査と数値ガードを
            // すべて通った場合のみ（言い換え回答を誤って落とさない保守運用）
            if (!timeSensitive && !clarifyConfirm && sources.Count > 0 && !validationRegenerated && answer.Length > 0)
            {
                var focusBad = Rag.FocusMismatch(message, answer.ToString());
                var ungrounded = focusBad ? new List<string>() : Rag.UngroundedListItems(answer.ToString(), validationCorpus);
                if (focusBad || ungrounded.Count > 0)
                {
                    _log.Warn($"quality guard: {(focusBad ? "off-focus answer" : "ungrounded items x" + ungrounded.Count)} — regenerating once");
                    await Sse(ctx, "stage", new { text = "回答を検証中…（要点と根拠を確認して答え直します）" });
                    var why = focusBad
                        ? "直前の回答は質問の要点（誰が・いつ・どこで・いくら等）に直接答えていません。"
                        : "直前の回答には参照情報に根拠の無い項目が含まれています。";
                    var retryMsgs = new List<(string, string)>(messages)
                    {
                        ("assistant", answer.ToString()),
                        ("user", why + "【参照情報】の内容だけを使い、質問された点に直接答えてください。参照情報に該当が無い場合は「該当する記載がありません」と答えてください。")
                    };
                    var retry = new StringBuilder();
                    try
                    {
                        await _gw.ChatStreamAsync(_cfg.EnginePortLlm, retryMsgs, 0.0, 300, _ => Task.CompletedTask, ctx.RequestAborted);
                        var fixedText = retry.ToString();
                        var ok = fixedText.Trim().Length > 0
                            && Rag.FabricatedNumbers(fixedText, validationCorpus).Count == 0
                            && !Rag.FocusMismatch(message, fixedText)
                            && Rag.UngroundedListItems(fixedText, validationCorpus).Count == 0;
                        if (ok)
                        {
                            answer.Clear().Append(fixedText);
                            await Sse(ctx, "patch", new { content = answer.ToString() });
                            _log.Info("quality guard: answer regenerated and validated");
                        }
                        else
                        {
                            _log.Warn("quality guard: regenerated answer still invalid — keeping original");
                        }
                    }
                    catch (Exception ex2)
                    {
                        _log.Warn("quality regeneration failed: " + ex2.Message);
                    }
                }
            }

            // 連続同一文の畳み込み（繰り返し出力クセ対策・v2.3.2）: 出典行正規化の前に適用し、
            // DB・回答キャッシュには畳み後のテキストを保存する（キャッシュ経由で繰り返しを恒久化しない）
            var rawAnswer = answer.ToString();
            var collapsedText = Rag.CollapseDuplicateSentences(rawAnswer) ?? rawAnswer;
            if (collapsedText != rawAnswer)
            {
                _log.Info("repeat collapse: consecutive duplicated sentence removed");
                // 繰り返しガード（2026-10-23実機E2Eの「回答が不自然」対応）: 小型モデルが同一文を
                // 連発した場合、畳み込みだけでなくvalidation guardと同じくサイレント再生成→patchで
                // 差し替える。短い重複は畳み込みで十分なため、回答がある程度長い場合のみ再生成
                if (rawAnswer.Length >= 80 && !clarifyConfirm && !timeSensitive)
                {
                    try
                    {
                        _log.Warn("repeat guard: repetition detected — regenerating once");
                        var retryMsgs = new List<(string, string)>(messages)
                        {
                            ("assistant", rawAnswer),
                            ("user", "直前の回答は同じ文章を繰り返しています。繰り返しを無くし、【参照情報】の内容に基づいて簡潔に答え直してください。参照情報に該当が無い場合は「該当する記載がありません」と答えてください。")
                        };
                        var retry = new StringBuilder();
                        await _gw.ChatStreamAsync(_cfg.EnginePortLlm, retryMsgs, 0.0, 300, _ => Task.CompletedTask, ctx.RequestAborted);
                        var fixedText = retry.ToString();
                        if (fixedText.Trim().Length > 0 && (Rag.CollapseDuplicateSentences(fixedText) ?? fixedText) == fixedText)
                        {
                            collapsedText = fixedText;
                            _log.Info("repeat guard: regenerated answer is clean — replacing");
                        }
                        else
                        {
                            _log.Warn("repeat guard: regenerated answer still repetitive or empty — keeping collapsed");
                        }
                    }
                    catch (Exception ex2)
                    {
                        _log.Warn("repeat regeneration failed: " + ex2.Message);
                    }
                }
            }

            // 回答の見た目を整える（v2.7.0）: 参照チャンクの見出し（【経費精算】等）の写しと
            // 同一内容の重複行を除去する。全文注入モードの小規模KBで発生しやすい「機械的な回答」対策
            var sanitized = Rag.SanitizeAnswer(collapsedText);
            if (sanitized != collapsedText)
            {
                _log.Info("answer sanitized: document labels/duplicate lines removed");
                collapsedText = sanitized;
            }

            // 拒否文言の統一（自然文→定型・t73実測）: 出典行が無く1文目が拒否そのものの応答は
            // 「該当する記載がありません。」へ置き換える。部分回答・但し書き付きは変更しない（Rag側で保護）
            if (Rag.NormalizeRefusalWording(collapsedText) is { } unified)
            {
                collapsedText = unified;
                _log.Info("refusal wording normalized to the fixed phrase");
            }

            // 出典行を出典パネルと完全一致する正規形へ（モデルが省略・1件のみ記載するのを防止）
            var finalText = NormalizeSourcesLine(collapsedText, sources);
            // 出典表記の自然化（2026-10-23）: 出典は出典パネル（sources_json→UIの出典チップ/パネル）が担うため
            // 本文から「出典：○○.md」行を除く。モデルが参照スニペット見出しを写して付与する行の掃除
            finalText = System.Text.RegularExpressions.Regex.Replace(
                finalText, "^[ \\t]*出典[：:].*$", "", System.Text.RegularExpressions.RegexOptions.Multiline).Trim();
            if (finalText != answer.ToString())
                await Sse(ctx, "patch", new { content = finalText });

            var sourcesJson = JsonSerializer.Serialize(sources, JsonOpts);
            var responseKind = clarifyConfirm ? "clarify" : "answer";
            var route = clarifyConfirm ? "clarify" : intent.Route switch
            {
                AnswerRoute.MultiPart => "rag-multi",
                AnswerRoute.DrawingQa => "rag-drawing",
                _ => "rag",
            };
            _db.Exec("INSERT INTO messages(chat_id, role, content, sources_json, kind) VALUES($c,'assistant',$m,$s,$k)",
                ("$c", chatId), ("$m", finalText), ("$s", sourcesJson), ("$k", responseKind));
            var assistantMessageId = _db.LastInsertId();
            _db.Exec("UPDATE chats SET updated_at=datetime('now') WHERE id=$c", ("$c", chatId));
            // Web由来の回答はキャッシュしない: Web検索結果は時々刻々変わる_snapshot_で、
            // 古い（誤った）回答の恒久化を防ぐため。天気・ニュース類はTimeSensitive判定でも排除。
            // 確認促進(clarify)は回答ではないためキャッシュ対象外
            if (!clarifyConfirm && !timeSensitive && !nonJa && !webContributed && sources.Count > 0 && answer.Length > 0 && !answer.ToString().Contains("該当する記載がありません"))
            {
                // 保存本文は finalText（出典行削除後）に統一（Phase 1.2修正）: 旧実装は出典行を含む
                // collapsedText を保存していたため、キャッシュヒット時だけ本文に出典行が復活していた。
                _db.Exec("INSERT INTO answer_cache(question, answer, sources_json, emb, model) VALUES($q,$a,$s,$e,$m)",
                    ("$q", message), ("$a", finalText), ("$s", sourcesJson), ("$e", ChunkIndex.FloatsToBytes(qEmb)), ("$m", _cfg.ChatModelFile));
                // メモリ側のIdをDBの実IDに合わせる（Phase 1.2修正）: 0固定ではヒット時の
                // UPDATE answer_cache SET hits=hits+1 が不発になっていた
                var cacheId = _db.LastInsertId();
                lock (_cacheLock) _cache.Insert(0, new CacheEntry(message, finalText, sourcesJson, qEmb, _cfg.ChatModelFile, cacheId));
            }
            var totalMs = sw.ElapsedMilliseconds;
            await Sse(ctx, "done", new
            {
                cached = false, kind = responseKind, route,
                stages = Stages(ttfb, totalMs, tEmbed, tSearch, tRetrieve, tPrompt),
                guard = (string?)null, sources, ttfb_ms = ttfb, ms = totalMs, message_id = assistantMessageId,
            });
            // 計測サンプル（通常回答）。p50/p95 は GET /api/diagnostics で参照（外部送信なし）
            _diag.Record(new Diagnostics.Sample(route, responseKind, false, ttfb, tEmbed,
                Diff(tSearch, tEmbed), Diff(tRetrieve, tEmbed), Diff(tPrompt, tRetrieve), Diff(ttfb, tPrompt), totalMs));
            // 長時間運用のメモリ増加対策: 回答完了後に上限超過ならLLMを再起動（次問までに暖機）
            _sup.RecycleLlmIfMemoryHigh();
        }
        catch (OperationCanceledException) { /* クライアント切断 */ }
        catch (FileNotFoundException ex)
        {
            _log.Error($"model/engine file missing: {ex.FileName}");
            await Sse(ctx, "error", new { code = "SHINE_E_MODEL_NOT_FOUND", message = "AIモデルが未インストールです。設定からモデルをダウンロードしてください。", detail = ex.FileName });
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("SHINE_E_MODEL_HASH"))
        {
            _log.Error($"model corrupted: {ex.Message}");
            await Sse(ctx, "error", new { code = "SHINE_E_MODEL_HASH", message = "AIモデルのファイルが破損しています。設定画面からモデルを再ダウンロードしてください。", detail = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("SHINE_E_ENGINE_DOWN"))
        {
            _log.Error($"engine down: {ex.Message}");
            await Sse(ctx, "error", new { code = "SHINE_E_ENGINE_DOWN", message = "AIエンジンの起動に失敗しました。再試行してください。", detail = ex.Message });
        }
        catch (Exception ex)
        {
            _log.Error($"chat failed: {ex}");
            await Sse(ctx, "error", new { code = "SHINE_E_INTERNAL", message = "内部エラーが発生しました。", detail = ex.Message });
        }
    }

    /// <summary>文書要約（v2.4.1）: 指定ファイルの全チャンク（seq順・上限8000字）を注入して要約を
    /// ストリーミング生成する。出典は対象ファイル1件・kind=answer。検索ガード類は経由しない
    /// （全文注入のため検証コーパス=文書全体）。回答キャッシュには書かない（ファイル再取り込みで
    /// 内容が変わるため）。繰り返し畳み込みのみ適用</summary>
    private async Task SummarizeFileAsync(HttpContext ctx, long chatId, long fileId, System.Diagnostics.Stopwatch sw)
    {
        var fileRow = _db.Query("SELECT name, status, kind FROM files WHERE file_id=$i", ("$i", fileId));
        if (fileRow.Count == 0 || (string)fileRow[0]["status"]! != "ready")
        {
            var miss = "指定された文書が見つかりません（取り込み中か、削除された可能性があります）。";
            _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'refusal')", ("$c", chatId), ("$m", miss));
            await Sse(ctx, "delta", new { content = miss });
            await Sse(ctx, "done", new { cached = false, kind = "refusal", route = "summary", guard = "summary-missing", sources = Array.Empty<object>(), ms = sw.ElapsedMilliseconds });
            return;
        }
        var fileName = (string)fileRow[0]["name"]!;
        var fileKind = (string)fileRow[0]["kind"]!;
        var (fullText, truncated) = Rag.SelectSummaryText(_index.ByFile(fileId).Select(c => c.Text).ToList(), 8000);
        _log.Info($"summary: file={fileName} id={fileId} chunks-text={fullText.Length} chars truncated={truncated}");
        if (fullText.Trim().Length == 0)
        {
            var empty = "このファイルには要約できるテキストがありません。";
            _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'refusal')", ("$c", chatId), ("$m", empty));
            await Sse(ctx, "delta", new { content = empty });
            await Sse(ctx, "done", new { cached = false, kind = "refusal", route = "summary", guard = "summary-empty", sources = Array.Empty<object>(), ms = sw.ElapsedMilliseconds });
            return;
        }
        // 出典は対象ファイル1件（図面はメタも添えてUIの【図面】表示に対応）
        var src = new SourceInfo { File = fileName, Snippet = "文書要約", Kind = "kb", FileId = fileId, IsDrawing = fileKind == "drawing" };
        if (src.IsDrawing)
        {
            var meta = _db.Query("SELECT zuban_raw, hinmei, revision FROM drawing_meta WHERE file_id=$i", ("$i", fileId));
            if (meta.Count > 0)
            {
                src.Zuban = meta[0]["zuban_raw"] as string;
                src.Hinmei = meta[0]["hinmei"] as string;
                src.Revision = meta[0]["revision"] as string;
            }
        }
        var sources = new List<SourceInfo> { src };
        await Sse(ctx, "refs", new { files = sources.Select(s => s.File).ToArray(), web = Array.Empty<object>() });
        var messages = new List<(string, string)>
        {
            ("system", "あなたは社内文書の要約担当です。渡された文書から要点を3〜5個の箇条書き（- ）でまとめてください。重要な数値・金額・日付・条件は文書の記載どおり含めてください。文書に書かれていないことを推測で追加せず、前置きと見出しは不要です。"),
            ("user", "次の文書を要約してください。\n\n" + fullText),
        };
        var answer = new StringBuilder();
        var ttfb = -1L;
        await _gw.ChatStreamAsync(_cfg.EnginePortLlm, messages, 0.0, 500, async delta =>
        {
            if (ttfb < 0) ttfb = sw.ElapsedMilliseconds;
            answer.Append(delta);
            await Sse(ctx, "delta", new { content = delta });
        }, ctx.RequestAborted);

        var finalText = Rag.CollapseDuplicateSentences(answer.ToString()) ?? answer.ToString();
        if (finalText != answer.ToString())
            await Sse(ctx, "patch", new { content = finalText });

        var sourcesJson = JsonSerializer.Serialize(sources, JsonOpts);
        _db.Exec("INSERT INTO messages(chat_id, role, content, sources_json, kind) VALUES($c,'assistant',$m,$s,'answer')",
            ("$c", chatId), ("$m", finalText), ("$s", sourcesJson));
        _db.Exec("UPDATE chats SET updated_at=datetime('now') WHERE id=$c", ("$c", chatId));
        await Sse(ctx, "done", new { cached = false, kind = "answer", route = "summary", sources, ttfb_ms = ttfb, ms = sw.ElapsedMilliseconds });
    }

    /// <summary>下書き作成（v2.5.0）: 指示を章立て（タスク）に分解し、参照チャンクを注入して章ごとに
    /// 生成する決定的パイプライン（Draft.cs参照）。出典契約ではなく「参照: ファイル名」フッターで
    /// 出所を示す。検索ガード・回答キャッシュ・評価カウントは不経由。kind='draft'で履歴保存</summary>
    private async Task DraftAsync(HttpContext ctx, long chatId, string message, JsonElement draft, System.Diagnostics.Stopwatch sw)
    {
        // テンプレート: 手動指定（auto以外の有効ID）を優先、なければ指示文から自動判定
        var templateId = draft.TryGetProperty("template", out var te) && te.ValueKind == JsonValueKind.String ? te.GetString()! : "auto";
        bool auto = templateId is "" or "auto" || !Draft.Templates.ContainsKey(templateId);
        if (auto) templateId = Draft.InferTemplate(message);
        var tpl = Draft.Templates[templateId];

        // 種となるQ&A（回答→資料にする、v2.5.0）
        (string question, string answer)? seed = null;
        if (draft.TryGetProperty("seed", out var se) && se.ValueKind == JsonValueKind.Object)
        {
            var q = se.TryGetProperty("question", out var qe) && qe.ValueKind == JsonValueKind.String ? qe.GetString()! : "";
            var a = se.TryGetProperty("answer", out var ae) && ae.ValueKind == JsonValueKind.String ? ae.GetString()! : "";
            if (q.Length + a.Length > 0) seed = (q, a);
        }

        // 参照は自動（v2.6.0）: 指示文（+種の質問）でナレッジ全体をハイブリッド検索し、
        // ヒットしたチャンクを参照として使う。手動のrefs指定（API互換）があればそちらを優先
        _sup.StartEmbedAndRank(); // 検索にembed/rankが必要。止まっていれば自己修復
        var refs = new List<(long id, string name, string text)>();
        if (draft.TryGetProperty("refs", out var re) && re.ValueKind == JsonValueKind.Array && re.EnumerateArray().Any())
        {
            foreach (var idEl in re.EnumerateArray())
            {
                if (idEl.ValueKind != JsonValueKind.Number) continue;
                long id = idEl.GetInt64();
                var row = _db.Query("SELECT name, status FROM files WHERE file_id=$i", ("$i", id));
                if (row.Count == 0 || (string)row[0]["status"]! != "ready") continue;
                var text = string.Join("\n", _index.ByFile(id).Select(c => c.Text));
                if (text.Trim().Length == 0) continue;
                refs.Add((id, (string)row[0]["name"]!, text));
            }
        }
        else
        {
            // 自動検索（v2.6.0）: 指示文+テンプレート種別のキーワードでナレッジ全体を検索する。
            // 指示文だけではKBチャンクの語彙と合わず拾い漏れることがあるため（実測: 新入社員FAQで
            // 経費精算のみヒット、宿泊費/パスワード等が漏れた）、テンプレート種別の関連語をクエリに足す
            var tplKeywords = tpl.Id switch
            {
                "procedure" => " 手順 申請 承認 方法",
                "report" => " 報告 実績 結果",
                "proposal" => " 提案 見積 条件",
                "faq" => " よくある質問 規定 マニュアル 手続き",
                _ => "",
            };
            var draftQuery = seed is { } sd && sd.question.Length > 0 ? sd.question + " " + message : message + tplKeywords;
            var qEmb = (await _gw.EmbedAsync(_cfg.EnginePortEmb, new[] { draftQuery }, ctx.RequestAborted))[0];
            var tokens = Rag.Tokenize(draftQuery + " " + tplKeywords).ToHashSet();
            // Dify流の参照選定（langgenius/dify dataset_retrieval.py: 候補→リランク→top_kファイル。
            // Dify既定はscore_threshold無効）: ハイブリッド候補12件を指示文との関連でリランクし、
            // 上位4ファイルに絞る。無関係文書（CID検証PDF・実績CSV等）が参照予算(2,400字)を
            // 圧迫して関連文書が落ちる問題の対策（2026-10-23実測: FAQ集のrefsにノイズ8件混入）
            var cand = _index.Search(qEmb, tokens, 12).ToList();
            if (_sup.IsRankAlive && cand.Count > 0)
            {
                var candDocs = cand.Select(h => h.Rec.Text).ToList();
                var ranked = await _gw.RerankAsync(_cfg.EnginePortRank, draftQuery, candDocs, cand.Count, ctx.RequestAborted);
                cand = ranked.Select(r => cand[r.Index]).ToList();
                _log.Info($"draft refs rerank: {cand.Count} candidates reranked, top files by relevance");
            }
            foreach (var h in cand)
            {
                var rec = h.Rec;
                // 同名ファイルは1件に統合する（出典の重複表示防止・2026-10-31実測）:
                // KBに同名ファイルがfile_id違いで二重登録されている場合、file_id単位の判定だと
                // 「慶弔休暇規程.md ×2」のように出典に同一文書が並ぶ。テキストは統合して1件に
                var idx = refs.FindIndex(r => r.name == rec.FileName);
                if (idx >= 0) refs[idx] = (refs[idx].id, refs[idx].name, refs[idx].text + "\n" + rec.Text);
                else refs.Add((rec.FileId, rec.FileName, rec.Text));
            }
            // Dify既定のtop_k=4ファイルに絞る（スコア順。ファイルごとのテキストは全チャンク連結）
            refs = refs.Take(4).ToList();
        }
        var refText = Draft.BuildReferenceText(refs.Select(r => (r.name, r.text)).ToList(), seed);
        // 出典の該当箇所に参照チャンクの実際の文（Markdown記法をプレーン化）を入れる。
        // Snippet（120字）に加えてText（全文・2000字上限）も持たせることで、出典モーダルで
        // 全文表示ができ「…で切れて全文が見えない」問題を解消する（2026-10-31実測）
        var sources = refs.Select(r => new SourceInfo
        {
            File = r.name,
            Snippet = Rag.PlainSnippet(r.text, 120),
            Text = Rag.PlainSnippet(r.text, 2000),
            Kind = "kb",
            FileId = r.id,
        }).ToList();
        if (sources.Count > 0)
            await Sse(ctx, "refs", new { files = sources.Select(s => s.File).ToArray(), web = Array.Empty<object>() });
        _log.Info($"draft: template={tpl.Id}{(auto ? "(auto)" : "")} refs={refs.Count} auto-search={refs.Count > 0 && !draft.TryGetProperty("refs", out var _)} seed={seed != null} refChars={refText.Length}");
        if (refText.Trim().Length == 0)
        {
            var emptyKb = "ナレッジに文書が登録されていません。参照する文書を先にナレッジへ登録してください（作成をOFFにして通常の質問もできます）。";
            _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'refusal')", ("$c", chatId), ("$m", emptyKb));
            await Sse(ctx, "delta", new { content = emptyKb });
            await Sse(ctx, "done", new { cached = false, kind = "refusal", route = "draft", guard = "draft-empty", sources = Array.Empty<object>(), ms = sw.ElapsedMilliseconds });
            return;
        }

        // 単発生成（v2.6.0）: 検索で集めた参照を1コンテキストへ注入し、1回の生成で下書き全体を作る。
        // 章ごとの独立生成は廃止 — 参照なし生成で「各章が同一パターンで満たされる」劣化の
        // 根本原因は、章が互いの内容を知らずに同じ材料で埋まることだった（実測: 同一動詞92%）。
        // 1コンテキスト内ならモデルの自己抑制が働き、繰り返しが構造的に起きない（Difyの検索+LLMノードと同一構造）
        await Sse(ctx, "phase", new { text = "下書きを生成中…" });
        var messages = new List<(string, string)>
        {
            ("system", "あなたは社内文書の下書き担当。参照情報と指示に基づいて、文書全体を1つまとめて書く。"
                + $"この文書の書式: {tpl.SectionFormat}\n書式の例:\n{tpl.Example}\n"
                + "参照情報に書かれた事実のみを使い、書かれていない数値・名前を作らない。参照情報の数値・固有名詞・英単語は表記を変えないこと。英語の語は正しい英語のまま使うか自然な日本語に訳すこと。英字と日本語を混ぜた表記（例: メeting）は作らない。\n"
                + "構成: 冒頭に文書タイトルの見出し（# ）は付けない（別途付与される）。内容が豊富なら章見出し（## ）で整理してよい。同じ主語+動詞の繰り返しで項目を水増ししない。各項目は異なる内容について書く。600字以内。前置きと締めの挨拶は不要。"),
            ("user", $"指示: {message}\n\n{refText}\n\n指示に沿って文書全体を書いてください。"),
        };
        var answer = new StringBuilder();
        await _gw.ChatStreamAsync(_cfg.EnginePortLlm, messages, 0.0, 600, async delta =>
        {
            answer.Append(delta);
            await Sse(ctx, "delta", new { content = delta });
        }, ctx.RequestAborted);

        // 下書きの重複畳み込み（v2.7.0）: 単発生成パスは後処理が無く、1.7Bモデルの
        // ブロック反復（FAQ項目を2巡書く）がそのまま出力されていた（2026-10-31実測）。
        // ChatFlow回答と同じ連続系の畳み込み＋非連続の完全重複行除去（箇条書き項目限定）を適用
        var draftBody = Rag.CollapseDuplicateSentences(answer.ToString()) ?? answer.ToString();
        draftBody = Rag.CollapseDuplicateLines(draftBody) ?? draftBody;

        var finalText = Draft.AssembleMarkdown(tpl.Label, auto,
            outlineTitleFrom(message, draftBody), new List<(string, string)> { ("本文", draftBody) },
            refs.Select(r => r.name).ToList());
        var sourcesJson = JsonSerializer.Serialize(sources, JsonOpts);
        _db.Exec("INSERT INTO messages(chat_id, role, content, sources_json, kind) VALUES($c,'assistant',$m,$s,'draft')",
            ("$c", chatId), ("$m", finalText), ("$s", sourcesJson));
        var draftMessageId = _db.LastInsertId();
        _db.Exec("UPDATE chats SET updated_at=datetime('now') WHERE id=$c", ("$c", chatId));
        await Sse(ctx, "patch", new { content = finalText });
        // message_idを添える: 下書き応答にも👍/👎のアクション行を出すため（v2.7.0）
        await Sse(ctx, "done", new { cached = false, kind = "draft", route = "draft", template = tpl.Label, auto_inferred = auto, title = outlineTitleFrom(message, answer.ToString()), sources, ms = sw.ElapsedMilliseconds, message_id = draftMessageId });
    }

    /// <summary>チャットタイトル等の短い表示名（24字以内）。超える場合は句読点等の自然な境界で
    /// 切り、切れたことは「…」で明示する（24字固定だと「まと」で切れて誤字に見えた実測・2026-10-31）</summary>
    internal static string ShortTitle(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= 24) return text ?? "";
        var cut = text[..24];
        var m = System.Text.RegularExpressions.Regex.Match(cut, @"^(.+[、。（）「」・])");
        if (m.Success && m.Groups[1].Value.Length >= 8) return m.Groups[1].Value.TrimEnd('、', '。') + "…";
        return cut + "…";
    }

    /// <summary>下書きのタイトル決定: 生成文の先頭が「# 」見出しならそれを使い、無ければ指示文から作る。
    /// 切り詰めはShortTitle（自然な境界+「…」）に統一</summary>
    internal static string outlineTitleFrom(string instruction, string generated)
    {
        foreach (var line in (generated ?? "").Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("# ")) return t[2..].Trim();
        }
        return ShortTitle(instruction);
    }
}
