using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DualNet.Core.Agent;

/// <summary>
/// Client for a LOCAL Ollama server (default http://localhost:11434) running
/// qwen3.5:2b. Uses JSON-mode replies, small temperature and a compact context for
/// speed on small models. keep_alive is managed so the model is released when idle.
/// </summary>
public sealed class OllamaClient : IAgentChatClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public string Model { get; }
    public int TimeoutSeconds { get; set; } = 120;
    public string KeepAlive { get; set; } = "10m";
    public int NumCtx { get; set; } = 8192;
    public int NumPredict { get; set; } = 400;
    public double Temperature { get; set; } = 0.2;

    public OllamaClient(string? baseUrl = null, string? model = null)
    {
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434" : baseUrl.TrimEnd('/'));
        Model = string.IsNullOrWhiteSpace(model) ? "qwen3.5:2b" : model.Trim();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
    }

    public async Task<OllamaStatus> CheckStatusAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/api/tags", ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var names = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in models.EnumerateArray())
                    {
                        if (m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                            names.Add(n.GetString() ?? "");
                    }
                }
            }
            catch { }
            bool available = names.Any(n =>
                n.Equals(Model, StringComparison.OrdinalIgnoreCase) ||
                n.Split(':')[0].Equals(Model.Split(':')[0], StringComparison.OrdinalIgnoreCase));
            return new OllamaStatus(true, available, null, names);
        }
        catch (Exception ex)
        {
            return new OllamaStatus(false, false, FriendlyError(ex), new List<string>());
        }
    }

    public async Task<string> ChatAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        var payload = new
        {
            model = Model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage },
            },
            stream = false,
            format = "json",
            keep_alive = KeepAlive,
            options = new
            {
                temperature = Temperature,
                num_ctx = NumCtx,
                num_predict = NumPredict,
            },
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{_baseUrl}/api/chat", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var c))
                return c.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("error", out var err))
                throw new InvalidOperationException("Ollama: " + err.GetString());
            return "";
        }
        catch (JsonException)
        {
            return body;   // some builds reply with raw text
        }
    }

    /// <summary>Asks Ollama to release the model weights immediately (frees RAM/VRAM).</summary>
    public async Task UnloadAsync(CancellationToken ct)
    {
        try
        {
            var payload = new { model = Model, keep_alive = 0 };
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{_baseUrl}/api/generate", content, ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    private static string FriendlyError(Exception ex) =>
        ex is HttpRequestException
            ? "Ollama is not reachable. Start it (or install from ollama.com) and pull the model."
            : ex.Message;
}

/// <summary>
/// The agent's ReAct prompt: strict JSON-only replies, a small action vocabulary and
/// explicit confirmation rules. Sized for small local models (2B-class).
/// </summary>
public static class AgentPrompt
{
    public const string System = """
        You are the DualNet browser agent. Reply with ONLY one JSON object — no prose, no markdown.
        Schema: {"thought":"one short sentence","action":{"type":"...","...args},"needs_confirmation":false}
        Action types:
        - {"type":"search","query":"..."}                 — open DuckDuckGo results in a new tab
        - {"type":"navigate","url":"https://...","tab":"new"|"active"}
        - {"type":"read_page","max_chars":4000}           — returns the visible page text
        - {"type":"list_links","filter":"regex"}          — returns matching links (text + url)
        - {"type":"extract_table","index":0}              — returns the Nth table on the page
        - {"type":"download","urls":["https://..."]}      — enqueue file downloads
        - {"type":"open_tabs","urls":["https://..."],"arrange":"split"|"stack"}
        - {"type":"recall_tabs"}                          — returns the user's recent/bookmarked tabs
        - {"type":"click","text_or_selector":"Sign in"}   — click by visible text or CSS selector (needs confirmation)
        - {"type":"type","selector":"#email","text":"...","submit":true} (needs confirmation)
        - {"type":"finish","summary":"what was accomplished"}
        Rules:
        - ONE action per reply.
        - Inspect before acting: use search, read_page, list_links first.
        - For purchases, payments, form submissions, deletions, or anything irreversible set "needs_confirmation":true.
        - When the task is complete, reply with finish and a short summary.
        """;

    public static string UserTurn(string task, IReadOnlyList<string> history, string observation, int turn, int maxTurns)
    {
        var sb = new StringBuilder();
        sb.Append("TASK: ").Append(task.Trim()).Append('\n');
        if (history.Count > 0)
        {
            sb.Append("ACTIONS SO FAR:\n");
            foreach (var h in history.TakeLast(8))
                sb.Append("- ").Append(h).Append('\n');
        }
        sb.Append("OBSERVATION OF LAST ACTION:\n").Append(observation).Append('\n');
        sb.Append($"Turn {turn} of {maxTurns}. Reply with the next JSON action only.");
        return sb.ToString();
    }
}
