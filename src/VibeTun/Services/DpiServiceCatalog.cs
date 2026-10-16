using VibeTun.Models;

namespace VibeTun.Services;

/// <summary>
/// Сервисы и профили раздела «DPI»: встроенные профили настроек обхода, поиск профиля сервиса и
/// выбор настройки под конкретный домен.
///
/// Встроенные профили повторяют приёмы zapret (см. <c>--dpi-desync</c> из zapret-discord-youtube):
/// дробление ClientHello (split/multisplit), фейк-пакет с малым TTL и повторами, фейк с чужим SNI
/// (fake-tls/hostfakesplit), неверный порядок сегментов (disorder), авто-TTL и дробление по MSS.
/// Всё это выполняется обычными сокетами локального прокси — без драйвера WinDivert.
/// </summary>
public static class DpiServiceCatalog
{
    /// <summary>Домен, который подставляется в фейк-пакет по умолчанию — как <c>sni=www.google.com</c> в zapret.</summary>
    public const string DefaultFakeSni = "www.google.com";

    /// <summary>
    /// Встроенные профили: порядок — от самого «мягкого» приёма к самому агрессивному.
    /// Имена берутся из локализации (<see cref="DpiStrategyProfile.NameKey"/>), поэтому
    /// переключатель языка их переводит.
    /// </summary>
    private static readonly (string Key, string NoteKey, Action<DpiBypassSettings> Apply)[] Presets =
    {
        ("dpi.p.split2", "dpi.n.split2", s =>
        {
            s.SniSplit = true;
            s.TlsRecordSplit = true;
            s.FakeTtl = false;
            s.Multisplit = false;
            s.ClampMss = false;
            s.Disorder = false;
            s.SplitPosition = DpiSplitPosition.Sni;
        }),
        ("dpi.p.split1", "dpi.n.split1", s =>
        {
            s.SniSplit = true;
            s.TlsRecordSplit = false;
            s.FakeTtl = false;
            s.Multisplit = false;
            s.ClampMss = false;
            s.Disorder = false;
            s.SplitPosition = DpiSplitPosition.FirstByte;
        }),
        ("dpi.p.multisplit", "dpi.n.multisplit", s =>
        {
            s.SniSplit = true;
            s.TlsRecordSplit = false;
            s.FakeTtl = false;
            s.Multisplit = true;
            s.MultisplitSize = 8;
            s.ClampMss = false;
            s.Disorder = false;
            s.SplitPosition = DpiSplitPosition.FirstByte;
        }),
        ("dpi.p.fake", "dpi.n.fake", s =>
        {
            s.SniSplit = true;
            s.TlsRecordSplit = false;
            s.FakeTtl = true;
            s.FakeTtlValue = 8;
            s.FakeCount = 3;
            s.FakeAfter = false;
            s.FakeSni = "";
            s.FakeAutoTtl = false;
            s.Multisplit = false;
            s.ClampMss = false;
            s.Disorder = false;
        }),
        ("dpi.p.fakesplit", "dpi.n.fakesplit", s =>
        {
            s.SniSplit = true;
            s.TlsRecordSplit = true;
            s.FakeTtl = true;
            s.FakeTtlValue = 8;
            s.FakeCount = 6;
            s.FakeAfter = false;
            s.FakeSni = "";
            s.FakeAutoTtl = false;
            s.Multisplit = false;
            s.ClampMss = false;
            s.Disorder = false;
        }),
        ("dpi.p.faketls", "dpi.n.faketls", s =>
        {
            s.SniSplit = true;
            s.TlsRecordSplit = false;
            s.FakeTtl = true;
            s.FakeTtlValue = 8;
            s.FakeCount = 6;
            s.FakeAfter = false;
            s.FakeSni = DefaultFakeSni;
            s.FakeAutoTtl = false;
            s.Multisplit = false;
            s.ClampMss = false;
            s.Disorder = false;
        }),
        ("dpi.p.autottl", "dpi.n.autottl", s =>
        {
            s.SniSplit = true;
            s.TlsRecordSplit = false;
            s.FakeTtl = true;
            s.FakeAutoTtl = true;
            s.FakeCount = 3;
            s.FakeAfter = false;
            s.FakeSni = "";
            s.Multisplit = false;
            s.ClampMss = false;
            s.Disorder = false;
        }),
        ("dpi.p.mss", "dpi.n.mss", s =>
        {
            s.SniSplit = false;
            s.TlsRecordSplit = false;
            s.FakeTtl = false;
            s.Multisplit = false;
            s.ClampMss = true;
            s.MssValue = 64;
            s.Disorder = false;
        }),

        // --- приёмы, которых раньше не было: без них фейк-пакет бесполезен для DPI
        ("dpi.p.fakebadsum", "dpi.n.fakebadsum", s =>
        {
            s.SniSplit = true;
            s.TlsRecordSplit = true;
            s.FakeTtl = true;
            s.FakeAutoTtl = false;
            s.FakeTtlValue = 8;
            s.FakeCount = 3;
            s.FakeFooling = DpiFooling.BadSum;
            s.FakePlacement = DpiFakePlacement.Overlap;
            s.SplitPosition = DpiSplitPosition.Sni;
        }),
        ("dpi.p.seqovl", "dpi.n.seqovl", s =>
        {
            s.SniSplit = true;
            s.TlsRecordSplit = false;
            s.SplitPosition = DpiSplitPosition.FirstByte;
            s.FakeTtl = true;
            s.FakeAutoTtl = true;
            s.FakeCount = 1;
            s.FakePlacement = DpiFakePlacement.Ahead;   // фейк занимает байты перед началом запроса
        }),
        ("dpi.p.tlsrec", "dpi.n.tlsrec", s =>
        {
            s.SniSplit = true;
            s.TlsRecordSplit = true;
            s.FakeTtl = false;
            s.SplitPosition = DpiSplitPosition.Sni;
        }),
        ("dpi.p.disorder", "dpi.n.disorder", s =>
        {
            s.SniSplit = true;
            s.TlsRecordSplit = false;
            s.FakeTtl = false;
            s.Multisplit = false;
            s.ClampMss = false;
            s.Disorder = true;
            s.SplitPosition = DpiSplitPosition.FirstByte;
        }),
        ("dpi.p.oob", "dpi.n.oob", s =>
        {
            s.SniSplit = true;
            s.TlsRecordSplit = false;
            s.FakeTtl = false;
            s.OobSplit = true;
            s.SplitPosition = DpiSplitPosition.FirstByte;
        })
    };

