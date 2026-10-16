using System.Net;
using System.Net.Sockets;
using System.Text;
using DualNet.Core.Agent;
using Xunit;

namespace DualNet.Tests;

/// <summary>
/// Browser-automation agent tests: JSON reply parsing, confirmation gating, the ReAct
/// loop against a MOCK local Ollama server (real HTTP, canned replies), the risk
/// classifier and the idle model-unload manager.
/// </summary>
public class AgentTests
{
    // ---- JSON parsing ---------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"thought":"go","action":{"type":"search","query":"react docs"},"needs_confirmation":false}""", "search")]
    [InlineData("```json\n{\"action\":{\"type\":\"finish\",\"summary\":\"done\"}}\n```", "finish")]
    [InlineData("Sure! Here is the action:\n{\"thought\":\"t\",\"action\":{\"type\":\"read_page\"}} hope that helps", "read_page")]
    public void AgentAction_Parse_ToleratesFencesAndProse(string reply, string expectedType)
    {
        var action = AgentAction.Parse(reply);
        Assert.NotNull(action);
        Assert.Equal(expectedType, action!.Type);
    }

    [Fact]
    public void AgentAction_Parse_PicksUpAllFields()
    {
        var a = AgentAction.Parse("""
            {"thought":"d","needs_confirmation":true,
             "action":{"type":"open_tabs","urls":["https://a.dev","https://b.dev"],"arrange":"split"}}
            """);
        Assert.NotNull(a);
        Assert.True(a!.NeedsConfirmation);
        Assert.Equal(2, a.Urls.Count);
        Assert.Equal("split", a.Arrange);
        Assert.Equal("d", a.Thought);
    }

    [Fact]
    public void AgentAction_Parse_Garbage_ReturnsNull()
    {
        Assert.Null(AgentAction.Parse(""));
        Assert.Null(AgentAction.Parse("no json here"));
        Assert.Null(AgentAction.Parse("{\"action\": broken"));
    }

    // ---- confirmation classifier ----------------------------------------------------------------

    [Fact]
    public void Classifier_GatesInteractionAndRiskyActions()
    {
        // ANY click/type is gated (direct page interaction).
        Assert.True(ConfirmationClassifier.IsConsequential(new AgentAction { Type = "click", TextOrSelector = "Log in" }));
        Assert.True(ConfirmationClassifier.IsConsequential(new AgentAction { Type = "type", Selector = "#q", Text = "x" }));

        // Read-only actions are not gated.
        Assert.False(ConfirmationClassifier.IsConsequential(new AgentAction { Type = "read_page" }));
        Assert.False(ConfirmationClassifier.IsConsequential(new AgentAction { Type = "search", Query = "docs" }));
        Assert.False(ConfirmationClassifier.IsConsequential(new AgentAction { Type = "download", Urls = { "https://x/y.pdf" } }));

        // Risky URLs / task text are gated even for navigation.
        Assert.True(ConfirmationClassifier.IsConsequential(new AgentAction { Type = "navigate", Url = "https://shop.example/checkout/step2" }));
        Assert.True(ConfirmationClassifier.IsConsequential(new AgentAction { Type = "open_tabs", Urls = { "https://a.example" } },
            taskText: "buy the cheapest one"));
        Assert.True(ConfirmationClassifier.IsConsequential(new AgentAction { Type = "search", Query = "x" }, taskText: "delete all my posts"));
    }

    // ---- mock ollama server ---------------------------------------------------------------------

    private sealed class MockOllama : IDisposable
    {
        private TcpListener? _listener;
        public string ChatReply = "{\"action\":{\"type\":\"finish\",\"summary\":\"ok\"}}";
        public int ChatCalls;
        public List<string> KeepAliveValues = new();
        public bool UnloadCalled;
        public int TagsCalls;

