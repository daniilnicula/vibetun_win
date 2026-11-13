using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using VibeTun.Services;
using VibeTun.Utils;
using VibeTun.ViewModels;
using VibeTun.Views;
using VibeTun.Views.Controls;

namespace VibeTun;

public partial class App : Application
{
    private const string MutexName = "VibeTun.SingleInstance";
    private const string PipeName = "VibeTun.ShowWindow";

    private Mutex? _mutex;
    private MainWindow? _window;
    public static SettingsService Settings { get; } = new();

    public static bool StartMinimized { get; private set; }

    /// <summary>Что сообщить после запуска о месте хранения данных (или null).</summary>
    public static string? StartupNotice { get; private set; }

    /// <summary>Перезапустить приложение при выходе (нужно после смены места хранения данных).</summary>
    public static bool RestartOnExit { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var restarted = e.Args.Any(a => a.Equals("--restarted", StringComparison.OrdinalIgnoreCase));

        // при перезапуске (смена места хранения) предыдущий экземпляр ещё освобождает мьютекс — подождём его
        _mutex = new Mutex(false, MutexName);
        bool owned;
        try
        {
            owned = _mutex.WaitOne(restarted ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true;   // предыдущий экземпляр завершился аварийно — мьютекс остался у нас
        }
        if (!owned)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        if (restarted) Thread.Sleep(1500);   // даём браузеру «Панели» отпустить файлы перед переносом данных

        // папка данных выбирается до загрузки настроек: от неё зависят все остальные пути
        StartupNotice = StorageLocation.Initialize();
        AppPaths.EnsureCreated();
        AppUpdateService.CleanupOldExecutable();
        Settings.Load();
        L.SetLanguage(Settings.Current.Language);
        ThemeService.Apply(Settings.Current.Theme);
        ThemeService.ApplyAccent(Settings.Current.AccentColor, Settings.Current.UseSystemAccent);
        Motion.SetSpeed(Settings.Current.AnimationSpeed);

        // настройка «запускать от имени администратора»: сразу перезапускаемся с правами,
        // чтобы пользователю не приходилось делать это вручную. Флаг --elevated защищает от
        // бесконечного цикла, --restarted даёт новому экземпляру подождать освобождения мьютекса.
        if (Settings.Current.RunAsAdmin && !IsAdministrator() &&
            !e.Args.Any(a => a.Equals("--elevated", StringComparison.OrdinalIgnoreCase)))
        {
            if (TryStartElevated()) { Shutdown(); return; }
            // отказ от UAC — продолжаем без прав
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteCrashLog(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        _window = new MainWindow();
        MainWindow = _window;

        if (!StartMinimized) _window.Show();
        else _window.Hide();

        SessionEnding += (_, _) =>
        {
            try { _window?.PrepareForSystemShutdown(); } catch { /* ignore */ }
        };

        _ = _window.InitializeAsync();
        StartPipeListener();

        // Скрытые TUN/TAP-адаптеры и фантомы Device Manager («sing-tun Tunnel») от убитых ядер:
        // чистим сразу при запуске, не дожидаясь TUN-старта. Живые туннели (Status = Up)
        // не трогаются; без прав администратора удаление всё равно не сработает.
        if (IsAdministrator())
            _ = Task.Run(() => { try { TunCleanup.CleanupHiddenTunAdapters(); } catch { /* ignore */ } });

        // сетевой облик (User-Agent): режим из настроек, свежая версия браузера раз в сутки, фоном и молча
        NetworkPersonaService.Initialize(Settings.Current.PersonaMode);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        try
        {
            MessageBox.Show(L.T("S0268") + e.Exception.Message +
                            L.T("S0269") + Path.Combine(AppPaths.LogsDir, "errors.log"),
                "VibeTun", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* ignore */ }
        e.Handled = true;
    }

    /// <summary>Записать исключение в errors.log (используется обработчиками, которые не должны падать).</summary>
    public static void LogCrash(Exception? ex) => WriteCrashLog(ex);

    private static void WriteCrashLog(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(Path.Combine(AppPaths.LogsDir, "errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }

    // ------------------------------------------------- единый экземпляр приложения
    private void StartPipeListener()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server);
                    var command = await reader.ReadLineAsync();

                    if (string.Equals(command, "SHOW", StringComparison.OrdinalIgnoreCase))
                        await Dispatcher.InvokeAsync(() => _window?.ShowFromTray());
                }
                catch
                {
                    await Task.Delay(500);
                }
            }
        });
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("SHOW");
        }
        catch { /* ignore */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Settings.SaveNow();
        }
        catch { /* ignore */ }

        ThemeService.Shutdown();
        try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        _mutex?.Dispose();

        // мьютекс уже освобождён: новый экземпляр стартует без конфликта
        if (RestartOnExit) StartNewInstance();
        base.OnExit(e);
    }

    internal static void StartNewInstance()
    {
        try
        {
            var exe = AppPaths.ExecutablePath;
            if (string.IsNullOrEmpty(exe)) return;
            Process.Start(new ProcessStartInfo(exe, "--restarted")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            });
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    // --------------------------------------------------- запуск от имени администратора
    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Поднять новый экземпляр через UAC (verb runas). False — пользователь отклонил запрос.</summary>
    private static bool TryStartElevated()
    {
        try
        {
            var exe = AppPaths.ExecutablePath;
            if (string.IsNullOrEmpty(exe)) return false;
            Process.Start(new ProcessStartInfo(exe, "--restarted --elevated")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            });
            return true;
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            return false;
        }
    }
}
