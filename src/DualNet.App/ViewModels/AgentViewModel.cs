using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Windows.Input;
using DualNet.App.Browser;
using DualNet.App.Services;
using DualNet.Core.Agent;

namespace DualNet.App.ViewModels;

public enum AgentItemKind { UserTask, Thought, Action, Observation, Confirmation, Summary, Error, Note }

/// <summary>One line of the agent transcript rendered in the UI.</summary>
public sealed class AgentTranscriptItem
{
    public AgentItemKind Kind { get; init; }
    public string Text { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Icon { get; init; } = "";
}

public sealed class AgentLinkResult
{
    public string Text { get; init; } = "";
    public string Url { get; init; } = "";
    public ICommand? DownloadCommand { get; init; }
    public ICommand? OpenCommand { get; init; }
}

/// <summary>
/// Drives the browser automation agent from the UI: local Ollama model status,
/// ReAct task execution, confirmation flow, transcript and results.
/// </summary>
public sealed class AgentViewModel : INotifyPropertyChanged, IConfirmationGate
{
    private readonly MainViewModel _main;
    private OllamaClient? _client;
    private AgentIdleManager? _idle;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _pendingConfirmation;

    public ObservableCollection<AgentTranscriptItem> Transcript { get; } = new();
    public ObservableCollection<AgentLinkResult> ResultLinks { get; } = new();
    public ObservableCollection<AgentModelChoice> KnownModels { get; } = new();

    public AgentViewModel(MainViewModel main)
    {
        _main = main;
        RunTaskCommand = new RelayCommand(_ => _ = RunTaskAsync());
        StopCommand = new RelayCommand(_ => Stop(), _ => IsRunning);
        UnloadNowCommand = new RelayCommand(_ => _ = UnloadNowAsync());
        ConfirmYesCommand = new RelayCommand(_ => _pendingConfirmation?.TrySetResult(true));
        ConfirmNoCommand = new RelayCommand(_ => _pendingConfirmation?.TrySetResult(false));
        ConfirmCancelCommand = new RelayCommand(_ =>
        {
            _pendingConfirmation?.TrySetResult(false);
            Stop();
        });
        OpenModelPageCommand = new RelayCommand(_ =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://ollama.com/library", UseShellExecute = true }); } catch { }
        });
        _ = RefreshStatusAsync();
    }

    // ---------------- state ------------------------------------------------------------------------

    private string _taskInput = "";
    public string TaskInput { get => _taskInput; set { _taskInput = value; Changed(nameof(TaskInput)); } }

    private string _status = "Checking local model…";
    public string StatusText { get => _status; set { _status = value; Changed(nameof(StatusText)); } }

    private bool _statusOk;
    public bool StatusOk { get => _statusOk; set { _statusOk = value; Changed(nameof(StatusOk)); } }

    private bool _statusWarn;
    public bool StatusWarn { get => _statusWarn; set { _statusWarn = value; Changed(nameof(StatusWarn)); } }

    private string _model = "";
    public string Model { get => _model; set { _model = value; Changed(nameof(Model)); } }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            _isRunning = value;
            Changed(nameof(IsRunning));
            Changed(nameof(CanRun));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanRun => !IsRunning && TaskInput.Trim().Length > 0;

    private string _pendingActionText = "";
    public string PendingActionText { get => _pendingActionText; set { _pendingActionText = value; Changed(nameof(PendingActionText)); } }

    private bool _showConfirmation;
    public bool ShowConfirmation { get => _showConfirmation; set { _showConfirmation = value; Changed(nameof(ShowConfirmation)); } }

    private DataView? _resultTable;
    public DataView? ResultTable { get => _resultTable; set { _resultTable = value; Changed(nameof(ResultTable)); Changed(nameof(HasTable)); } }

    public bool HasTable => _resultTable is { Count: > 0 };
    public bool HasLinks => ResultLinks.Count > 0;