    /// <summary>
    /// Версия набора встроенных профилей: при обновлении программы добавляются только новые
    /// профили, уже знакомые пользователю не трогаются.
    /// </summary>
    private const int CurrentPresetsVersion = 2;

    /// <summary>Имя профиля, который создаёт кнопка «Подобрать».</summary>
    public const string AutoNameKey = "dpi.p.auto";

    /// <summary>
    /// Первое открытие раздела: добавляем встроенные профили. Пользователь их потом может удалить —
    /// маркер <see cref="DpiServices.Initialized"/> не даёт им вернуться.
    /// </summary>
    public static void EnsureSeeded(AppSettings settings)
    {
        var section = settings.DpiServices;

        // "Services": null в settings.json, поправленном вручную, — как Entries у раздела «DNS»
        section.Services ??= new List<DpiService>();
        section.Profiles ??= new List<DpiStrategyProfile>();
        section.GeneralDns ??= new DpiDnsChoice { Id = DpiServices.GeneralId };

        // «Общие» вытеснили раздел «Обход DPI» из настроек: набор приёмов у них тот же ключевой
        // (фейк с TTL 8 и разрыв ClientHello), поэтому старым настройкам возвращаем значения
        // по умолчанию — один раз, чтобы дальше правки пользователя не затирались.
        if (!section.GeneralDefaults)
        {
            section.GeneralDefaults = true;
            ApplyDefaults(settings.Dpi);
        }

        foreach (var service in section.Services)
        {
            service.Dns ??= new DpiDnsChoice { Id = service.Id };
            NormalizeDnsChoice(service.Dns);
        }

        NormalizeDnsChoice(section.GeneralDns);

        // у профилей-двойников (импорт одного файла дважды) должен остаться один Id
        foreach (var group in section.Profiles.GroupBy(p => p.Id).Where(g => g.Count() > 1).ToList())
            foreach (var duplicate in group.Skip(1))
                duplicate.Id = Guid.NewGuid().ToString("N");

        // Встроенные профили: при первом открытии разделе — все, при обновлении программы —
        // только новые (версия набора), чтобы правки пользователя не затирались.
        if (section.Initialized && section.PresetsVersion >= CurrentPresetsVersion) return;
        section.Initialized = true;
        section.PresetsVersion = CurrentPresetsVersion;

        foreach (var (key, noteKey, apply) in Presets)
        {
            if (section.Profiles.Any(p => p.NameKey == key)) continue;

            // имя переменной не «settings»: так называется параметр метода, и такое объявление
            // внутри его тела не собирается (CS0136)
            var preset = new DpiBypassSettings();
            apply(preset);
            section.Profiles.Add(new DpiStrategyProfile
            {
                NameKey = key,
                Note = noteKey,
                BuiltIn = true,
                Settings = preset
            });
        }
    }

