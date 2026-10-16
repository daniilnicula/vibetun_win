using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VibeTun.Models;

namespace VibeTun.Services;

/// <summary>Локальный вход sing-box, через который движок отдаёт трафик в режиме «через профиль».</summary>
public sealed class DpiUpstream
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; }

    /// <summary>true — SOCKS5, false — HTTP CONNECT.</summary>
    public bool Socks { get; init; }
}

/// <summary>
/// Сервер принял соединение (TCP поднялся), но на ClientHello не ответил ни одним байтом —
/// классический признак того, что поток обрезал DPI. Движок считает такую стратегию неудачной
/// и пробует следующую, а не выдаёт клиенту «пустое» соединение.
/// </summary>
internal sealed class DpiSilentException : Exception
{
    public DpiSilentException(bool reset = false) : base(reset ? "reset" : "silent") => Reset = reset;

    /// <summary>Сервер не молчал, а сбросил соединение — другой признак той же блокировки.</summary>
    public bool Reset { get; }
}

/// <summary>Удавшееся соединение: сокет до сайта и первые байты ответа, если они уже пришли.</summary>
internal sealed class DpiSession : IDisposable
{
    public DpiSession(TcpClient target, byte[] head, int headLen)
    {
        Target = target;
        Head = head;
        HeadLen = headLen;
    }

    public TcpClient Target { get; }

    public byte[] Head { get; }

    public int HeadLen { get; }

    public void Dispose()
    {
        try { Target.Client.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
        try { Target.Dispose(); } catch { /* ignore */ }
    }
}

/// <summary>
/// Обход DPI: локальный HTTP/SOCKS-прокси, который не даёт системе глубокого анализа пакетов
/// увидеть имя сайта. Приёмы — те же, что в zapret и ByeDPI (см. <c>docs/dpi-strategies.md</c>):
///
/// 1) <b>разрыв</b> ClientHello (<see cref="DpiBypassSettings.SniSplit"/>) в разных позициях —
///    аналог <c>--dpi-desync=split</c> / <c>--split-pos</c>;
/// 2) <b>мультисплит</b> — то же, но кусками по несколько байт (<c>--dpi-desync=multisplit</c>);
/// 3) <b>переупаковка в две записи TLS</b> — <c>tlsrec</c> у ByeDPI;
/// 4) <b>disorder</b> — первая часть уходит с TTL=1 и до DPI не доходит, вторая уходит сразу,
///    а начало ядро пересылает следом: DPI получает сегменты не по порядку (<c>--dpi-desync=disorder</c>);
/// 5) <b>фейк-пакет</b> — тот же ClientHello с испорченным именем уходит РАНЬШЕ настоящего,
///    с тем же номером последовательности, но с малой TTL (или битой суммой, или сдвинутым seq):
///    DPI его видит и успокаивается, а сервер выбрасывает (<c>--dpi-desync=fake</c> + <c>fooling</c>).
///    Чтобы фейк оказался в том же потоке, сегмент собирается вручную и отправляется raw-сокетом —
///    это и есть то, чего не хватало: обычный сокет пишет байты по порядку, и «фейк» через
///    отдельное соединение для DPI — чужой поток (Windows разрешает raw-сокеты только
///    администратору, поэтому без прав приём ослабляется, см. <see cref="InjectionAvailable"/>);
/// 6) <b>фейк перед началом запроса</b> (seqovl) — сегмент занимает байты в «прошлом» потока,
///    как <c>--dpi-desync-split-seqovl</c>;
/// 7) <b>срочные данные</b> (OOB) — <c>--oob</c> у ByeDPI;
/// 8) <b>дробление по MSS</b> — <c>--wsize</c>/MSS у zapret.
///
/// Плюс к этому: своя стратегия у каждого сервиса раздела «DPI» и перебор нескольких стратегий
/// на одно соединение, если сервер молчит (<see cref="DpiBypassSettings.FailoverCount"/>) — как
/// список <c>--filter-l7</c>-правил в zapret, где стратегии идут по очереди.
/// </summary>
public sealed class DpiBypassEngine
{
    /// <summary>Буфер под ClientHello: он бывает больше килобайта (много расширений).</summary>
    private const int HelloBuffer = 16 * 1024;

    /// <summary>Сколько соединение может молчать, прежде чем мы его закроем.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Сколько ждём подключение до одного адреса, прежде чем пробовать следующий.</summary>
    private const int ConnectTimeoutMs = 2000;

    /// <summary>Сколько адресов из ответа DNS пробуем максимум.</summary>
    private const int MaxAddresses = 3;

    /// <summary>Сколько стратегий разрешено перебрать на одно соединение (страховка от зацикливания).</summary>
    private const int MaxFailover = 4;

    /// <summary>SOCKS5: «всё хорошо, туннель открыт».</summary>
    private static readonly byte[] SocksOk = { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };

    /// <summary>SOCKS5: «не смог» (0x05 — general failure).</summary>
    private static readonly byte[] SocksFail = { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };

    private static readonly byte[] HeadEnd = { 13, 10, 13, 10 };
    private static readonly byte[] CrlfHost = { 13, 10, (byte)'H', (byte)'o', (byte)'s', (byte)'t' };

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private DpiBypassSettings _options = new();
    private DpiUpstream? _upstream;
    private int _sessions;
    private long _up;
    private long _down;
    private bool _injectionWarned;
    private bool _seqWarned;

    /// <summary>Про доступность инъекции пишем один раз за запуск: во время подбора движков много.</summary>
    private static bool _injectionLogged;

    /// <summary>
    /// Какая стратегия открыла поток для домена в прошлый раз. Следующие соединения к тому же
    /// домену начинают с неё — иначе браузер на каждой странице заново перебирал бы неудачные
    /// варианты и ждал по нескольку секунд (страница грузилась бы «печально медленно»).
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> LastGood = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Фейки с чужим именем: один и тот же ClientHello годится всем сервисам.</summary>
    private static readonly Dictionary<string, byte[]> Decoys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Замок к кэшу фейков: соединений много, они идут параллельно.</summary>
    private static readonly object DecoyGate = new();
    private readonly Dictionary<string, (int Ttl, DateTime At)> _autoTtl = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Настройки под конкретный домен: раздел «DPI» отдаёт профиль сервиса, для чужих доменов
    /// возвращает null — тогда работает общий набор. Читается на каждом соединении.
    /// </summary>
    public Func<string, DpiBypassSettings?>? StrategyResolver { get; set; }

    /// <summary>
    /// Прочие стратегии сервиса — по порядку, на случай если первая не откроет поток
    /// (так у сервиса работает сразу несколько найденных приёмов, а не один).
    /// </summary>
    public Func<string, IReadOnlyList<DpiBypassSettings>>? StrategyListResolver { get; set; }

    /// <summary>
    /// Под какой набор попал домен (сервис, «Общие» или ничего) — пишем в журнал, чтобы это
    /// было видно: по журналу понятно, почему к домену применились (или не применились) приёмы.
    /// </summary>
    public Func<string, string>? RouteLabelResolver { get; set; }

    /// <summary>
    /// Учитывать наборы приёмов из раздела «DPI». Общий «DPI тест» из карточки подключения
    /// перебирает варианты сам, поэтому на время перебора профили сервисов надо отключать —
    /// иначе перебор проверял бы не свой вариант, а профиль сервиса.
    /// </summary>
    public bool UseServiceStrategies { get; set; } = true;

    /// <summary>
    /// Проверять стратегию строго, даже если она одна: молчание сервера считается провалом,
    /// и движок сразу говорит об этом, не отдавая клиенту «пустое» соединение. Так работает
    /// подбор стратегий: иначе каждая молчащая попытка ждала бы рукопожатия до таймаута.
    /// </summary>
    public bool StrictProbe { get; set; }

    /// <summary>Служебные сообщения движка (подъём, ошибки, режим).</summary>
    public event Action<string>? Logged;

    public bool IsRunning { get; private set; }

    /// <summary>Порт, на котором слушаем (0, если ещё не поднят).</summary>
    public int Port { get; private set; }

