using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DualNet.Core.Models;

namespace DualNet.App.ViewModels;

/// <summary>
/// Editor for user-defined "manual network providers" — adapters the automatic scan
/// does not offer (VPN tunnels, virtual switches, multi-IP NICs, …). Changes are
/// persisted to settings immediately; the caller refreshes the network UI afterwards.
/// </summary>
public partial class ManualProvidersViewModel : ObservableObject
{
    public ObservableCollection<ManualAdapterEntry> Providers { get; } = new();

    [ObservableProperty] private string newName = "";
    [ObservableProperty] private string newIp = "";
    [ObservableProperty] private int newTypeIndex;   // 0 Ethernet · 1 Wi-Fi · 2 Other
    [ObservableProperty] private string? validationMessage;
    [ObservableProperty] private bool hasProviders;

    public ManualProvidersViewModel()
    {
        Reload();
    }

    private void Reload()
    {
        Providers.Clear();
        foreach (var entry in AppServices.Settings.ManualAdapters)
            Providers.Add(entry);
        HasProviders = Providers.Count > 0;
    }

    private static string TypeNameFor(int index) => index switch
    {
        0 => "Ethernet",
        1 => "Wi-Fi",
        _ => "Other"
    };

    private bool Validate(out string ipCanonical, out string name)
    {
        ipCanonical = "";
        name = "";
        ValidationMessage = null;

        if (!IPAddress.TryParse((NewIp ?? "").Trim(), out var parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            ValidationMessage = "Enter a valid IPv4 address, e.g. 192.168.1.50";
            return false;
        }
        var ip = parsed.ToString();

        var nameLocal = string.IsNullOrWhiteSpace(NewName) ? $"Manual ({ip})" : NewName.Trim();

        if (Providers.Any(p => string.Equals(p.IPv4Address, ip, StringComparison.OrdinalIgnoreCase)))
        {
            ValidationMessage = $"A manual provider for {ip} already exists";
            return false;
        }

        // Redundant when the auto-scan already offers this IP — refuse with a hint.
        try
        {
            var auto = DualNet.Core.Net.NetworkAdapterService.GetActiveAdapters(null);
            if (auto.Any(a => a.IsOnline &&
                    string.Equals(a.IPv4Address, ip, StringComparison.OrdinalIgnoreCase)))
            {
                ValidationMessage = $"{ip} is already auto-detected — no manual provider needed";
                return false;
            }
        }
        catch { }

        ipCanonical = ip;
        name = nameLocal;
        return true;
    }

    [RelayCommand]
    private void Add()
    {
        if (!Validate(out var ip, out var name))
            return;

        var entry = new ManualAdapterEntry
        {
            Name = name,
            IPv4Address = ip,
            TypeName = TypeNameFor(NewTypeIndex)
        };
        AppServices.Settings.ManualAdapters.Add(entry);
        AppServices.SaveSettings();
        Reload();
        NewName = "";
        NewIp = "";
        ValidationMessage = $"Provider '{name}' added — it appears in the header and gets its own tab when online.";
    }

    [RelayCommand]
    private void Remove(ManualAdapterEntry entry)
    {
        if (entry is null)
            return;
        AppServices.Settings.ManualAdapters.Remove(entry);
        AppServices.SaveSettings();
        Reload();
        ValidationMessage = $"Provider '{entry.Name}' removed.";
    }
}
