using System.Windows;
using System.Windows.Input;

namespace VibeTun.Views.Dialogs;

public partial class CoreLogWindow : DialogWindow
{
    public CoreLogWindow(string title, string text)
    {
        InitializeComponent();
        TitleText.Text = title;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        LogText.Text = string.Join(Environment.NewLine, lines.TakeLast(Math.Max(1, App.Settings.Current.LogLimit)));
        LogText.CaretIndex = LogText.Text.Length;
        LogText.ScrollToEnd();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogText.Text ?? ""); } catch { /* ignore */ }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