    /// <summary>
    /// Доступна ли честная инъекция сегментов (raw-сокеты). Без прав администратора она
    /// недоступна, и фейк-пакет отправляется отдельным соединением — приём много слабее.
    /// </summary>
    public static bool InjectionAvailable => DpiRawFlow.Available;

    public int SessionCount => Volatile.Read(ref _sessions);

    public long BytesUp => Interlocked.Read(ref _up);

    public long BytesDown => Interlocked.Read(ref _down);

    /// <summary>Движок отдаёт трафик в туннель sing-box: десинхронизация при этом не применяется.</summary>
    public bool UsesUpstream => _upstream is not null;

    public async Task StartAsync(DpiBypassSettings options, DpiUpstream? upstream, CancellationToken ct = default)
    {
        if (IsRunning) return;

        _options = options.Clone();
        _upstream = upstream is { Port: > 0 } ? upstream : null;
        _injectionWarned = false;
        _seqWarned = false;

        // Адрес сайта нельзя брать у провайдера: при подмене DNS движок пойдёт на подставной
        // адрес, и тогда ни один приём обхода не поможет (zapret по этой же причине советует
        // Secure DNS). Если DoH недоступен, решатель сам вернётся к системному DNS.
        if (_options.Doh == DohMode.Off)
        {
            _options.Doh = DohMode.Cloudflare;
            Log(L.T("dpi.log.dohAuto"));
        }

        var listener = new TcpListener(IPAddress.Loopback, options.ListenPort);
        listener.Start();   // порт занят — исключение уходит вызывающему, состояние не меняем

        _listener = listener;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        IsRunning = true;

        Log(L.F("S0396", Port, StrategiesText(), ExitText()));

        if (!_injectionLogged)
        {
            _injectionLogged = true;
            Log(InjectionAvailable ? L.T("dpi.log.rawReady") : L.T("dpi.log.rawMissing"));
        }

        var token = _cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(listener, token));
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        try { _cts?.Cancel(); }
        catch { /* ignore */ }

        try { _listener?.Stop(); }
        catch { /* ignore */ }

        Log(L.T("S0397"));

        await Task.Delay(100);   // даём сокету освободить порт, иначе повторный старт упадёт

        _cts?.Dispose();
        _cts = null;
        _listener = null;
        Port = 0;
    }

    /// <summary>Обманка фейк-пакета словами (fooling у zapret).</summary>
    public static string FoolingText(DpiFooling fooling)
    {
        if (fooling == DpiFooling.None) return L.T("dpi.fool.none");

        var names = new List<string>();
        if ((fooling & DpiFooling.Ttl) != 0) names.Add("ttl");
        if ((fooling & DpiFooling.BadSum) != 0) names.Add("badsum");
        if ((fooling & DpiFooling.BadSeq) != 0) names.Add("badseq");
        if ((fooling & DpiFooling.DataNoAck) != 0) names.Add("noack");
        return string.Join("+", names);
    }

    public string StrategiesText() => DpiTester.Describe(_options);

    private string ExitText() =>
        _upstream is null
            ? L.T("S0401")
            : L.F("S0402", _upstream.Host, _upstream.Port, (_upstream.Socks ? "socks" : "http"));

    private void Log(string line) => Logged?.Invoke(line);

    /// <summary>
    /// Адреса домена: сервером, выбранным для сервиса или «Общих» в разделе «DNS» (обычный DNS
    /// по адресу), либо как раньше — системный DNS или DoH обхода. Подменённый адрес провайдера
    /// делает бесполезным любой приём, поэтому выбор сервера важнее самих приёмов.
    /// </summary>
    private static async Task<DnsLookup> ResolveAsync(string host, DpiBypassSettings opt, CancellationToken ct)
    {
        if (opt.ResolveServer.Length > 0)
        {
            var server = await DnsResolver.ServerAddressAsync(opt.ResolveServer, ct);
            if (server is not null) return await DnsResolver.ResolveViaAsync(host, server, ct);
        }

        return await DnsResolver.ResolveAsync(host, opt.Doh, opt.DohUrl, ct);
    }

    /// <summary>Про маршрут домена пишем один раз на домен: иначе журнал зальёт повторами.</summary>
    private void LogRoute(string host)
    {
        if (!UseServiceStrategies || RouteLabelResolver is null) return;
        if (!_matchLogged.TryAdd(host, 0)) return;

        Log(L.F("dpi.log.route", host, RouteLabelResolver(host)));
    }

    private static readonly ConcurrentDictionary<string, byte> _matchLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Как часто на живом трафике сообщать, что соединение к площадке не поднялось.</summary>
    private const int ConnectFailReportSeconds = 300;

    private readonly ConcurrentDictionary<string, long> _loggedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _loggedSkipped = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Строка в журнал не чаще одного раза в <paramref name="seconds"/> на ключ. Браузер открывает
    /// десятки соединений в секунду, и без ограничения журнал (и его файл на диске) рос бы на мегабайты,
    /// а приложение лагало на записи. Пропущенные повторы считаются и показываются вместе со следующей
    /// строкой. В подборе ограничения нет: там проверка одна и нужны все строки.
    /// </summary>
    private void LogLimited(string key, string line, int seconds = 60)
    {
        if (StrictProbe || seconds <= 0) { Log(line); return; }

        // за долгую сессию домены накапливаются: список не должен расти бесконечно
        if (_loggedAt.Count > 4096)
        {
            _loggedAt.Clear();
            _loggedSkipped.Clear();
        }

        var now = Environment.TickCount64;
        if (_loggedAt.TryGetValue(key, out var at) && now - at < seconds * 1000L)
        {
            _loggedSkipped.AddOrUpdate(key, 1, (_, count) => count + 1);
            return;
        }

        _loggedAt[key] = now;
        var skipped = _loggedSkipped.TryRemove(key, out var count) ? count : 0;
        Log(skipped > 0 ? line + L.F("dpi.log.repeats", skipped) : line);
    }

    /// <summary>
    /// Адрес, к которому пойдёт движок, и откуда он взят: подмена DNS видна сразу,
    /// и её не спутать с блокировкой по содержимому.
    /// </summary>
    private void LogLookup(string host, DnsLookup lookup)
    {
        if (lookup.Logged) return;
        lookup.Logged = true;

        // клиент и так шёл на адрес: «адрес 149.154.167.41: 149.154.167.41 (это адрес)» — не новость
        if (IPAddress.TryParse(host, out _)) return;

        var text = string.Join(", ", lookup.InConnectOrder.Take(MaxAddresses).Select(a => a.ToString()));
        LogLimited("resolve:" + host, L.F("dpi.log.resolve", host, text, lookup.Source));
    }

    // ------------------------------------------------------------------ приём
    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch
            {
                break;   // слушатель остановлен
            }

