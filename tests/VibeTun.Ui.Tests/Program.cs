using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using VibeTun.Models;
using VibeTun.Services;
using VibeTun.Utils;
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
            ZipSafety();
            CoreAssetChoice();
            Console.WriteLine("PASS: log retention, runtime localization, theme transitions, accent preservation, archive safety and core asset choice.");
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
        // В headless-тесте WPF может завершить анимацию сразу, поэтому промежуточный кадр
        // по таймеру не проверяем: важен гарантированный конечный цвет после перехода.
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
        ThemeService.ApplyAccent(null, useSystem: true);
        Check(ThemeService.UseSystemAccent && ColorOf(app, "Accent") == ThemeService.GetSystemAccentColor(),
            "System accent mode applies the current Windows color");
        ThemeService.ApplyAccent("#FF123456");
        Check(!ThemeService.UseSystemAccent && ColorOf(app, "Accent") == (Color)ColorConverter.ConvertFromString("#FF123456"),
            "Manual accent selection disables system color tracking");
        ThemeService.ApplyAccent(null);
    }

    /// <summary>
    /// ZipGuard стоит перед каждой распаковкой архива из сети (обновление приложения, ядро): обычный
    /// архив распаковывается байт в байт, а бомба, выход из папки назначения, оборванная загрузка и
    /// испорченные данные отклоняются — и мимо папки назначения ничего не пишется.
    /// </summary>
    /// <summary>
    /// Which archive of a release the core installer takes. The lists repeat real releases: the
    /// variants of an architecture sort before its main archive, and the installer used to take
    /// "-legacy-windows-7" for amd64.
    /// </summary>
    private static void CoreAssetChoice()
    {
        var official = new[]
        {
            "sing-box-1.14.4-windows-386-legacy-windows-7.zip",
            "sing-box-1.14.4-windows-386.zip",
            "sing-box-1.14.4-windows-amd64-legacy-windows-7.zip",
            "sing-box-1.14.4-windows-amd64.zip",
            "sing-box-1.14.4-windows-arm64.zip"
        };
        var extended = new[]
        {
            "sing-box-1.14.3-extended-2.7.3-windows-386.zip",
            "sing-box-1.14.3-extended-2.7.3-windows-amd64-purego.zip",
            "sing-box-1.14.3-extended-2.7.3-windows-amd64.zip",
            "sing-box-1.14.3-extended-2.7.3-windows-arm64-purego.zip",
            "sing-box-1.14.3-extended-2.7.3-windows-arm64.zip"
        };
        var fork = new[]
        {
            "sing-box-1.14.4-lx.33-windows-386-legacy-windows-7.zip",
            "sing-box-1.14.4-lx.33-windows-amd64.zip",
            "sing-box-1.14.4-lx.33-windows-arm64.zip"
        };

        static string Picked(string[] names, string arch)
        {
            var index = CoreAssetPicker.Pick(names, arch);
            return index < 0 ? "" : names[index];
        }

        Check(Picked(official, "amd64") == "sing-box-1.14.4-windows-amd64.zip", "amd64 takes the main archive, not legacy-windows-7");
        Check(Picked(official, "386") == "sing-box-1.14.4-windows-386.zip", "386 takes the main archive, not legacy-windows-7");
        Check(Picked(official, "arm64") == "sing-box-1.14.4-windows-arm64.zip", "arm64 takes its own archive");
        Check(Picked(extended, "amd64") == "sing-box-1.14.3-extended-2.7.3-windows-amd64.zip", "amd64 takes the main archive, not purego");
        Check(Picked(extended, "arm64") == "sing-box-1.14.3-extended-2.7.3-windows-arm64.zip", "arm64 takes the main archive, not purego");
        Check(Picked(fork, "386") == "sing-box-1.14.4-lx.33-windows-386-legacy-windows-7.zip", "A variant is taken when the architecture has no main archive");
        Check(Picked(new[] { "core-windows-arm64.zip", "core-windows-amd64-purego.zip" }, "amd64") == "core-windows-amd64-purego.zip",
            "A variant of our architecture beats the main archive of another one");
        Check(Picked(new[] { "core-windows-arm64-purego.zip", "core-windows-arm64.zip" }, "amd64") == "core-windows-arm64.zip",
            "Nothing for our architecture: the first full archive is taken");
        Check(CoreAssetPicker.Pick(Array.Empty<string>(), "amd64") == -1, "An empty release yields no archive");
    }

    private static void ZipSafety()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vibetun-zip-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // обычный архив: запись-папка, файл во вложенной папке, файл в корне
            var good = Path.Combine(dir, "good.zip");
            var payload = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31 % 251)).ToArray();
            WriteZip(good, zip =>
            {
                zip.CreateEntry("bin/");
                AddEntry(zip, "bin/core.exe", payload);
                AddEntry(zip, "readme.txt", new byte[] { (byte)'o', (byte)'k' });
            });
            ZipGuard.Validate(good);
            var target = Path.Combine(dir, "out");
            ZipGuard.ExtractToDirectory(good, target);
            Check(File.ReadAllBytes(Path.Combine(target, "bin", "core.exe")).SequenceEqual(payload) &&
                  File.ReadAllText(Path.Combine(target, "readme.txt")) == "ok",
                "A regular archive is extracted byte for byte");

            // zip-slip: запись, которая легла бы мимо папки назначения
            foreach (var name in new[] { "../evil.txt", "bin/../../evil.txt", "/evil.txt", "C:/evil.txt", "bin\\..\\..\\evil.txt" })
            {
                var slip = Path.Combine(dir, "slip.zip");
                WriteZip(slip, zip => AddEntry(zip, name, new byte[] { 1 }));
                var slipOut = Path.Combine(dir, "slip-out");
                Check(Rejects(() => ZipGuard.Validate(slip)) && Rejects(() => ZipGuard.ExtractToDirectory(slip, slipOut)),
                    "An entry that escapes the target folder is rejected: " + name);
                Check(!Directory.Exists(slipOut) && !File.Exists(Path.Combine(dir, "evil.txt")),
                    "A rejected archive writes nothing: " + name);
            }

            // бомба: 8 МБ нулей в нескольких килобайтах
            var bomb = Path.Combine(dir, "bomb.zip");
            WriteZip(bomb, zip => AddEntry(zip, "zeros.bin", new byte[8 * 1024 * 1024]));
            Check(new FileInfo(bomb).Length < 100 * 1024, "8 MB of zeros compress to a few kilobytes");
            var bombOut = Path.Combine(dir, "bomb-out");
            Check(Rejects(() => ZipGuard.Validate(bomb)) && Rejects(() => ZipGuard.ExtractToDirectory(bomb, bombOut)),
                "An implausible compression ratio is rejected");
            Check(!Directory.Exists(bombOut), "A zip bomb is rejected before anything is unpacked");

            // общий объём: записи ровно по 1 МБ коэффициентом не проверяются — срабатывает именно лимит суммы
            var big = Path.Combine(dir, "big.zip");
            var megabyte = new byte[1024 * 1024];
            var parts = (int)(ZipGuard.MaxTotalUncompressedBytes / megabyte.Length) + 1;
            WriteZip(big, zip =>
            {
                for (var i = 0; i < parts; i++) AddEntry(zip, "part" + i, megabyte, CompressionLevel.Fastest);
            });
            Check(Rejects(() => ZipGuard.Validate(big)), "The total unpacked size is limited");

            // слишком много записей
            var many = Path.Combine(dir, "many.zip");
            WriteZip(many, zip =>
            {
                for (var i = 0; i <= ZipGuard.MaxEntries; i++) zip.CreateEntry("f" + i);
            });
            Check(Rejects(() => ZipGuard.Validate(many)), "The number of entries is limited");

            // страница ошибки вместо файла и оборванная загрузка
            var page = Path.Combine(dir, "page.zip");
            File.WriteAllText(page, "<html><body>404</body></html>");
            Check(Rejects(() => ZipGuard.Validate(page)), "A web page saved as an archive is rejected");
            var cut = Path.Combine(dir, "cut.zip");
            var whole = File.ReadAllBytes(good);
            File.WriteAllBytes(cut, whole.AsSpan(0, whole.Length / 2).ToArray());
            Check(Rejects(() => ZipGuard.Validate(cut)), "A truncated download is rejected");

            // испорченный байт данных: оглавление цело, контрольная сумма файла не сходится
            var damaged = Path.Combine(dir, "damaged.zip");
            var marker = Enumerable.Repeat((byte)'A', 4096).ToArray();
            WriteZip(damaged, zip => AddEntry(zip, "data.bin", marker, CompressionLevel.NoCompression));
            var raw = File.ReadAllBytes(damaged);
            var at = new ReadOnlySpan<byte>(raw).IndexOf(new ReadOnlySpan<byte>(marker, 0, 64));
            Check(at > 0, "Uncompressed data is stored in the archive as is");
            raw[at + 10] ^= 0xFF;
            File.WriteAllBytes(damaged, raw);
            Check(Rejects(() => ZipGuard.ExtractToDirectory(damaged, Path.Combine(dir, "damaged-out"))),
                "A file whose checksum does not match is rejected");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* временная папка: не критично */ }
        }
    }

    private static void WriteZip(string path, Action<ZipArchive> fill)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        fill(zip);
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] data,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        using var stream = zip.CreateEntry(name, level).Open();
        stream.Write(data, 0, data.Length);
    }

    /// <summary>
    /// Действие отклонено ZipGuard. Ловим как IOException: для кода установки отклонённый архив — обычная
    /// ошибка чтения файла, и его обработчики такого исключения обязаны её перехватывать.
    /// </summary>
    private static bool Rejects(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (IOException ex) when (ex is ZipRejectedException)
        {
            return ex.Message.Length > 0;
        }
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
