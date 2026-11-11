using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VibeTun.Models;
using VibeTun.Services;
using VibeTun.Utils;

namespace VibeTun.Views;

/// <summary>
/// Страница одной панели: браузер WebView2 с адресной строкой. URL запоминается за записью
/// панели, страница прогружается в фоне при показе (чтобы вкладка открывалась сразу),
/// при сворачивании в трей браузер выгружается полностью, при возврате — подгружается заново.
/// Окружение WebView2 одно на все страницы (общий процесс браузера и папка профиля).
/// <para>
/// Память: браузер — самая тяжёлая часть приложения (около 250 МБ даже на простой странице), поэтому
/// страница, которую не видно (открыта другая вкладка или другая панель), через <see cref="SleepAfter"/>
/// засыпает: её скрипты и таймеры останавливаются, состояние сохраняется, а память система может
/// забрать, когда она понадобится (сразу её освобождается немного). Через <see cref="UnloadAfter"/>
/// браузер выгружается совсем, как при сворачивании в трей, — вот тогда память освобождается целиком.
/// Страница, которую подгрузили заранее и к сроку сна так и не открыли, выгружается сразу: усыпить
/// такой браузер нельзя, а беречь в ней нечего. Пока окно приложения скрыто в трее, браузер не создаётся.
/// </para>
/// </summary>
public partial class PanelPage : UserControl
{
    /// <summary>Сколько страницу должно быть не видно, чтобы она заснула (состояние сохраняется).</summary>
    private static readonly TimeSpan SleepAfter = TimeSpan.FromSeconds(60);

    /// <summary>Сколько страницу должно быть не видно, чтобы браузер выгрузился целиком.</summary>
    private static readonly TimeSpan UnloadAfter = TimeSpan.FromMinutes(10);

    private readonly PanelEntry _entry;
    private readonly DispatcherTimer _loadTimeout;
    private readonly DispatcherTimer _idleTimer;
    private long _hiddenAt;     // Environment.TickCount64 на момент, когда страницу перестало быть видно
    private bool _asleep;
    private bool _idleUnloaded; // браузер выгружен из-за простоя: при показе страницы создаём его заново

    /// <summary>
    /// Страницу показывали с тех пор, как создан её браузер. Усыпить можно только такой браузер:
    /// WebView2 засыпает, лишь когда считает себя невидимым, а о видимости элемент WPF сообщает ему
    /// только при её изменении. Браузер, созданный в скрытой вкладке и ни разу не показанный,
    /// продолжает считаться видимым, и TrySuspendAsync для него завершается ошибкой.
    /// </summary>
    private bool _shown;
    private WebView2? _browser;
    private bool _busy;
    private ulong _navigationId;
    private string _navigationUri = "";
    private bool _autoRetryUsed;
    private bool _retryInProgress;

    /// <summary>Логин, отправленный на сайт, и ещё не предложенный к сохранению.</summary>
    private sealed record PendingLogin(string Host, string User, string Password, DateTime AtUtc);
    private PendingLogin? _pending;
    private bool _offerShown;   // запрос «Сохранить пароль» уже показан на этой загрузке страницы

    private static Task<CoreWebView2Environment>? _envTask;

    public PanelPage(PanelEntry entry)
    {
        _entry = entry;
        InitializeComponent();
        _loadTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        _loadTimeout.Tick += OnLoadTimeout;
        _idleTimer = new DispatcherTimer { Interval = SleepAfter };
        _idleTimer.Tick += OnIdleTick;
        IsVisibleChanged += OnPageVisibleChanged;
        var rotation = new DoubleAnimation(0, -360, new Duration(TimeSpan.FromMilliseconds(800)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        LoadingRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, rotation);
        UrlLoadingRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, rotation);
    }

    private void SetLoading(bool loading)
    {
        var visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LoadingOverlay.Visibility = visibility;
        UrlLoadingIndicator.Visibility = visibility;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await EnsureBrowserAsync();

    private static string MarkerPath => Path.Combine(AppPaths.DataRoot, "run", "clear-webview2.flag");

    private static Task<CoreWebView2Environment> GetEnvAsync()
    {
        // Если при прошлой очистке профиль занял процесс браузера — дочищаем его маркером сейчас.
        if (File.Exists(MarkerPath))
        {
            try
            {
                var folder = Path.Combine(AppPaths.DataRoot, "webview2");
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                File.Delete(MarkerPath);
            }
            catch { /* папку кто-то держит — браузер стартует с текущим профилем, маркер останется */ }
        }
        return _envTask ??= CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(AppPaths.DataRoot, "webview2"));
    }

