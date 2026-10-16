using System.Text.Json;

namespace DualNet.Core.Agent;

/// <summary>Robust JSON extraction from LLM replies (fences, prose, trailing junk).</summary>
public static class AgentJson
{
    /// <summary>
    /// Finds the first balanced JSON object in <paramref name="text"/>, ignoring string literals.
    /// Handles ```json fences and chatty preambles that small models love to add.
    /// </summary>
    public static string? ExtractObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Fast path: clean JSON already.
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{'))
        {
            var candidate = BalancedSlice(trimmed, 0);
            if (candidate is not null && IsValid(candidate)) return candidate;
        }

        // Strip markdown fences if present.
        var fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = text.IndexOf('\n', fence);
            var endFence = text.IndexOf("```", start + 1, StringComparison.Ordinal);
            if (start > 0 && endFence > start)
                text = text[(start + 1)..endFence];
        }

        int brace = text.IndexOf('{');
        while (brace >= 0)
        {
            var candidate = BalancedSlice(text, brace);
            if (candidate is not null && IsValid(candidate)) return candidate;
            brace = text.IndexOf('{', brace + 1);
        }
        return null;
    }

    private static string? BalancedSlice(string text, int start)
    {
        int depth = 0;
        bool inString = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (c == '\\') i++;               // skip escaped char
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return text.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    private static bool IsValid(string candidate)
    {
        try
        {
            using var _ = JsonDocument.Parse(candidate);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>Chat client abstraction so tests can stub the model (Ollama in production).</summary>
public interface IAgentChatClient
{
    string Model { get; }
    /// <summary>Single-shot chat completion; returns the assistant message content.</summary>
    Task<string> ChatAsync(string systemPrompt, string userMessage, CancellationToken ct);
    /// <summary>True when the endpoint answers and the model is available.</summary>
    Task<OllamaStatus> CheckStatusAsync(CancellationToken ct);
    /// <summary>Releases the model weights (RAM/VRAM) immediately.</summary>
    Task UnloadAsync(CancellationToken ct);
}

public sealed record OllamaStatus(bool Reachable, bool ModelAvailable, string? Error, List<string> AvailableModels);
