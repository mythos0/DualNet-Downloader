using System.Text;

namespace DualNet.Core.Engine;

/// <summary>
/// Extracts HTTP Basic credentials embedded in a URL's userinfo section
/// (https://user:pass@server/file) and returns the cleaned URL. DownloadJob
/// turns them into an Authorization header — IDM-style credentials without a dialog.
/// </summary>
public static class AuthUrlParser
{
    public readonly record struct UrlAuth(string Url, string? UserName, string? Password);

    public static UrlAuth Parse(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new UrlAuth(url ?? "", null, null);

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            return new UrlAuth(url, null, null);

        string user, pass;
        try
        {
            var parts = uri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        catch
        {
            return new UrlAuth(url, null, null);
        }

        string cleaned;
        try
        {
            var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
            cleaned = builder.Uri.ToString();
        }
        catch
        {
            return new UrlAuth(url, null, null);
        }

        return new UrlAuth(cleaned, user, pass);
    }

    /// <summary>Builds the Basic authorization header value, or null when no credentials.</summary>
    public static string? BasicHeaderValue(string? user, string? password) =>
        string.IsNullOrEmpty(user) ? null : Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{user}:{password ?? ""}"));
}
