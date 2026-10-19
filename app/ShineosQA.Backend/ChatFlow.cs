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
    private readonly object _cacheLock = new();
    private List<CacheEntry> _cache = new();

    private sealed record CacheEntry(string Question, string Answer, string SourcesJson, float[] Emb, string Model, long Id);

    public ChatFlow(AppConfig cfg, Db db, Supervisor sup, LlmGateway gw, ChunkIndex index, WebSearch web, ILogger log)
    {
        _cfg = cfg; _db = db; _sup = sup; _gw = gw; _index = index; _web = web; _log = log; LoadCache();
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

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

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
            _db.Exec("UPDATE chats SET title=$t WHERE id=$c", ("$t", message.Length > 24 ? message.Substring(0, 24) : message), ("$c", chatId));
        }
        await Sse(ctx, "meta", new { chat_id = chatId, chat_uuid = chatUuid });

        // 会話コンテキスト: 同一チャットの直近やり取り（2往復・各250字）を記憶する
        var historyRows = _db.Query("SELECT role, content FROM messages WHERE chat_id=$c ORDER BY id DESC LIMIT 4", ("$c", chatId));
        historyRows.Reverse();
        var history = historyRows
            .Select(r => ((string)r["role"]! == "user" ? "user" : "assistant", Truncate((string)r["content"]!, 250)))
            .ToList();
        string prevUser = history.LastOrDefault(h => h.Item1 == "user").Item2 ?? "";

        _db.Exec("INSERT INTO messages(chat_id, role, content) VALUES($c,'user',$m)", ("$c", chatId), ("$m", message));
        if (captureImage != null) SaveCaptureImage(chatId, captureImage);

        try
        {
            _sup.StartEmbedAndRank(); // 前回クラッシュ・tier変更後のStopAll等で止まっていたら自己修復

            // 検索クエリ補強: 指示語・短い質問は直前の質問で補完（「料金はいくら？」等のfollow-up対応）
            var queryForRetrieval = message;
            if (prevUser.Length > 0 && (message.Length < 12 || RegexHasAnaphora(message)))
                queryForRetrieval = prevUser + " " + message;

            // 1) 質問埋め込み
            var qEmb = (await _gw.EmbedAsync(_cfg.EnginePortEmb, new[] { queryForRetrieval }, ctx.RequestAborted))[0];

            // 2) 回答キャッシュ（cos>=0.97 → 即時）。LLM未ロードでも返せるのでEnsureLlmより前に置く
            // 日付感応質問（「今日は何日」等）は正解が日付で変わるためキャッシュから読まない
            bool timeSensitive = Rag.TimeSensitiveQuestion().IsMatch(message);
            if (!timeSensitive)
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
                    Sse(ctx, "delta", new { content = hit.Answer }).GetAwaiter().GetResult();
                    Sse(ctx, "done", new { cached = true, kind = "answer", sources = JsonSerializer.Deserialize<object>(hit.SourcesJson), ms = sw.ElapsedMilliseconds }).GetAwaiter().GetResult();
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
            _sup.EnsureLlm(); // idle unload後の再確保（キャッシュヒット時は不要のためここで確保）

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

            // 4) ハイブリッド検索 top8
            var qTokens = Rag.Tokenize(queryForRetrieval).ToHashSet();
            var hits = _index.Search(qEmb, qTokens, Rag.RerankPool);
            // 図面チャンクの判別（意図ブーストとスニペット免除で共用。files は小型テーブルなので都度照会で即時反映）
            var drawingIds = _db.Query("SELECT file_id FROM files WHERE kind='drawing'")
                .Select(r => Convert.ToInt64(r["file_id"] ?? 0L)).ToHashSet();
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
            string webNote = webFailed ? "\n※Web検索に失敗したため、社内ナレッジのみで判定しています。" : "";
            // 日付感応質問（「今日は何日」「今何時」等）は参照情報がなくてもシステム日時から
            // 直接回答する（PCのシステム時計が根拠。ガードで「該当なし」にしない）
            if (hits.Count == 0 && string.IsNullOrEmpty(webContext) && !timeSensitive)
            {
                var refusal = "該当する記載がありません。" + webNote;
                _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'refusal')", ("$c", chatId), ("$m", refusal));
                await Sse(ctx, "delta", new { content = refusal });
                await Sse(ctx, "done", new { cached = false, kind = "refusal", guard = "empty", sources = Array.Empty<object>(), ms = sw.ElapsedMilliseconds });
                return;
            }

            // 5) リランク → ガード（入力はスニペット化して高速化: 約-30%、精度は関連文中心で維持）
            // 注意: 160字への圧縮は実測で品質を落とした（Golden QA 108→101/118。タクシー22時・会議室等の
            // 事実が窓外に切れた）。240字が実測上の最適点
            var qTokForSnip = qTokens;
            var docs = hits.Select(h => Rag.Snippet(h.Rec.Text, qTokForSnip, 240)).ToList();
            var sources = new List<SourceInfo>();
            List<ChunkIndex.HybridHit> chosen = hits;
            bool rerankValidated = false; // リランクを実行しガード(GuardThreshold)を通過→弱一致バックストップの対象外
            var rerankTop1 = double.NegativeInfinity; // リランク実行時のtop1スコア（低信頼×低Kw判定用・未実行時は∞で不発）
            if (docs.Count > 0)
            {
                // 高信頼ショートカット: ベクトル・キーワードがともに強一致ならリランク（実測~2秒）を省略して
                // ハイブリッド上位2件を採用。判定は生cos+kw（正規化前）。低スコア側は従来どおりリランク+ガード。
                var top = hits[0];
                bool skip = top.Cos >= Rag.RerankSkipCos && top.Kw >= Rag.RerankSkipKw;
                if (skip)
                {
                    // 超高信頼（cos・kwとも強一致）は文書1件のみ注入しプロンプト短縮（pp大幅減）。それ以外はtop2
                    chosen = top.Cos >= 0.72 && top.Kw >= 0.35 ? hits.Take(1).ToList() : hits.Take(2).ToList();
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
                        var ranked = await _gw.RerankAsync(_cfg.EnginePortRank, queryForRetrieval, docs, 2, ctx.RequestAborted);
                        var top1 = ranked.Count > 0 ? ranked[0].Score.ToString("F2") : "none";
                        _log.Info($"rerank: top1={top1} cos={top.Cos:F2} kw={top.Kw:F2} file={top.Rec.FileName}");
                        if (ranked.Count == 0 || ranked[0].Score < Rag.GuardThreshold)
                        {
                            if (string.IsNullOrEmpty(webContext) && !timeSensitive)
                            {
                                var refusal = "該当する記載がありません。" + webNote;
                                _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'refusal')", ("$c", chatId), ("$m", refusal));
                                await Sse(ctx, "delta", new { content = refusal });
                                await Sse(ctx, "done", new { cached = false, kind = "refusal", guard = "rerank", sources = Array.Empty<object>(), ms = sw.ElapsedMilliseconds });
                                return;
                            }
                            chosen = new(); // Webのみで回答（日付感応質問はシステム日時のみで回答）
                        }
                        else
                        {
                            // 高信頼（top1スコア≥RerankHighConf）なら文書1件のみ注入してプロンプト短縮（pp削減）。それ以外はtop2。
                            // ここを通った = クロスエンコーダが関連性を検証済み → 弱一致バックストップの対象外
                            rerankValidated = true;
                            rerankTop1 = ranked[0].Score;
                            var take = ranked[0].Score >= Rag.RerankHighConf ? 1 : 2;
                            // 2件目が no-hit 帯（リランカが無関連と判定）なら1件に削る。
                            // top1のみ検査して2件目を無条件採用していた抜けを塞ぐ。
                            // ※プールが1件のときは ranked[1] が存在しない（v2.3.2で小型KBの質問が
                            //   内部エラーになる実害を実測・A/B検証中に発見）ため件数を先に見る
                            if (take == 2 && (ranked.Count < 2 || ranked[1].Score < Rag.GuardThreshold)) take = 1;
                            chosen = ranked.Take(take).Select(r => hits[r.Index]).ToList();
                        }
                    }
                }
            }
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
                await Sse(ctx, "done", new { cached = false, kind = "refusal", guard = guard, sources = Array.Empty<object>(), ms = sw.ElapsedMilliseconds });
                return;
            }
            // 弱一致ガード（バックストップ・生成前）: kw≈0の採択を拒否する。識別の主役はリランクスコア
            // （GuardThreshold: nohit実測≤-7.8）で、RerankSkipKw=0.50により弱一致は必ずリランクを経由する。
            // 本ガードが守るのはリランカー未導入環境（ウィザード既定ではrerankを導入するが、削除した場合の
            // 最終防波堤）。0.15〜0.50帯はリランカーが無いと識別不能（リランカー推奨の根拠）。
            // 対象外（IsWeakMatch参照）: 図面質問/Web文脈/時間感応/リランク検証済み
            var maxKw = chosen.Max(h => h.Kw);
            if (Rag.IsWeakMatch(maxKw,
                    hasWebContext: !string.IsNullOrEmpty(webContext),
                    timeSensitive: timeSensitive,
                    drawingIntent: Rag.HasDrawingIntent(message),
                    rerankValidated: rerankValidated))
            {
                _log.Info($"weak-match guard: kw={maxKw:F2} < {Rag.MinAcceptKw} — 該当なし拒否へ（偽出典防止）: {Truncate(message, 40)}");
                var refusal = "該当する記載がありません。" + webNote;
                _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'refusal')", ("$c", chatId), ("$m", refusal));
                await Sse(ctx, "delta", new { content = refusal });
                await Sse(ctx, "done", new { cached = false, kind = "refusal", guard = "weak-match", sources = Array.Empty<object>(), ms = sw.ElapsedMilliseconds });
                return;
            }
            // 低信頼×低Kwの組み合わせ拒否: リランクはno-hit帯を逃れたものの確信できないグレー帯
            // (top1<RerankHighConf)で、かつキーワード一致もほぼ無い(maxKw<MinAcceptKw)ケース。
            // 表層のみ類似の無関係チャンクで一般知識+偽出典回答ができる最後の経路を塞ぐ
            // （実測: 正解top1≥+7.3 vs 無関係≤-10.6 — グレー帯に落ちる半端な組み合わせのみ拒否）。
            // 言い換えヒット(top1≥RerankHighConf)とキーワード確認済み(maxKw≥MinAcceptKw)は通す
            if (Rag.IsLowConfidenceWeak(rerankTop1, maxKw, rerankValidated)
                && string.IsNullOrEmpty(webContext) && !timeSensitive && !Rag.HasDrawingIntent(message))
            {
                _log.Info($"low-conf guard: rerankTop1={rerankTop1:F2} kw={maxKw:F2} — 該当なし拒否へ（偽出典防止）: {Truncate(message, 40)}");
                var refusal = "該当する記載がありません。" + webNote;
                _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'refusal')", ("$c", chatId), ("$m", refusal));
                await Sse(ctx, "delta", new { content = refusal });
                await Sse(ctx, "done", new { cached = false, kind = "refusal", guard = "low-conf", sources = Array.Empty<object>(), ms = sw.ElapsedMilliseconds });
                return;
            }
            // 対象語リルート: リランクの意味的選択が、質問の対象語（会議室等）を字面通り含まないチャンクを
            // 採択することがある（s11実測: top1スコア3.40でも対象語欠落 → 既存の対象ガードが拒否へ倒していた）。
            // 拒否ではなく、プール内の対象語含有チャンクへ根拠を差し替える。プール全体にも無い場合は
            // 従来どおり対象ガードが拒否する（t74型: 別対象の手続きを転用する回答の防止は維持）
            var missingTargets = Rag.MissingProcedureTargets(message, chosen.Select(h => MergedChunkText(h.Rec)));
            if (missingTargets.Count > 0 && string.IsNullOrEmpty(webContext) && !timeSensitive)
            {
                // 対象語をいくつ含むかでプールチャンクを ranking し、上位3件へ根拠を差し替える
                // （対象語すべてが1チャンクに揃うことは稀 — s11実測: 会議室/大会議室/収容人数/申請期限は
                //   3チャンクに分散。2件では事実欠落(30日)が出たため3件）
                var rerouted = hits
                    .Select(h => (Hit: h, N: Rag.ContainedProcedureTargets(message, new[] { MergedChunkText(h.Rec) }).Count))
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
            foreach (var h in chosen)
                    sources.Add(EnrichDrawing(new SourceInfo { File = h.Rec.FileName, Snippet = await SnippetForSourceAsync(MergedChunkText(h.Rec), qTokens, message, ctx.RequestAborted), Text = MergedChunkText(h.Rec) }, h.Rec.FileId));
            // 参照確定をUIに通知（思考中の1行表示: どの資料を見ているか）
            await Sse(ctx, "refs", new { files = chosen.Select(h => h.Rec.FileName).ToArray(), web = (webResults ?? new List<WebSearch.WebResult>()).Select(wr => wr.Url).ToList() });

            // 同一図番（改訂違い・別ファイル）が絡む質問: 単断定・混在回答を避け、ユーザーに図面を選んでもらう
            // （UI選択式。選択後は（図番: X・改訂Y）付きで再質問され、該当図面に絞った回答になる）
            // 検出範囲: 質問に図番が明示されている場合は検索プール(hits)全体 — 同一図番の別ファイルがプールに
            // いても採択外だと気づけず、片方だけを根拠に断定するため。図番の明示がない場合は無関係な図面を
            // 巻き込まないよう採択済み(chosen)のみ。再質問済み（改訂指定あり）では発火させない
            if (!message.Contains("改訂") && !message.Contains("（図番:"))
            {
                var qZubanNorm = DrawingIngest.ZubansIn(message).FirstOrDefault();
                var ambScan = new List<(ChunkIndex.HybridHit Hit, string ZubanNorm)>();
                foreach (var h in (qZubanNorm != null ? hits : chosen))
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
                    await Sse(ctx, "done", new { cached = false, kind = "clarify", guard = "clarify", sources = Array.Empty<object>(), ms = sw.ElapsedMilliseconds });
                    _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'clarify')", ("$c", chatId), ("$m", $"CLARIFY:{zubRaw}"));
                    return;
                }
            }

            // 対象ガード（生成前）: 「〜できますか/方法」等の手続き質問で、質問の対象語（助詞直前の漢語等）が
            // 取得文書のどれにも現れない場合、QA文書の類似手続きを別対象へ転用した回答（t74型）になる前に
            // 拒否へ倒す。Web検索時は規則①〜④が効くため適用しない。時間感応質問も対象外
            if (sources.Count > 0 && string.IsNullOrEmpty(webContext) && !timeSensitive &&
                Rag.ProcedureTargetMissing(message, chosen.Select(h => MergedChunkText(h.Rec))))
            {
                _log.Info($"target guard: subject of procedure question not found in sources: {Truncate(message, 40)}");
                var refusal = "該当する記載がありません。" + webNote;
                _db.Exec("INSERT INTO messages(chat_id, role, content, kind) VALUES($c,'assistant',$m,'refusal')", ("$c", chatId), ("$m", refusal));
                await Sse(ctx, "delta", new { content = refusal });
                await Sse(ctx, "done", new { cached = false, kind = "refusal", guard = "target", sources = Array.Empty<object>(), ms = sw.ElapsedMilliseconds });
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
            string MergedChunkText(ChunkIndex.Rec r)
            {
                var next = _index.NextChunkText(r.FileId, r.Seq);
                return next is null ? r.Text : r.Text + "\n" + next;
            }
            var covered = new HashSet<(long, int)>();
            var ctxDocs = new List<(string, string)>();
            foreach (var h in chosen)
            {
                // 直前チャンクとの連結で既に含めた続きチャンクは二重注入しない
                if (covered.Contains((h.Rec.FileId, h.Rec.Seq))) continue;
                covered.Add((h.Rec.FileId, h.Rec.Seq));
                if (_index.NextChunkText(h.Rec.FileId, h.Rec.Seq) is not null) covered.Add((h.Rec.FileId, h.Rec.Seq + 1));
                var merged = MergedChunkText(h.Rec);
                // 図面チャンク（1枚=1チャンク・テキスト量は取り込み時に DrawingIngest.MaxTextChars で上限）は
                // スニペット化せず全文注入する: 240字の窓は寸法ノイズの間に散らばる表題欄・注記を切断し、
                // 「チャンク内に記載があるのに模型に渡らない」実図面検証cr02（JIS B 0405-m）の原因だった
                if (!drawingIds.Contains(h.Rec.FileId)) merged = Rag.Snippet(merged, qTokens);
                ctxDocs.Add((h.Rec.FileName, merged));
            }
            var context = Rag.BuildContext(ctxDocs, webContext);
            var messages = new List<(string, string)> { ("system", Rag.SystemPrompt + Rag.CurrentDateLine()) };
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
            messages.Add(("user", message + sysInfo + ctxPart));
            // 7) ストリーム生成。Web参照ありの回答は日付・項目の列挙が長くなるため上限を緩める
            //    （14日分の予報列挙で400トークンでは途切れる実測。prompt込みでもctx=2048内に収まる）
            var answer = new StringBuilder();
            var ttfb = -1L;
            await _gw.ChatStreamAsync(_cfg.EnginePortLlm, messages, 0.0, webContributed ? 500 : 300, async delta =>
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
            var validationCorpus = string.Concat(chosen.Select(h => MergedChunkText(h.Rec))) + "\n" + message
                + (string.IsNullOrEmpty(webContext) ? "" : "\n" + webContext)
                + (jissekiText.Length > 0 ? "\n" + jissekiText : "");
            if (!timeSensitive && !clarifyConfirm && (sources.Count > 0 || !string.IsNullOrEmpty(webContext)))
            {
                var badNums = Rag.FabricatedNumbers(answer.ToString(), validationCorpus);
                if (badNums.Count > 0)
                {
                    _log.Warn($"validation guard: numbers not in sources [{string.Join(", ", badNums)}] — regenerating once");
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

            // 連続同一文の畳み込み（繰り返し出力クセ対策・v2.3.2）: 出典行正規化の前に適用し、
            // DB・回答キャッシュには畳み後のテキストを保存する（キャッシュ経由で繰り返しを恒久化しない）
            var collapsedText = Rag.CollapseDuplicateSentences(answer.ToString()) ?? answer.ToString();
            if (collapsedText != answer.ToString())
                _log.Info("repeat collapse: consecutive duplicated sentence removed");

            // 出典行を出典パネルと完全一致する正規形へ（モデルが省略・1件のみ記載するのを防止）
            var finalText = NormalizeSourcesLine(collapsedText, sources);
            if (finalText != answer.ToString())
                await Sse(ctx, "patch", new { content = finalText });

            var sourcesJson = JsonSerializer.Serialize(sources, JsonOpts);
            var responseKind = clarifyConfirm ? "clarify" : "answer";
            _db.Exec("INSERT INTO messages(chat_id, role, content, sources_json, kind) VALUES($c,'assistant',$m,$s,$k)",
                ("$c", chatId), ("$m", finalText), ("$s", sourcesJson), ("$k", responseKind));
            _db.Exec("UPDATE chats SET updated_at=datetime('now') WHERE id=$c", ("$c", chatId));
            // Web由来の回答はキャッシュしない: Web検索結果は時々刻々変わる_snapshot_で、
            // 古い（誤った）回答の恒久化を防ぐため。天気・ニュース類はTimeSensitive判定でも排除。
            // 確認促進(clarify)は回答ではないためキャッシュ対象外
            if (!clarifyConfirm && !timeSensitive && !webContributed && sources.Count > 0 && answer.Length > 0 && !answer.ToString().Contains("該当する記載がありません"))
            {
                _db.Exec("INSERT INTO answer_cache(question, answer, sources_json, emb, model) VALUES($q,$a,$s,$e,$m)",
                    ("$q", message), ("$a", collapsedText), ("$s", sourcesJson), ("$e", ChunkIndex.FloatsToBytes(qEmb)), ("$m", _cfg.ChatModelFile));
                lock (_cacheLock) _cache.Insert(0, new CacheEntry(message, collapsedText, sourcesJson, qEmb, _cfg.ChatModelFile, 0));
            }
            await Sse(ctx, "done", new { cached = false, kind = responseKind, guard = (string?)null, sources, ttfb_ms = ttfb, ms = sw.ElapsedMilliseconds });
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
}
