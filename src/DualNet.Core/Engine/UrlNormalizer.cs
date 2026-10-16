namespace DualNet.Core.Engine;

/// <summary>
/// Normalizes user-typed input into an absolute URL, the way a browser address bar does:
/// trims whitespace, inserts https:// when the scheme is missing, and returns null for
/// anything that cannot be a usable link. Non-http schemes (ftp://, magnet:, …) are
/// returned unchanged so callers can reject them with a precise error message.
/// </summary>
public static class UrlNormalizer
{
    /// <summary>Returns the normalized absolute URL, or null when the input is unusable.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var text = input.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = text.StartsWith("//", StringComparison.Ordinal)
                ? "https:" + text
                : "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return null;

        // A usable host: dotted domain, IP literal, or "localhost".
        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
            return null;
        if (!host.Contains('.', StringComparison.Ordinal) &&
            !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
            Uri.CheckHostName(host) != UriHostNameType.IPv4 &&
            Uri.CheckHostName(host) != UriHostNameType.IPv6)
            return null;

        return uri.ToString();
    }
}
