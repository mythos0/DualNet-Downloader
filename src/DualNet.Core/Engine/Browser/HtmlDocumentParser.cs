using HtmlAgilityPack;

namespace DualNet.Core.Engine.Browser;

/// <summary>
/// Turns a fetched HTML document into reader-view blocks (headings, paragraphs, bullets,
/// images, code) plus a de-duplicated list of absolute links — using the adapter-bound
/// pipeline's final URL as the base for relative references.
/// </summary>
public static class HtmlDocumentParser
{
    private const int MaxBlocks = 400;
    private const int MaxLinks = 500;
    private const int MaxParagraphChars = 4_000;
    private const int MaxTextChars = 300;

    private static readonly HashSet<string> SkippedTags = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "noscript", "template", "head", "iframe", "svg", "canvas", "form", "button", "select", "option", "input" };

    public static BrowserPage Parse(string html, string baseUrl)
    {
        var doc = new HtmlDocument();
        doc.OptionFixNestedTags = true;
        doc.OptionAutoCloseOnEnd = true;
        doc.LoadHtml(html ?? "");

        var baseUri = ResolveBaseUri(doc, baseUrl);
        var title = ExtractTitle(doc);

        var links = new List<BrowserLink>();
        var seenLinks = new HashSet<string>(StringComparer.Ordinal);
        CollectLinks(doc.DocumentNode, baseUri, links, also: null, seenLinks);

        var blocks = new List<PageBlock>();
        var paragraphLinks = new List<BrowserLink>();
        var text = new System.Text.StringBuilder();

        Walk(doc.DocumentNode, baseUri, blocks, paragraphLinks, text, links, seenLinks);

        FlushParagraph(blocks, paragraphLinks, text);
        if (blocks.Count == 0 && links.Count > 0)
        {
            // Nothing renderable (e.g. a link-list page) — still show the links.
            blocks.Add(new ParagraphBlock("", Array.Empty<BrowserLink>()));
        }

        return new BrowserPage(baseUri.AbsoluteUri, title, blocks, links);
    }

    private static Uri ResolveBaseUri(HtmlDocument doc, string baseUrl)
    {
        Uri baseUri = TryCreate(baseUrl) ?? new Uri("about:blank");
        var baseTag = doc.DocumentNode.SelectSingleNode("//base[@href]");
        if (baseTag is not null && TryCreate(baseTag.GetAttributeValue("href", "")) is { } tagUri &&
            !tagUri.IsFile)
        {
            try { baseUri = new Uri(baseUri, tagUri); } catch { }
        }
        return baseUri;
    }

    private static Uri? TryCreate(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var uri = new Uri(url, UriKind.RelativeOrAbsolute);
            return uri.IsAbsoluteUri ? uri : null;
        }
        catch { return null; }
    }

    private static string ExtractTitle(HtmlDocument doc)
    {
        var title = doc.DocumentNode.SelectSingleNode("//title");
        var t = Clean(title?.InnerText);
        if (!string.IsNullOrWhiteSpace(t)) return t.Length > MaxTextChars ? t[..MaxTextChars] : t;
        var h1 = doc.DocumentNode.SelectSingleNode("//h1");
        return Clean(h1?.InnerText) ?? "";
    }

    /// <summary>Collects anchors under <paramref name="node"/> into the global list and,
    /// when <paramref name="also"/> differs, into the paragraph-owned list as well.</summary>
    private static void CollectLinks(HtmlNode node, Uri baseUri, List<BrowserLink> links,
        List<BrowserLink>? also, HashSet<string> seen)
    {
        foreach (var anchor in node.Descendants("a"))
        {
            var href = anchor.GetAttributeValue("href", null);
            if (string.IsNullOrWhiteSpace(href))
                continue;
            var trimmed = href.Trim();
            if (trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("#"))
                continue;

            string absolute;
            try { absolute = new Uri(baseUri, trimmed).AbsoluteUri; }
            catch { continue; }
            if (!absolute.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !absolute.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!seen.Add(absolute))
                continue;

            var text = Clean(anchor.InnerText) ?? "";
            if (text.Length > MaxTextChars) text = text[..MaxTextChars];
            var link = new BrowserLink(absolute, text, LinkClassifier.IsFileLink(absolute));
            links.Add(link);
            also?.Add(link);
            if (links.Count >= MaxLinks)
                return;
        }
    }

    private static void Walk(HtmlNode node, Uri baseUri, List<PageBlock> blocks,
        List<BrowserLink> paragraphLinks, System.Text.StringBuilder text,
        List<BrowserLink> links, HashSet<string> seen)
    {
        if (blocks.Count >= MaxBlocks)
            return;

        if (node.NodeType == HtmlNodeType.Text)
        {
            AppendText(text, node.InnerText);
            return;
        }

        if (node.NodeType == HtmlNodeType.Document)
        {
            // The synthetic root — behave like a plain container.
            foreach (var child in node.ChildNodes)
                Walk(child, baseUri, blocks, paragraphLinks, text, links, seen);
            return;
        }

        if (node.NodeType != HtmlNodeType.Element)
            return;

        var name = node.Name.ToLowerInvariant();
        if (SkippedTags.Contains(name) || node.HasClass("hidden") ||
            node.GetAttributeValue("hidden", null) is not null ||
            node.GetAttributeValue("aria-hidden", null) == "true")
            return;

        switch (name)
        {
            case "br":
                AppendText(text, "\n");
                return;
            case "hr":
                FlushParagraph(blocks, paragraphLinks, text);
                blocks.Add(new DividerBlock());
                return;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                FlushParagraph(blocks, paragraphLinks, text);
                var headingText = Clean(node.InnerText) ?? "";
                if (headingText.Length > 0)
                    blocks.Add(new HeadingBlock(Math.Min(3, name[1] - '0'), headingText));
                return;
            case "p":
                FlushParagraph(blocks, paragraphLinks, text);
                CollectLinks(node, baseUri, links, paragraphLinks, seen);
                AppendText(text, Clean(node.InnerText) ?? "");
                FlushParagraph(blocks, paragraphLinks, text);
                return;
            case "blockquote":
                FlushParagraph(blocks, paragraphLinks, text);
                var quote = Clean(node.InnerText) ?? "";
                if (quote.Length > 0)
                    blocks.Add(new QuoteBlock(quote.Length > MaxParagraphChars ? quote[..MaxParagraphChars] : quote));
                return;
            case "pre":
                FlushParagraph(blocks, paragraphLinks, text);
                var code = CleanPre(node.InnerText);
                if (code.Length > 0)
                    blocks.Add(new PreBlock(code.Length > MaxParagraphChars ? code[..MaxParagraphChars] : code));
                return;
            case "li":
                FlushParagraph(blocks, paragraphLinks, text);
                CollectLinks(node, baseUri, links, paragraphLinks, seen);
                var liText = Clean(node.InnerText) ?? "";
                if (liText.Length > 0)
                    blocks.Add(new BulletBlock("•  " + liText));
                return;
            case "img":
                FlushParagraph(blocks, paragraphLinks, text);
                var src = node.GetAttributeValue("src", null) ??
                          node.GetAttributeValue("data-src", null);
                if (!string.IsNullOrWhiteSpace(src))
                {
                    try
                    {
                        var absolute = new Uri(baseUri, src.Trim()).AbsoluteUri;
                        if (absolute.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            blocks.Add(new ImageBlock(absolute, Clean(node.GetAttributeValue("alt", "")) ?? ""));
                    }
                    catch { }
                }
                return;
            case "table":
                FlushParagraph(blocks, paragraphLinks, text);
                WalkTable(node, blocks, paragraphLinks, text);
                return;
            default:
                // Generic container / inline element: descend, keeping the current
                // paragraph open so inline text and links flow together.
                CollectLinks(node, baseUri, links, paragraphLinks, seen);
                foreach (var child in node.ChildNodes)
                    Walk(child, baseUri, blocks, paragraphLinks, text, links, seen);
                return;
        }
    }

    private static void WalkTable(HtmlNode table, List<PageBlock> blocks,
        List<BrowserLink> paragraphLinks, System.Text.StringBuilder text)
    {
        foreach (var row in table.Descendants("tr").Take(60))
        {
            var cells = row.Descendants().Where(n => n.Name is "td" or "th")
                .Select(n => Clean(n.InnerText) ?? "")
                .Where(c => c.Length > 0)
                .Take(8)
                .ToList();
            if (cells.Count == 0) continue;
            var line = string.Join("   ·   ", cells);
            blocks.Add(new ParagraphBlock(line.Length > MaxParagraphChars ? line[..MaxParagraphChars] : line,
                Array.Empty<BrowserLink>()));
            if (blocks.Count >= MaxBlocks) return;
        }
    }

    /// <summary>Commits the open paragraph (if any) into the block list.</summary>
    private static void FlushParagraph(List<PageBlock> blocks, List<BrowserLink> paragraphLinks,
        System.Text.StringBuilder text)
    {
        if (blocks.Count >= MaxBlocks)
        {
            text.Clear();
            paragraphLinks.Clear();
            return;
        }

        var body = text.ToString();
        text.Clear();
        var ownedLinks = paragraphLinks.Distinct().ToList();
        paragraphLinks.Clear();

        // Split on manual line breaks (from <br>) so text stays readable.
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            var capped = line.Length > MaxParagraphChars ? line[..MaxParagraphChars] : line;
            blocks.Add(new ParagraphBlock(capped, ownedLinks));
            if (blocks.Count >= MaxBlocks) return;
        }
    }

    private static void AppendText(System.Text.StringBuilder text, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var value = System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ");
        if (value.Length == 0) return;
        if (text.Length > 0 && text[^1] != '\n' && text[^1] != ' ')
            text.Append(' ');
        text.Append(value);
        if (text.Length > MaxParagraphChars * 2)
            text.Remove(MaxParagraphChars * 2, text.Length - MaxParagraphChars * 2);
    }

    private static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var decoded = HtmlEntity.DeEntitize(raw);
        var value = System.Text.RegularExpressions.Regex.Replace(decoded, @"\s+", " ").Trim();
        return value.Length == 0 ? null : value;
    }

    private static string CleanPre(string raw)
    {
        var decoded = HtmlEntity.DeEntitize(raw ?? "");
        return decoded.Replace("\r\n", "\n").TrimEnd();
    }
}