    /// <summary>
    /// Значения по умолчанию набора приёмов («Общие» и новый сервис): разрыв ClientHello по SNI
    /// с дроблением записи плюс фейк-пакет с TTL 8 — то, что в zapret-сборке стоит в general.bat.
    /// Свойства класса уже такие; метод нужен, чтобы вернуть их набору, поправленному вручную.
    /// </summary>
    public static void ApplyDefaults(DpiBypassSettings settings)
    {
        var defaults = new DpiBypassSettings();

        settings.SniSplit = defaults.SniSplit;
        settings.TlsRecordSplit = defaults.TlsRecordSplit;
        settings.FakeTtl = defaults.FakeTtl;
        settings.InjectFake = defaults.InjectFake;
        settings.FakeFooling = defaults.FakeFooling;
        settings.FakePlacement = defaults.FakePlacement;
        settings.FakeTtlValue = defaults.FakeTtlValue;
        settings.FakeCount = defaults.FakeCount;
        settings.FakeSni = defaults.FakeSni;
        settings.FakeAutoTtl = defaults.FakeAutoTtl;
        settings.FakeAfter = defaults.FakeAfter;
        settings.ClampMss = defaults.ClampMss;
        settings.MssValue = defaults.MssValue;
        settings.Multisplit = defaults.Multisplit;
        settings.MultisplitSize = defaults.MultisplitSize;
        settings.SplitPosition = defaults.SplitPosition;
        settings.Disorder = defaults.Disorder;
        settings.OobSplit = defaults.OobSplit;
        settings.SplitDelayMs = defaults.SplitDelayMs;
        settings.FailoverCount = defaults.FailoverCount;
        settings.FailoverTimeoutMs = defaults.FailoverTimeoutMs;

        // транспортные поля (порт, режим выхода, системный прокси) остаются как были:
        // их пользователь настраивал под себя
    }

    /// <summary>Встроенный набор приёмов «fake TTL» — из него начинают «Тест» и «Подобрать».</summary>
    public static DpiBypassSettings PresetSettings(string nameKey)
    {
        var settings = new DpiBypassSettings();
        foreach (var (key, _, apply) in Presets)
        {
            if (key != nameKey) continue;
            apply(settings);
            break;
        }
        return settings;
    }

    /// <summary>Все встроенные ключи профилей — по ним настраивается первое открытие окна сервиса.</summary>
    public static IEnumerable<string> PresetKeys => Presets.Select(p => p.Key);

    public static DpiStrategyProfile? FindProfile(DpiServices section, string? id) =>
        string.IsNullOrEmpty(id) ? null : section.Profiles.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Настройки обхода сервиса: свои (их скопировали из профиля в окне сервиса) или, если сервис
    /// ещё не правили, — набор выбранного профиля. null — сервис не найден.
    /// </summary>
    public static DpiBypassSettings? SettingsFor(DpiServices section, DpiService? service)
    {
        if (service is null) return null;
        var own = service.Settings;
        if (HasAnyStrategy(own)) return own;
        return FindProfile(section, service.ProfileId)?.Settings ?? own;
    }

    /// <summary>В настройках включён хоть один приём обхода (иначе сервис ничего не делает).</summary>
    public static bool HasAnyStrategy(DpiBypassSettings s) =>
        s.SniSplit || s.TlsRecordSplit || s.FakeTtl || s.Multisplit || s.ClampMss || s.Disorder || s.OobSplit;

