using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using GoreBox.Models;
using GoreBox.Services;
using GoreBox.Utils;

namespace GoreBox.Views;

/// <summary>
/// Страница одной панели: браузер WebView2 с адресной строкой. URL запоминается за записью
/// панели, страница прогружается в фоне при показе (чтобы вкладка открывалась сразу),
/// при сворачивании в трей браузер выгружается полностью, при возврате — подгружается заново.
/// Окружение WebView2 одно на все страницы (общий процесс браузера и папка профиля).
/// </summary>
public partial class PanelPage : UserControl
{
    private readonly PanelEntry _entry;
    private WebView2? _browser;
    private bool _busy;

    /// <summary>Логин, отправленный на сайт, и ещё не предложенный к сохранению.</summary>
    private sealed record PendingLogin(string Host, string User, string Password, DateTime AtUtc);
    private PendingLogin? _pending;
    private bool _offerShown;   // запрос «Сохранить пароль» уже показан на этой загрузке страницы

    private static Task<CoreWebView2Environment>? _envTask;

    public PanelPage(PanelEntry entry)
    {
        _entry = entry;
        InitializeComponent();
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
        _busy = true;
        try
        {
            var browser = new WebView2();
            BrowserHost.Children.Add(browser);
            _browser = browser;

            var env = await GetEnvAsync();
            await browser.EnsureCoreWebView2Async(env);

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
                RememberUrl(args.Uri);
                _offerShown = false;
            };
            core.SourceChanged += (_, _) => SyncUrlBox();

            var url = Normalize(_entry.Url);
            if (url is not null)
            {
                UrlBox.Text = url;
                core.Navigate(url);
            }
        }
        catch (Exception ex)
        {
            // WebView2 Runtime отсутствует или не запустился — оставляем подсказку вместо браузера
            try { BrowserHost.Children.Clear(); } catch { /* ignore */ }
            _browser = null;
            var hint = new TextBlock
            {
                Text = "Не удалось запустить браузер: " + ex.Message + Environment.NewLine +
                       "Нужен Microsoft Edge WebView2 Runtime (обычно уже установлен в Windows 11).",
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

    /// <summary>Выгрузить страницу (сворачивание в трей): освобождаем ресурсы браузера.</summary>
    public void UnloadBrowser()
    {
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
