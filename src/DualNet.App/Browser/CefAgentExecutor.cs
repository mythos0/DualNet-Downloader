using System.Text;
using System.Text.Json;

using CefSharp;
using CefSharp.DevTools.Page;
using DualNet.App.Services;
using DualNet.App.ViewModels;
using DualNet.Core.Agent;
using DualNet.Core.Engine.Browser;

namespace DualNet.App.Browser;

/// <summary>
/// Runs agent actions against the real Chromium browser: navigation, page reading,
/// link discovery, table extraction, downloads and (confirmation-gated) clicks/typing.
/// All DOM work goes through JavaScript evaluation on the active tab.
/// </summary>
public sealed class CefAgentExecutor : IAgentActionExecutor
{
    private readonly MainViewModel _vm;

    public CefAgentExecutor(MainViewModel vm) => _vm = vm;

    public string CurrentUrl() => _vm.ActiveTab?.Url ?? "";

    public async Task<AgentObservation> ExecuteAsync(AgentAction action, CancellationToken ct)
    {
        switch (action.Type)
        {
            case "search":
                var searchUrl = SearchEngines.BuildUrl(
                    AppBootstrapper.Settings.SearchEngine, action.Query);
                _vm.CreateTab(searchUrl);
                return new AgentObservation { Text = $"Opened search results for '{action.Query}'" };

            case "navigate":
                var url = NormalizeUrl(action.Url);
                if (url is null)
                    return new AgentObservation { Ok = false, Text = "Invalid URL" };
                if (action.Tab == "active" && _vm.ActiveTab is not null)
                    _vm.Navigate(_vm.ActiveTab, url);
                else
                    _vm.CreateTab(url);
                await WaitLoadedAsync(ct);
                return new AgentObservation { Text = $"Opened {url}" };

            case "read_page":
                var text = await EvalJsonAsync(JsReadPage(action.MaxChars), ct) ?? "";
                return new AgentObservation { Text = text.Length == 0 ? "(empty page)" : text };

            case "list_links":
                var raw = await EvalJsonAsync(JsListLinks(action.Filter), ct);
                var links = new List<LinkHit>();
                if (raw is not null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            var t = item.TryGetProperty("t", out var te) ? te.GetString() ?? "" : "";
                            var u = item.TryGetProperty("u", out var ue) ? ue.GetString() ?? "" : "";
                            if (u.Length > 0) links.Add(new LinkHit(t, u));
                        }
                    }
                    catch { }
                }
                return new AgentObservation
                {
                    Links = links,
                    Text = links.Count == 0 ? "No matching links on this page." : $"{links.Count} matching links.",
                };

            case "extract_table":
                var tableJson = await EvalJsonAsync(JsExtractTable(Math.Max(0, action.Index)), ct);
                var table = new List<string[]>();
                if (!string.IsNullOrEmpty(tableJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(tableJson);
                        foreach (var row in doc.RootElement.EnumerateArray())
                        {
                            var cells = row.EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
                            table.Add(cells);
                        }
                    }
                    catch { }
                }
                return new AgentObservation
                {
                    Table = table,
                    Text = table.Count == 0 ? "No table found at that index." : $"{table.Count} rows extracted.",
                };

            case "download":
                var started = new List<string>();
                foreach (var dl in action.Urls.Take(10))
                {
                    var u = NormalizeUrl(dl);
                    if (u is null) continue;
                    var tab = _vm.ActiveTab;
                    var item = _vm.EnqueueDownload(u, GuessName(u),
                        tab?.NetScope ?? "all",
                        string.IsNullOrWhiteSpace(tab?.Proxy) ? null : tab!.Proxy);
                    if (item is not null) started.Add(u);
                }
                return new AgentObservation
                {
                    Text = $"Enqueued {started.Count} download(s) on the DualNet engine.",
                    OpenedUrls = started,
                };