    public ICommand RunTaskCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand UnloadNowCommand { get; }
    public ICommand ConfirmYesCommand { get; }
    public ICommand ConfirmNoCommand { get; }
    public ICommand ConfirmCancelCommand { get; }
    public ICommand OpenModelPageCommand { get; }

    // ---------------- transcript helpers ---------------------------------------------------------------

    private void Add(AgentItemKind kind, string icon, string text, string detail = "")
    {
        void AddInternal() => Transcript.Add(new AgentTranscriptItem { Kind = kind, Icon = icon, Text = text, Detail = detail });
        _main.PostToUi(AddInternal);
    }

    // ---------------- model status ----------------------------------------------------------------------

    public async Task RefreshStatusAsync()
    {
        Model = AppBootstrapper.Settings.AgentModel;
        _client ??= NewClient();
        var status = await _client.CheckStatusAsync(CancellationToken.None);
        KnownModels.Clear();
        foreach (var m in status.AvailableModels.Take(12))
            KnownModels.Add(new AgentModelChoice(m));
        if (!status.Reachable)
        {
            StatusOk = false; StatusWarn = true;
            StatusText = "Ollama not reachable — install from ollama.com, then: ollama pull " + AppBootstrapper.Settings.AgentModel;
        }
        else if (!status.ModelAvailable)
        {
            StatusOk = false; StatusWarn = true;
            StatusText = $"Model '{AppBootstrapper.Settings.AgentModel}' not pulled yet — run: ollama pull {AppBootstrapper.Settings.AgentModel}";
        }
        else
        {
            StatusOk = true; StatusWarn = false;
            StatusText = $"Local AI ready · {AppBootstrapper.Settings.AgentModel} · RAM auto-clears after " +
                         $"{AppBootstrapper.Settings.AgentIdleUnloadSeconds}s idle";
            _idle ??= NewIdleManager();
        }
    }

    private OllamaClient NewClient()
    {
        var client = new OllamaClient(AppBootstrapper.Settings.OllamaUrl, AppBootstrapper.Settings.AgentModel)
        {
            KeepAlive = "15m",
        };
        return client;
    }

    private AgentIdleManager NewIdleManager()
    {
        var idle = new AgentIdleManager(_client!, RamTrimmer.Trim)
        {
            IdleUnloadSeconds = Math.Max(30, AppBootstrapper.Settings.AgentIdleUnloadSeconds),
        };
        idle.Note += s => Add(AgentItemKind.Note, "🧹", s);
        return idle;
    }

    /// <summary>Fire-and-forget status refresh used by the page.</summary>
    public void RefreshStatusCommandSafe() => _ = RefreshStatusAsync();

    public async Task UnloadNowAsync()
    {
        _client ??= NewClient();
        await _client.UnloadAsync(CancellationToken.None);
        RamTrimmer.Trim();
        _idle?.MarkBusy();   // pretend we just used it so the timer does not double-fire
        _idle?.CheckNowForTests();
        Add(AgentItemKind.Note, "🧹", "Model unloaded — RAM freed. It reloads automatically on the next task.");
    }

    // ---------------- task execution ----------------------------------------------------------------------

