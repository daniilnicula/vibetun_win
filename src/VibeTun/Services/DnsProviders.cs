using System.Net;
using System.Net.Sockets;
using VibeTun.Models;

namespace VibeTun.Services;

/// <summary>DNS-сервер провайдера: адрес и вариант — режим фильтрации или регион выхода.</summary>
/// <param name="Label">Подпись рядом с адресом в строке сервера; пусто — без подписи.</param>
public sealed record DnsProviderServer(string Address, string Label);

/// <summary>Имя, A-записи которого — DNS-серверы провайдера (у провайдеров, чьи серверы меняются).</summary>
/// <param name="Label">Подпись серверов этого имени, например регион выхода.</param>
public sealed record DnsProviderHost(string Host, string Label);

/// <summary>Встроенный провайдер DNS.</summary>
/// <param name="Id">Постоянный ключ записи в настройках.</param>
/// <param name="Tagline">Коротко, для чего провайдер, — вторая строка в списке.</param>
/// <param name="Site">Сайт провайдера (кнопка «Сайт» в раскрытой строке).</param>
/// <param name="Servers">Адреса из справочника приложения — только IPv4 обычного DNS (порт 53).</param>
/// <param name="Hosts">
/// Серверы у провайдера меняются: адреса не храним в справочнике, а спрашиваем DNS-запросом
/// к этим именам (см. <see cref="DnsProviders.ResolveAsync"/>). Пусто — адреса из <paramref name="Servers"/>.
/// </param>
/// <param name="Description">Подробности над списком серверов; пусто — не показываются.</param>
public sealed record DnsProviderInfo(
    string Id, string Name, string Tagline, string Site,
    IReadOnlyList<DnsProviderServer> Servers, IReadOnlyList<DnsProviderHost> Hosts, string Description)
{
    /// <summary>Список серверов спрашиваем у провайдера, а не берём из справочника.</summary>
    public bool IsDynamic => Hosts.Count > 0;
}

/// <summary>Ответ на запрос к провайдеру: адреса его DNS-серверов.</summary>
public sealed class DnsProviderLookup
{
    public List<DnsProviderServer> Servers { get; } = new();

    /// <summary>Чем получены адреса: «системный DNS» или «DoH …», плюс имена, которые не ответили.</summary>
    public string Source { get; set; } = "";

    public string Error { get; set; } = "";
}

/// <summary>
/// Провайдеры для раздела «DNS». У большинства адреса постоянные — они в справочнике ниже (только
/// IPv4 обычного DNS: шифрованные варианты DoH/DoT/DoQ сюда не входят). У GeoHide серверы меняются,
/// поэтому их адреса спрашиваем у самого провайдера DNS-запросом к его именам.
/// Провайдеры, которые не принимают обычный DNS на порту 53 (DNS-AI, Mullvad, BlahDNS — только
/// шифрование), в справочник не включены. Новый провайдер — один элемент в <see cref="BuiltIn"/>.
/// </summary>
public static class DnsProviders
{
    private static IReadOnlyList<DnsProviderInfo>? _builtIn;
    private static string? _catalogLanguage;
    public static IReadOnlyList<DnsProviderInfo> BuiltIn
    {
        get
        {
            if (_builtIn is null || _catalogLanguage != L.Language)
            {
                _builtIn = CreateBuiltIn();
                _catalogLanguage = L.Language;
            }
            return _builtIn;
        }
    }

