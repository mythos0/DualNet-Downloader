using System.IO;
using DualNet.Core.Models;
using DualNet.Core.Storage;

namespace DualNet.App.Services;

/// <summary>Persists AppSettings as JSON with in-memory caching and crash-safe writes.</summary>
public sealed class JsonSettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private AppSettings _data;

    public JsonSettingsStore(string path)
    {
        _path = path;
        _data = JsonStore.Load<AppSettings>(path);

        // Migration: stale v1.x defaults (empty download dir) → user's Downloads folder.
        if (string.IsNullOrWhiteSpace(_data.DownloadDirectory))
        {
            _data.DownloadDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    public AppSettings Data => _data;

    public void Save()
    {
        lock (_gate) JsonStore.Save(_path, _data);
    }
}
