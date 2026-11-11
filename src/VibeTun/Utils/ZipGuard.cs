using System.IO;
using System.IO.Compression;

namespace VibeTun.Utils;

/// <summary>
/// Архив отклонён проверкой <see cref="ZipGuard"/>; в сообщении — причина для пользователя.
/// Наследник <see cref="IOException"/>: <see cref="InvalidDataException"/> запечатан, а для вызывающего
/// кода отклонённый архив — такая же ошибка чтения файла, как и любая другая.
/// </summary>
public sealed class ZipRejectedException : IOException
{
    public ZipRejectedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Проверка zip-архива перед распаковкой и сама распаковка с учётом записанных байт.
/// Архивы приходят из сети (обновление приложения, ядро sing-box) или от пользователя
/// («Установить из архива»), поэтому заголовкам не верим: «zip-бомба» раскладывает гигабайты
/// из пары мегабайт, а запись с «..» или абсолютным путём пишет файл мимо папки назначения.
/// </summary>
public static class ZipGuard
{
    /// <summary>Записей в архиве релиза — единицы (exe, dll, лицензия); 2000 — с большим запасом.</summary>
    public const int MaxEntries = 2000;

    /// <summary>Суммарный распакованный размер: VibeTun.exe ~70 МБ, ядро с libcronet ~60 МБ.</summary>
    public const long MaxTotalUncompressedBytes = 500L * 1024 * 1024;

    /// <summary>
    /// 1 байт сжатого — не больше 100 байт распакованного. Исполняемые файлы сжимаются в 2–4 раза,
    /// а у deflate предел около 1032:1 — именно так упакованы бомбы.
    /// </summary>
    public const double MaxCompressionRatio = 100.0;

    /// <summary>
    /// Коэффициент проверяем только у записей крупнее этого размера: короткий текстовый файл из
    /// повторов честно сжимается сильнее 100:1 и ничем не грозит — его объём уже учтён в общем лимите.
    /// </summary>
    private const long RatioFloorBytes = 1024 * 1024;

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Что и куда распаковывать: путь уже проверен и лежит внутри папки назначения.</summary>
    private readonly record struct PlannedEntry(ZipArchiveEntry Entry, string TargetPath, bool IsDirectory);

    /// <summary>
    /// Проверить архив, ничего не распаковывая. Бросает <see cref="ZipRejectedException"/> с понятной
    /// причиной: не zip, повреждён, слишком много записей, слишком большой, бомба или выход из папки.
    /// </summary>
    public static void Validate(string zipPath)
    {
        using var stream = OpenArchiveFile(zipPath);
        using var archive = OpenArchive(stream);
        Plan(archive, Path.Combine(Path.GetTempPath(), "vibetun-zip-check"));
    }

    /// <summary>
    /// Проверить архив и распаковать его в <paramref name="destinationDir"/>. Распаковка считает
    /// реально записанные байты (запись не может дать больше заявленного в заголовке, весь архив —
    /// больше <see cref="MaxTotalUncompressedBytes"/>) и сверяет CRC-32 каждого файла.
    /// </summary>
    public static void ExtractToDirectory(string zipPath, string destinationDir, CancellationToken ct = default)
    {
        using var stream = OpenArchiveFile(zipPath);
        using var archive = OpenArchive(stream);
        var plan = Plan(archive, destinationDir);

        Directory.CreateDirectory(destinationDir);

        long total = 0;
        var buffer = new byte[81920];
        foreach (var item in plan)
        {
            ct.ThrowIfCancellationRequested();

            if (item.IsDirectory)
            {
                Directory.CreateDirectory(item.TargetPath);
                continue;
            }

            var parent = Path.GetDirectoryName(item.TargetPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            try
            {
                using var source = item.Entry.Open();
                using var target = new FileStream(item.TargetPath, FileMode.Create, FileAccess.Write, FileShare.None);

                long written = 0;
                var crc = 0xFFFFFFFFu;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    total += read;
                    if (written > item.Entry.Length) throw Rejected(L.F("S1072", DisplayName(item.Entry)));
                    if (total > MaxTotalUncompressedBytes) throw Rejected(TooLarge());

                    crc = UpdateCrc(crc, buffer.AsSpan(0, read));
                    target.Write(buffer, 0, read);

                    ct.ThrowIfCancellationRequested();
                }

                // данные короче заявленного или не сходится контрольная сумма — файл испорчен
                if (written != item.Entry.Length || (crc ^ 0xFFFFFFFFu) != item.Entry.Crc32)
                    throw Rejected(L.T("S1069"));
            }
            catch (InvalidDataException ex)
            {
                // сжатые данные записи не разбираются (обрыв загрузки, неизвестный метод сжатия)
                throw Rejected(L.T("S1069"), ex);
            }
        }
    }

