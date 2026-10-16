using VibeTun.Models;

namespace VibeTun.Services;

/// <summary>Итог проверки одного сервиса кнопкой «Тест».</summary>
/// <param name="Answered">Сервер ответил (напрямую или через обход).</param>
/// <param name="Direct">Ответил напрямую — обход для него не нужен.</param>
/// <param name="NeedsVpn">TCP вообще не поднимается: адрес режут, разрыв пакетов не поможет.</param>
/// <param name="Ms">Время ответа, мс; -1 — ответа нет.</param>
/// <param name="Detail">Короткое пояснение для строки состояния.</param>
public readonly record struct DpiServiceTest(bool Answered, bool Direct, bool NeedsVpn, int Ms, string Detail);

/// <summary>Одна найденная стратегия: набор приёмов, подпись и время ответа через неё.</summary>
/// <param name="Settings">Приёмы обхода.</param>
/// <param name="Label">Как приём называется в журнале.</param>
/// <param name="Ms">Сколько шёл ответ через эту стратегию, мс.</param>
public readonly record struct DpiAutoFound(DpiBypassSettings Settings, string Label, int Ms);

/// <summary>Итог подбора стратегии кнопкой «Подобрать».</summary>
/// <param name="Ok">Нашлась хотя бы одна рабочая комбинация.</param>
/// <param name="NotNeeded">Сервис и так открывается напрямую — обход не нужен.</param>
/// <param name="AddressBlocked">Провайдер не пускает до адреса: нужен VPN, а не десинхронизация.</param>
/// <param name="Working">Все найденные рабочие наборы (пусто, если ничего не подошло).</param>
/// <param name="Detail">Короткое пояснение для строки состояния.</param>
public readonly record struct DpiAutoResult(
    bool Ok, bool NotNeeded, bool AddressBlocked, IReadOnlyList<DpiAutoFound> Working, string Detail);

/// <summary>
/// Тест и подбор стратегии для сервиса из раздела «DPI».
///
/// Работает так же, как «Run Tests» в zapret-discord-youtube: берёт домен сервиса, проверяет его
/// напрямую, затем поднимает временный экземпляр движка обхода и пробует приёмы по очереди
/// (дробление ClientHello в разных позициях, disorder, фейк-пакет с малым TTL и повторами,
/// фейк с чужим SNI, авто-TTL, MSS), пока сервер не ответит.
///
/// Движок для проверки слушает свободный порт (0 — порт выбирает система) и гасится после замера,
/// поэтому можно тестировать сервисы, не включая общий обход.
/// </summary>
public static class DpiServiceTester
{
    /// <summary>
    /// Сколько ждём ответа при проверке «Тест». В этот бюджет входит и подключение к адресу,
    /// и ожидание первого байта ответа: если дать меньше, провал стратегии спишется на таймаут,
    /// хотя на самом деле сервер молчит после ClientHello.
    /// </summary>
    private const int TestTimeoutMs = 8000;

    /// <summary>
    /// Сколько ждём ответа на один приём при подборе. Движок под этой проверкой решает судьбу
    /// приёма сам (два окна ожидания по «Ожидание ответа»), поэтому запас нужен с избытком —
    /// иначе провал приёма спишется на таймаут замера.
    /// </summary>
    private const int ScanTimeoutMs = 7000;