        public string BaseUrl { get; private set; } = "";

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync();
                        _ = Task.Run(() => Handle(client));
                    }
                }
                catch { }
            });
        }

        private void Handle(TcpClient client)
        {
            try
            {
                using var s = client.GetStream();
                var sb = new StringBuilder();
                var buf = new byte[8192];
                while (!sb.ToString().Contains("\r\n\r\n"))
                {
                    int n = s.Read(buf);
                    if (n <= 0) break;
                    sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                }
                var head = sb.ToString();
                var requestLine = head.Split("\r\n")[0];
                var path = requestLine.Split(' ')[1];
                string body;
                if (path.StartsWith("/api/tags"))
                {
                    TagsCalls++;
                    body = "{\"models\":[{\"name\":\"qwen3.5:2b\"},{\"name\":\"llama3:8b\"}]}";
                }
                else if (path.StartsWith("/api/chat"))
                {
                    ChatCalls++;
                    // capture keep_alive from the request body (tail of the head may include it)
                    var lower = head.ToLowerInvariant();
                    if (lower.Contains("keep_alive")) KeepAliveValues.Add("present");
                    body = "{\"message\":{\"role\":\"assistant\",\"content\":" +
                           System.Text.Json.JsonSerializer.Serialize(ChatReply) + "}}";
                }
                else if (path.StartsWith("/api/generate"))
                {
                    UnloadCalled = true;
                    body = "{}";
                }
                else body = "{}";

                var bytes = Encoding.UTF8.GetBytes(body);
                var header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n" +
                    $"Content-Length: {bytes.Length}\r\n\r\n");
                s.Write(header);
                s.Write(bytes);
                s.Flush();
                client.Close();
            }
            catch { }
        }

        public void Dispose() { try { _listener?.Stop(); } catch { } }
    }

    private sealed class FakeChat : IAgentChatClient
    {
        public string Model => "qwen3.5:2b";
        public Queue<string> Replies = new();
        public int Calls;
        public string ModelUnloaded;

        public Task<string> ChatAsync(string systemPrompt, string userMessage, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Replies.Count > 0 ? Replies.Dequeue() : "{\"action\":{\"type\":\"read_page\"}}");
        }

        public Task<OllamaStatus> CheckStatusAsync(CancellationToken ct) =>
            Task.FromResult(new OllamaStatus(true, true, null, new List<string> { Model }));

        public Task UnloadAsync(CancellationToken ct) { ModelUnloaded = "y"; return Task.CompletedTask; }
    }

    private sealed class FakeExecutor : IAgentActionExecutor
    {
        public string CurrentUrl() => "https://example.com/page";
        public List<AgentAction> Executed = new();

        public Task<AgentObservation> ExecuteAsync(AgentAction action, CancellationToken ct)
        {
            Executed.Add(action);
            return Task.FromResult(action.Type switch
            {
                "list_links" => new AgentObservation
                {
                    Links = new List<LinkHit> { new("React docs", "https://react.dev/learn"), new("Download pdf", "https://x/y.pdf") }
                },
                "recall_tabs" => new AgentObservation
                {
                    Links = new List<LinkHit> { new("Mail", "https://mail.example.com"), new("Calendar", "https://cal.example.com") }
                },
                _ => new AgentObservation { Text = "page text " + action.Type },
            });
        }
    }

    private sealed class FakeGate : IConfirmationGate
    {
        public bool Approve = true;
        public List<AgentAction> Asked = new();
        public Task<bool> ConfirmAsync(AgentAction action, string reason)
        {
            Asked.Add(action);
            return Task.FromResult(Approve);
        }
    }

    // ---- orchestrator -----------------------------------------------------------------------------

    [Fact]
    public async Task Orchestrator_FindsDocsAndOpensTabs_WithSplitArrange()
    {
        var chat = new FakeChat();
        chat.Replies.Enqueue("""{"thought":"search","action":{"type":"search","query":"react official docs"}}""");
        chat.Replies.Enqueue("""{"action":{"type":"open_tabs","urls":["https://react.dev","https://vuejs.org"],"arrange":"split"}}""");
        chat.Replies.Enqueue("""{"action":{"type":"finish","summary":"Opened React + Vue docs side by side"}}""");

        var exec = new FakeExecutor();
        var gate = new FakeGate();
        var orch = new AgentOrchestrator(chat, exec, gate) { MaxTurns = 5 };

        var summary = await orch.RunAsync("Find the official documentation for react and vue and open them side by side", CancellationToken.None);

        Assert.Contains("side by side", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, exec.Executed.Count);   // finish does not execute
        Assert.Equal("search", exec.Executed[0].Type);
        Assert.Equal("open_tabs", exec.Executed[1].Type);
        Assert.Equal(2, exec.Executed[1].Urls.Count);
        Assert.Empty(gate.Asked);   // read-only flow never asks
    }

    [Fact]
    public async Task Orchestrator_GatesClicks_AndRespectsSkip()
    {
        var chat = new FakeChat();
        chat.Replies.Enqueue("""{"action":{"type":"click","text_or_selector":"Complete purchase"}}""");
        chat.Replies.Enqueue("""{"action":{"type":"finish","summary":"nothing done"}}""");

        var exec = new FakeExecutor();
        var gate = new FakeGate { Approve = false };
        var orch = new AgentOrchestrator(chat, exec, gate);

        var summary = await orch.RunAsync("buy the thing", CancellationToken.None);

        Assert.Single(gate.Asked);        // the click asked once…
        Assert.Empty(exec.Executed);      // …was declined, so nothing executed
        Assert.Equal("nothing done", summary);
    }

    [Fact]
    public async Task Orchestrator_DownloadsPdfs_WithoutConfirmation()
    {
        var chat = new FakeChat();
        chat.Replies.Enqueue("""{"action":{"type":"list_links","filter":"\\.pdf$"}}""");
        chat.Replies.Enqueue("""{"action":{"type":"download","urls":["https://x/y.pdf","https://x/z.pdf"]}}""");
        chat.Replies.Enqueue("""{"action":{"type":"finish","summary":"Downloaded 2 PDFs"}}""");

        var exec = new FakeExecutor();
        var gate = new FakeGate();
        var orch = new AgentOrchestrator(chat, exec, gate);

        var summary = await orch.RunAsync("Download all PDFs from this page", CancellationToken.None);

        Assert.Contains("2 PDFs", summary);
        Assert.Equal(2, exec.Executed.Count);
        Assert.Empty(gate.Asked);
    }

    [Fact]
    public async Task Orchestrator_RecoversFromInvalidReply_WithinMaxTurns()
    {
        var chat = new FakeChat();
        chat.Replies.Enqueue("I am not sure what to do, sorry!");
        chat.Replies.Enqueue("""{"action":{"type":"finish","summary":"ok after hiccup"}}""");
        var orch = new AgentOrchestrator(chat, new FakeExecutor(), new FakeGate()) { MaxTurns = 3 };

        var summary = await orch.RunAsync("do something", CancellationToken.None);
        Assert.Equal("ok after hiccup", summary);
    }

    [Fact]
    public async Task Orchestrator_StopsAtMaxTurns()
    {
        var chat = new FakeChat();
        var orch = new AgentOrchestrator(chat, new FakeExecutor(), new FakeGate()) { MaxTurns = 3 };
        // endless read_page replies
        var summary = await orch.RunAsync("never finish", CancellationToken.None);
        Assert.Contains("more than 3 steps", summary);
        Assert.Equal(3, chat.Calls);
    }

    // ---- real Ollama client against the mock server --------------------------------------------------

    [Fact]
    public async Task OllamaClient_CheckStatusAndChat_WorkOverHttp()
    {
        using var server = new MockOllama();
        server.Start();
        server.ChatReply = "{\"thought\":\"t\",\"action\":{\"type\":\"search\",\"query\":\"qwen\"}}";

        var client = new OllamaClient(server.BaseUrl, "qwen3.5:2b") { TimeoutSeconds = 20 };
        var status = await client.CheckStatusAsync(CancellationToken.None);
        Assert.True(status.Reachable);
        Assert.True(status.ModelAvailable);
        Assert.Equal(2, status.AvailableModels.Count);

        var reply = await client.ChatAsync(AgentPrompt.System, "TASK: x", CancellationToken.None);
        var action = AgentAction.Parse(reply);
        Assert.NotNull(action);
        Assert.Equal("search", action!.Type);
    }

    [Fact]
    public async Task OllamaClient_UnreachableStatus_IsFriendly()
    {
        var client = new OllamaClient("http://127.0.0.1:9", "qwen3.5:2b") { TimeoutSeconds = 3 };
        var status = await client.CheckStatusAsync(CancellationToken.None);
        Assert.False(status.Reachable);
        Assert.Contains("Ollama", status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OllamaClient_Unload_PostsGenerate()
    {
        using var server = new MockOllama();
        server.Start();
        var client = new OllamaClient(server.BaseUrl, "qwen3.5:2b") { TimeoutSeconds = 10 };
        await client.UnloadAsync(CancellationToken.None);
        Assert.True(server.UnloadCalled);
    }

    // ---- idle manager ---------------------------------------------------------------------------------

    private sealed class CountingChat : IAgentChatClient
    {
        public string Model => "m";
        public int Unloads;
        public Task<string> ChatAsync(string s, string u, CancellationToken ct) => Task.FromResult("{}");
        public Task<OllamaStatus> CheckStatusAsync(CancellationToken ct) =>
            Task.FromResult(new OllamaStatus(true, true, null, new List<string>()));
        public Task UnloadAsync(CancellationToken ct) { Unloads++; return Task.CompletedTask; }
    }

    [Fact]
    public async Task IdleManager_UnloadsAfterIdleWindow_AndReloadsOnBusy()
    {
        var chat = new CountingChat();
        bool trimmed = false;
        var t0 = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var now = t0;
        var manager = new AgentIdleManager(chat, () => trimmed = true)
        {
            IdleUnloadSeconds = 120,
            Clock = () => now,
        };

        manager.MarkBusy();
        manager.MarkIdle();

        now = t0.AddSeconds(60);               // inside the idle window
        manager.CheckNowForTests();
        await Task.Delay(50);
        Assert.Equal(0, chat.Unloads);
        Assert.False(manager.IsModelUnloaded);

        now = t0.AddSeconds(180);              // past the window
        manager.CheckNowForTests();
        await Task.Delay(50);
        Assert.Equal(1, chat.Unloads);
        Assert.True(trimmed);
        Assert.True(manager.IsModelUnloaded);

        manager.CheckNowForTests();            // no double unload while flagged unloaded
        await Task.Delay(50);
        Assert.Equal(1, chat.Unloads);

        manager.MarkBusy();                    // a new task re-arms the model
        now = t0.AddSeconds(190);              // only 10s since the new busy mark
        manager.CheckNowForTests();
        await Task.Delay(50);
        Assert.Equal(1, chat.Unloads);
        Assert.False(manager.IsModelUnloaded);
    }
}
