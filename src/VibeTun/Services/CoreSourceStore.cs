using System.IO;
using VibeTun.Models;
using VibeTun.Utils;

namespace VibeTun.Services;

/// <summary>Журнал источников ядра: <c>core\core-source.json</c>, ключ — имя файла ядра.</summary>
public static class CoreSourceStore
{
    private const string FileName = "core-source.json";
    private static readonly object Gate = new();

    private static string StorePath => Path.Combine(AppPaths.CoreDir, FileName);

    /// <summary>Запомнить источник только что установленного файла ядра.</summary>
    public static void Record(string corePath, CoreSourceInfo source)
    {
        try
        {
            var file = new FileInfo(corePath);
            if (!file.Exists || !IsInCoreDir(corePath)) return;

            source.InstalledAtUtc = DateTime.UtcNow;
            source.FileSize = file.Length;
            source.FileTimeUtcTicks = file.LastWriteTimeUtc.Ticks;

            lock (Gate)
            {
                var all = ReadAll();
                all[Key(corePath)] = source;
                Json.Write(StorePath, all);
            }
        }
        catch
        {
            /* запись справочная: без неё ядро работает, а источник покажется как «неизвестен» */
        }
    }

    /// <summary>
    /// Источник файла ядра; <c>null</c> — записи нет или файл с тех пор заменён
    /// (другой размер либо время изменения).
    /// </summary>
    public static CoreSourceInfo? Find(string corePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(corePath) || !IsInCoreDir(corePath)) return null;

            var file = new FileInfo(corePath);
            if (!file.Exists) return null;

            CoreSourceInfo? source;
            lock (Gate)
            {
                if (!ReadAll().TryGetValue(Key(corePath), out source)) return null;
            }

            return source.FileSize == file.Length && source.FileTimeUtcTicks == file.LastWriteTimeUtc.Ticks
                ? source
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Строка для «Настроек»: вариант сборки и источник найденного ядра.</summary>
    public static string Describe(CoreInfo info)
    {
        if (!info.Exists) return "";
        return L.F("S1075", L.T(info.IsExtended ? "S1077" : "S1076"), DescribeSource(info));
    }

    private static string DescribeSource(CoreInfo info)
    {
        // файл вне каталога ядра: путь указан кнопкой «Указать файл ядра» либо ядро лежит рядом с приложением
        if (!IsInCoreDir(info.Path)) return L.T("S1084");

        var source = info.Source;
        if (source is null) return L.T("S1085");

        switch (source.Kind)
        {
            case CoreSourceInfo.KindGitHub:
            {
                var text = string.IsNullOrWhiteSpace(source.Tag)
                    ? L.F("S1079", source.Repo ?? "")
                    : L.F("S1078", source.Repo ?? "", source.Tag);
                // основной форк не ответил, и ядро взято из запасного — у них разные возможности AmneziaWG
                return string.IsNullOrWhiteSpace(source.FallbackFor) ? text : text + L.F("S1080", source.FallbackFor);
            }
            case CoreSourceInfo.KindBundled:
                return L.T("S1081");
            case CoreSourceInfo.KindArchive:
                return L.F("S1082", source.Asset ?? "");
            case CoreSourceInfo.KindFile:
                return L.F("S1083", source.Asset ?? "");
            default:
                return L.T("S1085");
        }
    }

    private static bool IsInCoreDir(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            return string.Equals(dir, Path.GetFullPath(AppPaths.CoreDir), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string Key(string corePath) => Path.GetFileName(corePath).ToLowerInvariant();

    private static Dictionary<string, CoreSourceInfo> ReadAll() =>
        Json.Read<Dictionary<string, CoreSourceInfo>>(StorePath) ?? new Dictionary<string, CoreSourceInfo>();
}
