using System.Net;
using System.Net.Sockets;

namespace VibeTun.Utils;

/// <summary>
/// Адрес своего DNS из окна «Добавить свой»: IP (IPv4/IPv6) или, для шифрованного DNS,
/// адрес вида tls://, https://, quic://, h3://, udp:// — те же формы, что понимает сборщик конфига.
/// </summary>
public static class DnsAddress
{
    private static readonly string[] Schemes = { "udp", "tls", "https", "quic", "h3" };

    /// <summary>Проверить ввод и привести его к виду для конфига. false — <paramref name="error"/> объясняет, что не так.</summary>
    public static bool TryNormalize(string? input, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        var text = (input ?? "").Trim();

        if (text.Length == 0)
        {
            error = "Укажите IP-адрес DNS-сервера";
            return false;
        }
        if (text.Any(char.IsWhiteSpace))
        {
            error = "В адресе не должно быть пробелов";
            return false;
        }

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            if (TryParseIp(text, out var ip))
            {
                normalized = ip.ToString();
                return true;
            }
            error = "Это не IP-адрес. Пример: 1.1.1.1 или 2606:4700:4700::1111";
            return false;
        }

        var scheme = text[..schemeEnd].ToLowerInvariant();
        if (!Schemes.Contains(scheme))
        {
            error = "Поддерживаются адреса udp://, tls://, https://, quic:// и h3://";
            return false;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            error = "Не удалось разобрать адрес сервера";
            return false;
        }

        normalized = scheme + text[schemeEnd..];
        return true;
    }

    /// <summary>
    /// Разобрать адрес DNS из списка раздела на схему, хост и порт — для пинга:
    /// «1.1.1.1» → udp, 1.1.1.1, 0; «tls://dns.example:853» → tls, dns.example, 853;
    /// «[2606:4700::1111]:53» → udp, 2606:4700::1111, 53. Порт 0 — стандартный для схемы.
    /// Путь DoH отбрасывается.
    /// </summary>
    public static bool TryParseEndpoint(string? address, out string scheme, out string host, out int port)
    {
        scheme = "udp";
        host = "";
        port = 0;
        var rest = (address ?? "").Trim();

        var schemeEnd = rest.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            scheme = rest[..schemeEnd].ToLowerInvariant();
            rest = rest[(schemeEnd + 3)..];
        }

        var slash = rest.IndexOf('/');
        if (slash >= 0) rest = rest[..slash];

        if (rest.StartsWith('['))
        {
            // [IPv6] или [IPv6]:порт
            var close = rest.IndexOf(']');
            if (close < 0) return false;
            var tail = rest[(close + 1)..];
            if (tail.StartsWith(':') && int.TryParse(tail[1..], out var bracketPort) && bracketPort is > 0 and < 65536)
                port = bracketPort;
            host = rest[1..close];
        }
        else if (rest.Count(c => c == ':') == 1)
        {
            // имя:порт или IPv4:порт (у IPv6 без скобок двоеточий больше одного)
            var colon = rest.IndexOf(':');
            if (int.TryParse(rest[(colon + 1)..], out var hostPort) && hostPort is > 0 and < 65536)
                port = hostPort;
            host = rest[..colon];
        }
        else
        {
            host = rest;   // имя, IPv4 или IPv6 без скобок
        }

        return host.Length > 0 && Schemes.Contains(scheme);
    }

    /// <summary>
    /// IPv4 строго из четырёх чисел или IPv6 (можно в скобках). IPAddress.TryParse сам по себе
    /// принимает и «1», и «1.2» (это 0.0.0.1 и 1.0.0.2) — для DNS-сервера нужен полный адрес.
    /// </summary>
    public static bool TryParseIp(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        var value = (text ?? "").Trim();
        if (value.Length > 2 && value[0] == '[' && value[^1] == ']') value = value[1..^1];
        if (!IPAddress.TryParse(value, out var parsed)) return false;

        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            var parts = value.Split('.');
            if (parts.Length != 4 || parts.Any(p => p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit))) return false;
        }
        else if (parsed.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        address = parsed;
        return true;
    }
}
