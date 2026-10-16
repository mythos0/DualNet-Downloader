using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DualNet.App.Services;

namespace DualNet.App.Views;

public partial class ExtensionsPage : UserControl
{
    public ExtensionsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        var enabled = AppBootstrapper.Settings.EnabledExtensions;
        ExtList.ItemsSource = ExtensionManager.Available.Select(a => new ExtensionRow(
            a.Name, a.Version, a.Id, a.Dir,
            a.Bundled ? (enabled.Contains(a.Id, StringComparer.Ordinal) || enabled.Count == 0) : true,
            a.Bundled ? "bundled" : "user")).ToList();
    }

    private void Ext_Changed(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExtensionRow row) return;
        if (sender is not CheckBox box) return;
        var nowEnabled = box.IsChecked == true;
        var set = new HashSet<string>(AppBootstrapper.Settings.EnabledExtensions, StringComparer.Ordinal);
        if (nowEnabled) set.Add(row.Id); else set.Remove(row.Id);
        AppBootstrapper.SetSetting(s => s.EnabledExtensions = set.ToList());
        Reload();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ExtensionManager.ExtensionsDir);
            Process.Start(new ProcessStartInfo { FileName = ExtensionManager.ExtensionsDir, UseShellExecute = true });
        }
        catch { }
    }
}

public sealed class ExtensionRow
{
    public string Name { get; }
    public string Version { get; }
    public string Id { get; }
    public string Dir { get; }
    public bool EnabledCheckbox { get; }
    public string TagText { get; }

    public ExtensionRow(string name, string version, string id, string dir, bool enabled, string tag)
    {
        Name = name; Version = version; Id = id; Dir = dir; TagText = tag;
        EnabledCheckbox = enabled;
    }
}