            case "open_tabs":
                var opened = new List<string>();
                var tabs = new List<TabViewModel>();
                foreach (var u in action.Urls.Take(6))
                {
                    var nu = NormalizeUrl(u);
                    if (nu is null) continue;
                    tabs.Add(_vm.CreateTab(nu, background: true));
                    opened.Add(nu);
                    await WaitLoadedAsync(ct);
                }
                if (opened.Count == 0)
                    return new AgentObservation { Ok = false, Text = "No valid URLs to open." };

                if (string.Equals(action.Arrange, "split", StringComparison.OrdinalIgnoreCase) && tabs.Count >= 2)
                    _vm.ArrangeSideBySide(tabs.Take(2).ToList());
                else
                    _vm.ActivateTab(tabs[^1]);
                return new AgentObservation { Text = $"Opened {opened.Count} tab(s).", OpenedUrls = opened };

            case "recall_tabs":
                var recents = new List<LinkHit>();
                foreach (var v in AppBootstrapper.BrowserStore.Visited.Take(25))
                    recents.Add(new LinkHit(v.Title, v.Url));
                foreach (var b in AppBootstrapper.BrowserStore.Bookmarks.Take(15))
                    if (recents.All(r => r.Url != b.Url)) recents.Add(new LinkHit(b.Title, b.Url));
                return new AgentObservation
                {
                    Links = recents,
                    Text = $"{recents.Count} recent/bookmarked tabs.",
                };

            case "click":
                var clickJson = await EvalJsonAsync(JsClick(action.TextOrSelector), ct);
                var ok = clickJson?.Contains("\"clicked\":true", StringComparison.Ordinal) == true;
                return ok
                    ? new AgentObservation { Text = $"Clicked '{action.TextOrSelector}'." }
                    : new AgentObservation { Ok = false, Text = $"Could not find or click '{action.TextOrSelector}'." };

            case "type":
                var typeJson = await EvalJsonAsync(JsType(action.Selector, action.Text, action.Submit), ct);
                var typed = typeJson?.Contains("\"typed\":true", StringComparison.Ordinal) == true;
                return typed
                    ? new AgentObservation { Text = $"Typed into '{action.Selector}'{(action.Submit ? " and submitted" : "")}." }
                    : new AgentObservation { Ok = false, Text = $"Could not type into '{action.Selector}'." };