    /// <summary>
    /// Проверка обмена пакетами с сервисом: напрямую и через обход с текущими настройками.
    /// В журнал уходит каждый шаг — так же подробно, как в тесте профилей.
    /// </summary>
    public static async Task<DpiServiceTest> TestAsync(
        string domain,
        DpiBypassSettings settings,
        Action<string> log,
        CancellationToken ct = default)
    {
        var host = DpiServiceCatalog.NormalizeHost(domain);
        if (host.Length == 0) return new DpiServiceTest(false, false, false, -1, L.T("dpi.test.noDomain"));

        log(L.F("dpi.log.direct", host));
        var direct = await DpiTester.ProbeAsync(host, 0, TestTimeoutMs, ct);
        log(L.F("dpi.log.directResult", direct.DescribeKind()));

        // напрямую — значит адрес взят у системного DNS: сравниваем его с DoH, потому что
        // подменённый адрес выглядит в замерах точно так же, как блокировка по содержимому
        var poisoned = direct.Ok ? false : await LogDnsAsync(host, log, ct);

        if (direct.Ok)
        {
            var answer = direct.AnswerMs > 0 ? direct.AnswerMs : direct.ConnectMs;
            return new DpiServiceTest(true, true, false, (int)answer, L.F("dpi.test.directOk", answer));
        }

        if (direct.Kind == DpiProbeKind.NoRoute)
        {
            // TCP не поднимается: никакой разрыв пакетов адрес не откроет
            return new DpiServiceTest(false, false, true, -1, L.T("dpi.test.addressBlocked"));
        }

        var engine = await StartEngineAsync(settings, log, ct);
        if (engine is null) return new DpiServiceTest(false, false, false, -1, L.T("dpi.test.engineFailed"));

        try
        {
            log(L.F("dpi.log.via", DpiTester.Describe(settings)));
            var via = await DpiTester.ProbeAsync(host, engine.Port, TestTimeoutMs, ct);
            log(L.F("dpi.log.viaResult", via.DescribeKind()));

            if (via.Ok)
            {
                var answer = via.AnswerMs > 0 ? via.AnswerMs : via.ConnectMs;
                return new DpiServiceTest(true, false, false, (int)answer, L.F("dpi.test.bypassOk", answer));
            }

            return new DpiServiceTest(false, false, false, -1,
                poisoned ? L.F("dpi.test.bypassPoison", host) : L.F("dpi.test.bypassFail", via.Describe()));
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    /// <summary>Сколько рабочих стратегий считаем достаточным (у одной площадки их должно быть несколько).</summary>
    public const int WantedStrategies = 5;

    /// <summary>Сколько вариантов повторяем во второй волне: время подбора тоже конечно.</summary>
    private const int RetryLimit = 12;

    /// <summary>
    /// Подбор приёмов под сервис: сначала проверяем, не открывается ли он и так, затем перебираем
    /// рецепты из <see cref="DpiTester.Strategies"/> и собираем <b>несколько</b> рабочих — одну
    /// стратегию держать бессмысленно: провайдер меняет правила, и запасные нужны сразу.
    ///
    /// Проверка честная: каждая стратегия считается рабочей, только если через неё поднимается
    /// рукопожатие TLS до самого сайта.
    /// </summary>
    public static async Task<DpiAutoResult> AutoAsync(
        string domain,
        DpiBypassSettings settings,
        Action<string> log,
        Action<int, int, string>? progress = null,
        CancellationToken ct = default)
    {
        var host = DpiServiceCatalog.NormalizeHost(domain);
        if (host.Length == 0) return new DpiAutoResult(false, false, false, Array.Empty<DpiAutoFound>(), L.T("dpi.test.noDomain"));

        // У площадки содержимое приходит и с других её доменов (у Discord — discordapp.net,
        // discord.media). Стратегия считается рабочей, только если открывает и такой домен:
        // иначе главная страница откроется, а картинки и вложения останутся «сломанными».
        var contentHost = ContentHostOf(domain);
        if (contentHost is not null) log(L.F("dpi.log.contentCheck", contentHost));

        log(L.F("dpi.log.autoStart", host));
        log(L.F("dpi.log.autoCount", DpiTester.Strategies(settings).Count() + 1, WantedStrategies));

        var direct = await DpiTester.ProbeAsync(host, 0, TestTimeoutMs, ct);
        log(L.F("dpi.log.directResult", direct.DescribeKind()));

        if (direct.Ok)
        {
            log(L.T("dpi.log.autoNotNeeded"));
            return new DpiAutoResult(true, true, false, Array.Empty<DpiAutoFound>(), L.T("dpi.test.directOkShort"));
        }

        if (direct.Kind == DpiProbeKind.NoRoute)
        {
            log(L.T("dpi.log.autoAddressBlocked"));
            return new DpiAutoResult(false, false, true, Array.Empty<DpiAutoFound>(), L.T("dpi.test.addressBlocked"));
        }

        // подменённый адрес выглядит как блокировка по содержимому: говорим о ней до перебора,
        // иначе все причины провала будут названы неверно
        var poisoned = await LogDnsAsync(host, log, ct);

        // Проверяем на контрольном сайте, принимают ли серверы наш браузерный ClientHello
        // (тот же, что уходит в фейк-пакете). Если да — варианты мерим им: это ближе к браузеру,
        // чем «приветствие» .NET, и быстрее полного рукопожатия. Если нет — меряем рукопожатием.
        var useHello = false;
        var control = DpiTester.ControlHosts.FirstOrDefault();
        if (control is not null)
        {
            var check = await DpiTester.ProbeHelloAsync(control, 0, TestTimeoutMs, ct);
            useHello = check.Ok;
            log(useHello
                ? L.T("dpi.log.helloOk")
                : L.F("dpi.log.helloFail", check.DescribeKind()));
        }

        // первым делом — что уже выбрано у сервиса, затем общий набор из «Настроек»:
        // часто обход уже открывает похожие сайты, и это самый быстрый способ проверки
        var candidates = new List<(string Label, DpiBypassSettings Settings)>
        {
            (L.T("dpi.auto.current"), settings)
        };

        var general = App.Settings.Current.Dpi;
        if (DpiServiceCatalog.HasAnyStrategy(general) &&
            DpiServiceCatalog.Signature(general) != DpiServiceCatalog.Signature(settings))
        {
            candidates.Add((L.T("dpi.auto.general"), general.Clone()));
        }

        foreach (var variant in DpiTester.Strategies(settings))
            candidates.Add((DpiTester.Describe(variant), variant));

        var found = new List<DpiAutoFound>();

        // Варианты, которые открывают главную страницу, но не служебный домен (картинки).
        // Держим их отдельно: если ничего лучшего не найдётся, лучше отдать их и сказать об этом.
        var partial = new List<DpiAutoFound>();

        var silent = 0;
        var resets = 0;

        // Варианты, у которых TCP доходил до площадки, но ответа не было: их стоит повторить —
        // первая попытка часто попадает под разрыв или «глушение» ТСПУ.
        var retryable = new List<(string Label, DpiBypassSettings Settings)>();

        async Task TryAsync(string label, DpiBypassSettings candidate)
        {
            var engine = await StartEngineAsync(candidate, log, ct);
            if (engine is null) return;

            DpiProbeResult probe;
            try
            {
                probe = useHello
                    ? await DpiTester.ProbeHelloAsync(host, engine.Port, ScanTimeoutMs, ct)
                    : await DpiTester.ProbeAsync(host, engine.Port, ScanTimeoutMs, ct);

                if (probe.Ok && contentHost is not null)
                {
                    // тот же движок, тот же набор приёмов — но домен из списка сервиса
                    var extra = useHello
                        ? await DpiTester.ProbeHelloAsync(contentHost, engine.Port, ScanTimeoutMs, ct)
                        : await DpiTester.ProbeAsync(contentHost, engine.Port, ScanTimeoutMs, ct);

                    if (extra.Ok)
                    {
                        log(L.F("dpi.log.contentOk", contentHost));
                    }
                    else
                    {
                        log(L.F("dpi.log.contentFail", contentHost, extra.DescribeKind()));

                        // запоминаем и повторяем в конце: ТСПУ часто «глушит» только первую попытку,
                        // а со второго раза тот же набор вполне может открыть и служебный домен
                        if (!partial.Any(f => f.Label == label))
                            partial.Add(new DpiAutoFound(candidate, label, (int)probe.AnswerMs));
                        retryable.Add((label, candidate));

                        probe = extra;
                    }
                }
            }
            finally
            {
                await engine.StopAsync();
            }

            log(L.F("dpi.log.viaResult", probe.DescribeKind()));

            if (probe.Ok)
            {
                var ms = (int)(probe.AnswerMs > 0 ? probe.AnswerMs : probe.ConnectMs);
                found.Add(new DpiAutoFound(candidate, label, ms));
                log(L.F("dpi.log.autoHit", label, found.Count, WantedStrategies));
                return;
            }

            if (probe.Reset)
            {
                resets++;
            }
            else if (probe.Connected)
            {
                silent++;
                retryable.Add((label, candidate));
            }
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (found.Count >= WantedStrategies) break;

            var (label, candidate) = candidates[i];
            progress?.Invoke(i + 1, candidates.Count, label);
            log(L.F("dpi.log.autoTry", i + 1, candidates.Count, label));
            await TryAsync(label, candidate);
        }

        // Вторая волна: повторяем те варианты, у которых TCP доходил до площадки, но ответа
        // не было. Браузер в такой ситуации просто повторяет соединение — здесь так же.
        if (found.Count < WantedStrategies && retryable.Count > 0)
        {
            // один и тот же набор мог попасть в список дважды (служебный домен и «молчание»)
            var again = retryable.GroupBy(r => r.Label).Select(g => g.First()).Take(RetryLimit).ToList();
            log(L.F("dpi.log.autoRetry", again.Count));

            for (var i = 0; i < again.Count && found.Count < WantedStrategies; i++)
            {
                ct.ThrowIfCancellationRequested();
                log(L.F("dpi.log.autoRetryTry", again[i].Label));
                await TryAsync(again[i].Label, again[i].Settings);
            }
        }

        // Ни одна стратегия не открыла служебный домен: отдаём те, что открывают хотя бы
        // главную страницу (сайт будет работать), и прямо говорим, что картинки — нет.
        var partialOnly = false;
        if (found.Count == 0 && partial.Count > 0)
        {
            found.AddRange(partial.Take(WantedStrategies));
            partialOnly = true;
            log(L.F("dpi.log.autoPartial", found.Count));
        }

        if (found.Count == 0)
        {
            // причина называется прямо: сервер молчит после ClientHello (режут по содержимому)
            // или TCP вообще не поднимается (режут по адресу) — «тихого» успеха быть не должно
            log(L.T("dpi.log.autoNone"));

            // Если сайт при этом открывается в браузере при включённом обходе, значит браузер
            // идёт мимо движка — чаще всего по QUIC (UDP/443). Об этом стоит сказать прямо:
            // иначе подбор выглядит сломанным там, где его просто не тот путь.
            log(L.T("dpi.log.autoQuic"));
            // имя не detail: ниже в этом же методе объявлена внешняя переменная с таким именем (CS0136)
            var noneDetail = poisoned
                ? L.F("dpi.test.autoPoison", host)
                : silent > 0
                ? L.F("dpi.test.autoSilent", host)
                : resets > 0 ? L.T("dpi.test.autoReset") : L.T("dpi.test.autoNone");
            return new DpiAutoResult(false, false, false, Array.Empty<DpiAutoFound>(), noneDetail);
        }

        log(L.F("dpi.log.autoFound", found.Count, WantedStrategies));
        var best = found.OrderBy(f => f.Ms).First();
        var detail = partialOnly
            ? L.F("dpi.test.autoPartial", found.Count, contentHost ?? host)
            : found.Count >= WantedStrategies
            ? L.F("dpi.test.autoMany", found.Count, best.Label)
            : L.F("dpi.test.autoFew", found.Count, WantedStrategies, best.Label);

        return new DpiAutoResult(true, false, false, found, detail);
    }

    /// <summary>
    /// Домен площадки, с которого приходит содержимое (картинки, вложения, видео). Нужен, чтобы
    /// подбор не принял за рабочую стратегию ту, что открывает только главную страницу.
    /// </summary>
    private static string? ContentHostOf(string domain)
    {
        var list = DpiServiceCatalog.DomainList(domain);
        if (list.Count < 2) return null;

        var rest = list.Skip(1).ToList();
        var content = rest.FirstOrDefault(h =>
            h.StartsWith("cdn.", StringComparison.Ordinal) ||
            h.StartsWith("img.", StringComparison.Ordinal) ||
            h.StartsWith("static.", StringComparison.Ordinal) ||
            h.Contains("media", StringComparison.Ordinal) ||
            h.Contains("img", StringComparison.Ordinal));

        return content ?? rest[0];
    }

    /// <summary>
    /// Проверка доступности сервиса для списка: время ответа сервера. Через работающий обход, если он
    /// поднят (тогда видно, помогает ли выбранный профиль), иначе напрямую.
    /// </summary>
    public static async Task<DpiServiceTest> PingAsync(string domain, int enginePort, CancellationToken ct = default)
    {
        var host = DpiServiceCatalog.NormalizeHost(domain);
        if (host.Length == 0) return new DpiServiceTest(false, false, false, -1, L.T("dpi.test.noDomain"));

        var probe = await DpiTester.ProbeAsync(host, enginePort, TestTimeoutMs, ct);
        if (probe.Ok)
        {
            var answer = probe.AnswerMs > 0 ? probe.AnswerMs : probe.ConnectMs;
            return new DpiServiceTest(true, enginePort <= 0, false, (int)answer, probe.Describe());
        }

        return new DpiServiceTest(false, false, probe.Kind == DpiProbeKind.NoRoute, -1, probe.Describe());
    }

    /// <summary>
    /// Сравнение системного DNS с DoH. Провайдерский DNS часто отдаёт подставной адрес
    /// заблокированной площадки: соединение при этом «есть», а на ClientHello никто не отвечает,
    /// и по замерам это неотличимо от блокировки по содержимому. Сам zapret по этой же причине
    /// советует Secure DNS — здесь это проверяется и пишется в журнал прямо.
    /// </summary>
    private static async Task<bool> LogDnsAsync(string host, Action<string> log, CancellationToken ct)
    {
        DnsLookup system;
        DnsLookup doh;
        try
        {
            system = await DnsResolver.ResolveSystemAsync(host, ct);
            log(L.F("dpi.log.dnsSystem", host, system.Ok ? system.Text : system.Error));

            doh = await DnsResolver.ResolveDohAsync(host, DohMode.Cloudflare, "", ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            log(L.F("dpi.log.dnsFail", ex.Message));
            return false;
        }

        if (!doh.Ok)
        {
            log(L.F("dpi.log.dnsNoDoh", doh.Error));
            return false;
        }

        log(L.F("dpi.log.dnsDoh", doh.Text));

        var known = system.Addresses.Select(a => a.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (doh.Addresses.Any(a => known.Contains(a.ToString()))) return false;

        log(L.F("dpi.log.dnsPoison", host, system.Text, doh.Text));
        return true;
    }

    /// <summary>Временный движок на свободном порту: слушает только этот замер и сразу гасится.</summary>
    private static async Task<DpiBypassEngine?> StartEngineAsync(
        DpiBypassSettings settings, Action<string> log, CancellationToken ct)
    {
        var engine = new DpiBypassEngine();
        var options = settings.Clone();
        options.ListenPort = 0;      // свободный порт выбирает система
        options.FailoverCount = 1;   // замер идёт по одной стратегии: иначе движок подменит её своей

        // молчание сервера здесь — провал именно этой стратегии: движок скажет об этом сам,
        // не дожидаясь, пока истечёт время у того, кто его об этом попросил
        engine.StrictProbe = true;

        // подъём и остановку движка тоже пишем в журнал: видно, что именно слушалось
        engine.Logged += log;

        try
        {
            await engine.StartAsync(options, upstream: null, ct);
            log(L.F("dpi.log.enginePort", engine.Port));
            return engine;
        }
        catch (Exception ex)
        {
            log(L.F("dpi.log.engineFail", ex.Message));
            return null;
        }
    }
}
