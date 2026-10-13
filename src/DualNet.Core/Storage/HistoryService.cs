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

    private void Save()
    {
        try { JsonStore.Save(_path, _entries); } catch { }
    }
}
