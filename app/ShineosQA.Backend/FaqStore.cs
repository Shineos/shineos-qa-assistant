using System.Text.Json;

namespace ShineosQA.Backend;

/// <summary>公開FAQ（現場の正解）の保存・照合。DifyのAnnotation Replyに相当するが、
/// ローカルDBだけで完結し、出典ハッシュで陳腐化を検出する。
/// 完全一致はLLMも埋め込みも使わず、正規化質問のDB索引だけで返す（目標 &lt;50ms）。</summary>
public sealed class FaqStore
{
    private readonly Db _db;
    private readonly ILogger? _log;

    public FaqStore(Db db, ILogger? log = null)
    {
        _db = db;
        _log = log;
    }

    public sealed record Entry(long Id, string Question, string Answer, string SourcesJson, int Hits);

    /// <summary>正規化質問（または別名）が公開FAQに一致すれば返す。日付感応質問は呼び出し側で除外する。</summary>
    public Entry? TryMatch(string message)
    {
        var norm = QuestionNorm.Normalize(message);
        if (norm.Length < 2) return null;
        try
        {
            var rows = _db.Query(
                "SELECT f.id AS id, f.question AS question, f.answer AS answer, " +
                "       COALESCE(f.sources_json,'[]') AS sources_json, f.hits AS hits " +
                "FROM faq f " +
                "WHERE f.status='published' AND (" +
                "  f.question_norm=$n OR EXISTS(" +
                "    SELECT 1 FROM faq_variants v WHERE v.faq_id=f.id AND v.variant_norm=$n)) " +
                "ORDER BY f.hits DESC, f.id LIMIT 1", ("$n", norm));
            if (rows.Count == 0) return null;
            var r = rows[0];
            var id = Convert.ToInt64(r["id"] ?? 0L);
            _db.Exec("UPDATE faq SET hits=hits+1 WHERE id=$i", ("$i", id));
            return new Entry(id,
                Convert.ToString(r["question"]) ?? "",
                Convert.ToString(r["answer"]) ?? "",
                Convert.ToString(r["sources_json"]) ?? "[]",
                Convert.ToInt32(r["hits"] ?? 0) + 1);
        }
        catch (Exception ex)
        {
            // FAQテーブル未作成・破損時は通常RAGへフォールバック（回答不能にしない）
            _log?.Warn($"faq lookup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>FAQを登録（同一正規化質問は更新）。別名も同時に登録する。</summary>
    public long Upsert(string question, string answer, string sourcesJson, IEnumerable<string>? variants = null,
        string status = "published", string tags = "")
    {
        var norm = QuestionNorm.Normalize(question);
        if (norm.Length < 2) throw new ArgumentException("question is too short", nameof(question));
        if (string.IsNullOrWhiteSpace(answer)) throw new ArgumentException("answer is empty", nameof(answer));

        var existing = _db.Scalar("SELECT id FROM faq WHERE question_norm=$n", ("$n", norm));
        long id;
        if (existing is null)
        {
            _db.Exec(
                "INSERT INTO faq(question, question_norm, answer, sources_json, tags, status) " +
                "VALUES($q,$n,$a,$s,$t,$st)",
                ("$q", question.Trim()), ("$n", norm), ("$a", answer.Trim()),
                ("$s", string.IsNullOrWhiteSpace(sourcesJson) ? "[]" : sourcesJson),
                ("$t", tags), ("$st", status));
            id = _db.LastInsertId();
        }
        else
        {
            id = Convert.ToInt64(existing);
            _db.Exec(
                "UPDATE faq SET question=$q, answer=$a, sources_json=$s, tags=$t, status=$st, " +
                "updated_at=datetime('now') WHERE id=$i",
                ("$q", question.Trim()), ("$a", answer.Trim()),
                ("$s", string.IsNullOrWhiteSpace(sourcesJson) ? "[]" : sourcesJson),
                ("$t", tags), ("$st", status), ("$i", id));
            _db.Exec("DELETE FROM faq_variants WHERE faq_id=$i", ("$i", id));
        }

        foreach (var v in variants ?? Enumerable.Empty<string>())
        {
            var vn = QuestionNorm.Normalize(v);
            if (vn.Length < 2 || vn == norm) continue;
            _db.Exec("INSERT OR IGNORE INTO faq_variants(faq_id, variant_norm) VALUES($i,$n)", ("$i", id), ("$n", vn));
        }
        return id;
    }

    public List<Entry> List(string? status = "published", int limit = 500) =>
        _db.Query(
            "SELECT id, question, answer, COALESCE(sources_json,'[]') AS sources_json, hits " +
            "FROM faq WHERE ($s IS NULL OR status=$s) ORDER BY updated_at DESC, id DESC LIMIT $l",
            ("$s", status), ("$l", limit))
        .Select(r => new Entry(Convert.ToInt64(r["id"] ?? 0L),
            Convert.ToString(r["question"]) ?? "",
            Convert.ToString(r["answer"]) ?? "",
            Convert.ToString(r["sources_json"]) ?? "[]",
            Convert.ToInt32(r["hits"] ?? 0)))
        .ToList();

    public bool Delete(long id)
    {
        _db.Exec("DELETE FROM faq_variants WHERE faq_id=$i", ("$i", id));
        _db.Exec("DELETE FROM faq WHERE id=$i", ("$i", id));
        return true;
    }

    /// <summary>JSON入出力（現場間のパック配布用）。sourcesはそのまま保持する。</summary>
    public static string ExportJson(IEnumerable<Entry> entries) =>
        JsonSerializer.Serialize(entries.Select(e => new
        {
            question = e.Question,
            answer = e.Answer,
            sources = JsonSerializer.Deserialize<object>(e.SourcesJson) ?? Array.Empty<object>(),
        }), new JsonSerializerOptions { WriteIndented = true });
}
