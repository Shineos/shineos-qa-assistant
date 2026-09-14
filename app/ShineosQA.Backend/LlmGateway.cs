using System.Text;
using System.Text.Json;

namespace ShineosQA.Backend;

/// <summary>OpenAI互換APIクライアント（llama-server向け）。rerank契約: results[{index, relevance_score}]（実測: phase0-report §2.3）</summary>
public sealed class LlmGateway
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<float[][]> EmbedAsync(int port, string[] texts, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { input = texts });
        using var resp = await _http.PostAsync($"http://127.0.0.1:{port}/v1/embeddings",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("data").EnumerateArray().OrderBy(d => d.GetProperty("index").GetInt32())
            .Select(d => d.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToArray();
    }

    public sealed record RankResult(int Index, double Score);

    public async Task<List<RankResult>> RerankAsync(int port, string query, IReadOnlyList<string> docs, int topN, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { query, documents = docs, top_n = topN });
        using var resp = await _http.PostAsync($"http://127.0.0.1:{port}/v1/rerank",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<RankResult>();
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
            list.Add(new RankResult(r.GetProperty("index").GetInt32(), r.GetProperty("relevance_score").GetDouble()));
        return list.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>SSEストリーム。deltaのcontentをonDeltaへ、最終usage/timingsを返す</summary>
    public async Task ChatStreamAsync(int port, IReadOnlyList<(string role, string content)> messages, double temperature, int maxTokens,
        Func<string, Task> onDelta, CancellationToken ct)
    {
        var msgs = messages.Select(m => new { role = m.role, content = m.content });
        // Qwen3ハイブリッド思考モデルの <think> 推論を無効化（Instruct-2507系は影響なし）。
        // 思考トークンは reasoning_content に分離されUIに見えない分、純増の遅延になるため。
        var body = JsonSerializer.Serialize(new
        {
            model = "local",
            stream = true,
            stream_options = new { include_usage = true },
            temperature,
            max_tokens = maxTokens,
            chat_template_kwargs = new { enable_thinking = false },
            messages = msgs,
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/chat/completions");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ")) continue;
            var payload = line.Substring(6);
            if (payload == "[DONE]") break;
            using var chunk = JsonDocument.Parse(payload);
            if (chunk.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var s = c.GetString();
                    if (!string.IsNullOrEmpty(s)) await onDelta(s);
                }
            }
        }
    }
}
