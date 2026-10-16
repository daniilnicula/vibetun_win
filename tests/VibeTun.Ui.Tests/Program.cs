using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using VibeTun.Models;
using VibeTun.Services;
using VibeTun.ViewModels;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            LogRetention();
            LanguageSwitching();
            ThemeSwitching(app);
            Console.WriteLine("PASS: log retention, runtime localization, theme transitions and accent preservation.");
        }
        finally
        {
            ThemeService.Shutdown();
            app.Shutdown();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void LogRetention()
    {
        var feed = new LogFeed();
        Check(feed.MaxLines == 100, "Default log limit must be 100");
        for (var i = 0; i < 150; i++) feed.Add("line " + i);
        feed.Snapshot();
        Check(feed.Lines.Count == 100 && feed.Lines[0].Text == "line 50", "Keep latest 100 entries");
        feed.MaxLines = 10;
        Check(feed.Lines.Count == 10 && feed.Lines[0].Text == "line 140", "Lower limit trims immediately");
        feed.MaxLines = 200;
        feed.Add("line 150");
        feed.Add("line 150");
        feed.Snapshot();
        Check(feed.Lines.Count == 11 && feed.Lines[^1].Repeat == 2, "Deduplication remains intact");
        feed.MaxLines = 0;
        Check(feed.MaxLines == 1 && feed.Lines.Count == 1, "Invalid limits cannot empty the feed");
        feed.Clear();
        Check(feed.Snapshot() == "", "Clear removes displayed and pending entries");
        var burst = new LogFeed(7);
        Parallel.For(0, 1000, i => burst.Add("parallel " + i));
        burst.Snapshot();
        Check(burst.Lines.Count == 7, "Concurrent producers respect display limit");
    }

    private static void LanguageSwitching()
    {
        Check(L.Language == "ru" && L.T("S0136") == "Настройки", "Default language is Russian");
        var model = new TestModel();
        var notifications = 0;
        model.PropertyChanged += (_, _) => notifications++;
        var text = new TextBlock();
        BindingOperations.SetBinding(text, TextBlock.TextProperty,
            new Binding("[S0136]") { Source = L.Instance });
        Check(text.Text == "Настройки", "Russian binding");
        L.SetLanguage("en");
        Pump();
        Check(text.Text == "Settings" && notifications > 0, "Bindings and view models refresh immediately");
        Check(L.F("S0274", 3, 2) == "3 rules · 2 applications", "English format arguments");
        L.SetLanguage("ru");
        Pump();
        Check(text.Text == "Настройки" && L.F("S0274", 3, 2) == "3 правил · 2 приложений", "Switch back to Russian");
        L.SetLanguage("invalid");
        Check(L.Language == "ru", "Unknown language falls back to Russian");
    }

    private static void ThemeSwitching(Application app)
    {
        ThemeService.Apply(ThemeMode.Dark);
        Check(ColorOf(app, "WindowBg") == (Color)ColorConverter.ConvertFromString("#191B1D"), "Existing dark palette");
        ThemeService.ApplyAccent("#FF123456");
        ThemeService.Apply(ThemeMode.Light);
        Pump(TimeSpan.FromMilliseconds(30));
        Check(ColorOf(app, "WindowBg") != (Color)ColorConverter.ConvertFromString("#F4F5F7"), "Transition does not jump directly to light");
        Pump(TimeSpan.FromMilliseconds(500));
        Check(ColorOf(app, "WindowBg") == (Color)ColorConverter.ConvertFromString("#F4F5F7"), "Existing light palette");
        Check(ColorOf(app, "Accent") == (Color)ColorConverter.ConvertFromString("#FF123456"), "Accent survives theme changes");
        ThemeService.Apply(ThemeMode.Black);
        Pump(TimeSpan.FromMilliseconds(500));
        Check(ColorOf(app, "WindowBg") == Colors.Black, "Black theme has a black window background");
        ThemeService.Apply(ThemeMode.Dark);
        ThemeService.Apply(ThemeMode.Light);
        ThemeService.Apply(ThemeMode.Black);
        Pump(TimeSpan.FromMilliseconds(500));
        Check(ColorOf(app, "WindowBg") == Colors.Black, "Rapid interrupted transitions reach the last selected theme");
        ThemeService.Apply(ThemeMode.System);
        Check(ThemeService.Current == ThemeMode.System &&
              ThemeService.EffectiveMode is ThemeMode.Dark or ThemeMode.Light, "Device theme resolves to light/dark");
        ThemeService.ApplyAccent(null);
    }

    private static Color ColorOf(Application app, string key) => ((SolidColorBrush)app.FindResource(key)).Color;

    private static void Pump(TimeSpan? duration = null)
    {
        var frame = new DispatcherFrame();
        if (duration is { } delay)
        {
            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
        }
        else Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class TestModel : ObservableObject { }
}
