using System.Windows;
using System.Windows.Controls;
using DualNet.App.Services;
using DualNet.App.ViewModels;
using DualNet.Core;
using DualNet.Core.Engine;
using DualNet.Core.Engine.Browser;
using DualNet.Core.Models;

namespace DualNet.App.Views;

public partial class SettingsPage : UserControl
{
    private bool _loading = true;
    private MainViewModel VM => (MainViewModel)DataContext!;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { Load(); _loading = false; };
    }

    private void Load()
    {
        var s = AppBootstrapper.Settings;
        SearchEngineBox.ItemsSource = SearchEngines.Names;
        SearchEngineBox.SelectedIndex = Math.Clamp(s.SearchEngine, 0, SearchEngines.Names.Length - 1);
        ThemeBox.SelectedIndex = Math.Clamp(s.ThemeMode, 0, 2);
        DownloadDirBox.Text = s.DownloadDirectory;
        MaxConcurrentBox.Text = s.MaxConcurrentDownloads.ToString();
        MaxConnBox.Text = s.MaxConnectionsPerDownload.ToString();
        SpeedLimitBox.Text = s.SpeedLimitKbps.ToString();
        OrganizeCheck.IsChecked = s.OrganizeByCategory;
        BookmarksBarCheck.IsChecked = s.ShowBookmarksBar;
        RestoreCheck.IsChecked = s.RestoreSession;
        ThirdPartyCheck.IsChecked = s.BlockThirdPartyCookies;
        SentryCheck.IsChecked = s.SentryEnabled;
        OllamaUrlBox.Text = s.OllamaUrl;
        AgentModelBox.Text = s.AgentModel;
        IdleUnloadBox.Text = s.AgentIdleUnloadSeconds.ToString();

        var nics = new List<NicChoice> { new("all", "Combined (all networks)") };
        nics.AddRange(VM.NicChoices.Where(n => n.Id != "all"));
        DefaultNicBox.ItemsSource = nics;
        DefaultNicBox.SelectedItem = nics.FirstOrDefault(n => n.Id == s.DefaultTabNetScope) ?? nics[0];
    }

    // General

    private void SearchEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppBootstrapper.SetSetting(s => s.SearchEngine = SearchEngineBox.SelectedIndex);
    }

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var mode = ThemeBox.SelectedIndex;
        AppBootstrapper.SetSetting(s => s.ThemeMode = mode);
        App.Current.ApplyTheme(mode);
    }

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose download folder" };
        if (dlg.ShowDialog(Window.GetWindow(this)!) == true)
        {
            DownloadDirBox.Text = dlg.FolderName;
            AppBootstrapper.SetSetting(s => s.DownloadDirectory = dlg.FolderName);
        }
    }

    private void DownloadDir_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var dir = DownloadDirBox.Text.Trim();
        if (dir.Length == 0) return;
        AppBootstrapper.SetSetting(s => s.DownloadDirectory = dir);
    }

    // Downloads

    private void MaxConcurrent_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (int.TryParse(MaxConcurrentBox.Text, out var v))
            AppBootstrapper.SetSetting(s => s.MaxConcurrentDownloads = Math.Clamp(v, 1, 10));
        MaxConcurrentBox.Text = AppBootstrapper.Settings.MaxConcurrentDownloads.ToString();
    }

    private void MaxConn_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (int.TryParse(MaxConnBox.Text, out var v))
            AppBootstrapper.SetSetting(s => s.MaxConnectionsPerDownload = Math.Clamp(v, 4, 64));
        MaxConnBox.Text = AppBootstrapper.Settings.MaxConnectionsPerDownload.ToString();
    }

    private void SpeedLimit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (long.TryParse(SpeedLimitBox.Text, out var v))
        {
            AppBootstrapper.SetSetting(s => s.SpeedLimitKbps = Math.Max(0, v));
            GlobalSpeedLimit.SetLimit(AppBootstrapper.Settings.SpeedLimitKbps * 1024);
        }
    }

    private void Organize_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppBootstrapper.SetSetting(s => s.OrganizeByCategory = OrganizeCheck.IsChecked == true);
    }

    // Browser

    private void BookmarksBar_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppBootstrapper.SetSetting(s => s.ShowBookmarksBar = BookmarksBarCheck.IsChecked == true);
        VM.ReloadBookmarksBar();
    }

    private void Restore_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppBootstrapper.SetSetting(s => s.RestoreSession = RestoreCheck.IsChecked == true);
    }

    private void DefaultNic_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (DefaultNicBox.SelectedItem is NicChoice c)
            AppBootstrapper.SetSetting(s => s.DefaultDownloadScope = c.Id);
    }

    // Privacy

    private void ThirdParty_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppBootstrapper.SetSetting(s => s.BlockThirdPartyCookies = ThirdPartyCheck.IsChecked == true);
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        VM.ClearHistoryCommand.Execute(null);
        TestEventResult.Text = "Browsing history cleared.";
    }

    // Telemetry

    private void Sentry_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enabled = SentryCheck.IsChecked == true;
        AppBootstrapper.SetSetting(s => s.SentryEnabled = enabled);
        SentrySetup.Restart(enabled ? SentrySetup.DefaultDsn : "");
    }

    // AI Agent

    private void OllamaUrl_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var url = OllamaUrlBox.Text.Trim();
        if (url.Length == 0) return;
        AppBootstrapper.SetSetting(s => s.OllamaUrl = url);
    }

    private void AgentModel_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var model = AgentModelBox.Text.Trim();
        if (model.Length == 0) return;
        AppBootstrapper.SetSetting(s => s.AgentModel = model);
    }

    private void IdleUnload_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (int.TryParse(IdleUnloadBox.Text, out var v))
            AppBootstrapper.SetSetting(s => s.AgentIdleUnloadSeconds = Math.Clamp(v, 30, 7200));
        IdleUnloadBox.Text = AppBootstrapper.Settings.AgentIdleUnloadSeconds.ToString();
    }

    private async void TestEvent_Click(object sender, RoutedEventArgs e)
    {
        TestEventResult.Text = "Sending…";
        var ok = await SentrySetup.SendTestEventAsync();
        TestEventResult.Text = ok
            ? "✓ Test event sent — check your Sentry dashboard (Issues)."
            : "✕ Sending failed — check the DSN / connection.";
    }
}