    private static FileStream OpenArchiveFile(string zipPath)
    {
        var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            // Архив с файлами начинается с локального заголовка «PK\x03\x04». Пустой архив,
            // HTML-страница ошибки вместо файла и оборванная в самом начале загрузка сюда не проходят.
            var signature = new byte[4];
            var got = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
            if (got != 4 || signature[0] != (byte)'P' || signature[1] != (byte)'K' || signature[2] != 3 || signature[3] != 4)
                throw Rejected(L.T("S1068"));

            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static ZipArchive OpenArchive(Stream stream)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw Rejected(L.T("S1069"), ex);
        }
    }

    /// <summary>Проверяет оглавление архива целиком и возвращает записи с готовыми путями назначения.</summary>
    private static List<PlannedEntry> Plan(ZipArchive archive, string destinationDir)
    {
        IReadOnlyCollection<ZipArchiveEntry> entries;
        try
        {
            entries = archive.Entries;   // здесь читается центральный каталог
        }
        catch (InvalidDataException ex)
        {
            throw Rejected(L.T("S1069"), ex);
        }

        if (entries.Count > MaxEntries) throw Rejected(L.F("S1070", entries.Count, MaxEntries));

        var root = Path.GetFullPath(destinationDir);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        var plan = new List<PlannedEntry>(entries.Count);
        long total = 0;
        foreach (var entry in entries)
        {
            // запись-папка: имя кончается разделителем, данных у неё быть не должно
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var target = ResolveTarget(rootPrefix, entry, isDirectory);

            if (isDirectory)
            {
                if (entry.Length != 0) throw Rejected(L.T("S1069"));
                plan.Add(new PlannedEntry(entry, target, true));
                continue;
            }

            if (entry.Length < 0 || entry.CompressedLength < 0) throw Rejected(L.T("S1069"));

            total += entry.Length;
            if (total > MaxTotalUncompressedBytes) throw Rejected(TooLarge());

            if (entry.Length > RatioFloorBytes &&
                (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > MaxCompressionRatio))
                throw Rejected(L.F("S1072", DisplayName(entry)));

            plan.Add(new PlannedEntry(entry, target, false));
        }

        return plan;
    }

    /// <summary>
    /// Zip-slip: имя записи — относительный путь внутри архива. Абсолютные пути, буквы дисков,
    /// «..», потоки NTFS («файл:поток») и управляющие символы отклоняются до обращения к диску,
    /// а итоговый путь обязан остаться внутри папки назначения.
    /// </summary>
    private static string ResolveTarget(string rootPrefix, ZipArchiveEntry entry, bool isDirectory)
    {
        var name = entry.FullName.Replace('\\', '/');
        if (name.Length == 0 || name[0] == '/') throw Outside(entry);

        foreach (var segment in name.Split('/'))
        {
            if (segment == ".." || segment.IndexOfAny(InvalidNameChars) >= 0) throw Outside(entry);
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(rootPrefix, name));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Outside(entry, ex);
        }

        // файл — строго внутри папки назначения; сама папка («./») допустима только как запись-папка
        var inside = full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && full.Length > rootPrefix.Length;
        var isRoot = string.Equals(full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, rootPrefix,
            StringComparison.OrdinalIgnoreCase);
        if (!inside && !(isDirectory && isRoot)) throw Outside(entry);

        return full;
    }

    /// <summary>Имя записи для сообщения: без управляющих символов и не длиннее 80 знаков.</summary>
    private static string DisplayName(ZipArchiveEntry entry)
    {
        var name = new string(entry.FullName.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return name.Length > 80 ? name[..77] + "…" : name;
    }

    private static string TooLarge() => L.F("S1071", MaxTotalUncompressedBytes / (1024 * 1024));

    private static ZipRejectedException Outside(ZipArchiveEntry entry, Exception? inner = null) =>
        Rejected(L.F("S1073", DisplayName(entry)), inner);

    private static ZipRejectedException Rejected(string reason, Exception? inner = null) =>
        new(L.F("S1067", reason), inner);

    // CRC-32 (IEEE 802.3), как в формате zip: System.IO.Compression при чтении её не сверяет
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