    private static IReadOnlyList<DnsProviderInfo> CreateBuiltIn() => new[]
    {
        Dynamic("geohide", "GeoHide", L.T("S0344"), "https://geohide.ru/",
            L.T("S0345") +
            L.T("S0346") +
            L.T("S0347"),
            new DnsProviderHost("dns.geohide.ru", "RU"),
            new DnsProviderHost("eu.geohide.ru", "EU"),
            new DnsProviderHost("us.geohide.ru", "US")),

        Static("xbox-dns", "Xbox DNS", L.T("S0348"), "https://xbox-dns.ru/",
            L.T("S0349"),
            Group("", "111.88.96.54", "111.88.96.55")),

        Static("comss", "Comss.one", L.T("S0350"), "https://www.comss.ru/page.php?id=7315",
            L.T("S0351"),
            Group("", "83.220.169.155", "212.109.195.93")),

        Static("quad9", "Quad9", L.T("S0352"), "https://quad9.net/", "",
            Group("", "9.9.9.9", "149.112.112.112")),

        Static("adguard", "AdGuard", L.T("S0353"), "https://adguard-dns.io/", "",
            Group(L.T("S0354"), "94.140.14.14", "94.140.15.15"),
            Group(L.T("S0355"), "94.140.14.15", "94.140.15.16"),
            Group(L.T("S0356"), "94.140.14.140", "94.140.14.141")),

        Static("cloudflare", "Cloudflare", L.T("S0357"), "https://one.one.one.one/", "",
            Group(L.T("S0356"), "1.1.1.1", "1.0.0.1"),
            Group(L.T("S0358"), "1.1.1.2", "1.0.0.2"),
            Group(L.T("S0355"), "1.1.1.3", "1.0.0.3")),

        Static("dns4eu", "DNS4EU", L.T("S0359"), "https://www.joindns4.eu/for-public", "",
            Group(L.T("S0360"), "86.54.11.1", "86.54.11.201"),
            Group(L.T("S0361"), "86.54.11.12", "86.54.11.212"),
            Group(L.T("S0362"), "86.54.11.13", "86.54.11.213"),
            Group(L.T("S0363"), "86.54.11.11", "86.54.11.211"),
            Group(L.T("S0356"), "86.54.11.100", "86.54.11.200")),

        Static("dnsforge", "dnsforge.de", L.T("S0364"), "https://dnsforge.de/", "",
            Group(L.T("S0365"), "49.12.67.122", "91.99.154.175", "176.9.93.198", "176.9.1.117"),
            Group(L.T("S0366"), "49.12.223.2", "49.12.43.208"),
            Group(L.T("S0367"), "49.12.222.213", "88.198.122.154"),
            Group(L.T("S0356"), "138.199.149.249", "78.47.71.194")),

        Static("cleanbrowsing", "CleanBrowsing", L.T("S0368"), "https://cleanbrowsing.org/filters/", "",
            Group(L.T("S0360"), "185.228.168.9", "185.228.169.9"),
            Group(L.T("S0369"), "185.228.168.10", "185.228.169.11"),
            Group(L.T("S0355"), "185.228.168.168", "185.228.169.168")),

        Static("yandex", "Yandex DNS", L.T("S0370"), "https://dns.yandex.ru/", "",
            Group(L.T("S0371"), "77.88.8.8", "77.88.8.1"),
            Group(L.T("S0372"), "77.88.8.88", "77.88.8.2"),
            Group(L.T("S0355"), "77.88.8.7", "77.88.8.3")),

        Static("google", "Google DNS", L.T("S0373"), "https://developers.google.com/speed/public-dns", "",
            Group("", "8.8.8.8", "8.8.4.4")),

        Static("opendns", "OpenDNS", L.T("S0374"), "https://www.opendns.com/", "",
            Group(L.T("S0375"), "208.67.222.222", "208.67.220.220"),
            Group("FamilyShield", "208.67.222.123", "208.67.220.123")),

        Static("nextdns", "NextDNS", L.T("S0376"), "https://nextdns.io/",
            L.T("S0377") +
            L.T("S0378"),
            Group("", "45.90.28.0", "45.90.30.0")),

        Static("controld", "Control D", L.T("S0379"), "https://controld.com/free-dns", "",
            Group(L.T("S0356"), "76.76.2.0", "76.76.10.0"),
            Group(L.T("S0358"), "76.76.2.1", "76.76.10.1"),
            Group(L.T("S0380"), "76.76.2.2", "76.76.10.2"),
            Group(L.T("S0381"), "76.76.2.3", "76.76.10.3"),
            Group(L.T("S0355"), "76.76.2.4", "76.76.10.4"),
            Group(L.T("S0382"), "76.76.2.5", "76.76.10.5")),
    };

    private static DnsProviderInfo Static(string id, string name, string tagline, string site, string description,
        params DnsProviderServer[][] groups) =>
        new(id, name, tagline, site, groups.SelectMany(g => g).ToArray(), Array.Empty<DnsProviderHost>(), description);

    private static DnsProviderInfo Dynamic(string id, string name, string tagline, string site, string description,
        params DnsProviderHost[] hosts) =>
        new(id, name, tagline, site, Array.Empty<DnsProviderServer>(), hosts, description);

    /// <summary>Серверы одного варианта (режима фильтрации) провайдера.</summary>
    private static DnsProviderServer[] Group(string label, params string[] addresses) =>
        addresses.Select(a => new DnsProviderServer(a, label)).ToArray();

    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(6);

    public static DnsProviderInfo? Find(string? id) =>
        id is null ? null : BuiltIn.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Привести список из настроек в порядок: встроенные провайдеры есть все и с актуальными
    /// именами (новый встаёт сразу за уже известными провайдерами), провайдеры, которых в приложении
    /// больше нет, и свои DNS без адреса убираются, повторяющиеся ключи заменяются.
    /// </summary>
    public static void Normalize(List<DnsEntry> entries)
    {
        entries.RemoveAll(e => e is null);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            // свой DNS не может занять ключ встроенного провайдера (ручная правка settings.json)
            var clash = !entry.IsProvider && Find(entry.Id) is not null;
            if (string.IsNullOrWhiteSpace(entry.Id) || clash || !seen.Add(entry.Id))
            {
                entry.Id = Guid.NewGuid().ToString("N");
                seen.Add(entry.Id);
            }
            entry.Name ??= "";
            entry.Servers ??= new List<string>();
        }

        entries.RemoveAll(e => e.IsProvider ? Find(e.Id) is null : string.IsNullOrWhiteSpace(e.Address));

