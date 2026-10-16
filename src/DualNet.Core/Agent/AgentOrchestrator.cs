namespace DualNet.Core.Agent;

/// <summary>Executes an action in the real browser. Implemented by the WPF layer (CEF); faked in tests.</summary>
public interface IAgentActionExecutor
{
    /// <summary>URL of the page the agent would act on right now (for risk classification).</summary>
    string CurrentUrl();

    Task<AgentObservation> ExecuteAsync(AgentAction action, CancellationToken ct);
}

/// <summary>Asks the user to approve a consequential step. Implemented by the UI.</summary>
public interface IConfirmationGate
{
    /// <summary>True = run it, false = skip this action, throws OperationCanceled = stop everything.</summary>
    Task<bool> ConfirmAsync(AgentAction action, string reason);
}

public sealed record AgentProgress(
    int Turn, AgentAction Action, string Decision,   // ran | skipped | needs-confirmation | done | error
    string? Note, bool IsConsequential);

/// <summary>
/// Drives the agent loop: model turn → parse action → (gate consequential actions) →
/// execute → observe → repeat, until finish / max turns / cancellation.
/// All model calls flow through <see cref="IAgentChatClient"/> so tests can fake the LLM.
/// </summary>
public sealed class AgentOrchestrator
{
    private readonly IAgentChatClient _chat;
    private readonly IAgentActionExecutor _executor;
    private readonly IConfirmationGate _gate;

    public int MaxTurns { get; set; } = 14;
    public int ObservationMaxChars { get; set; } = 4000;

    public event Action<AgentProgress>? Progress;
    public event Action<AgentObservation>? Observed;

    public AgentOrchestrator(IAgentChatClient chat, IAgentActionExecutor executor, IConfirmationGate gate)
    {
        _chat = chat;
        _executor = executor;
        _gate = gate;
    }

    /// <summary>Runs the task; returns the finish summary (or a failure note).</summary>
    public async Task<string> RunAsync(string task, CancellationToken ct)
    {
        var history = new List<string>();
        string lastSummary = "";
        for (int turn = 1; turn <= MaxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();

            string reply;
            try
            {
                reply = await _chat.ChatAsync(AgentPrompt.System,
                    AgentPrompt.UserTurn(task, history, history.Count == 0
                        ? "(no actions yet — start by searching or reading a page)"
                        : _lastObservationRender, turn, MaxTurns), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return "The local model could not be reached: " + ex.Message;
            }

            var action = AgentAction.Parse(reply);
            if (action is null || string.IsNullOrWhiteSpace(action.Type))
            {
                history.Add($"turn {turn}: INVALID reply — expected a JSON action");
                Progress?.Invoke(new AgentProgress(turn, new AgentAction { Type = "invalid" }, "error",
                    "The model reply was not a valid action.", false));
                continue;
            }

            if (action.Type == "finish")
            {
                lastSummary = string.IsNullOrWhiteSpace(action.Summary) ? "Task finished." : action.Summary;
                Progress?.Invoke(new AgentProgress(turn, action, "done", lastSummary, false));
                return lastSummary;
            }

            if (AgentAction.KnownTypes.All(t => t != action.Type))
            {
                history.Add($"turn {turn}: unknown action '{action.Type}'");
                Progress?.Invoke(new AgentProgress(turn, action, "error", $"Unknown action '{action.Type}'.", false));
                continue;
            }

            bool consequential = ConfirmationClassifier.IsConsequential(action, _executor.CurrentUrl(), task);
            if (consequential)
            {
                Progress?.Invoke(new AgentProgress(turn, action, "needs-confirmation", action.Describe(), true));
                bool approved;
                try
                {
                    approved = await _gate.ConfirmAsync(action,
                        "This step acts on the page directly and may be consequential.").ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return "Stopped by you.";
                }
                if (!approved)
                {
                    history.Add($"turn {turn}: SKIPPED {action.Describe()} (user declined)");
                    Progress?.Invoke(new AgentProgress(turn, action, "skipped", "Skipped by you.", true));
                    continue;
                }
            }

            AgentObservation observation;
            try
            {
                observation = await _executor.ExecuteAsync(action, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                observation = new AgentObservation { Ok = false, Text = ex.Message };
            }

            var rendered = observation.Render(ObservationMaxChars);
            _lastObservationRender = rendered;
            Observed?.Invoke(observation);
            history.Add($"turn {turn}: {action.Describe()}");
            Progress?.Invoke(new AgentProgress(turn, action, "ran", observation.Ok ? "Done" : "Failed", consequential));
        }
        return "Stopped: the task needed more than " + MaxTurns + " steps.";
    }

    private string _lastObservationRender = "(no actions yet — start by searching or reading a page)";
}

/// <summary>
/// Keeps the local model's RAM footprint at zero while the agent is idle:
/// after <see cref="IdleUnloadSeconds"/> without a model call the weights are
/// unloaded from Ollama (keep_alive=0) and the app's working set is trimmed.
/// </summary>
public sealed class AgentIdleManager : IDisposable
{
    private readonly IAgentChatClient _chat;
    private readonly Action? _trimWorkingSet;
    private Timer? _timer;
    private long _lastUseUtcTicks;
    private bool _unloaded;

    public int IdleUnloadSeconds { get; set; } = 120;
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    public event Action<string>? Note;

    public AgentIdleManager(IAgentChatClient chat, Action? trimWorkingSet = null)
    {
        _chat = chat;
        _trimWorkingSet = trimWorkingSet;
        _timer = new Timer(_ => Check(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    /// <summary>Call right before a model request so the model is loaded again.</summary>
    public void MarkBusy()
    {
        _unloaded = false;
        Volatile.Write(ref _lastUseUtcTicks, Clock().Ticks);
    }

    /// <summary>Call after the last model reply of a task.</summary>
    public void MarkIdle() => Volatile.Write(ref _lastUseUtcTicks, Clock().Ticks);

    public bool IsModelUnloaded => _unloaded;

    public string StatusText => _unloaded ? "Model unloaded (RAM saved) — reloads automatically" : "Model in memory";

    private async void Check()
    {
        try
        {
            if (_unloaded) return;
            var last = new DateTime(Volatile.Read(ref _lastUseUtcTicks), DateTimeKind.Utc);
            if (last == DateTime.MinValue) return;             // never used yet
            if (Clock() - last < TimeSpan.FromSeconds(Math.Max(0, IdleUnloadSeconds))) return;

            _unloaded = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _chat.UnloadAsync(cts.Token).ConfigureAwait(false);
            _trimWorkingSet?.Invoke();
            Note?.Invoke(StatusText);
        }
        catch { }
    }

    /// <summary>Manual timer tick (tests + explicit checks).</summary>
    public void CheckNowForTests() => Check();

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