    private async Task RunTaskAsync()
    {
        var task = TaskInput.Trim();
        if (task.Length == 0 || IsRunning) return;

        IsRunning = true;
        Transcript.Clear();
        ResultLinks.Clear();
        ResultTable = null;
        TaskInput = "";

        Add(AgentItemKind.UserTask, "🧑", task);
        _cts = new CancellationTokenSource();

        _client ??= NewClient();
        _idle ??= NewIdleManager();
        _idle.IdleUnloadSeconds = Math.Max(30, AppBootstrapper.Settings.AgentIdleUnloadSeconds);
        _idle.MarkBusy();

        var executor = new CefAgentExecutor(_main);
        var orchestrator = new AgentOrchestrator(_client, executor, this) { MaxTurns = 14 };
        orchestrator.Progress += p =>
        {
            if (p.Decision == "error")
                Add(AgentItemKind.Error, "⚠", p.Note ?? "Action failed", p.Action.Describe());
            else
                Add(AgentItemKind.Action, p.Decision switch
                {
                    "ran" => "▶",
                    "skipped" => "⤼",
                    "needs-confirmation" => "❓",
                    _ => "•",
                }, p.Action.Describe(), p.Decision == "needs-confirmation" ? "Waiting for your approval…" : p.Note);
        };
        orchestrator.Observed += obs =>
        {
            if (obs.Links is { Count: > 0 })
            {
                foreach (var l in obs.Links.Take(50))
                {
                    ResultLinks.Add(new AgentLinkResult
                    {
                        Text = string.IsNullOrWhiteSpace(l.Text) ? l.Url : l.Text,
                        Url = l.Url,
                        DownloadCommand = new RelayCommand(_ => DownloadUrl(l.Url)),
                        OpenCommand = new RelayCommand(_ => _main.CreateTab(l.Url)),
                    });
                }
                Changed(nameof(HasLinks));
            }
            if (obs.Table is { Count: > 0 })
                ResultTable = BuildTable(obs.Table);
            Add(AgentItemKind.Observation, "👁", FirstLine(obs.Render(300)));
        };

        try
        {
            var summary = await Task.Run(() => orchestrator.RunAsync(task, _cts.Token));
            Add(AgentItemKind.Summary, "✅", summary);
        }
        catch (OperationCanceledException)
        {
            Add(AgentItemKind.Error, "⏹", "Task stopped.");
        }
        catch (Exception ex)
        {
            Add(AgentItemKind.Error, "⚠", "Task failed: " + ex.Message);
            DualNet.Core.SentrySetup.CaptureException(ex, "agent-task");
        }
        finally
        {
            _idle.MarkIdle();
            IsRunning = false;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _pendingConfirmation?.TrySetCanceled();
        ShowConfirmation = false;
    }

    private void DownloadUrl(string url)
    {
        var tab = _main.ActiveTab;
        _main.EnqueueDownload(url, Path.GetFileName(new Uri(url).LocalPath).Length > 0
            ? Uri.UnescapeDataString(Path.GetFileName(new Uri(url).LocalPath))
            : "download",
            tab?.NetScope ?? "all",
            string.IsNullOrWhiteSpace(tab?.Proxy) ? null : tab!.Proxy);
        Add(AgentItemKind.Note, "⬇", "Queued download: " + url);
    }

    // ---------------- IConfirmationGate -------------------------------------------------------------------

    public Task<bool> ConfirmAsync(AgentAction action, string reason)
    {
        _pendingConfirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingActionText = action.Describe() + " — " + reason;
        ShowConfirmation = true;
        Add(AgentItemKind.Confirmation, "❓", PendingActionText);

        return _pendingConfirmation.Task.ContinueWith(t =>
        {
            _main.PostToUi(() => ShowConfirmation = false);
            return t.Status == TaskStatus.RanToCompletion && t.Result;
        });
    }

    // ---------------- helpers ------------------------------------------------------------------------------

    private static DataView BuildTable(List<string[]> rows)
    {
        var dt = new DataTable();
        if (rows.Count == 0) return dt.DefaultView;
        int cols = rows.Max(r => r.Length);
        var first = rows[0];
        bool headerLooksReal = first.All(c => c.Length > 0) && first.Length > 1;
        if (headerLooksReal)
            foreach (var h in first) dt.Columns.Add(string.IsNullOrWhiteSpace(h) ? "col" : h, typeof(string));
        else
        {
            for (int i = 0; i < cols; i++) dt.Columns.Add($"Col {i + 1}", typeof(string));
            dt.Rows.Add(first);
        }
        foreach (var r in rows.Skip(headerLooksReal ? 1 : 0))
        {
            var dr = dt.NewRow();
            for (int i = 0; i < cols; i++) dr[i] = i < r.Length ? r[i] : "";
            dt.Rows.Add(dr);
        }
        return dt.DefaultView;
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record AgentModelChoice(string Name);
