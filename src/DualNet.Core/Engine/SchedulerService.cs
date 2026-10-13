namespace DualNet.Core.Engine;

/// <summary>IDM-style download scheduler: allow downloads only inside a daily time window.</summary>
public enum ScheduleWindow
{
    Outside,
    Inside
}

public static class SchedulerService
{
    /// <summary>
    /// Returns whether <paramref name="now"/> falls inside the daily [start, stop) window.
    /// Supports overnight windows (e.g. 23:00 → 07:00).
    /// </summary>
    public static ScheduleWindow Evaluate(TimeOnly now, TimeOnly start, TimeOnly stop)
    {
        if (start == stop)
            return ScheduleWindow.Inside;   // degenerate config = always allowed

        if (start < stop)
            return now >= start && now < stop ? ScheduleWindow.Inside : ScheduleWindow.Outside;

        // Overnight window wraps midnight.
        return now >= start || now < stop ? ScheduleWindow.Inside : ScheduleWindow.Outside;
    }

    /// <summary>Transition helper: what the UI should do when the window state changes.</summary>
    public static string? ActionForTransition(ScheduleWindow previous, ScheduleWindow current) =>
        previous == current
            ? null
            : current == ScheduleWindow.Inside ? "start" : "pause";

    public static bool TryParseHhMm(string? text, out TimeOnly value) =>
        TimeOnly.TryParseExact(text?.Trim(), "HH:mm", out value);
}