            Interlocked.Increment(ref _sessions);
            var token = ct;
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(client, token); }
                catch (OperationCanceledException) { /* движок останавливают — это не ошибка */ }
                catch (Exception ex)
                {
                    if (ex.InnerException is not OperationCanceledException) Log(L.T("S0403") + ex.Message);
                }
                finally
                {
                    Interlocked.Decrement(ref _sessions);
                    Close(client);
                }
            });
        }
    }

    // -------------------------------------------------------------- соединение
    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var stream = client.GetStream();

        var head = new byte[8192];
        var headLen = 0;
        try
        {
            headLen = await stream.ReadAsync(head.AsMemory(), ct);
        }
        catch { /* клиент отвалился */ }

        if (headLen <= 0) return;

        string host;
        int port;
        byte[]? pending = null;   // для обычного HTTP: переписанный запрос
        var socks = false;

        if (head[0] == 0x05)
        {
            socks = true;
            var handshake = await SocksHandshakeAsync(stream, head, headLen, ct);
            if (!handshake.ok) return;
            host = handshake.host;
            port = handshake.port;
        }
        else
        {
            var (buffer, length) = await ReadHeadAsync(stream, head, headLen, ct);
            var requestLine = Encoding.ASCII.GetString(buffer, 0, Math.Min(length, 4096));
            var eol = requestLine.IndexOf("\r\n", StringComparison.Ordinal);
            var line = eol < 0 ? requestLine : requestLine[..eol];
            var parts = line.Split(' ');
            if (parts.Length < 2) return;

            if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                SplitHostPort(parts[1], out host, out port);
            }
            else
            {
                if (parts.Length < 3 || !Uri.TryCreate(parts[1], UriKind.Absolute, out var uri)) return;
                host = uri.Host;
                port = uri.Port;
                pending = RewriteRequest(buffer, length, uri);
            }
        }

        if (host.Length == 0 || port <= 0) return;

        var primary = (UseServiceStrategies ? StrategyResolver?.Invoke(host) : null) ?? _options;

        // Набор без приёмов — это режим «только сервисы»: «Общие» выключены, а своего сервиса у домена
        // нет (или в наборе не осталось ни одного приёма). Такой трафик идёт как есть: ни перебора
        // стратегий, ни фейков, ни строк в журнале на каждое соединение.
        var relay = !DpiServiceCatalog.HasAnyStrategy(primary);

        // по журналу видно, что домен (в том числе служебный: картинки, вложения) попал под набор сервиса
        LogRoute(host);

        // адрес узнаём один раз на соединение: повторять запрос на каждую стратегию и долго,
        // и бессмысленно (при DoH это ещё и лишний запрос по HTTPS)
        DnsLookup? lookup = null;
        var check = StrictProbe;
        // Порядок здесь принципиален: туннель клиенту открывается ДО чтения ClientHello.
        // Браузер (и любой корректный клиент) по правилам не шлёт данные, пока не получит
        // ответ на CONNECT — если ждать ClientHello первым, соединение зависает у обеих сторон,
        // и обход не работает вообще ни с одной стратегией.
        if (pending is null)
        {
            // адрес узнаём заранее (результат кэшируется): если DNS не знает имени,
            // об этом ещё можно сказать честно — кодом ответа, а не молчанием
            if (_upstream is null)
            {
                lookup = await ResolveAsync(host, primary, ct);
                if (lookup.Addresses.Count == 0)
                {
                    LogLimited("noaddr:" + host, L.F("S0404", host, L.T("S0405") + host));
                    await RefuseAsync(stream, socks, ct);
                    return;
                }

                // в режиме «как есть» адрес ни на что не влияет: обход не применяется
                if (check || !relay) LogLookup(host, lookup);
            }

            await AcceptAsync(stream, socks, ct);
        }

        // Первые байты клиента: ClientHello для HTTPS (читаем ровно одну запись TLS — один
        // ReadAsync мог вернуть только её начало, и тогда разрыв применился бы к обрубку),
        // либо запрос обычного HTTP, если он идёт не через CONNECT.
        byte[] payload;
        int payloadLen;
        if (pending is not null)
        {
            payload = pending;
            payloadLen = pending.Length;
        }
        else
        {
            payload = new byte[HelloBuffer];
            payloadLen = 0;
            try { payloadLen = await ReadPayloadAsync(stream, payload, ct); }
            catch { /* клиент успел закрыться */ }

            if (payloadLen <= 0) return;
        }

        // Живой трафик движок не проверяет: он отправляет первые байты выбранным набором и сразу
        // ретранслирует соединение. Ожидание ответа сервера, разбор молчания и сброса, перебор
        // запасных наборов и подсказки — дело подбора и теста в окне сервиса: там движок работает
        // в режиме StrictProbe, и проверка нужна именно ему.
        

        // Прочие найденные для сервиса стратегии: если первая не откроет поток,
        // движок попробует следующую (как очередь правил в zapret).
        var strategies = check ? BuildStrategies(host, primary) : new List<DpiBypassSettings> { primary };

        // Ждать ответа, чтобы понять, сработала ли стратегия, можно только у запросов, которые
        // «первым пакетом» и заканчиваются: ClientHello или запрос без тела. У POST тело приходит
        // следом, и ожидание ответа до его отправки повесило бы соединение.
        var canProbe = check &&
            (TlsHello.IsHandshakeRecord(payload, payloadLen) || !HttpHasBody(payload, payloadLen));

        // Молчание площадки — не повод рвать уже поднятое соединение: отдаём его клиенту как есть.
        var acceptSilent = !check;

        DpiSession? session = null;
        var silent = 0;
        var resets = 0;

        for (var i = 0; i < strategies.Count && session is null; i++)
        {
            var strategy = strategies[i];
            var last = i == strategies.Count - 1;

            if (check && strategies.Count > 1)
                Log(L.F("dpi.log.strategyTry", i + 1, strategies.Count, DpiTester.Describe(strategy)));

            try
            {
                session = await ConnectWithStrategyAsync(
                    host, port, payload, payloadLen, strategy, canProbe, acceptSilent, lookup, ct);
            }
            catch (DpiSilentException ex)
            {
                if (ex.Reset)
                {
                    resets++;
                    if (check) Log(L.F("dpi.log.strategyReset", host));
                }
                else
                {
                    silent++;
                    if (check) Log(L.F("dpi.log.strategySilent", host));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // на живом трафике про неудачу соединения говорим редко: браузер повторяет запрос
                // десятки раз в секунду, и одинаковые строки не нужны ни в окне, ни в файле
                LogLimited("fail:" + host, L.F("dpi.log.strategyFail", host, ex.Message),
                    check ? 0 : ConnectFailReportSeconds);
            }

            if (session is not null)
            {
                LastGood[host] = DpiServiceCatalog.Signature(strategy);

                // поток открылся: следующая неудача по этому домену снова попадёт в журнал целиком
                _loggedAt.TryRemove("fail:" + host, out _);
                _loggedSkipped.TryRemove("fail:" + host, out _);
                break;
            }

            if (last) break;

            // небольшая пауза: DPI успевает «забыть» неудачную попытку, а сервер — закрыть поток
            try { await Task.Delay(120, ct); }
            catch (OperationCanceledException) { return; }
        }

        if (session is null)
        {
            // Ничего не подошло: прежняя удачная стратегия для этого домена больше не работает
            LastGood.TryRemove(host, out _);

            if (check)
            {
                // Причина называется прямо: TCP доходит, но сервер молчит после ClientHello —
                // значит поток обрезал DPI и ни один приём его не открыл.
                var reason = silent == strategies.Count
                    ? L.F("dpi.log.allSilent", host, strategies.Count)
                    : resets == strategies.Count
                        ? L.F("dpi.log.allReset", host, strategies.Count)
                        : L.F("dpi.log.allFailed", host, strategies.Count);

                Log(reason);
            }

            if (pending is not null)
            {
                // обычный HTTP: клиенту ещё ничего не отвечали — говорим честно кодом 502
                await RefuseAsync(stream, socks, ct);
            }
            else
            {
                // туннель уже открыт: остаётся только закрыть соединение, клиент повторит запрос
                if (check) Log(L.T("dpi.log.tunnelClosed"));
            }

            return;
        }

        using (session)
        {
            try
            {
                // Ответ клиенту уже дан: туннель открыт до чтения ClientHello (см. выше),
                // а для обычного HTTP отвечать нечего — клиент ждёт ответ сайта.

                // первые байты ответа уже прочитаны, пока решали, работает ли стратегия
                if (session.HeadLen > 0)
                {
                    await stream.WriteAsync(session.Head.AsMemory(0, session.HeadLen), ct);
                    Interlocked.Add(ref _down, session.HeadLen);
                }

                await PumpAsync(client, session.Target, ct);
            }
            finally
            {
                Close(session.Target);
            }
        }
    }

    /// <summary>
    /// Порядок стратегий для домена: своя (профиль сервиса или общий набор), затем остальные
    /// найденные для этого сервиса. Число попыток ограничено настройкой.
    /// </summary>
    private List<DpiBypassSettings> BuildStrategies(string host, DpiBypassSettings primary)
    {
        var list = new List<DpiBypassSettings> { primary };

        var extra = UseServiceStrategies ? StrategyListResolver?.Invoke(host) : null;
        if (extra is not null)
        {
            foreach (var candidate in extra)
            {
                if (list.Count >= MaxFailover) break;
                if (!DpiServiceCatalog.HasAnyStrategy(candidate)) continue;
                if (list.Any(s => DpiServiceCatalog.Signature(s) == DpiServiceCatalog.Signature(candidate))) continue;
                list.Add(candidate);
            }
        }

        var allowed = Math.Clamp(primary.FailoverCount, 1, MaxFailover);
        if (list.Count > allowed) list = list.Take(allowed).ToList();

        // стратегия, которая уже открывала поток для этого домена, идёт первой: обычно она
        // и срабатывает, и тогда перебор не тратит время на заведомо неудачные варианты
        if (LastGood.TryGetValue(host, out var known))
        {
            var index = list.FindIndex(s => DpiServiceCatalog.Signature(s) == known);
            if (index > 0)
            {
                var saved = list[index];
                list.RemoveAt(index);
                list.Insert(0, saved);
            }
        }

        // Последняя попытка — набор «Общих» из раздела «DPI». Если у сервиса свой профиль и он
        // не открыл поток, лучше попробовать уже проверенный общий набор, чем закрыть соединение.
        // Так добавление сервиса не может сломать то, что работало; на чужой трафик «Общие»
        // при этом не распространяются — они применяются только внутри соединений сервиса.
        if (UseServiceStrategies && !ReferenceEquals(primary, _options) &&
            DpiServiceCatalog.HasAnyStrategy(_options) &&
            list.All(s => DpiServiceCatalog.Signature(s) != DpiServiceCatalog.Signature(_options)))
        {
            list.Add(_options);
            LogLimited("general:" + host, L.F("dpi.log.strategyGeneral", DpiTester.Describe(_options)));
        }

        return list;
    }

    /// <summary>
    /// Подключиться к сайту с выбранной стратегией и убедиться, что поток открыт.
    /// Возвращает null-значимый результат: либо соединение (сервер ответил), либо исключение
    /// <see cref="DpiSilentException"/> — тогда движок пробует следующую стратегию.
    /// </summary>
    private async Task<DpiSession> ConnectWithStrategyAsync(
        string host, int port, byte[] payload, int payloadLen, DpiBypassSettings opt, bool canProbe,
        bool acceptSilent, DnsLookup? lookup, CancellationToken ct)
    {
        // В режиме «через профиль» десинхронизация не применяется: трафик уже идёт в туннель,
        // и любые наши вставки лишь мешали бы ядру.
        if (_upstream is not null)
        {
            var tunnel = await ConnectViaUpstreamAsync(host, port, ct);
            await tunnel.GetStream().WriteAsync(payload.AsMemory(0, payloadLen), ct);
            await tunnel.GetStream().FlushAsync(ct);
            return new DpiSession(tunnel, Array.Empty<byte>(), 0);
        }

        lookup ??= await ResolveAsync(host, opt, ct);
        if (lookup.Addresses.Count == 0) throw new IOException(L.T("S0405") + host);
        LogLookup(host, lookup);

        // Сколько ждём ответа на одну стратегию. Ждать «до отказа» нельзя: подбор обязан
        // укладываться в отведённое время, иначе рабочая стратегия просто не успеет дойти
        // до своей очереди.
        var waitMs = Math.Clamp(opt.FailoverTimeoutMs, 800, 8000);

        // На живом трафике стратегию пробуем дважды: ТСПУ часто рвёт или «глушит» именно первую
        // попытку к площадке, а браузер такие обрывы переживает только потому, что повторяет сам.
        // В подборе повторов нет — там время дороже, а повторяет сам подбор (вторая волна).
        var attempts = StrictProbe ? 1 : 2;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var session = await ConnectAttemptAsync(
                host, port, payload, payloadLen, opt, canProbe, acceptSilent, lookup, waitMs,
                retryAllowed: attempt + 1 < attempts, ct);

            if (session is not null) return session;

            LogLimited("retry:" + host, L.F("dpi.log.strategyRetry", host, attempt + 2, waitMs));
        }

        // сюда попасть нельзя: последняя попытка либо возвращает поток, либо бросает исключение
        throw new IOException(L.F("S0406", host, L.T("dpi.log.noAttempts")));
    }

    /// <summary>
    /// Одна попытка стратегии: перебрать адреса площадки и вернуть открытый поток. Возвращает
    /// null, если поток не открылся, но попытку стратегии можно повторить (<paramref name="retryAllowed"/>);
    /// иначе — исключение <see cref="DpiSilentException"/>.
    /// </summary>
    private async Task<DpiSession?> ConnectAttemptAsync(
        string host, int port, byte[] payload, int payloadLen, DpiBypassSettings opt, bool canProbe,
        bool acceptSilent, DnsLookup lookup, int waitMs, bool retryAllowed, CancellationToken ct)
    {
        Exception? lastError = null;

        foreach (var address in lookup.InConnectOrder.Take(MaxAddresses))
        {
            ct.ThrowIfCancellationRequested();

            TcpClient? target = null;
            DpiRawFlow? flow = null;
            try
            {
                // пара «соединение + слежение за потоком»: у асинхронного метода не может быть
                // out-параметра (CS1988), а слежение нужно вместе с соединением
                var link = await ConnectAsync(address, port, opt, ct);
                target = link.Target;
                flow = link.Flow;

                // число прыжков до площадки нужно и фейку (чтобы он не дошёл до сервера),
                // и приёму disorder (для этого TTL всегда 1)
                var ttl = opt.FakeAutoTtl && opt.FakeTtl
                    ? await AutoTtlAsync(address, opt, ct)
                    : Math.Clamp(opt.FakeTtlValue, 1, 255);

                await SendAsync(target, flow, payload, payloadLen, opt, ttl, ct);

                // запрос с телом: тело отправит клиент, поэтому «нет ответа» здесь ничего не значит
                if (!canProbe) return new DpiSession(target, Array.Empty<byte>(), 0);

                var head = new byte[7];
                var read = await WaitServerAsync(target, head, waitMs, ct);

                // Ответ может опоздать: если начало ClientHello ушло с малой TTL, сервер дождётся
                // его только после повторной передачи ядра (около секунды). В подборе такое
                // молчание нельзя принимать за провал — ждём ещё одно окно, и только потом
                // называем приём неудачным.
                if (read == 0 && StrictProbe && !ct.IsCancellationRequested)
                {
                    Log(L.F("dpi.log.strategyLate", host, waitMs));
                    read = await WaitServerAsync(target, head, waitMs, ct);
                }

                // 0 — сервер молчит, -1 — сбросил соединение: и то и другое значит,
                // что поток не открылся, и движок переходит к следующей стратегии
                if (read <= 0)
                {
                    if (!acceptSilent)
                    {
                        Close(target);

                        // повтор: и молчание, и сброс часто свойственны именно первой попытке
                        if (retryAllowed) return null;

                        throw new DpiSilentException(reset: read < 0);
                    }

                    LogLimited("silent:" + host, L.F("dpi.log.singleSilent", host, waitMs));
                    return new DpiSession(target, head, 0);
                }

                return new DpiSession(target, head, read);
            }
            catch (DpiSilentException)
            {
                Close(target);
                throw;   // молчание — свойство площадки и стратегии, а не адреса: сразу к следующей
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Close(target);
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Close(target);
            }
            finally
            {
                flow?.Dispose();
            }
        }

        throw new IOException(L.F("S0406", host, lastError?.Message));
    }

    /// <summary>
    /// Подключение к адресу. Если инъекция доступна, порт источника и номер последовательности
    /// берём заранее: фейк-пакет должен уйти в тот же поток, то есть с тем же адресом и портом,
    /// что и настоящее соединение.
    /// </summary>
    private async Task<(TcpClient Target, DpiRawFlow? Flow)> ConnectAsync(
        IPAddress address, int port, DpiBypassSettings opt, CancellationToken ct)
    {
        DpiRawFlow? flow = null;
        var target = new TcpClient(address.AddressFamily);

        try
        {
            if (opt.ClampMss) SetMss(target.Client, opt.MssValue);

            // Порт источника фиксируем до connect: по нему слежение узнаёт SYN/ACK нужного потока
            // и получает оба номера последовательности (без них фейк для DPI — мусор).
            if (opt.FakeTtl && opt.InjectFake)
            {
                var local = LocalAddressFor(address);
                if (local is not null)
                {
                    try
                    {
                        target.Client.Bind(new IPEndPoint(local, 0));
                        var localPort = ((IPEndPoint)target.Client.LocalEndPoint!).Port;
                        flow = DpiRawFlow.TryBegin(local, address, localPort, port);
                    }
                    catch
                    {
                        flow?.Dispose();
                        flow = null;   // без инъекции остаются сокетные приёмы
                    }
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeoutMs);
            await target.ConnectAsync(address, port, timeout.Token);
            target.NoDelay = true;

            if (flow is not null) await flow.LearnAsync(ct);
            return (target, flow);
        }
        catch
        {
            // инъекция без соединения не нужна: слежение снимаем, сокет закрываем
            flow?.Dispose();
            Close(target);
            throw;
        }
    }

    /// <summary>
    /// Адрес, с которого система пойдёт к этому получателю: узнаётся пустым UDP-«подключением»
    /// (пакеты при этом не отправляются). Нужен, чтобы слежение за потоком слушало нужный интерфейс.
    /// </summary>
    private static IPAddress? LocalAddressFor(IPAddress remote)
    {
        try
        {
            using var probe = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(remote, 53));
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Ждём первый байт ответа. 0 — сервер молчит, -1 — сбросил соединение.</summary>
    private static async Task<int> WaitServerAsync(TcpClient target, byte[] head, int timeoutMs, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeoutMs);

        try
        {
            var read = 0;
            while (read < 5)
            {
                var n = await target.GetStream().ReadAsync(head.AsMemory(read, head.Length - read), wait.Token);
                if (n <= 0) break;
                read += n;
            }
            return read;
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return 0;   // таймаут: сервер молчит
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return -1;
        }
        catch
        {
            return -1;   // соединение разорвано — тоже неудача стратегии
        }
    }

    /// <summary>
    /// TTL фейк-пакета: число прыжков до площадки минус один (аналог <c>--dpi-desync-autottl</c>).
    /// Такой пакет доходит до любого узла на пути (в том числе до DPI), но умирает за один шаг
    /// до сервера. Число прыжков оценивается по TTL ответа ICMP; результат держим в кэше:
    /// соединений много, а маршрут меняется редко.
    /// </summary>
    private async Task<int> AutoTtlAsync(IPAddress address, DpiBypassSettings opt, CancellationToken ct)
    {
        var key = address.ToString();
        lock (_autoTtl)
        {
            if (_autoTtl.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(5))
                return cached.Ttl;
        }

        var ttl = Math.Clamp(opt.FakeTtlValue, 1, 255);
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(2));
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success && reply.Options is { } options)
            {
                // начальный TTL у Windows и Linux — 64 или 128; вычитаем полученный и получаем путь
                var initial = options.Ttl > 64 ? 128 : 64;
                var hops = initial - options.Ttl;
                if (hops > 1) ttl = Math.Clamp(hops - 1, 1, 255);
            }
        }
        catch { /* ICMP закрыт — оставляем значение из настроек */ }

        lock (_autoTtl) _autoTtl[key] = (ttl, DateTime.UtcNow);
        return ttl;
    }

    /// <summary>Отказ клиенту: сайт недоступен, туннель мы не открывали.</summary>
    private static async Task RefuseAsync(NetworkStream stream, bool socks, CancellationToken ct)
    {
        try
        {
            if (socks) await stream.WriteAsync(SocksFail, ct);
            else await WriteAsciiAsync(stream, "HTTP/1.1 502 Bad Gateway\r\n\r\n", ct);
        }
        catch { /* клиент уже ушёл */ }
    }

    /// <summary>
    /// Согласие клиенту: туннель открыт. Отвечаем сразу после разбора запроса — клиент
    /// (браузер, curl, SslStream) не начинает рукопожатие, пока не увидит этот ответ.
    /// </summary>
    private static async Task AcceptAsync(NetworkStream stream, bool socks, CancellationToken ct)
    {
        try
        {
            if (socks) await stream.WriteAsync(SocksOk, ct);
            else await WriteAsciiAsync(stream, "HTTP/1.1 200 Connection established\r\n\r\n", ct);
            await stream.FlushAsync(ct);
        }
        catch { /* клиент уже ушёл */ }
    }

    /// <summary>
    /// Первые байты соединения: либо ровно одна запись TLS (длина — в её заголовке; один ReadAsync
    /// мог вернуть только начало ClientHello, и тогда разрыв применился бы к обрубку), либо
    /// заголовки обычного HTTP — их ждём до пустой строки, но не дольше <see cref="HttpGraceMs"/>.
    /// </summary>
    private static async Task<int> ReadPayloadAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;

        while (read < TlsHello.RecordHeader)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n <= 0) return read;
            read += n;
        }

        var size = TlsHello.RecordSize(buffer, read);
        if (size > 0)
        {
            while (read < size && read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
                if (n <= 0) break;
                read += n;
            }

            return read;
        }

        // не TLS: дочитываем заголовки запроса, чтобы разрыв по «Host:» применился ко всему запросу
        while (read < buffer.Length && IndexOf(buffer, read, HeadEnd) < 0)
        {
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
            grace.CancelAfter(HttpGraceMs);

            try
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), grace.Token);
                if (n <= 0) break;
                read += n;
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                break;   // заголовки уже пришли — дальше ждать нечего
            }
        }

        return read;
    }

    /// <summary>Сколько ждём заголовки обычного HTTP, если они пришли не целиком (мс).</summary>
    private const int HttpGraceMs = 400;
    // ------------------------------------------------------------------ SOCKS5
    /// <summary>
    /// Приветствие SOCKS5. Возвращает признак успеха и адрес назначения:
    /// параметры out в асинхронном методе запрещены (CS1988), поэтому кортеж.
    /// </summary>
    private async Task<(bool ok, string host, int port)> SocksHandshakeAsync(
        NetworkStream stream, byte[] head, int headLen, CancellationToken ct)
    {
        var host = "";
        var port = 0;

        if (headLen < 2) return (false, host, port);
        var methods = head[1];
        var need = 2 + methods;
        if (headLen < need)
        {
            if (!await ReadExactAsync(stream, head, headLen, need - headLen, ct)) return (false, host, port);
        }

        await stream.WriteAsync(new byte[] { 0x05, 0x00 }, ct);

        var request = new byte[4];
        if (!await ReadExactAsync(stream, request, 0, 4, ct)) return (false, host, port);
        if (request[0] != 0x05 || request[1] != 0x01) return (false, host, port);   // только CONNECT

        var atyp = request[3];
        if (atyp == 0x01)
        {
            var ipv4 = new byte[4];
            if (!await ReadExactAsync(stream, ipv4, 0, 4, ct)) return (false, host, port);
            host = new IPAddress(ipv4).ToString();
        }
        else if (atyp == 0x03)
        {
            var size = new byte[1];
            if (!await ReadExactAsync(stream, size, 0, 1, ct)) return (false, host, port);
            var name = new byte[size[0]];
            if (!await ReadExactAsync(stream, name, 0, name.Length, ct)) return (false, host, port);
            host = Encoding.ASCII.GetString(name);
        }
        else if (atyp == 0x04)
        {
            var ipv6 = new byte[16];
            if (!await ReadExactAsync(stream, ipv6, 0, 16, ct)) return (false, host, port);
            host = new IPAddress(ipv6).ToString();
        }
        else
        {
            return (false, host, port);
        }

        var portBytes = new byte[2];
        if (!await ReadExactAsync(stream, portBytes, 0, 2, ct)) return (false, host, port);
        port = (portBytes[0] << 8) | portBytes[1];

        // ответ об успехе не шлём: сначала реально подключаемся к сайту (см. HandleAsync)
        return (true, host, port);
    }

    // --------------------------------------------------------------- подключения
    private async Task<TcpClient> ConnectViaUpstreamAsync(string host, int port, CancellationToken ct)
    {
        var up = _upstream!;
        var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(up.Host, up.Port, timeout.Token);
        client.NoDelay = true;

        var stream = client.GetStream();
        if (up.Socks)
        {
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
            var greeting = new byte[2];
            if (!await ReadExactAsync(stream, greeting, 0, 2, ct) || greeting[1] != 0x00)
                throw new IOException(L.T("S0407"));

            var request = new List<byte> { 0x05, 0x01, 0x00 };
            if (IPAddress.TryParse(host, out var ip))
            {
                var bytes = ip.GetAddressBytes();
                request.Add(bytes.Length == 4 ? (byte)0x01 : (byte)0x04);
                request.AddRange(bytes);
            }
            else
            {
                var name = Encoding.ASCII.GetBytes(host);
                request.Add(0x03);
                request.Add((byte)name.Length);
                request.AddRange(name);
            }

            request.Add((byte)(port >> 8));
            request.Add((byte)(port & 0xFF));
            await stream.WriteAsync(request.ToArray(), ct);
            await stream.FlushAsync(ct);

            var reply = new byte[4];
            if (!await ReadExactAsync(stream, reply, 0, 4, ct) || reply[1] != 0x00)
                throw new IOException(L.T("S0408"));

            var rest = reply[3] switch
            {
                0x01 => 4,
                0x04 => 16,
                0x03 => await ReadLengthAsync(stream, ct),
                _ => -1
            };
            if (rest < 0) throw new IOException(L.T("S0409"));
            if (rest > 0 && !await ReadExactAsync(stream, new byte[rest], 0, rest, ct)) return client;
            var tail = new byte[2];
            await ReadExactAsync(stream, tail, 0, 2, ct);
            return client;
        }

        await WriteAsciiAsync(stream, $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n", ct);
        await stream.FlushAsync(ct);

        var (buffer, length) = await ReadHeadAsync(stream, new byte[2048], 0, ct);
        var answer = Encoding.ASCII.GetString(buffer, 0, length);
        if (!answer.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
            !answer.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("S0410") + answer.Split("\r\n")[0]);

        return client;
    }

    /// <summary>Длина доменного имени в SOCKS-ответе (байт после ATYP).</summary>
    private static async Task<int> ReadLengthAsync(NetworkStream stream, CancellationToken ct)
    {
        var size = new byte[1];
        return await ReadExactAsync(stream, size, 0, 1, ct) ? size[0] : -1;
    }

    // ------------------------------------------------------------ десинхронизация
    /// <summary>
    /// Отправить первые байты соединения всеми включёнными приёмами в правильном порядке:
    /// фейк-пакет (или его часть) — раньше настоящего ClientHello, disorder — с «потерянным»
    /// началом, дальше — разрыв, мультисплит или срочные данные.
    /// </summary>
    private async Task SendAsync(
        TcpClient target, DpiRawFlow? flow, byte[] data, int len, DpiBypassSettings opt, int ttl, CancellationToken ct)
    {
        if (len <= 0) return;

        var tls = TlsHello.IsHandshakeRecord(data, len);
        var recordLen = tls ? TlsHello.RecordSize(data, len) : 0;
        var body = recordLen > 0 ? recordLen : len;   // что режем и подменяем

        // позиция разрыва: первый байт, середина домена второго уровня, середина записи или SNI
        var cut = -1;
        if (tls && (opt.SniSplit || opt.TlsRecordSplit || opt.Disorder || opt.OobSplit))
        {
            cut = TlsHello.SplitPositionAt(data, body, opt.SplitPosition);
            if (cut <= 0) cut = TlsHello.SplitPosition(data, body, opt.SniSplit);
        }
        else if (!tls && opt.SniSplit)
        {
            cut = HttpHostSplit(data, len);
            if (cut >= 0) cut += 1;   // режем прямо перед двоеточием в «Host:» — имя остаётся во второй части
        }

        var stream = target.GetStream();

        // --- фейк-пакет: тот же ClientHello с испорченным (или чужим) именем, но раньше настоящего
        if (opt.FakeTtl && !opt.FakeAfter) SendFake(flow, data, len, body, cut, opt, ttl);

        if (opt.Disorder && cut > 0)
        {
            await SendDisorderAsync(target, stream, data, body, cut, opt, ct);
        }
        else if (opt.Multisplit && tls && body > 0)
        {
            await SendManyAsync(stream, data, body, Math.Clamp(opt.MultisplitSize, 1, 512), opt, ct);
        }
        else if (opt.OobSplit && cut > 0)
        {
            await SendOobAsync(target, stream, data, body, cut, opt, ct);
        }
        else if (cut > 0 && (opt.SniSplit || opt.TlsRecordSplit))
        {
            await SendSplitAsync(stream, data, body, cut, opt, ct);
        }
        else
        {
            await stream.WriteAsync(data.AsMemory(0, len), ct);
            await stream.FlushAsync(ct);
            body = len;
        }

        // остаток первой записи (клиент мог прислать больше одной) — как есть
        if (len > body) await stream.WriteAsync(data.AsMemory(body, len - body), ct);
        await stream.FlushAsync(ct);

        // --- фейк после настоящего ClientHello: DPI видит сначала настоящий, потом подменённый
        if (opt.FakeTtl && opt.FakeAfter) SendFake(flow, data, len, body, cut, opt, ttl);
    }

    /// <summary>
    /// Фейк-пакет. С инъекцией он уходит настоящим TCP-сегментом в тот же поток с тем же номером
    /// последовательности, что и настоящий ClientHello, и «портится» так, чтобы сервер его
    /// выбросил, а DPI разобрал (TTL, битая сумма, сдвинутый seq).
    ///
    /// Без прав администратора raw-сокет недоступен: фейк не отправляется вовсе (об этом один раз
    /// пишем в журнал), а обход продолжают разрыв, disorder и мультисплит.
    /// </summary>
    private void SendFake(
        DpiRawFlow? flow, byte[] data, int len, int body, int cut, DpiBypassSettings opt, int ttl)
    {
        if (!opt.InjectFake) return;

        var payload = FakeData(data, len, body, cut, opt);
        if (payload.Length == 0) return;

        if (flow is null)
        {
            // raw-сокет не дали: без прав администратора фейк в тот же поток отправить нечем
            WarnNoInjection();
            return;
        }

        var count = Math.Clamp(opt.FakeCount, 1, 10);
        var sent = 0;

        if (flow.Learned)
        {
            for (var i = 0; i < count; i++)
                sent = flow.SendFake(payload, payload.Length, ttl, opt.FakeFooling, opt.FakePlacement);

            // Подробности про фейк пишет только проверка (тест и подбор): на живом трафике такая
            // строка повторялась бы на каждое соединение и ничего не сообщала — размер ClientHello
            // у каждого соединения свой, поэтому и «один раз на набор» её не успокаивал.
            if (StrictProbe)
                Log(L.F("dpi.log.fakeInject", payload.Length, ttl, FoolingText(opt.FakeFooling), count,
                    opt.FakePlacement == DpiFakePlacement.Ahead ? L.T("dpi.place.ahead") : L.T("dpi.place.overlap")));

            if (sent == 0)
                LogLimited("fakefail", L.T("dpi.log.fakeFailed"), StrictProbe ? 0 : ConnectFailReportSeconds);
            return;
        }

        // Номера последовательности неизвестны (слежение не поймало SYN/ACK). Фейк всё равно
        // отправляем: DPI разбирает полезную нагрузку сегмента, а не его номер, а сервер
        // случайный номер выбросит как «вне окна приёма». Копии с обманками badsum и noack
        // страхуют на случай, если номер всё же попал в окно: такие сегменты сервер не примет.
        WarnNoSeq();

        for (var i = 0; i < count; i++)
            sent = flow.SendFake(payload, payload.Length, ttl, opt.FakeFooling, opt.FakePlacement);

        var safe = opt.FakeFooling | DpiFooling.BadSum | DpiFooling.DataNoAck;
        for (var i = 0; i < count; i++)
            sent = flow.SendFake(payload, payload.Length, ttl, safe, opt.FakePlacement);

        // «Фейк без номеров последовательности» — нормальный режим работы (см. WarnNoSeq, он пишет
        // об этом один раз на запуск), а не событие: на каждое соединение такая строка не нужна.
        if (StrictProbe)
            Log(L.F("dpi.log.fakeBlind", payload.Length, ttl, count * 2, FoolingText(safe)));

        if (sent == 0)
            LogLimited("fakefail", L.T("dpi.log.fakeFailed"), StrictProbe ? 0 : ConnectFailReportSeconds);
    }

    /// <summary>
    /// Сказать один раз, почему фейк не ушёл: без прав администратора Windows не даёт raw-сокет,
    /// поэтому «фейк» не может попасть в тот же поток, а отдельным соединением DPI не обмануть.
    /// </summary>
    private void WarnNoInjection()
    {
        if (_injectionWarned) return;
        _injectionWarned = true;
        Log(L.T("dpi.log.fakeNoInjection"));
    }

    /// <summary>
    /// Сказать один раз, что слежение за потоком не увидело SYN/ACK: фейк уходит без номеров
    /// последовательности. Числа в строке показывают, где именно спотыкается слежение.
    /// </summary>
    private void WarnNoSeq()
    {
        if (_seqWarned) return;
        _seqWarned = true;
        Log(L.F("dpi.log.fakeNoSeq", DpiRawFlow.Packets, DpiRawFlow.TcpPackets,
            DpiRawFlow.Candidates, DpiRawFlow.CaptureNote));
    }

    /// <summary>
    /// Данные фейка: настоящий ClientHello с испорченным именем (по умолчанию) или с чужим именем
    /// из настроек (<c>--dpi-desync-fake-tls-mod=sni=…</c>). При «фейке перед запросом» (seqovl)
    /// берётся только начало записи — тогда его последний байт стыкуется с первым байтом настоящего
    /// ClientHello.
    /// </summary>
    private static byte[] FakeData(byte[] data, int len, int body, int cut, DpiBypassSettings opt)
    {
        var decoy = opt.FakeSni.Trim();
        if (decoy.Length > 0)
        {
            lock (DecoyGate)
            {
                if (Decoys.TryGetValue(decoy, out var cached)) return cached;
            }

            var hello = DpiTester.BuildProbeHello(decoy);
            if (hello.Length == 0) return Array.Empty<byte>();

            lock (DecoyGate) Decoys[decoy] = hello;
            return hello;
        }

        if (opt.FakePlacement == DpiFakePlacement.Ahead)
        {
            // начало настоящей записи занимает байты «в прошлом» потока: DPI собирает запись
            // не с того места, а сервер эти байты выбрасывает как уже полученные
            var size = cut > 0 ? Math.Min(cut, body) : Math.Min(body, 64);
            if (size <= 0) return Array.Empty<byte>();

            var part = new byte[size];
            Buffer.BlockCopy(data, 0, part, 0, size);
            return part;
        }

        var copy = new byte[body];
        Buffer.BlockCopy(data, 0, copy, 0, body);
        if (TlsHello.TryGarbleSni(copy, copy.Length)) return copy;
        if (HttpHostGarble(copy, copy.Length)) return copy;

        // имя не нашли (не TLS и не HTTP) — портим отпечаток: пусть DPI видит не то, что ждёт
        for (var i = Math.Max(0, copy.Length / 2); i < copy.Length; i++)
            copy[i] = (byte)Random.Shared.Next(0x41, 0x5A);
        return copy;
    }

    /// <summary>
    /// Разрыв на две части. С переупаковкой в две записи TLS (<c>tlsrec</c> у ByeDPI) — тогда
    /// DPI, собирающий запись по длине из заголовка, получает две неполные записи.
    /// </summary>
    private static async Task SendSplitAsync(NetworkStream stream, byte[] data, int body, int cut, DpiBypassSettings opt, CancellationToken ct)
    {
        if (opt.TlsRecordSplit && TlsHello.TrySplitRecords(data, body, cut, out var rec1, out var rec2))
        {
            await stream.WriteAsync(rec1, ct);
            await stream.FlushAsync(ct);
            await PauseAsync(opt, ct);
            await stream.WriteAsync(rec2, ct);
            await stream.FlushAsync(ct);
            return;
        }

        await stream.WriteAsync(data.AsMemory(0, cut), ct);
        await stream.FlushAsync(ct);
        await PauseAsync(opt, ct);
        await stream.WriteAsync(data.AsMemory(cut, body - cut), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// Disorder (<c>--dpi-desync=disorder</c>): вторая часть уходит сразу, а первая — с TTL=1,
    /// то есть до DPI не доходит вовсе. Ядро считает её потерянной и пересылает через доли
    /// секунды уже с обычным TTL: DPI получает сегменты не по порядку и разрезанный запрос
    /// собирает неверно. Приём работает без прав администратора.
    /// </summary>
    private static async Task SendDisorderAsync(
        TcpClient target, NetworkStream stream, byte[] data, int body, int cut, DpiBypassSettings opt, CancellationToken ct)
    {
        SetTtl(target.Client, 1);
        try
        {
            await stream.WriteAsync(data.AsMemory(0, cut), ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            SetTtl(target.Client, 128);   // обычная TTL: пересылка начала дойдёт до сервера
        }

        await PauseAsync(opt, ct);

        await stream.WriteAsync(data.AsMemory(cut, body - cut), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// Срочные данные (<c>--oob</c> у ByeDPI, функция <c>send_oob</c>): байт в позиции разреза
    /// уходит срочным (MSG_OOB), причём его значение подменяется. DPI срочные данные не разбирает
    /// и видит на месте имени лишний байт, а сервер этот байт из потока выбрасывает (в Windows
    /// и Linux <c>SO_OOBINLINE</c> по умолчанию выключен) — и получает ровно тот же ClientHello,
    /// потому что настоящий байт отправляется повторно, уже обычным порядком.
    /// </summary>
    private static async Task SendOobAsync(
        TcpClient target, NetworkStream stream, byte[] data, int body, int cut, DpiBypassSettings opt, CancellationToken ct)
    {
        // позиция срочного байта: середина имени (как --split-pos у ByeDPI), но не край записи
        var pos = Math.Clamp(cut, 1, Math.Max(1, body - 1));

        var urgent = new byte[pos + 1];
        Buffer.BlockCopy(data, 0, urgent, 0, pos + 1);
        urgent[pos] = (byte)'a';

        var oob = false;
        try
        {
            target.Client.Send(urgent, 0, urgent.Length, SocketFlags.OutOfBand);
            oob = true;
        }
        catch
        {
            /* система не дала срочные данные — шлём то же самое обычным порядком */
        }

        if (oob)
        {
            // настоящий байт уходит ещё раз: сдвига в потоке сервера из-за срочного байта нет
            await stream.WriteAsync(data.AsMemory(pos, 1), ct);
            await stream.FlushAsync(ct);

            await PauseAsync(opt, ct);

            await stream.WriteAsync(data.AsMemory(pos + 1, body - pos - 1), ct);
            await stream.FlushAsync(ct);
            return;
        }

        await stream.WriteAsync(data.AsMemory(0, pos + 1), ct);
        await stream.FlushAsync(ct);

        await PauseAsync(opt, ct);

        await stream.WriteAsync(data.AsMemory(pos + 1, body - pos - 1), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    /// Мультисплит: тот же ClientHello, но кусками по <paramref name="chunk"/> байт с паузой.
    /// Сокет с NoDelay отправляет каждый кусок отдельным сегментом, поэтому DPI видит имя сайта
    /// по частям и собрать его не успевает (<c>--dpi-desync=multisplit</c>).
    /// </summary>
    private static async Task SendManyAsync(NetworkStream stream, byte[] data, int len, int chunk, DpiBypassSettings opt, CancellationToken ct)
    {
        var sent = 0;

        while (sent < len)
        {
            var size = Math.Min(chunk, len - sent);
            await stream.WriteAsync(data.AsMemory(sent, size), ct);
            await stream.FlushAsync(ct);
            sent += size;

            if (sent >= len) break;
            await PauseAsync(opt, ct);
        }
    }

    private static async Task PauseAsync(DpiBypassSettings opt, CancellationToken ct)
    {
        if (opt.SplitDelayMs <= 0) return;

        try { await Task.Delay(opt.SplitDelayMs, ct); }
        catch (OperationCanceledException) { /* соединение закрывают — пауза уже не нужна */ }
    }

    /// <summary>
    /// TTL сокета. Число 4 вместо имени перечисления: в Windows это IP_TTL (IPPROTO_IP)
    /// и IPV6_UNICAST_HOPS (IPPROTO_IPV6); готового имени в .NET под оба случая нет.
    /// </summary>
    private static void SetTtl(Socket socket, int ttl)
    {
        try
        {
            var level = socket.AddressFamily == AddressFamily.InterNetworkV6
                ? SocketOptionLevel.IPv6
                : SocketOptionLevel.IP;
            socket.SetSocketOption(level, (SocketOptionName)IpTtl, Math.Clamp(ttl, 1, 255));
        }
        catch
        {
            /* не дали выставить TTL — обход просто не сработает этим приёмом */
        }
    }

    /// <summary>
    /// Дробление по MSS: принудительный TCP_MAXSEG, чтобы система сама разбила
    /// ClientHello на мелкие сегменты. Ставится до соединения — иначе не действует.
    /// </summary>
    private static void SetMss(Socket socket, int mss)
    {
        try
        {
            // TCP_MAXSEG = 4 на уровне IPPROTO_TCP
            socket.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)TcpMaxSeg, Math.Clamp(mss, 64, 1500));
        }
        catch
        {
            /* не вышло — работаем без дробления по MSS */
        }
    }

    private const int IpTtl = 4;      // IP_TTL / IPV6_UNICAST_HOPS (Windows)
    private const int TcpMaxSeg = 4;  // TCP_MAXSEG (Windows)

    // ------------------------------------------------------------------- перекачка
    /// <summary>Перекачка в обе стороны: ждём, пока замолчит любая из них.</summary>
    private async Task PumpAsync(TcpClient client, TcpClient target, CancellationToken ct)
    {
        var up = PumpOneAsync(client, target, true, ct);
        var down = PumpOneAsync(target, client, false, ct);
        await Task.WhenAny(up, down);
        await Task.WhenAll(up, down);
    }

    private async Task PumpOneAsync(TcpClient from, TcpClient to, bool upload, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[16 * 1024];
            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await from.GetStream().ReadAsync(buffer.AsMemory(), ct).AsTask().WaitAsync(IdleTimeout, ct);
                }
                catch (TimeoutException)
                {
                    break;   // соединение молчит слишком долго
                }

                if (read <= 0) break;

                await to.GetStream().WriteAsync(buffer.AsMemory(0, read), ct);
                if (upload) Interlocked.Add(ref _up, read);
                else Interlocked.Add(ref _down, read);
            }
        }
        catch
        {
            /* соединение закрыто — нормальный конец перекачки */
        }
        finally
        {
            // half-close: без этого вторая сторона ждёт данных, которых уже не будет,
            // и соединение висит до таймаута
            try { to.Client.Shutdown(SocketShutdown.Send); } catch { /* ignore */ }
        }
    }

    // ------------------------------------------------------------------- helpers
    private static void Close(TcpClient? client)
    {
        if (client is null) return;
        try { client.Client.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
        try { client.Close(); } catch { /* ignore */ }
        try { client.Dispose(); } catch { /* ignore */ }
    }

    private static void SplitHostPort(string authority, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrEmpty(authority)) return;

        var text = authority.Trim();
        var colon = text.LastIndexOf(':');
        if (colon < 0)
        {
            host = text;
            port = 80;
            return;
        }

        host = text[..colon].Trim('[', ']');
        port = int.TryParse(text[(colon + 1)..], out var parsed) ? parsed : 0;
    }

    /// <summary>Позиция двоеточия в «Host:» — по ней режем обычный HTTP-запрос.</summary>
    private static int HttpHostSplit(byte[] data, int len)
    {
        var idx = IndexOf(data, len, CrlfHost);
        if (idx < 0) return -1;

        var colon = idx + CrlfHost.Length;   // прямо после «Host» должно быть двоеточие
        return colon < len && data[colon] == (byte)':' ? colon : -1;
    }

    /// <summary>У HTTP-запроса есть тело (POST и подобные): ждать ответа до его отправки нельзя.</summary>
    private static bool HttpHasBody(byte[] data, int len)
    {
        if (len <= 0 || (data[0] != (byte)'P' && data[0] != (byte)'p')) return false;

        var text = Encoding.ASCII.GetString(data, 0, Math.Min(len, 4096));

        // «Transfer-Encoding: chunked» и «Content-Length: N» (N > 0) — тело будет
        foreach (var line in text.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && value.Length > 0) return true;
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, out var size) && size > 0) return true;
        }

        return false;
    }

    /// <summary>Портим значение «Host:» в HTTP-запросе — фейк для DPI без правки структуры.</summary>
    private static bool HttpHostGarble(byte[] data, int len)
    {
        var colon = HttpHostSplit(data, len);
        if (colon < 0) return false;

        var i = colon + 1;
        while (i < len && (data[i] == (byte)' ' || data[i] == (byte)'\t')) i++;

        var start = i;
        while (i < len && data[i] != 13 && data[i] != 10) i++;
        if (i <= start) return false;

        for (var j = start; j < i; j++) data[j] = (byte)Random.Shared.Next(0x61, 0x7A);
        return true;
    }

    private static int IndexOf(byte[] data, int len, byte[] pattern)
    {
        for (var i = 0; i + pattern.Length <= len; i++)
        {
            var j = 0;
            while (j < pattern.Length && data[i + j] == pattern[j]) j++;
            if (j == pattern.Length) return i;
        }
        return -1;
    }

    /// <summary>Дочитывает HTTP-заголовки до пустой строки.</summary>
    private static async Task<(byte[] buffer, int length)> ReadHeadAsync(
        NetworkStream stream, byte[] buffer, int length, CancellationToken ct)
    {
        while (true)
        {
            if (IndexOf(buffer, length, HeadEnd) >= 0) return (buffer, length);
            if (length >= 65536) return (buffer, length);

            if (length == buffer.Length)
            {
                var bigger = new byte[buffer.Length * 2];
                Buffer.BlockCopy(buffer, 0, bigger, 0, length);
                buffer = bigger;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read <= 0) return (buffer, length);
            length += read;
        }
    }

    /// <summary>Абсолютный URI в запросе → origin-form, служебные Proxy-* заголовки долой.</summary>
    private static byte[] RewriteRequest(byte[] data, int length, Uri uri)
    {
        var text = Encoding.ASCII.GetString(data, 0, length);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0) end = text.Length;

        var lines = new List<string>(text[..end].Split("\r\n"));

        var parts = lines[0].Split(' ');
        if (parts.Length >= 3) lines[0] = $"{parts[0]} {uri.PathAndQuery} {parts[2]}";

        var hasHost = false;
        for (var i = 1; i < lines.Count; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;

            var name = lines[i][..colon];
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) hasHost = true;
            if (name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(i);
                i--;
            }
        }

        if (!hasHost) lines.Insert(1, "Host: " + uri.Authority);

        // keep-alive выключаем: иначе следующие запросы по тому же соединению ушли бы
        // без разрыва, и DPI увидел бы имя сайта целиком
        for (var i = lines.Count - 1; i >= 1; i--)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            if (lines[i][..colon].Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                lines[i][..colon].Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                lines[i][..colon].Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase))
                lines.RemoveAt(i);
        }
        lines.Insert(1, "Connection: close");

        return Encoding.ASCII.GetBytes(string.Join("\r\n", lines) + "\r\n\r\n");
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset + read, count - read), ct);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        return stream.WriteAsync(bytes, 0, bytes.Length, ct);
    }
}
