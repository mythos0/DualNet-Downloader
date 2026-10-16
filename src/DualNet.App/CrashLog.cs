using System.IO;
using System.Text;

namespace DualNet.App;

/// <summary>
/// Local crash/diagnostic log. Every startup- or runtime-level failure is appended here
/// so a failure can NEVER be fully invisible again: the UI shows a dialog with this
/// file's path, and the file itself contains the full exception + stack trace.
/// Kept small on purpose: last 20 files under %APPDATA%\DualNet\logs.
/// </summary>
public static class CrashLog
{
    public static string LogsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DualNet", "logs");

    /// <summary>Writes one crash entry; returns the log file path ("" on failure to write).</summary>
    public static string Write(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            var path = Path.Combine(LogsDir,
                $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            var sb = new StringBuilder();
            sb.AppendLine("==== DualNet Downloader crash ====");
            sb.AppendLine($"Time (local) : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Version      : {System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version}");
            sb.AppendLine($"Source       : {source}");
            sb.AppendLine($"OS           : {Environment.OSVersion} ({Environment.Is64BitProcess}64-bit process)");
            sb.AppendLine($"App directory: {AppContext.BaseDirectory}");
            sb.AppendLine();
            sb.AppendLine(ex.ToString());
            if (ex.InnerException is not null)
            {
                sb.AppendLine();
                sb.AppendLine("--- inner exception ---");
                sb.AppendLine(ex.InnerException.ToString());
            }
            File.WriteAllText(path, sb.ToString());
            PruneOldLogs();
            return path;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Appends a short note line to a rolling app log (not a crash) for diagnostics.</summary>
    public static void Note(string message)
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            var path = Path.Combine(LogsDir, "app.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
        }
        catch { }
    }

    private static void PruneOldLogs()
    {
        try
        {
            var files = new DirectoryInfo(LogsDir)
                .GetFiles("crash-*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();
            for (int i = 20; i < files.Count; i++)
            {
                try { files[i].Delete(); } catch { }
            }
        }
        catch { }
    }
}
