using System.Text.Json;

namespace DualNet.Core.Agent;

/// <summary>One action the agent wants to perform (parsed from the model's JSON reply).</summary>
public sealed class AgentAction
{
    public string Type { get; set; } = "";
    public string Query { get; set; } = "";
    public string Url { get; set; } = "";
    public string Tab { get; set; } = "new";
    public string Filter { get; set; } = "";
    public int Index { get; set; }
    public List<string> Urls { get; set; } = new();
    public string Arrange { get; set; } = "";
    public string TextOrSelector { get; set; } = "";
    public string Selector { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Submit { get; set; }
    public int MaxChars { get; set; } = 6000;
    public string Summary { get; set; } = "";
    public bool NeedsConfirmation { get; set; }
    public string Thought { get; set; } = "";

    public static readonly string[] KnownTypes =
    {
        "search", "navigate", "read_page", "list_links", "extract_table",
        "download", "open_tabs", "recall_tabs", "click", "type", "finish",
    };

    /// <summary>Parses the model reply into an action; tolerates fenced JSON and surrounding prose.</summary>
    public static AgentAction? Parse(string reply)
    {
        var json = AgentJson.ExtractObject(reply);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var action = new AgentAction();
            if (root.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.String)
                action.Thought = thought.GetString() ?? "";
            if (root.TryGetProperty("needs_confirmation", out var nc))
                action.NeedsConfirmation = nc.ValueKind == JsonValueKind.True ||
                                           (nc.ValueKind == JsonValueKind.String && nc.GetString() == "true");
            JsonElement a = root;
            if (root.TryGetProperty("action", out var inner) && inner.ValueKind == JsonValueKind.Object)
                a = inner;
            if (a.ValueKind != JsonValueKind.Object) return null;

            if (a.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                action.Type = t.GetString() ?? "";
            else if (a.TryGetProperty("action", out var t2) && t2.ValueKind == JsonValueKind.String)
                action.Type = t2.GetString() ?? "";
            action.Type = action.Type.Trim().ToLowerInvariant();

            TryStr(a, "query", v => action.Query = v);
            TryStr(a, "url", v => action.Url = v);
            TryStr(a, "tab", v => action.Tab = v);
            TryStr(a, "filter", v => action.Filter = v);
            TryStr(a, "arrange", v => action.Arrange = v);
            TryStr(a, "text_or_selector", v => action.TextOrSelector = v);
            TryStr(a, "selector", v => action.Selector = v);
            TryStr(a, "text", v => action.Text = v);
            TryStr(a, "summary", v => action.Summary = v);
            TryInt(a, "index", v => action.Index = v);
            TryInt(a, "max_chars", v => action.MaxChars = v);
            if (a.TryGetProperty("submit", out var sub))
                action.Submit = sub.ValueKind == JsonValueKind.True;
            if (a.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
            {
                foreach (var u in urls.EnumerateArray())
                {
                    var s = u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(s)) action.Urls.Add(s!.Trim());
                }
            }
            if (action.Urls.Count == 0 && !string.IsNullOrWhiteSpace(action.Url))
                action.Urls.Add(action.Url.Trim());
            return action;
        }
        catch
        {
            return null;
        }
    }

    private static void TryStr(JsonElement el, string name, Action<string> set)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrEmpty(s)) set(s);
        }
    }

    private static void TryInt(JsonElement el, string name, Action<int> set)
    {
        if (el.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) set(i);
            else if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var i2)) set(i2);
        }
    }

    public string Describe() => Type switch
    {
        "search" => $"Search DuckDuckGo for '{Query}'",
        "navigate" => $"Open {Url} ({Tab} tab)",
        "read_page" => "Read the current page",
        "list_links" => $"List links matching '{Filter}'",
        "extract_table" => $"Extract table #{Index}",
        "download" => $"Download {Urls.Count} file(s)",
        "open_tabs" => $"Open {Urls.Count} tab(s) {(Arrange == "split" ? "side by side" : "")}".Trim(),
        "recall_tabs" => "Look up your recent/bookmarked tabs",
        "click" => $"Click '{TextOrSelector}'",
        "type" => $"Type into '{Selector}'{(Submit ? " and submit" : "")}",
        "finish" => "Finish",
        _ => Type,
    };
}

/// <summary>Result of executing one action in the real browser.</summary>
public sealed class AgentObservation
{
    public bool Ok { get; init; } = true;
    public string Text { get; init; } = "";
    public List<LinkHit>? Links { get; init; }
    public List<string[]>? Table { get; init; }
    public List<string>? OpenedUrls { get; init; }

    /// <summary>Compact render for the model prompt.</summary>
    public string Render(int maxChars)
    {
        string body;
        if (Links is { Count: > 0 })
            body = string.Join("\n", Links.Take(50).Select(l => $"- {l.Text} -> {l.Url}"));
        else if (Table is { Count: > 0 })
            body = string.Join("\n", Table.Take(20).Select(r => "| " + string.Join(" | ", r) + " |"));
        else
            body = Text;
        if (body.Length > maxChars) body = body[..maxChars] + " ...[truncated]";
        return (Ok ? "" : "ERROR: ") + (body.Length == 0 ? "(nothing)" : body);
    }
}

public sealed record LinkHit(string Text, string Url);

/// <summary>Decides whether an action is consequential and must be confirmed by the user.</summary>
public static class ConfirmationClassifier
{
    private static readonly string[] RiskUrlWords =
    {
        "checkout", "purchase", "payment", "pay", "billing", "confirm-order", "placeorder",
        "delete", "remove", "unsubscribe", "submit", "buy",
    };

    private static readonly string[] RiskTaskWords =
    {
        "buy", "purchase", "checkout", "pay ", "payment", "delete", "remove ", "unsubscribe",
        "submit", "send ", "post ", "place order", "cancel ",
    };

    /// <summary>LLM flags + heuristic URL/selector/task-text analysis.</summary>
    public static bool IsConsequential(AgentAction action, string pageUrl = "", string taskText = "")
    {
        if (action.NeedsConfirmation) return true;
        if (action.Type is "click" or "type") return true;   // any direct page interaction is gated

        var task = taskText.ToLowerInvariant();
        if (RiskTaskWords.Any(w => task.Contains(w, StringComparison.Ordinal))) return true;

        var haystack = string.Join(' ', action.Url, pageUrl, action.TextOrSelector, action.Selector,
            action.Query).ToLowerInvariant();
        if (RiskUrlWords.Any(w => haystack.Contains(w, StringComparison.Ordinal))) return true;

        return false;
    }
}
