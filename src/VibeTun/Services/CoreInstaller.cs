using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using VibeTun.Models;
using VibeTun.Utils;

namespace VibeTun.Services;

/// <summary>Скачивание ядра sing-box с GitHub (все варианты — из одного репозитория <see cref="ConstantsService.CoreRepo"/>).</summary>
public static class CoreInstaller
{
    public const string UpstreamRepo = ConstantsService.CoreRepo;
    public const string ExtendedRepo = ConstantsService.CoreRepo;

    /// <summary>
    /// Репозиторий ядра: sing-box-lx — единственный источник установки. У него полная поддержка
    /// AmneziaWG 2.0/3.x (random_trailers/disable_cookies), готовые сборки под Windows собираются
    /// с тегом <c>with_awg</c> (Makefile.lx → LX_TAGS), а параметры обфускации лежат плоско
    /// в эндпоинте <c>wireguard</c>, как в .conf. Рядом с sing-box.exe в архиве лежит
    /// libcronet.dll — его переносит CopySiblings.
    /// </summary>
    public const string AwgRepo = ConstantsService.CoreRepo;

    private static HttpClient Client()
    {
        var client = new HttpClient(new HttpClientHandler { Proxy = null, UseProxy = false })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        // сетевой облик — общий для всех запросов приложения (см. ConstantsService)
        client.DefaultRequestHeaders.UserAgent.ParseAdd(NetworkPersonaService.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(ConstantsService.GithubAccept);
        return client;
    }

    // Все варианты ядра ставятся из одного репозитория; слот на диске (официальный/расширенный)
    // по-прежнему выбирается по flavor — см. TargetFor.
    public static string RepoFor(CoreFlavor flavor) => UpstreamRepo;

    /// <summary>Репозиторий, из которого берём архив: явно заданный или стандартный для варианта.</summary>
    private static string RepoName(CoreFlavor flavor, string? repo) =>
        string.IsNullOrWhiteSpace(repo) ? RepoFor(flavor) : repo!;

    public static async Task<string> GetLatestTagAsync(CoreFlavor flavor, CancellationToken ct = default, string? repo = null)
    {
        using var client = Client();
        using var resp = await client.GetAsync(string.Format(ConstantsService.GithubApiLatestReleaseUrl, RepoName(flavor, repo)), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("tag_name").GetString() ?? "";
    }

    /// <summary>Архитектура архива в терминах Go: amd64 / arm64 / 386.</summary>
    public static string GoArch() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "386",
        Architecture.Arm => "armv7",
        _ => "amd64"
    };

    /// <summary>Архив ядра под Windows из последнего релиза репозитория.</summary>
    /// <param name="Tag">Тег релиза — показывается в «Настройках» как источник ядра.</param>
    /// <param name="Sha256">SHA-256 архива из поля <c>digest</c> релиза; у старых релизов его нет.</param>
    public sealed record CoreAsset(string Url, string Name, string Tag, string? Sha256);

    public static async Task<CoreAsset> ResolveWindowsAssetAsync(CoreFlavor flavor,
        CancellationToken ct = default, string? repo = null)
    {
        using var client = Client();
        using var resp = await client.GetAsync(string.Format(ConstantsService.GithubApiLatestReleaseUrl, RepoName(flavor, repo)), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        if (!doc.RootElement.TryGetProperty("assets", out var assets))
            throw new InvalidOperationException(L.T("S0285"));

        var tag = doc.RootElement.TryGetProperty("tag_name", out var tagProp) && tagProp.ValueKind == JsonValueKind.String
            ? tagProp.GetString() ?? ""
            : "";

        var candidates = assets.EnumerateArray()
            .Select(a => new
            {
                Name = a.GetProperty("name").GetString() ?? "",
                Url = a.GetProperty("browser_download_url").GetString() ?? "",
                Digest = a.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String
                    ? digest.GetString()
                    : null
            })
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Where(a => a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException(L.T("S0286"));

        // Основной архив нашей архитектуры, а не его вариант («-legacy-windows-7», «-purego»): в списке
        // релиза варианты стоят раньше, и на Windows 10/11 ставилась сборка для Windows 7.
        var asset = candidates[CoreAssetPicker.Pick(candidates.Select(a => a.Name).ToList(), GoArch())];

        return new CoreAsset(asset.Url, asset.Name, tag, AppUpdateService.ParseSha256Digest(asset.Digest));
    }

    /// <summary>Куда класть ядро: официальная и расширенная сборки живут рядом и не перетирают друг друга.</summary>
    private static string TargetFor(CoreFlavor flavor) =>
        flavor == CoreFlavor.SingBoxExtended ? AppPaths.CoreExtendedBinary : AppPaths.CoreBinary;

    public static Task<CoreInfo?> InstallLatestAsync(CoreService core, CoreFlavor flavor,
        IProgress<string>? progress = null, CancellationToken ct = default, string? repo = null) =>
        InstallReleaseAsync(core, flavor, progress, ct, RepoName(flavor, repo), fallbackFor: null);

    /// <param name="fallbackFor">Основной репозиторий, вместо которого берём этот (он не ответил).</param>
    private static async Task<CoreInfo?> InstallReleaseAsync(CoreService core, CoreFlavor flavor,
        IProgress<string>? progress, CancellationToken ct, string repo, string? fallbackFor)
    {
        progress?.Report(L.T("S0287"));
        var asset = await ResolveWindowsAssetAsync(flavor, ct, repo);
        progress?.Report(L.F("S0288", asset.Name));

        var source = new CoreSourceInfo
        {
            Kind = CoreSourceInfo.KindGitHub,
            Repo = repo,
            Tag = asset.Tag,
            Asset = asset.Name,
            FallbackFor = fallbackFor
        };
        return await InstallFromUrlAsync(core, asset.Url, progress, ct, flavor, source, asset.Sha256);
    }

    /// <summary>
    /// Скачивает ядро с поддержкой AmneziaWG: единственный репозиторий <see cref="AwgRepo"/>,
    /// слот на диске — расширенный (<see cref="CoreFlavor.SingBoxExtended"/>). Репозиторий
    /// записывается вместе с ядром (<see cref="CoreSourceStore"/>) и виден в «Настройках».
    /// </summary>
    public static Task<CoreInfo?> InstallAwgCoreAsync(CoreService core,
        IProgress<string>? progress = null, CancellationToken ct = default) =>
        InstallReleaseAsync(core, CoreFlavor.SingBoxExtended, progress, ct, AwgRepo, fallbackFor: null);

    /// <summary>
    /// Распаковывает ядро, вшитое в приложение (ничего скачивать не нужно).
    /// </summary>
    public static async Task<CoreInfo?> InstallBundledAsync(CoreService core, CoreFlavor flavor,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var available = flavor == CoreFlavor.SingBoxExtended ? BundledCores.HasExtended : BundledCores.HasOfficial;
        if (!available) return null;

        progress?.Report(L.T("S0290"));

        // перезаписываем поверх, чтобы гарантированно восстановить рабочую копию
        var target = TargetFor(flavor);
        try { if (File.Exists(target)) File.Delete(target); } catch { /* ignore */ }

        await BundledCores.EnsureExtractedAsync(flavor, progress, ct);
        return await core.DetectAsync(target, flavor);
    }

    /// <param name="source">Откуда архив (репозиторий и релиз) — запоминается вместе с ядром.</param>
    /// <param name="sha256">Ожидаемый SHA-256 архива; сверяется до распаковки.</param>
    /// <param name="percent">Прогресс скачивания в процентах (0–100).</param>
    public static async Task<CoreInfo?> InstallFromUrlAsync(CoreService core, string url,
        IProgress<string>? progress = null, CancellationToken ct = default, CoreFlavor? flavor = null,
        CoreSourceInfo? source = null, string? sha256 = null, IProgress<double>? percent = null)
    {
        AppPaths.EnsureCreated();
        var tempZip = Path.Combine(Path.GetTempPath(), "vibetun-core-" + Guid.NewGuid().ToString("N")[..6] + ".zip");

        try
        {
            using (var client = Client())
                await DownloadAsync(client, url, tempZip, ct, percent);

            // хэш GitHub считает по самому архиву — сверяем его до распаковки
            AppUpdateService.VerifyAssetSha256(tempZip, sha256);

            progress?.Report(L.T("S0291"));
            return await InstallFromZipAsync(core, tempZip, progress, ct, flavor,
                source ?? new CoreSourceInfo { Kind = CoreSourceInfo.KindArchive, Asset = AssetNameOf(url) });
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Скачивает архив ядра с пределом размера: архив весит десятки мегабайт, и ответ на порядок
    /// больше — не релиз, а подмена или сбой сервера. Качать его до конца (и забивать диск) незачем.
    /// </summary>
    private static async Task DownloadAsync(HttpClient client, string url, string path, CancellationToken ct,
        IProgress<double>? percent = null)
    {
        const long limit = ZipGuard.MaxTotalUncompressedBytes;

        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        if (total > limit)
            throw new InvalidDataException(L.F("S1074", limit / (1024 * 1024)));

        await using var network = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(path);

        var buffer = new byte[81920];
        long received = 0;
        var lastReport = System.Diagnostics.Stopwatch.StartNew();
        percent?.Report(0);
        int read;
        while ((read = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            received += read;
            if (received > limit)
                throw new InvalidDataException(L.F("S1074", limit / (1024 * 1024)));
            await file.WriteAsync(buffer.AsMemory(0, read), ct);

            if (percent is not null && total > 0 && lastReport.ElapsedMilliseconds >= 35)
            {
                lastReport.Restart();
                percent.Report(Math.Clamp(received * 100.0 / total, 0, 100));
            }
        }

        percent?.Report(100);
    }

    private static string AssetNameOf(string url)
    {
        try { return Path.GetFileName(new Uri(url).AbsolutePath); }
        catch { return url; }
    }

    /// <param name="source">Откуда архив; <c>null</c> — файл выбрал пользователь («Установить из архива»).</param>
    public static async Task<CoreInfo?> InstallFromZipAsync(CoreService core, string zipPath,
        IProgress<string>? progress = null, CancellationToken ct = default, CoreFlavor? flavor = null,
        CoreSourceInfo? source = null)
    {
        AppPaths.EnsureCreated();
        var tempDir = Path.Combine(Path.GetTempPath(), "vibetun-core-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tempDir);

        try
        {
            // архив проверяется до распаковки (zip-бомба, выход за пределы папки) — см. ZipGuard
            ZipGuard.ExtractToDirectory(zipPath, tempDir, ct);
            var exe = Directory.EnumerateFiles(tempDir, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileName(f).Contains("sing-box", StringComparison.OrdinalIgnoreCase))
                ?? Directory.EnumerateFiles(tempDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();

            if (exe is null) throw new InvalidOperationException(L.T("S0292"));

            var target = TargetFor(flavor ?? core.Info.Flavor());

            // определяем сборку по имени файла внутри архива / по версии
            if (flavor is null)
            {
                var name = Path.GetFileName(exe);
                var probe = await core.DetectAsync(exe);
                if (probe is { Exists: true, IsExtended: true }) target = AppPaths.CoreExtendedBinary;
                else if (name.Contains("extended", StringComparison.OrdinalIgnoreCase)) target = AppPaths.CoreExtendedBinary;
            }

            try { if (File.Exists(target)) File.Delete(target); } catch { /* ignore */ }
            File.Copy(exe, target, true);

            // Рядом с sing-box.exe в архиве лежит libcronet.dll (сборки «naive»): без него ядро
            // теряет часть функций, поэтому переносим все соседние файлы в каталог ядра.
            CopySiblings(exe, target);

            CoreSourceStore.Record(target,
                source ?? new CoreSourceInfo { Kind = CoreSourceInfo.KindArchive, Asset = Path.GetFileName(zipPath) });

            progress?.Report(L.T("S0293"));
            return await core.DetectAsync(target, flavor);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// «Установить из архива» с выбранным .exe: файл копируется в каталог ядра как официальная
    /// сборка, а его имя запоминается как источник.
    /// </summary>
    public static async Task<CoreInfo?> InstallFromExeAsync(CoreService core, string exePath)
    {
        AppPaths.EnsureCreated();
        File.Copy(exePath, AppPaths.CoreBinary, true);
        CoreSourceStore.Record(AppPaths.CoreBinary,
            new CoreSourceInfo { Kind = CoreSourceInfo.KindFile, Asset = Path.GetFileName(exePath) });
        return await core.DetectAsync(AppPaths.CoreBinary);
    }

    private static void CopySiblings(string sourceExe, string targetExe)
    {
        try
        {
            var sourceDir = Path.GetDirectoryName(sourceExe);
            var targetDir = Path.GetDirectoryName(targetExe);
            if (string.IsNullOrEmpty(sourceDir) || string.IsNullOrEmpty(targetDir)) return;

            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                if (string.Equals(file, sourceExe, StringComparison.OrdinalIgnoreCase)) continue;
                if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true); }
                catch { /* не критично */ }
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>Текст для инструкции по ручной установке.</summary>
    public static string ManualHint =>
        L.F("S0294", UpstreamRepo, AppPaths.CoreDir) +
        L.F("S0295", AwgRepo) +
        L.T("S0296");
}
