using System.Text.RegularExpressions;
using VibeTun.Models;
using VibeTun.Services;

namespace VibeTun.Utils;

/// <summary>
/// Запись вкладки «Домены» раздела «DNS»: домен («youtube.com», «.ru», «*.example.org») или regex
/// («youtube», «regex:^ads\.»). Тип определяется так же, как в «Маршрутизации»
/// (<see cref="Text.NormalizeUserPattern"/>), а поле sing-box — тем же <see cref="SingBoxConfigBuilder.Normalize"/>:
/// одна и та же запись значит в обоих разделах одно и то же. IP не принимается: к IP-адресу
/// программа подключается сразу, DNS при этом не спрашивают.
/// </summary>
public static class DnsPattern
{
    /// <summary>Поле sing-box (domain_suffix, domain_keyword или domain_regex) и его значение.</summary>
    public sealed record Parsed(string Field, string Value)
    {
        public bool IsRegex => Field is "domain_regex" or "domain_keyword";

        /// <summary>Одинаковые записи (youtube.com и *.youtube.com) дают один и тот же ключ.</summary>
        public string Key => Field + ":" + Value;
    }

    /// <summary>
    /// Привести ввод к записи: адрес сайта («https://www.youtube.com/watch?v=…») обрезается до
    /// имени домена, лишние пробелы убираются. Regex («regex:…») не трогаем.
    /// </summary>
    public static string Clean(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0 || text.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)) return text;

        // адрес страницы: берём имя сайта, «www.» — лишнее (youtube.com и так включает www.youtube.com)
        if (text.Contains("://") && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            var host = uri.Host.TrimEnd('.');
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && host.Count(c => c == '.') >= 2 ? host[4..] : host;
        }

        // «youtube.com/watch» без схемы — тоже адрес страницы
        var slash = text.IndexOf('/');
        if (slash > 0 && !text.Contains(' ') && text[..slash].Contains('.') &&
            !Text.NormalizeUserPattern(text).kind.Equals(MatchKind.Ip))
            return text[..slash].TrimEnd('.');

        return text.TrimEnd('.');
    }

    /// <summary>
    /// Разделить ввод на записи: через запятую, точку с запятой или пробел можно добавить несколько
    /// сразу. Строка с «regex:» — одна запись целиком (в выражении бывают и запятые, и пробелы).
    /// </summary>
    public static List<string> Split(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return new List<string>();
        if (text.StartsWith("regex:", StringComparison.OrdinalIgnoreCase)) return new List<string> { text };

        return text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Clean)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Разобрать запись. false — <paramref name="error"/> объясняет, что не так.</summary>
    public static bool TryParse(string? input, out Parsed parsed, out string error)
    {
        parsed = new Parsed("", "");
        error = "";
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            error = L.T("S0573");
            return false;
        }

        var (_, kind) = Text.NormalizeUserPattern(text);
        if (kind == MatchKind.Ip)
        {
            error = L.T("S0574");
            return false;
        }

        var (field, value) = SingBoxConfigBuilder.Normalize(new RouteRule { Pattern = text, Kind = kind });
        if (value.Length == 0)
        {
            error = L.T("S0573");
            return false;
        }

        if (field == "domain_regex" && !IsCoreRegex(value, out var why))
        {
            error = why;
            return false;
        }

        parsed = new Parsed(field, value);
        return true;
    }

    /// <summary>
    /// Выражение, которое примет ядро. sing-box написан на Go (RE2): просмотров вперёд/назад и
    /// обратных ссылок там нет — с ними ядро не запустится. Остальное проверяем разбором.
    /// </summary>
    private static bool IsCoreRegex(string value, out string error)
    {
        error = "";
        if (value.Contains("(?=") || value.Contains("(?!") || value.Contains("(?<=") || value.Contains("(?<!"))
        {
            error = L.T("S0575");
            return false;
        }
        if (Regex.IsMatch(value, @"(?<!\\)\\[1-9]"))
        {
            error = L.T("S0576");
            return false;
        }

        try
        {
            _ = new Regex(value);
            return true;
        }
        catch (ArgumentException)
        {
            error = L.T("S0577");
            return false;
        }
    }
}