    /// <summary>
    /// Удалить профиль WebView2 (куки, кэш, сохранённые входы всех панелей).
    /// Вызывающий код должен предварительно выгрузить браузеры (PanelView.UnloadAll).
    /// Если папку занято — ставим маркер, и GetEnvAsync дочистит её при следующем запуске.
    /// </summary>
    public static async Task<bool> ClearStoredLoginsAsync()
    {
        var folder = Path.Combine(AppPaths.DataRoot, "webview2");
        var deleted = await Task.Run(() =>
        {
            for (var i = 0; i < 5 && Directory.Exists(folder); i++)
            {
                try { Directory.Delete(folder, recursive: true); }
                catch { Thread.Sleep(400); }
            }
            return !Directory.Exists(folder);
        });

        if (!deleted)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                await File.WriteAllTextAsync(MarkerPath, DateTime.Now.ToString("O"));
            }
            catch { /* ignore */ }
        }
        else if (File.Exists(MarkerPath))
        {
            try { File.Delete(MarkerPath); } catch { /* ignore */ }
        }

        PanelPasswords.DeleteAll();   // сохранённые пароли панелей — тоже часть «входов»
        _envTask = null;   // следующий браузер создаст окружение заново
        return deleted;
    }

    /// <summary>Создать браузер и открыть сохранённый URL (показ панели и возврат из трея).</summary>
    public async Task EnsureBrowserAsync()
    {
        if (_busy || _browser is not null) return;
        // Окно скрыто в трее (в том числе запуск свёрнутым): браузер не создаём — иначе он занимал бы
        // память всё время, пока окна не видно. Страницу подгрузит PanelView.Restore() при показе окна.
        // Страницы панелей живут только в главном окне, поэтому спрашиваем его напрямую.
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: false }) return;
        _busy = true;
        _idleUnloaded = false;
        SetLoading(true);
        try
        {
            var browser = new WebView2();
            BrowserHost.Children.Add(browser);
            _browser = browser;

            var env = await GetEnvAsync();
            await browser.EnsureCoreWebView2Async(env);
            _shown = IsVisible;

            var core = browser.CoreWebView2;

            // Пароли ведёт свой менеджер (PanelPasswords), а не встроенный WebView2: его запрос
            // «Сохранить пароль» в части версий рантайма не появляется для новых логинов.
            // Встроенное автосохранение выключаем, чтобы не было двух запросов подряд.
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = true;

            // скрипт на каждой загрузке страницы: ловит отправку логина и заполняет поля сохранённым паролем
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PanelPasswords.Script);
            core.WebMessageReceived += OnPasswordMessage;

            core.NavigationStarting += (_, args) =>
            {
                _navigationId = args.NavigationId;
                _navigationUri = args.Uri;
                _loadTimeout.Stop();
                _loadTimeout.Start();
                ErrorOverlay.Visibility = Visibility.Collapsed;
                if (!_retryInProgress) LoadingMessage.Visibility = Visibility.Collapsed;
                SetLoading(true);
                RememberUrl(args.Uri);
                _offerShown = false;
            };
            // Завершение старой, уже отменённой навигации не должно отменять таймер новой.
            core.NavigationCompleted += (_, args) =>
            {
                if (args.NavigationId != _navigationId) return;
                _loadTimeout.Stop();
                _retryInProgress = false;
                _autoRetryUsed = false;
                LoadingMessage.Visibility = Visibility.Collapsed;
                ErrorOverlay.Visibility = Visibility.Collapsed;
                SetLoading(false);
            };
            core.SourceChanged += (_, _) => SyncUrlBox();

            var url = Normalize(_entry.Url);
            if (url is not null)
            {
                _autoRetryUsed = false;
                UrlBox.Text = url;
                core.Navigate(url);
            }
            else
            {
                SetLoading(false);
            }

            // страницу подгрузили заранее, в скрытой вкладке: отсчёт до сна и выгрузки идёт сразу
            if (!IsVisible) StartIdleCountdown();
        }
        catch (Exception ex)
        {
            _loadTimeout.Stop();
            _idleTimer.Stop();
            SetLoading(false);
            // WebView2 Runtime отсутствует или не запустился — оставляем подсказку вместо браузера
            try { BrowserHost.Children.Clear(); } catch { /* ignore */ }
            _browser = null;
            var hint = new TextBlock
            {
                Text = L.T("S0935") + ex.Message + Environment.NewLine +
                       L.T("S0936"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(24),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (TryFindResource("TxtMuted") is Style muted) hint.Style = muted;
            BrowserHost.Children.Add(hint);
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnLoadTimeout(object? sender, EventArgs e)
    {
        _loadTimeout.Stop();
        if (_browser?.CoreWebView2 is not { } core || _navigationUri.Length == 0)
        {
            SetLoading(false);
            return;
        }

        if (_autoRetryUsed)
        {
            _navigationId = 0;   // завершение остановленного запроса больше не обрабатываем
            try { core.Stop(); } catch { /* браузер мог уже закрыться */ }
            _retryInProgress = false;
            LoadingMessage.Visibility = Visibility.Collapsed;
            SetLoading(false);
            ErrorMessage.Text = L.T("S1009");
            ErrorOverlay.Visibility = Visibility.Visible;
            return;
        }

        // Новый Navigate отменяет зависший запрос. Повтор только один; после успешной
        // загрузки таймер отключается и никаких периодических обновлений не выполняется.
        _autoRetryUsed = true;
        _retryInProgress = true;
        _navigationId = 0;   // отмена прежнего запроса не должна завершать повтор
        LoadingMessage.Text = L.T("S1008");
        LoadingMessage.Visibility = Visibility.Visible;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        SetLoading(true);
        try
        {
            core.Navigate(_navigationUri);
        }
        catch (Exception ex)
        {
            _retryInProgress = false;
            LoadingMessage.Visibility = Visibility.Collapsed;
            SetLoading(false);
            ErrorMessage.Text = L.T("S1009");
            ErrorOverlay.Visibility = Visibility.Visible;
            App.LogCrash(ex);
        }
    }

    // ------------------------------------------------------------- экономия памяти

    private void OnPageVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && _browser?.CoreWebView2 is not null) _shown = true;
        if (IsVisible) WakeUp();
        else StartIdleCountdown();
    }

    /// <summary>Страницу перестало быть видно: отсчитываем время до сна и до выгрузки.</summary>
    private void StartIdleCountdown()
    {
        _idleTimer.Stop();
        if (_browser is null) return;
        _hiddenAt = Environment.TickCount64;
        _idleTimer.Interval = SleepAfter;
        _idleTimer.Start();
    }

    /// <summary>Страницу снова видно: будим браузер, а выгруженный из-за простоя создаём заново.</summary>
    private void WakeUp()
    {
        _idleTimer.Stop();
        if (_browser is null)
        {
            // В остальных случаях (возврат из трея, ошибка запуска браузера) страницу загружают те же
            // вызовы, что и раньше: PanelView.ShowEntry и PanelView.Restore.
            if (_idleUnloaded) _ = EnsureBrowserAsync();
            return;
        }

        if (!_asleep) return;
        _asleep = false;
        if (_browser.CoreWebView2 is not { } core) return;
        try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; }
        catch { /* рантайм без этой настройки */ }
        // видимый браузер просыпается и сам; явный вызов — на случай, если этого ещё не произошло
        try { if (core.IsSuspended) core.Resume(); }
        catch { /* браузер мог уже закрыться */ }
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        _idleTimer.Stop();
        if (_browser is null || IsVisible) return;

        var hidden = TimeSpan.FromMilliseconds(Environment.TickCount64 - _hiddenAt);
        // Подошёл срок выгрузки — либо срок сна у страницы, которую ни разу не показывали: усыпить её
        // браузер нельзя (см. _shown), а состояния, которое стоило бы беречь, в ней ещё нет. При показе
        // страница загрузится заново, как после возврата из трея. Браузер, который ещё создаётся, не трогаем.
        if (hidden >= UnloadAfter || (!_shown && !_busy))
        {
            UnloadBrowser();
            _idleUnloaded = true;
            return;
        }

        // Страница, которая ещё создаётся или грузится, не засыпает: ждём следующего срабатывания.
        if (!_asleep && !_busy && !_loadTimeout.IsEnabled && _browser.CoreWebView2 is { } core)
        {
            _asleep = true;
            _ = SleepAsync(core);
        }

        var left = UnloadAfter - hidden;
        _idleTimer.Interval = _asleep || left < SleepAfter ? left : SleepAfter;
        _idleTimer.Start();
    }

    /// <summary>
    /// Сон скрытой страницы — то же, что «спящая вкладка» в браузере: скрипты и таймеры страницы
    /// останавливаются, память процесса система может забрать под другие задачи, а сама страница
    /// (введённый текст, прокрутка, вход в панель) остаётся как была. Оба вызова необязательные:
    /// рантайм может их не поддерживать.
    /// </summary>
    private static async Task SleepAsync(CoreWebView2 core)
    {
        try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; }
        catch { /* рантайм без этой настройки */ }
        try { await core.TrySuspendAsync(); }
        catch { /* браузер считает себя видимым или уже закрыт — страница просто продолжит работать */ }
    }

    /// <summary>Выгрузить страницу (трей, долгий простой, удаление панели): освобождаем ресурсы браузера.</summary>
    public void UnloadBrowser()
    {
        _loadTimeout.Stop();
        _idleTimer.Stop();
        _asleep = false;
        _shown = false;
        _idleUnloaded = false;
        _retryInProgress = false;
        LoadingMessage.Visibility = Visibility.Collapsed;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        SetLoading(false);
        var browser = _browser;
        _browser = null;
        if (browser is null) return;
        BrowserHost.Children.Clear();
        try { browser.Dispose(); }
        catch { /* процесс WebView2 мог уже завершиться */ }
    }

    /// <summary>Сообщения от скрипта страницы (логин, запрос заполнения, ответ на «Сохранить пароль»).</summary>
    private void OnPasswordMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (sender is not CoreWebView2 core) return;
            var json = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(json)) return;

            var host = PanelPasswords.HostOf(e.Source);
            if (host.Length == 0) return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "want":
                {
                    var hasPassword = root.TryGetProperty("hasPassword", out var hp) && hp.GetBoolean();
                    if (hasPassword && PanelPasswords.Find(host) is { } saved)
                        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "fill", user = saved.User, pass = saved.Password }));

                    if (_pending is { } p && p.Host == host && !_offerShown &&
                        DateTime.UtcNow - p.AtUtc < TimeSpan.FromMinutes(3))
                    {
                        _offerShown = true;
                        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "offer", user = p.User }));
                    }
                    break;
                }
                case "captured":
                {
                    var user = root.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "";
                    var pass = root.TryGetProperty("pass", out var ps) ? ps.GetString() ?? "" : "";
                    // уже сохранённый точно такой же вход повторно не предлагаем
                    _pending = pass.Length == 0 || PanelPasswords.Has(host, user, pass)
                        ? null
                        : new PendingLogin(host, user, pass, DateTime.UtcNow);
                    break;
                }
                case "save":
                    if (_pending is { } s && s.Host == host) PanelPasswords.Save(s.Host, s.User, s.Password);
                    _pending = null;
                    break;
                case "dismiss":
                    _pending = null;
                    break;
            }
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
    }

    private void OnUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitUrl();
    }

    private void OnUrlLostFocus(object sender, RoutedEventArgs e) => CommitUrl();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => CommitUrl();

    private void CommitUrl()
    {
        var url = Normalize(UrlBox.Text);
        if (url is null) return;
        _entry.Url = url;   // запоминаем последний введённый адрес за этой панелью
        App.Settings.SaveSoon();
        UrlBox.Text = url;
        _autoRetryUsed = false;
        _retryInProgress = false;
        LoadingMessage.Visibility = Visibility.Collapsed;
        ErrorOverlay.Visibility = Visibility.Collapsed;
        // Реакция видна сразу, ещё до NavigationStarting: Enter и «Обновить» работают одинаково.
        _loadTimeout.Stop();
        _navigationId = 0;
        SetLoading(true);
        if (_browser?.CoreWebView2 is { } core) core.Navigate(url);
        else _ = EnsureBrowserAsync();
    }

    private void RememberUrl(string uri)
    {
        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        _entry.Url = uri;
        App.Settings.SaveSoon();
    }

    private void SyncUrlBox()
    {
        if (_browser?.CoreWebView2 is not { } core) return;
        if (UrlBox.IsKeyboardFocused) return;
        var src = core.Source;
        if (string.IsNullOrWhiteSpace(src) || src.Contains("about:blank", StringComparison.OrdinalIgnoreCase)) return;
        UrlBox.Text = src;
    }

    /// <summary>Схему https:// добавляем сами; строки без неё (yandex.ru) тоже открываем.</summary>
    private static string? Normalize(string? text)
    {
        var s = text?.Trim();
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var uri) ? uri.ToString() : null;
    }
}