    /// <summary>
    /// Сервис, к которому относится домен: точное совпадение или поддомен («www.youtube.com» —
    /// это «youtube.com»). Ведущее «*.» у домена сервиса просто отбрасывается.
    /// </summary>
    public static DpiService? Match(DpiServices section, string host)
    {
        var name = NormalizeHost(host);
        if (name.Length == 0) return null;

        DpiService? best = null;
        var bestLength = -1;
        foreach (var service in section.Services)
        {
            if (!service.Enabled) continue;

            foreach (var domain in DomainList(service.Domain))
            {
                if (!IsMatch(name, domain)) continue;

                // самый точный (длинный) домен важнее общего: «media.discord.com» важнее «discord.com»
                if (domain.Length <= bestLength) continue;
                best = service;
                bestLength = domain.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// Итоговые настройки обхода для домена. Порядок старшинства: сначала набор сервиса (даже когда
    /// включены «Общие» — отдельный профиль важнее общего), затем «Общие», если они включены,
    /// иначе — набор без приёмов: трафик к чужим доменам идёт как есть.
    ///
    /// Транспортные поля (порт, режим выхода, системный прокси) всегда берутся из общего раздела —
    /// иначе сервис увёл бы движок на другой порт или в другой режим.
    /// </summary>
    public static DpiBypassSettings StrategyFor(AppSettings settings, string host)
    {
        var section = settings.DpiServices;
        var service = Match(section, host);

        if (service is not null)
        {
            var own = SettingsFor(section, service);
            if (own is not null && HasAnyStrategy(own))
            {
                var mine = own.Clone();
                CopyTransport(settings, mine);
                ApplyDns(mine, service.Dns, settings.Dns);
                return mine;
            }
        }

        if (section.GeneralEnabled)
        {
            // «Общие» включены: приёмы применяются ко всему, сервиса для этого домена нет
            var general = settings.Dpi.Clone();
            ApplyDns(general, section.GeneralDns, settings.Dns);
            return general;
        }

        // «Общие» выключены, а своего сервиса у домена нет — обход к нему не применяется
        return Passthrough(settings, section.GeneralDns);
    }

    /// <summary>
    /// Набор без приёмов: соединение поднимается и ретранслируется как есть. Так работает режим
    /// «только сервисы» — когда «Общие» выключены, а домен не принадлежит ни одному сервису.
    /// </summary>
    public static DpiBypassSettings Passthrough(AppSettings settings, DpiDnsChoice? dns = null)
    {
        var result = settings.Dpi.Clone();
        result.SniSplit = false;
        result.TlsRecordSplit = false;
        result.FakeTtl = false;
        result.InjectFake = false;
        result.FakeAfter = false;
        result.Multisplit = false;
        result.ClampMss = false;
        result.Disorder = false;
        result.OobSplit = false;
        result.FailoverCount = 1;
        ApplyDns(result, dns, settings.Dns);
        return result;
    }

    /// <summary>Транспортные поля берём из общего раздела: их настраивают только у «Общих».</summary>
    private static void CopyTransport(AppSettings settings, DpiBypassSettings target)
    {
        target.Enabled = settings.Dpi.Enabled;
        target.ListenPort = settings.Dpi.ListenPort;
        target.Doh = settings.Dpi.Doh;
        target.DohUrl = settings.Dpi.DohUrl;
        target.ExitMode = settings.Dpi.ExitMode;
        target.UseSystemProxy = settings.Dpi.UseSystemProxy;
    }

    /// <summary>
    /// Каким сервером спрашивать адреса доменов: у сервиса и «Общих» может быть выбран DNS
    /// в разделе «DNS» — так же, как для домена и приложения. Не выбрано — как раньше:
    /// системный DNS или DoH обхода из раздела «DNS».
    /// </summary>
    public static void ApplyDns(DpiBypassSettings target, DpiDnsChoice? choice, DnsSettings dns)
    {
        target.ResolveServer = "";

        if (choice is null || choice.Mode.Length == 0) return;

        switch (choice.Mode)
        {
            case DnsModes.System:
                target.Doh = DohMode.Off;      // адреса спрашивает системный резолвер
                break;

            case DnsModes.Proxy:
                target.Doh = DohMode.Cloudflare;   // провайдерский DNS не участвует
                break;

            case DnsModes.Server:
                var server = (choice.Server ?? "").Trim();
                if (server.Length == 0) break;

                if (server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    target.Doh = DohMode.Custom;
                    target.DohUrl = server;
                }
                else
                {
                    target.ResolveServer = StripScheme(server);
                }
                break;
        }
    }

    /// <summary>«tls://1.1.1.1» → «1.1.1.1»: движку нужен адрес, а не схема.</summary>
    private static string StripScheme(string server)
    {
        var index = server.IndexOf("://", StringComparison.Ordinal);
        return index >= 0 ? server[(index + 3)..].TrimEnd('/') : server;
    }

    /// <summary>
    /// Подпись выбранного DNS сервиса — теми же словами, что в разделе «DNS»: «Системный»,
    /// «Через Proxy», «Cloudflare · 1.1.1.1». Пусто — свой DNS не выбран.
    /// </summary>
    public static string DnsTextFor(DpiDnsChoice? choice, DnsSettings dns)
    {
        if (choice is null || choice.Mode.Length == 0) return "";

        if (choice.Mode == DnsModes.System) return L.T("dpi.dns.system");
        if (choice.Mode == DnsModes.Proxy) return L.T("dpi.dns.proxy");

        var server = (choice.Server ?? "").Trim();
        if (server.Length == 0) return "";

        var entry = dns.Entries.FirstOrDefault(e => e.Id == choice.EntryId);
        return entry is null ? server : entry.Name + " · " + server;
    }

    /// <summary>
    /// Привести выбор DNS сервиса к тому же виду, что у домена и приложения: режим без адреса
    /// и прочий мусор из старого settings.json — «не выбрано».
    /// </summary>
    public static void NormalizeDnsChoice(DpiDnsChoice? choice)
    {
        if (choice is null) return;

        if (choice.Mode is not (DnsModes.System or DnsModes.Proxy or DnsModes.Server)) choice.Mode = "";
        if (choice.Mode == DnsModes.Server && string.IsNullOrWhiteSpace(choice.Server)) choice.Mode = "";
        if (choice.Mode != DnsModes.Server)
        {
            choice.EntryId = null;
            choice.Server = null;
        }
    }

    /// <summary>
    /// Подпись набора приёмов: по ней видно, что два профиля делают одно и то же, и движок
    /// не тратит попытки на повторы (в подборе такие варианты тоже не дублируются).
    /// </summary>
    public static string Signature(DpiBypassSettings s)
    {
        var tls = $"{(s.SniSplit ? 1 : 0)}{(int)s.SplitPosition}{(s.TlsRecordSplit ? 1 : 0)}" +
                  $"{(s.Disorder ? 1 : 0)}{(s.OobSplit ? 1 : 0)}{(s.Multisplit ? s.MultisplitSize : 0)}" +
                  $"{(s.ClampMss ? s.MssValue : 0)}";

        var fake = s.FakeTtl
            ? $"{s.FakeAutoTtl}-{s.FakeTtlValue}-{s.FakeCount}-{(int)s.FakeFooling}-{(int)s.FakePlacement}-" +
              $"{(s.FakeAfter ? 1 : 0)}-{s.FakeSni.Trim().ToLowerInvariant()}{(s.InjectFake ? 1 : 0)}"
            : "-";

        return tls + "|" + fake;
    }

    /// <summary>
    /// Подпись маршрута для журнала: под какой набор попал домен. По ней видно и совпадение с
    /// сервисом (вместе со всеми его доменами), и работу «Общих», и режим «только сервисы».
    /// </summary>
    public static string RouteLabelFor(AppSettings settings, string host)
    {
        var section = settings.DpiServices;
        var service = Match(section, host);
        if (service is not null)
        {
            var domains = DomainList(service.Domain);
            return L.F("dpi.route.service",
                service.Name + (domains.Count == 0 ? "" : " (" + string.Join(", ", domains) + ")"));
        }

        return section.GeneralEnabled ? L.T("dpi.route.general") : L.T("dpi.route.none");
    }

    /// <summary>Основной домен сервиса: первый из списка — по нему берётся значок и имя профиля.</summary>
    public static string Primary(string domains)
    {
        var list = DomainList(domains);
        return list.Count > 0 ? list[0] : NormalizeHost(domains);
    }

    /// <summary>
    /// Запасные стратегии сервиса — то, что нашла кнопка «Подобрать»: если первая не откроет
    /// поток, движок попробует следующую (как очередь правил в zapret). Первым идёт самый
    /// быстрый набор, дальше — по порядку подбора. Профили ищутся по всем доменам сервиса.
    /// </summary>
    public static IReadOnlyList<DpiBypassSettings> StrategyListFor(AppSettings settings, string host)
    {
        var section = settings.DpiServices;
        var service = Match(section, host);
        if (service is null) return Array.Empty<DpiBypassSettings>();

        var domains = DomainList(service.Domain);
        if (domains.Count == 0) return Array.Empty<DpiBypassSettings>();

        var list = new List<DpiBypassSettings>();
        foreach (var profile in section.Profiles
                     .Where(p => p.Auto && domains.Contains(NormalizeHost(p.Domain), StringComparer.Ordinal))
                     .OrderBy(p => p.Rank)
                     .ThenBy(p => p.UpdatedAt))
        {
            if (!HasAnyStrategy(profile.Settings)) continue;

            // транспорт и выбранный DNS — те же, что у основной стратегии сервиса: запасной
            // вариант не должен ходить другим путём или спрашивать адреса у провайдера
            var clone = profile.Settings.Clone();
            CopyTransport(settings, clone);
            ApplyDns(clone, service.Dns, settings.Dns);
            list.Add(clone);
        }

        return list;
    }

    /// <summary>Имя профиля подбора: «auto», «auto 2», «auto 3» — по порядку найденных стратегий.</summary>
    public static string AutoName(int index) => index <= 0 ? L.T(AutoNameKey) : $"{L.T(AutoNameKey)} {index + 1}";

    /// <summary>
    /// Все домены сервиса: в поле можно перечислить несколько через запятую, точку с запятой
    /// или с новой строки. Так сервис покрывает и служебные домены площадки: у Discord это
    /// discordapp.com / discordapp.net / discord.media, у YouTube — ytimg.com / googlevideo.com.
    /// </summary>
    public static List<string> DomainList(string text)
    {
        var list = new List<string>();
        foreach (var part in (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var host = NormalizeHost(part);
            if (host.Length == 0) continue;
            if (list.Any(h => string.Equals(h, host, StringComparison.Ordinal))) continue;
            list.Add(host);
        }

        return list;
    }

    /// <summary>Домены сервиса одной строкой — так они и хранятся в настройках.</summary>
    public static string Join(IEnumerable<string> domains) => string.Join(", ", domains);

    /// <summary>
    /// Известные служебные домены площадок: без них открывается сама страница, а картинки,
    /// видео и вложения — нет, потому что они приходят с другого домена. Так устроен и zapret:
    /// в его hostlist у Discord перечислены discord.media и discordapp.
    /// </summary>
    private static readonly Dictionary<string, string[]> Related = new(StringComparer.Ordinal)
    {
        ["discord.com"] = new[] { "discordapp.com", "discordapp.net", "discord.media", "discord.gg" },
        ["youtube.com"] = new[] { "youtu.be", "ytimg.com", "googlevideo.com", "youtube-nocookie.com", "ggpht.com" }
    };

    /// <summary>Служебные домены площадки, которых ещё нет в поле домена (для подсказки в окне).</summary>
    public static List<string> RelatedMissing(string domains)
    {
        var have = DomainList(domains);
        var missing = new List<string>();

        foreach (var baseHost in have)
        {
            if (!Related.TryGetValue(baseHost, out var extra)) continue;

            foreach (var host in extra)
            {
                if (have.Any(h => string.Equals(h, host, StringComparison.Ordinal))) continue;
                if (missing.Any(h => string.Equals(h, host, StringComparison.Ordinal))) continue;
                missing.Add(host);
            }
        }

        return missing;
    }

    /// <summary>Домен вида «https://www.youtube.com/feed» → «www.youtube.com».</summary>
    public static string NormalizeHost(string text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();
        if (value.Length == 0) return "";

        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) value = value[(scheme + 3)..];

        // доменов у сервиса может быть несколько: берём первый из списка
        var cut = value.IndexOfAny(new[] { ' ', ',', ';', '\t', '\r', '\n' });
        if (cut >= 0) value = value[..cut];

        var slash = value.IndexOf('/');
        if (slash >= 0) value = value[..slash];

        var at = value.LastIndexOf('@');
        if (at >= 0) value = value[(at + 1)..];

        var colon = value.IndexOf(':');
        if (colon >= 0) value = value[..colon];

        while (value.StartsWith("*.", StringComparison.Ordinal)) value = value[2..];
        if (value.StartsWith('.')) value = value[1..];
        return value.TrimEnd('.');
    }

    private static bool IsMatch(string host, string domain) =>
        host.Equals(domain, StringComparison.Ordinal) ||
        host.EndsWith("." + domain, StringComparison.Ordinal);
}
