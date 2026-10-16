using System.Runtime.InteropServices;

namespace DualNet.App.Services;

/// <summary>
/// Windows working-set trim: asks the OS to page out what we do not need right now.
/// Used after the agent unloads its model so "idle" really shows in Task Manager.
/// </summary>
public static class RamTrimmer
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static long _lastTrimTicks;

    /// <summary>GC + empty-working-set trim. Cheap; safe to call from any thread.</summary>
    public static void Trim()
    {
        try
        {
            // Avoid hammering the trim API from the agent's periodic checks.
            if (Environment.TickCount64 - _lastTrimTicks < 15_000) return;
            _lastTrimTicks = Environment.TickCount64;

            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // Non-fatal everywhere.
        }
    }
}
