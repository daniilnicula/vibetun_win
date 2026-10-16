using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using VibeTun.Models;
using VibeTun.Services;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace VibeTun.Views;

/// <summary>
/// Страница одного SSH-подключения: строка подключения и интерактивный терминал (PTY), как
/// в PuTTY. Ctrl+C прерывает процесс, Ctrl+Shift+V / Ctrl+V вставляют из буфера, Ctrl+Shift+C
/// копирует. Адрес и порт запоминаются за записью подключения (общие логин/пароль — в
/// настройках); в трей соединение НЕ рвётся, рвётся только при выходе или удалении записи.
/// </summary>
public partial class SshPage : UserControl
{
    private const int MaxLineCharacters = 32_000;

    private readonly SshEntry _entry;
    private SshClient? _client;
    private ShellStream? _shell;
    private bool _ready;
    private bool _initialized;   // страницу показывают многократно — инициализация один раз

    // состояние потокового разбора вывода терминала
    private int _esc;              // 0 — текст, 1 — ESC, 2 — CSI, 3 — OSC, 4 — ESC внутри OSC
    private bool _crPending;       // видели \r, ждём \n
    private readonly StringBuilder _csi = new();
    private bool _ansiAccent;
    private bool _hostKeyRejected;
    private int _lineCharacters;
    private Run? _lastRun;
    private bool _lastRunAccent;

    private Paragraph? _line;      // текущая (незаконченная) строка вывода

    public SshPage(SshEntry entry)
    {
        _entry = entry;
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;   // повторные показы страницы не трогают живое соединение
        _initialized = true;

        HostBox.Text = _entry.Host;
        PortBox.Text = _entry.Port > 0 ? _entry.Port.ToString() : "22";

        _ready = true;
        var size = Math.Clamp(_entry.FontSize, 9, 20);
        FontBar.Alpha = (size - 9) / 11.0;
        // полоска красится акцентом — как прозрачность в пикере акцента
        var stored = App.Settings.Current.AccentColor;
        FontBar.BaseColor = !string.IsNullOrWhiteSpace(stored) && ThemeService.TryParseColor(stored!, out var c)
            ? c
            : ThemeService.DefaultAccent;
        ApplyFont(size);

        SetConnected(false);
        Write(L.T("S0937"),
            GetBrush("TextSecondary"));
    }

