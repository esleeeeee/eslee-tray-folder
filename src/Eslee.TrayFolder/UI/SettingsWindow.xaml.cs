using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;

namespace Eslee.TrayFolder.UI;

public partial class SettingsWindow : Window
{
    private bool _allowClose;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public event EventHandler<string>? SaveRequested;

    public event EventHandler? DiscoveryRequested;

    public string ExecutablePath
    {
        get => ExecutablePathTextBox.Text;
        set => ExecutablePathTextBox.Text = value;
    }

    public void ShowMessage(string message, bool isError)
    {
        MessageText.Foreground = new System.Windows.Media.SolidColorBrush(
            isError
                ? System.Windows.Media.Color.FromRgb(196, 61, 61)
                : System.Windows.Media.Color.FromRgb(28, 128, 83));
        MessageText.Text = message;
    }

    public void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "AutoPower 실행 파일 선택",
            Filter = "AutoPower 실행 파일 (AutoPower.App.exe)|AutoPower.App.exe|실행 파일 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            ExecutablePath = dialog.FileName;
            MessageText.Text = string.Empty;
        }
    }

    private void OnDiscoverClick(object sender, RoutedEventArgs e) =>
        DiscoveryRequested?.Invoke(this, EventArgs.Empty);

    private void OnSaveClick(object sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(this, ExecutablePath);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