        foreach (var info in BuiltIn)
        {
            var entry = entries.FirstOrDefault(e => e.IsProvider && e.Id == info.Id);
            if (entry is null)
            {
                entry = new DnsEntry { Id = info.Id, IsProvider = true };
                entries.Insert(entries.FindLastIndex(e => e.IsProvider) + 1, entry);
            }
            entry.Name = info.Name;
            entry.Host = null;      // имена для запроса теперь в справочнике (DnsProviderInfo.Hosts)
            entry.Address = null;

            if (!info.IsDynamic)
            {
                // адреса берутся из справочника приложения — хранить их в настройках незачем
                entry.Servers.Clear();
                entry.ServerLabels = null;
                entry.ServersUpdatedAt = null;
                entry.ServersSource = null;
            }
            else
            {
                // провайдерам — только IPv4; список старой версии (без подписей регионов)
                // показываем, но при раскрытии раздела перезапрашиваем
                entry.Servers.RemoveAll(a => !IsIPv4(a));
                if (entry.ServerLabels is null) entry.ServersUpdatedAt = null;
            }
        }
    }

    /// <summary>
    /// DNS-запрос к именам провайдера (все сразу): адреса из ответов — его DNS-серверы с подписью
    /// имени (регион выхода). Имя, которое не ответило, отмечается в <see cref="DnsProviderLookup.Source"/>;
    /// ошибка — только если адресов не дало ни одно имя.
    /// </summary>
    public static async Task<DnsProviderLookup> ResolveAsync(DnsProviderInfo info, CancellationToken ct = default)
    {
        var result = new DnsProviderLookup();
        if (!info.IsDynamic)
        {
            result.Error = L.T("S0383");
            return result;
        }

        var answers = await Task.WhenAll(info.Hosts.Select(h => ResolveHostAsync(h.Host, ct)));

        var sources = new List<string>();
        var failed = new List<string>();
        for (var i = 0; i < info.Hosts.Count; i++)
        {
            var host = info.Hosts[i];
            var answer = answers[i];
            if (answer.Servers.Count == 0)
            {
                failed.Add(host.Label.Length > 0 ? host.Label : host.Host);
                if (result.Error.Length == 0) result.Error = answer.Error;
                continue;
            }

            foreach (var address in answer.Servers)
                if (!result.Servers.Any(s => s.Address == address))
                    result.Servers.Add(new DnsProviderServer(address, host.Label));
            if (!sources.Contains(answer.Source)) sources.Add(answer.Source);
        }

        if (result.Servers.Count == 0)
        {
            if (result.Error.Length == 0) result.Error = L.T("S0384");
            return result;
        }

        result.Error = "";
        result.Source = string.Join(", ", sources) +
                        (failed.Count > 0 ? L.T("S0385") + string.Join(", ", failed) : "");
        return result;
    }

    /// <summary>
    /// DNS-запрос к одному имени. Сначала резолвер Windows; если он молчит или отдаёт заглушку — DoH
    /// (Cloudflare, затем Google): провайдерский DNS бывает подменён, а DoH идёт поверх HTTPS.
    /// </summary>
    private static async Task<(List<string> Servers, string Source, string Error)> ResolveHostAsync(
        string host, CancellationToken ct)
    {
        var error = "";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LookupTimeout);
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, timeout.Token);
            var servers = Usable(addresses);
            if (servers.Count > 0) return (servers, L.T("S0386"), "");
            error = L.T("S0387");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error = L.T("S0388");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }

        foreach (var doh in new[] { DohMode.Cloudflare, DohMode.Google })
        {
            ct.ThrowIfCancellationRequested();
            var lookup = await DnsResolver.ResolveDohAsync(host, doh, "", ct);
            var servers = Usable(lookup.Addresses);
            if (servers.Count > 0) return (servers, lookup.Source, "");
        }

        return (new List<string>(), "", error.Length > 0 ? error : L.T("S0384"));
    }

    private static bool IsIPv4(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    /// <summary>
    /// Адреса из ответа без мусора (0.0.0.0, 127.x, частные сети — так провайдеры «блокируют» имя)
    /// и без повторов: только IPv4 (IPv6 у провайдеров не используем), по возрастанию — порядок
    /// не скачет от запроса к запросу.
    /// </summary>
    private static List<string> Usable(IEnumerable<IPAddress> addresses) =>
        addresses
            .Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsUsable(a))
            .Distinct()
            .OrderBy(a => Convert.ToHexString(a.GetAddressBytes()), StringComparer.Ordinal)
            .Select(a => a.ToString())
            .ToList();

    private static bool IsUsable(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] >= 224) return false;      // 0.0.0.0/8, 10/8, мультикаст и выше
            if (b[0] == 169 && b[1] == 254) return false;                  // link-local
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;     // 172.16/12
            if (b[0] == 192 && b[1] == 168) return false;                  // 192.168/16
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal ||
                address.IsIPv6Multicast || address.IsIPv6SiteLocal) return false;
            var first = address.GetAddressBytes()[0];
            return (first & 0xFE) != 0xFC;                                 // fc00::/7 — локальные адреса
        }

        return false;
    }
}
