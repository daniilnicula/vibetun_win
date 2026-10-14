using System.Text;
using System.Text.RegularExpressions;

namespace VibeTun.Services;

/// <summary>Цвет фрагмента строки журнала: ключевые данные — акцентом, уровень — цветом предупреждения/ошибки.</summary>
public enum LogTone
{
    Plain,
    Muted,
    Key,
    Warn,
    Danger,
}

/// <summary>Кусок строки журнала одного цвета.</summary>
public readonly record struct LogSegment(string Text, LogTone Tone);

/// <summary>
/// Сокращение строк журнала без потери смысла.
/// <list type="bullet">
/// <item>Строка ядра (sing-box) теряет свою метку времени «+0300 2026-10-14 12:00:05» — у панели своя «[12:00:05]»;</item>
/// <item>уровень INFO не пишется (важны только WARN/ERROR и др.), ANSI-цвета ядра убираются;</item>
/// <item>«[1234 1.2s]» становится «#1234» (номер соединения важнее длительности);</item>
/// <item>«inbound/tun[tun-in]:» становится «tun-in:».</item>
/// </list>
/// Строки самого приложения (не из ядра) не меняются.
/// </summary>
public static partial class LogFormat
{
    /// <summary>Сокращает строку для журнала и файла. Пустую строку (остались одни управляющие коды) возвращает пустой.</summary>
    public static string Shorten(string raw)
    {
        var text = AnsiRegex().Replace(raw, string.Empty).TrimEnd();
        var core = CoreLineRegex().Match(text);
        if (!core.Success) return text;

        var sb = new StringBuilder();
        var level = core.Groups["lvl"].Value;
        if (level != "INFO") sb.Append(level).Append(' ');
        if (core.Groups["id"].Success) sb.Append('#').Append(core.Groups["id"].Value).Append(' ');

        var rest = core.Groups["rest"].Value;
        var tag = TagRegex().Match(rest);
        if (tag.Success)
        {
            sb.Append(tag.Groups["tag"].Value).Append(": ");
            rest = rest[tag.Length..];
        }
        return sb.Append(rest).ToString();
    }

    /// <summary>
    /// Ключ для склейки одинаковых строк подряд. Номер соединения в нём не участвует:
    /// «#12 proxy: connection reset» и «#13 proxy: connection reset» — одна и та же строка.
    /// </summary>
    public static string DedupKey(string text) => DedupIdRegex().Replace(text, m => m.Groups["lvl"].Value);

    /// <summary>Разбивает строку журнала на цветные фрагменты: время, уровень, ключевые данные, счётчик повторов.</summary>
    public static List<LogSegment> Segments(DateTime time, string text, int repeat)
    {
        var list = new List<LogSegment> { new($"[{time:HH:mm:ss}] ", LogTone.Muted) };

        var rest = text;
        var level = LevelRegex().Match(rest);
        if (level.Success)
        {
            list.Add(new(level.Value, ToneOf(level.Groups["lvl"].Value)));
            rest = rest[level.Length..];
        }

        var pos = 0;
        foreach (Match key in KeyDataRegex().Matches(rest))
        {
            if (key.Index > pos) list.Add(new(rest[pos..key.Index], LogTone.Plain));
            list.Add(new(key.Value, LogTone.Key));
            pos = key.Index + key.Length;
        }
        if (pos < rest.Length) list.Add(new(rest[pos..], LogTone.Plain));

        if (repeat > 1) list.Add(new($" ×{repeat}", LogTone.Key));
        return list;
    }

    private static LogTone ToneOf(string level) => level switch
    {
        "WARN" => LogTone.Warn,
        "ERROR" or "FATAL" or "PANIC" => LogTone.Danger,
        _ => LogTone.Muted,
    };

    [GeneratedRegex("\\x1b\\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiRegex();

    // «[+0300 2026-10-14 12:00:05 ]INFO [id длительность] остальное»
    [GeneratedRegex(@"^(?:[+-]\d{4} \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} )?(?<lvl>TRACE|DEBUG|INFO|WARN|ERROR|FATAL|PANIC) (?:\[(?<id>\d+) [^\]]*\] )?(?<rest>.*)$")]
    private static partial Regex CoreLineRegex();

    [GeneratedRegex(@"^(?:inbound|outbound|dns|router|service|endpoint|connection)/[^\[\]: ]+\[(?<tag>[^\]]+)\]: ")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^(?<lvl>[A-Z]+ )?#\d+ ")]
    private static partial Regex DedupIdRegex();

    [GeneratedRegex(@"^(?<lvl>WARN|ERROR|FATAL|PANIC|DEBUG|TRACE) ")]
    private static partial Regex LevelRegex();

    // IP[:порт], домен[:порт], число с единицей (мс, с, s, КБ, %), номер соединения «#123»
    [GeneratedRegex(
        @"\b\d{1,3}(?:\.\d{1,3}){3}(?::\d{1,5})?\b" +
        @"|\b(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,24}(?::\d{1,5})?\b" +
        @"|(?<![\w.])\d+(?:[.,]\d+)?\s?(?:мс|ms|КБ|МБ|ГБ|KB|MB|GB|с|s|%)(?!\w)" +
        @"|(?<!\w)\#\d+\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyDataRegex();
}