    // ------------------------------------------------------------- подключение

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_client is { IsConnected: true }) return;

        var raw = HostBox.Text.Trim();
        if (raw.Length == 0)
        {
            Write(L.T("S0938"), GetBrush("Warning"));
            return;
        }

        var user = "";
        var host = raw;
        var at = raw.IndexOf('@');
        if (at >= 0)
        {
            user = raw[..at].Trim();
            host = raw[(at + 1)..].Trim();
        }
        if (host.Length == 0)
        {
            Write(L.T("S0939"), GetBrush("Warning"));
            return;
        }

        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535) port = 22;
        PortBox.Text = port.ToString();

        _entry.Host = raw;
        _entry.Port = port;

        var owner = Window.GetWindow(this) ?? Application.Current.MainWindow;

        // логин свой для записи (шестерёнка в списке), важнее user@ из адреса.
        var storedLogin = _entry.User?.Trim() ?? "";
        if (storedLogin.Length > 0)
        {
            user = storedLogin;
        }

        // пароль свой для записи. Если логина или пароля нет — показываем окно с тем, что
        // известно: логин и пароль подставляются и редактируются разом, сохраняем за записью.
        var password = _entry.Password ?? "";
        if (user.Length == 0 || password.Length == 0)
        {
            var window = new Dialogs.SshCredentialsWindow($"SSH — {host}", user, password) { Owner = owner };
            if (window.ShowDialog() != true)
            {
                Write(L.T("S0940"), GetBrush("Warning"));
                return;
            }
            user = window.User;
            password = window.Password;
            _entry.User = user;
            _entry.Password = password;
        }
        App.Settings.SaveSoon();

        Write(L.F("S0941", user, host, port), GetBrush("TextSecondary"));
        SetConnected(false, busy: true);
        SshClient? pendingClient = null;
        _hostKeyRejected = false;
        try
        {
            var conn = new ConnectionInfo(host, port, user,
                new PasswordAuthenticationMethod(user, password))
            {
                Timeout = TimeSpan.FromSeconds(12),
            };
            var client = new SshClient(conn);
            pendingClient = client;
            client.HostKeyReceived += (_, args) => VerifyHostKey(args, host, port, owner);
            var shell = await Task.Run(() =>
            {
                client.Connect();
                return client.CreateShellStream("xterm-256color", 140, 40, 1280, 720, 4096);
            });

            _client = client;
            _shell = shell;
            pendingClient = null;   // ownership moved to this page
            SetConnected(true);
            AppLog.Write(L.F("S0942", user, host, port));
            Write(L.F("S0943", user, host, port) +
                  L.T("S0944"), GetBrush("Accent"));
            _ansiAccent = false;
            _esc = 0;
            _csi.Clear();
            _crPending = false;
            StartReader(shell);
        }
        catch (Exception ex)
        {
            try { pendingClient?.Dispose(); } catch { /* ignore */ }
            Write(_hostKeyRejected ? L.T("S0999") : L.T("S0945") + ex.Message, GetBrush("Warning"));
            AppLog.Write(L.T("S0946") + ex.Message);
            SetConnected(false);
        }
    }

    private void VerifyHostKey(HostKeyEventArgs args, string host, int port, Window? owner)
    {
        var fingerprint = "SHA256:" + args.FingerPrintSHA256;
        var algorithm = args.HostKeyName;
        var trusted = false;

        try
        {
            Dispatcher.Invoke(() =>
            {
                var settings = App.Settings.Current;
                settings.SshHostKeys ??= new List<SshKnownHostKey>();
                var normalizedHost = NormalizeHostKeyHost(host);
                var known = settings.SshHostKeys.FirstOrDefault(key => key is not null &&
                    !string.IsNullOrWhiteSpace(key.Host) && key.Port == port &&
                    string.Equals(NormalizeHostKeyHost(key.Host), normalizedHost, StringComparison.Ordinal) &&
                    string.Equals(key.Algorithm, algorithm, StringComparison.OrdinalIgnoreCase));

                if (known is not null && string.Equals(known.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    trusted = true;
                    return;
                }

                var endpoint = FormatSshEndpoint(host, port);
                var changed = known is not null;
                var message = changed
                    ? L.F("S0997", endpoint, algorithm, known!.Fingerprint, fingerprint)
                    : L.F("S0995", endpoint, algorithm, fingerprint);
                var button = L.T(changed ? "S0998" : "S0996");
                var dialog = new Dialogs.ConfirmWindow(L.T("S0994"), message, button,
                    danger: changed, okOnly: false)
                {
                    Owner = owner ?? Application.Current.MainWindow
                };
                trusted = dialog.ShowDialog() == true;
                if (!trusted) return;

                if (known is null)
                {
                    settings.SshHostKeys.Add(new SshKnownHostKey
                    {
                        Host = normalizedHost,
                        Port = port,
                        Algorithm = algorithm,
                        Fingerprint = fingerprint,
                    });
                }
                else
                {
                    known.Fingerprint = fingerprint;
                }
                App.Settings.SaveSoon();
            });
        }
        catch
        {
            trusted = false;
        }

        args.CanTrust = trusted;
        if (!trusted) _hostKeyRejected = true;
    }

    private static string NormalizeHostKeyHost(string host) =>
        host.Trim().Trim('[', ']').ToLowerInvariant();

    private static string FormatSshEndpoint(string host, int port) =>
        host.Contains(':') && !host.StartsWith("[", StringComparison.Ordinal)
            ? $"[{host}]:{port}"
            : $"{host}:{port}";

    private void OnDisconnectClick(object sender, RoutedEventArgs e) => Disconnect(L.T("S0947"));

    private void Disconnect(string message)
    {
        var shell = _shell;
        _shell = null;
        var client = _client;
        _client = null;

        if (shell is not null || client is not null) AppLog.Write(L.T("S0948"));
        try { shell?.Dispose(); } catch { /* канал мог уже закрыться */ }
        if (client is not null)
        {
            try { client.Disconnect(); } catch { /* уже отключён */ }
            try { client.Dispose(); } catch { /* ignore */ }
        }

        SetConnected(false);
        if (message.Length > 0) Write(message, GetBrush("TextSecondary"));
    }

    /// <summary>Полное закрытие при выходе из приложения (в трей — не вызывается).</summary>
    public void Shutdown() => Disconnect("");

    private void SetConnected(bool connected, bool busy = false)
    {
        ConnectBtn.IsEnabled = !connected && !busy;
        DisconnectBtn.IsEnabled = connected;
        if (connected) Term.Focus();
    }

    // ------------------------------------------------------------- поток вывода

    private void StartReader(ShellStream shell)
    {
        _ = Task.Run(() =>
        {
            var buf = new byte[4096];
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[4096];
            try
            {
                while (true)
                {
                    var n = shell.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    var count = decoder.GetChars(buf, 0, n, chars, 0);
                    if (count <= 0) continue;
                    var text = new string(chars, 0, count);
                    try { Dispatcher.Invoke(() => Feed(text)); }
                    catch { break; }   // окно уже закрыто
                }
            }
            catch { /* канал закрыт */ }
            finally
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!ReferenceEquals(_shell, shell)) return;  // отключились сами
                        _shell = null;
                        var client = _client;
                        _client = null;
                        try { client?.Dispose(); } catch { /* ignore */ }
                        SetConnected(false);
                        Write(L.T("S0949"), GetBrush("TextSecondary"));
                        AppLog.Write(L.T("S0950"));
                    }));
                }
                catch { /* dispatcher завершён */ }
            }
        });
    }

    /// <summary>
    /// Разбирает вывод терминала и применяет акцент ко всем ANSI-цветам, кроме белого.
    /// Текст буферизуется по фрагментам, а RichTextBox прокручивается один раз на пакет,
    /// чтобы интенсивный поток не создавал Run и layout-обновление на каждый символ.
    /// </summary>
    private void Feed(string text)
    {
        var pending = new StringBuilder();
        var pendingAccent = _ansiAccent;

        void FlushPending()
        {
            if (pending.Length == 0) return;
            Append(pending.ToString(), pendingAccent);
            pending.Clear();
        }

        void PreparePending()
        {
            if (pending.Length > 0 && pendingAccent != _ansiAccent) FlushPending();
            if (pending.Length == 0) pendingAccent = _ansiAccent;
        }

        void BufferText(string value)
        {
            PreparePending();
            pending.Append(value);
        }

        void BufferChar(char value)
        {
            PreparePending();
            pending.Append(value);
        }

        foreach (var ch in text)
        {
            if (_esc != 0)
            {
                switch (_esc)
                {
                    case 1:
                        if (ch == '[') { _esc = 2; _csi.Clear(); continue; }
                        if (ch == ']') { _esc = 3; continue; }
                        _esc = 0;
                        continue;
                    case 2:
                        if (ch >= '\u0040' && ch <= '\u007e')
                        {
                            if (ch == 'm')
                            {
                                FlushPending();
                                ApplyAnsiSgr(_csi.ToString());
                            }
                            _csi.Clear();
                            _esc = 0;
                        }
                        else if (_csi.Length < 128)
                        {
                            _csi.Append(ch);
                        }
                        continue;
                    case 3:
                        if (ch == '\a') _esc = 0;
                        else if (ch == '\u001b') _esc = 4;
                        continue;
                    case 4:
                        _esc = ch == '\\' ? 0 : 3;
                        continue;
                }
            }

            if (ch == '\u001b') { _esc = 1; continue; }

            // одиночный \r без \n — тоже перевод строки (без колонок это лучшее приближение)
            if (_crPending && ch != '\n')
            {
                _crPending = false;
                FlushPending();
                NewLine();
            }

            switch (ch)
            {
                case '\r':
                    _crPending = true;
                    continue;
                case '\n':
                    _crPending = false;
                    FlushPending();
                    NewLine();
                    continue;
                case '\b':
                    FlushPending();
                    TrimLast();
                    continue;
                case '\t':
                    BufferText("    ");
                    continue;
                case '\a':
                case '\0':
                    continue;
            }

            if (ch < ' ') continue;
            BufferChar(ch);
        }

        FlushPending();
        PruneAndScroll();
    }

    private void ApplyAnsiSgr(string sequence)
    {
        if (sequence.Length == 0)
        {
            _ansiAccent = false;
            return;
        }

        var parts = sequence.Split(';');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Contains(':'))
            {
                ApplyColonSgr(parts[i]);
                continue;
            }

            var code = parts[i].Length == 0 ? 0 : int.TryParse(parts[i], out var parsed) ? parsed : -1;
            if (code == 38)
            {
                if (i + 2 < parts.Length && int.TryParse(parts[i + 1], out var mode))
                {
                    if (mode == 5 && int.TryParse(parts[i + 2], out var indexedColor))
                    {
                        _ansiAccent = !IsAnsiWhiteIndex(indexedColor);
                        i += 2;
                        continue;
                    }
                    if (mode == 2 && i + 4 < parts.Length &&
                        int.TryParse(parts[i + 2], out var red) &&
                        int.TryParse(parts[i + 3], out var green) &&
                        int.TryParse(parts[i + 4], out var blue))
                    {
                        _ansiAccent = !IsWhiteRgb(red, green, blue);
                        i += 4;
                        continue;
                    }
                }
                _ansiAccent = true;
                continue;
            }

            ApplyAnsiSgrCode(code);
        }
    }

    private void ApplyColonSgr(string parameter)
    {
        var parts = parameter.Split(':');
        if (!int.TryParse(parts[0], out var code)) return;
        if (code != 38)
        {
            ApplyAnsiSgrCode(code);
            return;
        }

        if (parts.Length >= 3 && int.TryParse(parts[1], out var mode) && mode == 5 &&
            int.TryParse(parts[^1], out var indexedColor))
        {
            _ansiAccent = !IsAnsiWhiteIndex(indexedColor);
            return;
        }

        if (parts.Length >= 5 && int.TryParse(parts[1], out mode) && mode == 2)
        {
            var components = parts.Skip(2).Where(part => int.TryParse(part, out _))
                .Select(part => int.Parse(part)).ToArray();
            if (components.Length >= 3)
            {
                _ansiAccent = !IsWhiteRgb(components[^3], components[^2], components[^1]);
                return;
            }
        }

        _ansiAccent = true;
    }

    private void ApplyAnsiSgrCode(int code)
    {
        if (code is 0 or 39 or 37 or 97)
        {
            _ansiAccent = false;
        }
        else if (code is >= 30 and <= 36 or >= 90 and <= 96)
        {
            _ansiAccent = true;
        }
    }

    private static bool IsAnsiWhiteIndex(int index) => index is 7 or 15 or 231;

    private static bool IsWhiteRgb(int red, int green, int blue) =>
        red == 255 && green == 255 && blue == 255;

    private void Append(string s, bool accent)
    {
        if (s.Length == 0) return;
        if (_line is null)
        {
            _line = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            Term.Document.Blocks.Add(_line);
        }

        if (s.Length > MaxLineCharacters)
        {
            _line.Inlines.Clear();
            _lineCharacters = 0;
            s = s[^MaxLineCharacters..];
        }
        else if (_lineCharacters + s.Length > MaxLineCharacters)
        {
            TrimLineStart(_lineCharacters + s.Length - MaxLineCharacters);
        }

        if (_lastRun is not null && _lastRunAccent == accent &&
            ReferenceEquals(_line.Inlines.LastOrDefault(), _lastRun))
        {
            _lastRun.Text += s;
        }
        else
        {
            var run = new Run(s);
            if (accent) run.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            _line.Inlines.Add(run);
            _lastRun = run;
            _lastRunAccent = accent;
        }
        _lineCharacters += s.Length;
    }

    private void NewLine()
    {
        if (_line is null)
        {
            // пустая строка — иначе подряд идущие \n схлопнутся
            Term.Document.Blocks.Add(new Paragraph { Margin = new Thickness(0, 1, 0, 1) });
        }
        _line = null;
        _lineCharacters = 0;
        if (Term.Document.Blocks.Count > 512) PruneBlocks();
    }

    private void TrimLast()
    {
        if (_line is null || _line.Inlines.Count == 0) return;
        var last = _line.Inlines.Last();
        if (last is not Run run) return;
        if (run.Text.Length > 1) run.Text = run.Text[..^1];
        else _line.Inlines.Remove(run);
        _lineCharacters = Math.Max(0, _lineCharacters - 1);
    }

    private void TrimLineStart(int count)
    {
        while (count > 0 && _line is not null && _line.Inlines.FirstOrDefault() is Run first)
        {
            var length = first.Text.Length;
            if (length <= count)
            {
                _line.Inlines.Remove(first);
                count -= length;
                _lineCharacters -= length;
            }
            else
            {
                first.Text = first.Text[count..];
                _lineCharacters -= count;
                count = 0;
            }
        }
    }

    private void Write(string text, Brush? brush = null)
    {
        _line = null;   // заканчиваем текущую строку — дальше пойдёт новый абзац
        _lineCharacters = 0;
        var para = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
        var run = new Run(text);
        if (brush is not null) run.Foreground = brush;
        para.Inlines.Add(run);
        Term.Document.Blocks.Add(para);
        PruneAndScroll();
    }

    private void PruneBlocks()
    {
        while (Term.Document.Blocks.Count > 400 && Term.Document.Blocks.First() != _line)
            Term.Document.Blocks.Remove(Term.Document.Blocks.First());
    }

    private void PruneAndScroll()
    {
        PruneBlocks();
        Term.ScrollToEnd();
    }

    // ------------------------------------------------------------- ввод

    private void OnTermKeyDown(object sender, KeyEventArgs e)
    {
        var connected = _client is { IsConnected: true } && _shell is not null;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        // вставка из буфера
        if ((ctrl && e.Key == Key.V) || (shift && e.Key == Key.Insert))
        {
            e.Handled = true;
            if (!connected) return;
            var text = Clipboard.GetText();
            if (!string.IsNullOrEmpty(text))
                Send(text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r"));
            return;
        }

        // копирование выделения (Ctrl+Shift+C явно, Ctrl+C — когда есть выделение)
        if (ctrl && shift && e.Key == Key.C)
        {
            e.Handled = true;
            if (!Term.Selection.IsEmpty) ApplicationCommands.Copy.Execute(null, Term);
            return;
        }

        if (!connected) return;

        switch (e.Key)
        {
            case Key.Enter: Send("\r"); e.Handled = true; break;
            case Key.Back: Send("\u007f"); e.Handled = true; break;
            case Key.Tab: Send("\t"); e.Handled = true; break;
            case Key.Escape: Send("\u001b"); e.Handled = true; break;
            case Key.Left: Send("\u001b[D"); e.Handled = true; break;
            case Key.Right: Send("\u001b[C"); e.Handled = true; break;
            case Key.Up: Send("\u001b[A"); e.Handled = true; break;
            case Key.Down: Send("\u001b[B"); e.Handled = true; break;
            case Key.Home: Send("\u001b[H"); e.Handled = true; break;
            case Key.End: Send("\u001b[F"); e.Handled = true; break;
            case Key.Delete: Send("\u001b[3~"); e.Handled = true; break;
            case Key.PageUp: Send("\u001b[5~"); e.Handled = true; break;
            case Key.PageDown: Send("\u001b[6~"); e.Handled = true; break;
            default:
                // Ctrl+C без выделения = SIGINT; Ctrl+D = EOF
                if (ctrl && !shift && e.Key == Key.C && Term.Selection.IsEmpty)
                {
                    Send("\u0003");
                    e.Handled = true;
                }
                else if (ctrl && e.Key == Key.D)
                {
                    Send("\u0004");
                    e.Handled = true;
                }
                break;
        }
    }

    private void OnTermTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_client is not { IsConnected: true } || _shell is null) return;
        if (string.IsNullOrEmpty(e.Text)) return;
        Send(e.Text);
        e.Handled = true;
    }

    private void Send(string text)
    {
        var shell = _shell;
        if (shell is null) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            shell.Write(bytes, 0, bytes.Length);
            shell.Flush();
        }
        catch (Exception ex)
        {
            Write(L.T("S0951") + ex.Message, GetBrush("Warning"));
            Disconnect("");
        }
    }

    // ------------------------------------------------------------- размер текста

    private void OnFontBar()
    {
        if (!_ready) return;
        var size = Math.Round(9 + FontBar.Alpha * 11);
        FontBar.Alpha = (size - 9) / 11.0;   // щёлкаем по целым размерам шрифта
        ApplyFont(size);
    }

    private void ApplyFont(double size)
    {
        Term.FontSize = size;
        FontValue.Text = size.ToString("0");
        _entry.FontSize = size;
        App.Settings.SaveSoon();
    }

    private static SolidColorBrush GetBrush(string key)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush) return brush;
        return Brushes.Gray;
    }
}
