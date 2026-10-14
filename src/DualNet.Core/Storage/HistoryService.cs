using System.Globalization;
using System.Text;
using DualNet.Core.Models;

namespace DualNet.Core.Storage;

/// <summary>Persists completed/failed download records.</summary>
public sealed class HistoryService
{
    private const int MaxEntries = 500;
    private readonly string _path;
    private readonly object _sync = new();
    private List<HistoryEntry> _entries;

    public HistoryService(string appDataDirectory)
    {
        _path = Path.Combine(appDataDirectory, "history.json");
        _entries = JsonStore.Load<List<HistoryEntry>>(_path) ?? new List<HistoryEntry>();
    }

    public IReadOnlyList<HistoryEntry> Entries
    {
        get { lock (_sync) return _entries.ToList(); }
    }

    public event Action? Changed;

    public void Add(HistoryEntry entry)
    {
        lock (_sync)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            Save();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Writes every entry to a CSV file. Returns the number of rows written.</summary>
    public int ExportCsv(string path)
    {
        var entries = Entries;
        var sb = new StringBuilder();
        sb.AppendLine("FileName,Url,SizeBytes,Status,NetworkScope,FinishedUtc,FilePath,Message");
        foreach (var e in entries)
        {
            sb.AppendLine(string.Join(',',
                Csv(e.FileName), Csv(e.Url), e.SizeBytes.ToString(CultureInfo.InvariantCulture),
                Csv(e.Status), Csv(e.NetworkScope),
                e.FinishedUtc.ToString("o", CultureInfo.InvariantCulture),
                Csv(e.FilePath), Csv(e.Message ?? "")));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return entries.Count;
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private void Save()
    {
        try { JsonStore.Save(_path, _entries); } catch { }
    }
}
