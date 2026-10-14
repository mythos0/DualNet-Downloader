using System.Net;
using DualNet.Core.Net;

namespace DualNet.Core.Engine.Browser;

/// <summary>
/// The browsing pipeline of one network tab: fetches pages through an adapter-bound
/// HttpClient so the traffic provably flows through that tab's network, parses HTML
/// into a reader view, and hands non-HTML responses straight to the download engine.
/// </summary>
public static class BrowserService
{
    public const int PageTimeoutSeconds = 25;
    private const long MaxHtmlBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Fetches <paramref name="url"/>. HTML → parsed <see cref="BrowserPage"/>;
    /// anything else → <see cref="BrowserResult.IsDownload"/> with the suggested file name.
    /// Never throws — failures come back as <see cref="BrowserResult.Error"/>.
    /// </summary>
    public static async Task<BrowserResult> FetchAsync(
        string url, IReadOnlyList<IPAddress> binds, CancellationToken ct)
    {
        var normalized = UrlNormalizer.Normalize(url);
        if (normalized is null)
            return new BrowserResult { RequestedUrl = url, Error = $"'{Trim(url)}' is not a valid http(s) address." };
        if (binds.Count == 0)
            return new BrowserResult { RequestedUrl = normalized, Error = "No network adapter is online for this tab." };

        var client = HttpConnectionFactory.Create(binds[0]);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(PageTimeoutSeconds));

            using var response = await client.GetAsync(normalized, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? normalized;
            var contentType = (response.Content.Headers.ContentType?.MediaType ?? "").ToLowerInvariant();
            var disposition = (response.Content.Headers.ContentDisposition?.FileNameStar
                               ?? response.Content.Headers.ContentDisposition?.FileName)
                ?.Trim().Trim('\"');

            if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent)
            {
                int code = (int)response.StatusCode;
                return new BrowserResult
                {
                    RequestedUrl = normalized,
                    FinalUrl = finalUrl,
                    StatusCode = code,
                    Error = code switch
                    {
                        404 => "Page not found (404).",
                        403 => "Server refused the request (403).",
                        429 => "Too many requests (429) — slow down and retry.",
                        >= 500 => $"Server error ({code}).",
                        _ => $"Request failed (HTTP {code})."
                    }
                };
            }

            bool isHtml = contentType.Contains("text/html") || contentType.Contains("xhtml") ||
                          (contentType.Length == 0 && disposition is null &&
                           !LinkClassifier.IsFileLink(finalUrl));

            if (!isHtml)
            {
                return new BrowserResult
                {
                    RequestedUrl = normalized,
                    FinalUrl = finalUrl,
                    StatusCode = (int)response.StatusCode,
                    IsDownload = true,
                    DownloadFileName = string.IsNullOrWhiteSpace(disposition)
                        ? FileNameGuesser.FromUrl(finalUrl)
                        : FileNameGuesser.Sanitize(disposition) ?? FileNameGuesser.FromUrl(finalUrl),
                    DownloadSize = response.Content.Headers.ContentLength
                };
            }

            // HTML: read (bounded) and parse.
            var bytes = new System.IO.MemoryStream();
            await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxHtmlBytes)
                        break;   // huge page — render what we have
                    await bytes.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
            }

            var html = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
            BrowserPage page;
            try
            {
                page = HtmlDocumentParser.Parse(html, finalUrl);
            }
            catch (Exception ex)
            {
                return new BrowserResult
                {
                    RequestedUrl = normalized,
                    FinalUrl = finalUrl,
                    StatusCode = (int)response.StatusCode,
                    Error = $"Could not render the page: {ex.Message}"
                };
            }

            return new BrowserResult
            {
                RequestedUrl = normalized,
                FinalUrl = finalUrl,
                StatusCode = (int)response.StatusCode,
                Page = page
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new BrowserResult { RequestedUrl = normalized, Error = "Stopped." };
        }
        catch (OperationCanceledException)
        {
            return new BrowserResult
            {
                RequestedUrl = normalized,
                Error = $"Timed out after {PageTimeoutSeconds}s — the site did not respond through this network."
            };
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException se)
        {
            return new BrowserResult
            {
                RequestedUrl = normalized,
                Error = se.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.HostNotFound => "Address not found (DNS) — check the spelling.",
                    System.Net.Sockets.SocketError.NetworkUnreachable or
                    System.Net.Sockets.SocketError.HostUnreachable => "This network cannot reach the site.",
                    System.Net.Sockets.SocketError.ConnectionRefused => "The site refused the connection.",
                    _ => $"Network error: {se.SocketErrorCode}."
                }
            };
        }
        catch (HttpRequestException ex)
        {
            return new BrowserResult { RequestedUrl = normalized, Error = $"Connection failed: {ex.Message}" };
        }
        catch (Exception ex)
        {
            return new BrowserResult { RequestedUrl = normalized, Error = ex.Message };
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>Decides what a typed address bar input means: page URL, file URL, or search query.</summary>
    public static string ResolveInput(string input, int searchEngine)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0)
            return "";

        // Explicit scheme or domain-ish token → URL; anything else → search.
        bool looksLikeUrl = text.Contains("://", StringComparison.Ordinal) ||
                            (!text.Contains(' ') && (text.Contains('.') || text.Contains(':')) &&
                             !text.EndsWith(".", StringComparison.Ordinal));
        if (!looksLikeUrl)
            return SearchEngines.BuildUrl(searchEngine, text);

        // Files download immediately; pages browse. Normalization decides validity.
        return UrlNormalizer.Normalize(text) ?? SearchEngines.BuildUrl(searchEngine, text);
    }

    private static string Trim(string s) => s.Length <= 42 ? s : s[..39] + "…";
}
