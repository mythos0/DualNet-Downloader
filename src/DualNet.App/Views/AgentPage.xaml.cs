using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DualNet.App.ViewModels;

namespace DualNet.App.Views;

public partial class AgentPage : UserControl
{
    private AgentViewModel VM => (AgentViewModel)DataContext!;

    public AgentPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { VM.RefreshStatusCommandSafe(); TaskBox.Focus(); };
    }

    private void TaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !VM.IsRunning && VM.TaskInput.Trim().Length > 0)
        {
            VM.RunTaskCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void RefreshStatus_Click(object sender, RoutedEventArgs e) => VM.RefreshStatusCommandSafe();

    private void UnloadNow_Click(object sender, RoutedEventArgs e) => _ = VM.UnloadNowAsync();
}
