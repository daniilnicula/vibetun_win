using System.IO;
using System.IO.Compression;
using System.Reflection;
using VibeTun.Models;
using VibeTun.Utils;

namespace VibeTun.Services;

/// <summary>
/// Ядра sing-box, вшитые в сам исполняемый файл (Brotli-сжатые ресурсы
/// <c>core/sing-box.br</c> и <c>core/sing-box-extended.br</c>).
///
/// При первом запуске распаковываются в %LocalAppData%\VibeTun\core — приложению
/// не нужно ничего скачивать. Обновить ядро поверх встроенного можно на вкладке
/// «Настройки» → «Ядро» (загрузка с GitHub).
/// </summary>
public static class BundledCores
{
    public const string OfficialResource = "core/sing-box.br";
    public const string ExtendedResource = "core/sing-box-extended.br";

    private const string StampFile = ".bundled.stamp";

    public static bool HasOfficial => Has(OfficialResource);
    public static bool HasExtended => Has(ExtendedResource);

    private static bool Has(string resourceName)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            return stream is not null && stream.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Распаковывает встроенные ядра в каталог ядра (если их там ещё нет или они старой встроенной версии).
    /// <paramref name="flavor"/> позволяет развернуть только нужный вариант — файлы весят десятки мегабайт,
    /// поэтому второй ядро распаковывается по мере надобности.
    /// </summary>
    public static async Task<List<string>> EnsureExtractedAsync(CoreFlavor? flavor = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var extracted = new List<string>();
        AppPaths.EnsureCreated();

        var stamp = ReadStamp();

        var jobs = (flavor switch
        {
            CoreFlavor.SingBox => new[] { (OfficialResource, AppPaths.CoreBinary) },
            CoreFlavor.SingBoxExtended => new[] { (ExtendedResource, AppPaths.CoreExtendedBinary) },
            _ => new[] { (OfficialResource, AppPaths.CoreBinary), (ExtendedResource, AppPaths.CoreExtendedBinary) }
        });

        foreach (var (resource, target) in jobs)
        {
            ct.ThrowIfCancellationRequested();

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            if (stream is null || stream.Length == 0) continue;

            var marker = $"{resource}:{stream.Length}";
            if (File.Exists(target) && stamp.TryGetValue(resource, out var known) && known == marker)
            {
                // Файл развёрнут прошлым запуском. Если у него нет записи об источнике (ядро поставила
                // версия приложения, которая её ещё не вела, либо файл с тех пор заменили), один раз
                // сверяем его с вшитой копией — чтобы «Настройки» показывали источник, а не догадку.
                if (CoreSourceStore.Find(target) is null)
                {
                    var same = await SameAsBundledAsync(stream, target, ct);
                    CoreSourceStore.Record(target, new CoreSourceInfo
                    {
                        Kind = same ? CoreSourceInfo.KindBundled : CoreSourceInfo.KindUnknown
                    });
                }
                continue;
            }

            progress?.Report(L.F("S0284", Path.GetFileName(target)));
            var temp = target + ".tmp";
            try
            {
                await using (var output = File.Create(temp))
                await using (var brotli = new BrotliStream(stream, CompressionMode.Decompress))
                    await brotli.CopyToAsync(output, ct);

                File.Move(temp, target, true);
                stamp[resource] = marker;
                extracted.Add(target);
                // вшитая копия легла поверх прежнего файла: теперь источник ядра — само приложение
                CoreSourceStore.Record(target, new CoreSourceInfo { Kind = CoreSourceInfo.KindBundled });
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
                throw;
            }
        }

        if (extracted.Count > 0) WriteStamp(stamp);
        return extracted;
    }

    /// <summary>Совпадает ли файл на диске побайтно с вшитой (Brotli-сжатой) копией ядра.</summary>
    private static async Task<bool> SameAsBundledAsync(Stream resource, string path, CancellationToken ct)
    {
        try
        {
            await using var brotli = new BrotliStream(resource, CompressionMode.Decompress, leaveOpen: true);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

            var expected = new byte[81920];
            var actual = new byte[81920];
            while (true)
            {
                var wanted = await brotli.ReadAtLeastAsync(expected, expected.Length, throwOnEndOfStream: false, ct);
                var got = await file.ReadAtLeastAsync(actual, actual.Length, throwOnEndOfStream: false, ct);
                if (wanted != got) return false;
                if (wanted == 0) return true;
                if (!SameBytes(expected, actual, wanted)) return false;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SameBytes(byte[] left, byte[] right, int count) =>
        left.AsSpan(0, count).SequenceEqual(right.AsSpan(0, count));

    /// <summary>Убирает распакованные копии, чтобы при следующем запуске они развернулись заново.</summary>
    public static void RemoveExtracted()
    {
        foreach (var path in new[] { AppPaths.CoreBinary, AppPaths.CoreExtendedBinary })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }

        try { if (File.Exists(StampPath)) File.Delete(StampPath); } catch { /* ignore */ }
    }

    private static string StampPath => Path.Combine(AppPaths.CoreDir, StampFile);

    private static Dictionary<string, string> ReadStamp()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(StampPath)) return result;
            foreach (var line in File.ReadAllLines(StampPath))
            {
                var index = line.LastIndexOf(':');
                if (index > 0) result[line[..index]] = line;
            }
        }
        catch { /* ignore */ }
        return result;
    }

    private static void WriteStamp(Dictionary<string, string> stamp)
    {
        try { File.WriteAllLines(StampPath, stamp.Values); } catch { /* ignore */ }
    }
}