            default:
                return new AgentObservation { Ok = false, Text = $"Unknown action '{action.Type}'." };
        }
    }

    // ---------------- JS snippets (plain templates — braces stay literal) ---------------------------

    private static string JsReadPage(int maxChars) => """
        (function(){
            var el = document.body;
            var t = el ? (el.innerText || el.textContent || '') : '';
            t = t.replace(/\n{3,}/g,'\n\n').trim();
            return JSON.stringify(t.substring(0, __MAX__));
        })()
        """.Replace("__MAX__", Math.Clamp(maxChars, 200, 12000).ToString());

    private static string JsListLinks(string filter)
    {
        var pattern = string.IsNullOrWhiteSpace(filter) ? "" : filter.Trim();
        return """
        (function(){
            var re = null;
            try { if ("__FILTER__".length) re = new RegExp("__FILTER__", "i"); } catch(e) {}
            var out = [], seen = {};
            var anchors = document.querySelectorAll('a[href]');
            for (var i = 0; i < anchors.length && out.length < 80; i++) {
                var a = anchors[i];
                var href = a.href || '';
                if (!/^https?:/i.test(href)) continue;
                if (seen[href]) continue; seen[href] = 1;
                var text = (a.innerText || a.textContent || a.title || '').trim().replace(/\s+/g,' ').substring(0,120);
                if (re && !re.test(text) && !re.test(href)) continue;
                out.push({t: text, u: href});
            }
            return JSON.stringify(out);
        })()
        """.Replace("__FILTER__", EscapeJs(pattern));
    }

    private static string JsExtractTable(int index) => """
        (function(){
            var tables = document.querySelectorAll('table');
            if (!tables.length) return '[]';
            var tb = tables[Math.min(__INDEX__, tables.length - 1)];
            var rows = [];
            for (var r = 0; r < tb.rows.length && rows.length < 40; r++) {
                var cells = [];
                for (var c = 0; c < tb.rows[r].cells.length && cells.length < 12; c++)
                    cells.push((tb.rows[r].cells[c].innerText || '').trim().replace(/\s+/g,' ').substring(0,80));
                rows.push(cells);
            }
            return JSON.stringify(rows);
        })()
        """.Replace("__INDEX__", Math.Max(0, index).ToString());

    private static string JsClick(string textOrSelector) => """
        (function(){
            var target = null;
            var sel = "__SEL__";
            if (!sel) return JSON.stringify({clicked:false});
            try { target = document.querySelector(sel); } catch(e) {}
            if (!target) {
                var wanted = sel.toLowerCase();
                var els = document.querySelectorAll('a,button,input[type=submit],input[type=button],[role=button]');
                for (var i = 0; i < els.length; i++) {
                    var t = ((els[i].innerText || els[i].value || '') + '').trim().toLowerCase();
                    if (t && (t === wanted || t.indexOf(wanted) >= 0)) { target = els[i]; break; }
                }
            }
            if (!target) return JSON.stringify({clicked:false});
            target.scrollIntoView({block:'center'});
            target.click();
            return JSON.stringify({clicked:true});
        })()
        """.Replace("__SEL__", EscapeJs(textOrSelector.Trim()));

    private static string JsType(string selector, string text, bool submit)
    {
        var submitJs = submit
            ? "var f = el.closest('form'); if (f) { if (f.requestSubmit) f.requestSubmit(); else f.submit(); }"
            : "";
        return """
        (function(){
            var el = null;
            var sel = "__SEL__";
            try { el = document.querySelector(sel); } catch(e) {}
            if (!el) {
                var inputs = document.querySelectorAll('input,textarea');
                for (var i = 0; i < inputs.length; i++) {
                    var ty = (inputs[i].type || 'text').toLowerCase();
                    if (ty === 'text' || ty === 'search' || ty === 'email' || inputs[i].tagName === 'TEXTAREA') { el = inputs[i]; break; }
                }
            }
            if (!el) return JSON.stringify({typed:false});
            el.focus();
            var proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
            var setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
            setter.call(el, "__TEXT__");
            el.dispatchEvent(new Event('input', {bubbles:true}));
            el.dispatchEvent(new Event('change', {bubbles:true}));
            __SUBMIT__
            return JSON.stringify({typed:true});
        })()
        """.Replace("__SEL__", EscapeJs(selector.Trim()))
           .Replace("__TEXT__", EscapeJs(text))
           .Replace("__SUBMIT__", submitJs);
    }

    // ---------------- helpers -------------------------------------------------------------------------

    private async Task<string?> EvalJsonAsync(string script, CancellationToken ct)
    {
        var host = _vm.ActiveTab?.Host;
        if (host is null) return null;
        try
        {
            var response = await host.Browser.EvaluateScriptAsync(script, TimeSpan.FromSeconds(10));
            if (!response.Success) return null;
            return response.Result as string;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DualNet.Core.SentrySetup.CaptureException(ex, "agent-eval");
            return null;
        }
    }

    private static async Task WaitLoadedAsync(CancellationToken ct)
    {
        try { await Task.Delay(1500, ct); } catch { }
    }

    private static string? NormalizeUrl(string input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return null;
        if (Uri.TryCreate(s, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            return s;
        if (Uri.TryCreate("https://" + s, UriKind.Absolute, out var u2)) return u2.ToString();
        return null;
    }

    private static string GuessName(string url)
    {
        try
        {
            var u = new Uri(url);
            var last = u.AbsolutePath.Split('/').LastOrDefault(p => p.Length > 0);
            return string.IsNullOrWhiteSpace(last) ? u.Host : Uri.UnescapeDataString(last);
        }
        catch { return "download"; }
    }

    private static string EscapeJs(string s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
}
