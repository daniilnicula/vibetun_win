using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VibeTun.Utils;

namespace VibeTun.Services;

/// <summary>Сведения об одном релизе VibeTun для Windows с GitHub.</summary>
public sealed class AppReleaseInfo
{
    public long Id { get; init; }
    public string TagName { get; init; } = "";
    public string Name { get; init; } = "";
    public string TargetCommitish { get; init; } = "";
    public DateTime PublishedAt { get; init; }
    public string Body { get; init; } = "";
    public string AssetName { get; init; } = ConstantsService.PreferredAssetName;
    public string DownloadUrl { get; init; } = "";
    public long AssetSize { get; init; }
    public string? AssetSha256 { get; init; }
    public bool IsPrerelease { get; init; }

    public string Title => !string.IsNullOrWhiteSpace(Name) ? Name.Trim() : TagName.Trim();

    public string FormattedBody => AppUpdateService.FormatReleaseNotes(Body);
}

/// <summary>
/// Получение списка Windows-релизов с GitHub (daniilnicula/vibetun_win), определение текущей версии,
/// скачивание выбранного релиза с прогрессом и замена исполняемого файла с перезапуском.
/// </summary>
public static class AppUpdateService
{
    public const string Repo = ConstantsService.AppRepo;

    // URL ленты релизов параметризованы репозиторием: вкладки «Обновление» грузят и приложение,
    // и ядро (Leadaxe/sing-box-lx) из одного кода.
    private static string ApiReleasesUrl(string repo, int page, int perPage) =>
        $"https://api.github.com/repos/{repo}/releases?per_page={perPage}&page={page}";

    private static string WebReleasesUrl(string repo) => $"https://github.com/{repo}/releases";

    private static string AtomReleasesUrl(string repo) => $"https://github.com/{repo}/releases.atom";

    /// <summary>
    /// Предел размера скачиваемого релиза. VibeTun.exe весит около 70 МБ; файл на порядок больше —
    /// не релиз, а подмена или ошибка сервера, и качать его до конца незачем.
    /// </summary>
    private const long MaxDownloadBytes = ZipGuard.MaxTotalUncompressedBytes;

    private static string? _cachedExeSha256;

    private static HttpClient CreateClient(bool useSystemProxy, TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true,
            UseProxy = useSystemProxy
        };
        if (!useSystemProxy) handler.Proxy = null;

        var client = new HttpClient(handler) { Timeout = timeout };
        // сетевой облик — общий для всех запросов приложения (см. ConstantsService)
        client.DefaultRequestHeaders.UserAgent.ParseAdd(NetworkPersonaService.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(ConstantsService.GithubAccept);
        return client;
    }

    /// <summary>
    /// Страница релизов репозитория (от нового к старым) с пагинацией GitHub:
    /// первая страница — <paramref name="limit"/> 20, догрузки — по 10 (offset кратен limit).
    /// </summary>
    /// <param name="repo">Репозиторий «владелец/имя»: приложение или ядро.</param>
    /// <param name="offset">Сколько релизов уже загружено (0 — первая страница).</param>
    /// <param name="limit">Сколько релизов запросить (первая загрузка — 20, догрузка — 10).</param>
    /// <param name="forCore">Ядро: показывать только релизы с Windows-.zip-архивом.</param>
    public static async Task<List<AppReleaseInfo>> FetchReleasesPageAsync(
        string repo, int offset, int limit, bool forCore = false, CancellationToken ct = default)
    {
        if (limit <= 0) limit = 20;
        var page = offset / limit + 1;
        var url = ApiReleasesUrl(repo, page, limit);
        Exception? lastError = null;

        // Пробуем через текущие системные настройки прокси и напрямую (если прокси недоступен или наоборот нужен обход)
        foreach (var useProxy in new[] { true, false })
        {
            try
            {
                using var client = CreateClient(useProxy, TimeSpan.FromSeconds(20));
                using var resp = await client.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    var list = ParseApiReleases(json, repo, forCore);
                    if (list.Count > 0) return list;
                }
                else if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    // Лимит GitHub API для IP (актуально на общем VPN): резерв из веб-страницы + Atom-ленты
                    // работает только для первой страницы — пагинации в них нет.
                    if (page == 1)
                    {
                        var fallback = await FetchFallbackReleasesAsync(client, repo, forCore, ct);
                        if (fallback.Count > 0) return fallback;
                    }
                    return new List<AppReleaseInfo>();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null) throw lastError;
        return new List<AppReleaseInfo>();
    }

    private static List<AppReleaseInfo> ParseApiReleases(string json, string repo, bool forCore)
    {
        var result = new List<AppReleaseInfo>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
                continue;

            var tag = item.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(tag)) continue;

            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var commitish = item.TryGetProperty("target_commitish", out var c) ? c.GetString() ?? "" : "";
            var body = item.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var prerelease = item.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True;
            var id = item.TryGetProperty("id", out var idProp) && idProp.TryGetInt64(out var idVal) ? idVal : 0L;

            var publishedAt = DateTime.UtcNow;
            if (item.TryGetProperty("published_at", out var pubProp) &&
                pubProp.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(pubProp.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsedPub))
            {
                publishedAt = parsedPub;
            }
            else if (item.TryGetProperty("created_at", out var crProp) &&
                     crProp.ValueKind == JsonValueKind.String &&
                     DateTime.TryParse(crProp.GetString(), CultureInfo.InvariantCulture,
                         DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsedCr))
            {
                publishedAt = parsedCr;
            }

            // Ищем ассет под Windows (.exe или .zip, исключая Android .apk)
            string? assetName = null;
            string? downloadUrl = null;
            long assetSize = 0;
            string? assetSha256 = null;
            var hasOnlyAndroidAssets = false;
            var totalAssets = 0;
            var apkAssets = 0;

            if (item.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                if (forCore)
                {
                    // Ядро: только Windows-.zip (sing-box-…-windows-amd64.zip и т.п.),
                    // архитектуру выбирает CoreAssetPicker — как при установке ядра из файлов
                    var coreZips = new List<(string Name, string Url, long Size, string? Digest)>();
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        var aName = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                        if (!aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                            !aName.Contains("windows", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var aUrl = asset.TryGetProperty("browser_download_url", out var au) ? au.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(aUrl)) continue;

                        coreZips.Add((aName, aUrl,
                            asset.TryGetProperty("size", out var asz) && asz.TryGetInt64(out var sz) ? sz : 0L,
                            asset.TryGetProperty("digest", out var ad) && ad.ValueKind == JsonValueKind.String
                                ? ad.GetString()
                                : null));
                    }

                    if (coreZips.Count > 0)
                    {
                        var picked = coreZips[CoreAssetPicker.Pick(
                            coreZips.Select(z => z.Name).ToList(), CoreInstaller.GoArch())];
                        assetName = picked.Name;
                        downloadUrl = picked.Url;
                        assetSize = picked.Size;
                        assetSha256 = ParseSha256Digest(picked.Digest);
                    }
                }
                else
                {
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        totalAssets++;
                        var aName = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                        var aUrl = asset.TryGetProperty("browser_download_url", out var au) ? au.GetString() ?? "" : "";
                        var aSize = asset.TryGetProperty("size", out var asz) && asz.TryGetInt64(out var sz) ? sz : 0L;
                        var aDigest = asset.TryGetProperty("digest", out var ad) && ad.ValueKind == JsonValueKind.String
                            ? ad.GetString()
                            : null;

                        if (aName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                        {
                            apkAssets++;
                            continue;
                        }

                        var isExe = aName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                        var isWinZip = aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                       !ConstantsService.NonWindowsAssetKeywords.Any(k =>
                                           aName.Contains(k, StringComparison.OrdinalIgnoreCase));

                        if (!isExe && !isWinZip) continue;

                        // Предпочитаем VibeTun.exe или любой .exe перед .zip
                        var betterMatch = downloadUrl is null ||
                                          (isExe && !assetName!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ||
                                          aName.Equals(ConstantsService.PreferredAssetName, StringComparison.OrdinalIgnoreCase);
                        if (betterMatch && !string.IsNullOrWhiteSpace(aUrl))
                        {
                            assetName = aName;
                            downloadUrl = aUrl;
                            assetSize = aSize;
                            assetSha256 = ParseSha256Digest(aDigest);
                        }
                    }

                    hasOnlyAndroidAssets = totalAssets > 0 && apkAssets == totalAssets;
                }
            }

            if (forCore)
            {
                // Ядро без Windows-архива не показываем (это не core-релиз)
                if (downloadUrl is null) continue;
            }
            else
            {
                var isAndroidBranch = commitish.Equals("Android", StringComparison.OrdinalIgnoreCase);
                var isWindowsBranch = commitish.Equals("Windows", StringComparison.OrdinalIgnoreCase);

                // Релиз считается Windows-релизом, если в нём есть Windows-ассет (.exe/.zip)
                // или он выпущен из ветки Windows (и не является чисто Android-релизом)
                if (downloadUrl is null)
                {
                    if (isAndroidBranch || hasOnlyAndroidAssets) continue;
                    if (!isWindowsBranch && totalAssets > 0) continue;

                    assetName = ConstantsService.PreferredAssetName;
                    downloadUrl = $"https://github.com/{repo}/releases/download/{Uri.EscapeDataString(tag)}/{ConstantsService.PreferredAssetName}";
                }
            }

            result.Add(new AppReleaseInfo
            {
                Id = id,
                TagName = tag,
                Name = string.IsNullOrWhiteSpace(name) ? tag : name,
                TargetCommitish = commitish,
                PublishedAt = publishedAt,
                Body = body,
                AssetName = assetName ?? ConstantsService.PreferredAssetName,
                DownloadUrl = downloadUrl,
                AssetSize = assetSize,
                AssetSha256 = assetSha256,
                IsPrerelease = prerelease
            });
        }

        return result
            .OrderByDescending(r => r.PublishedAt)
            .ToList();
    }

    /// <summary>Резервный парсинг через страницу релизов и Atom-ленту, если GitHub API вернул лимит 403 (первая страница).</summary>
    private static async Task<List<AppReleaseInfo>> FetchFallbackReleasesAsync(
        HttpClient client, string repo, bool forCore, CancellationToken ct)
    {
        var html = await client.GetStringAsync(WebReleasesUrl(repo), ct);
        var releaseTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tagPattern = "/" + Regex.Escape(repo) + "/releases/tag/([^\"'<>?#\\s]+)";
        foreach (Match m in Regex.Matches(html, tagPattern))
        {
            var tag = Uri.UnescapeDataString(m.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(tag)) releaseTags.Add(tag);
        }

        var atomXml = await client.GetStringAsync(AtomReleasesUrl(repo), ct);
        var xdoc = XDocument.Parse(atomXml);
        XNamespace ns = "http://www.w3.org/2005/Atom";

        var list = new List<AppReleaseInfo>();
        foreach (var entry in xdoc.Descendants(ns + "entry"))
        {
            var link = entry.Element(ns + "link")?.Attribute("href")?.Value ?? "";
            var tagIdx = link.IndexOf("/releases/tag/", StringComparison.OrdinalIgnoreCase);
            if (tagIdx < 0) continue;

            var tag = Uri.UnescapeDataString(link[(tagIdx + "/releases/tag/".Length)..].Trim());
            if (releaseTags.Count > 0 && !releaseTags.Contains(tag)) continue;

            var title = entry.Element(ns + "title")?.Value ?? tag;
            var updatedStr = entry.Element(ns + "updated")?.Value ?? "";
            if (!DateTime.TryParse(updatedStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var publishedAt))
            {
                publishedAt = DateTime.UtcNow;
            }

            var contentHtml = entry.Element(ns + "content")?.Value ?? "";
            var plainBody = HtmlToPlainText(contentHtml);

            // Ассет для резервного списка: приложение — VibeTun.exe, ядро — стандартный zip по архитектуре
            var assetName = forCore
                ? $"sing-box-{(tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag)}-windows-{CoreInstaller.GoArch()}.zip"
                : "VibeTun.exe";

            list.Add(new AppReleaseInfo
            {
                TagName = tag,
                Name = title,
                TargetCommitish = "Windows",
                PublishedAt = publishedAt,
                Body = plainBody,
                AssetName = assetName,
                DownloadUrl = $"https://github.com/{repo}/releases/download/{Uri.EscapeDataString(tag)}/{assetName}"
            });
        }

        return list.OrderByDescending(r => r.PublishedAt).ToList();
    }

    private static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = WebUtility.HtmlDecode(html);
        text = Regex.Replace(text, @"<\s*li[^>]*>", "• ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*(br|/p|/div|/h[1-6]|/li)[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        return text.Trim();
    }

    internal static string? ParseSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        var s = digest.Trim();
        if (s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            s = s["sha256:".Length..].Trim();
        return s.Length == 64 ? s.ToLowerInvariant() : null;
    }

    /// <summary>SHA-256 текущего запущенного исполняемого файла VibeTun.exe.</summary>
    public static string? GetCurrentExeSha256()
    {
        if (_cachedExeSha256 is not null) return _cachedExeSha256;
        try
        {
            var exe = AppPaths.ExecutablePath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;
            using var stream = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = SHA256.HashData(stream);
            _cachedExeSha256 = Convert.ToHexString(hash).ToLowerInvariant();
            return _cachedExeSha256;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Нормализовать версию/тег: первая строка, обрезка по пробелу и «(», срез ведущей «v».
    /// Пример: «1.14.4-lx.33 (go1.24.1, windows/amd64…)» → «1.14.4-lx.33».
    /// </summary>
    public static string NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var s = value.Trim();
        var nl = s.IndexOf('\n');
        if (nl >= 0) s = s[..nl];
        var cut = s.IndexOfAny(new[] { ' ', '(' });
        if (cut >= 0) s = s[..cut];
        s = s.Trim();
        if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V') && char.IsDigit(s[1]))
            s = s[1..];
        return s;
    }

    private readonly record struct VersionToken(long? Num, string Text);

    private static List<VersionToken> TokenizeVersion(string normalized)
    {
        var tokens = new List<VersionToken>();
        var i = 0;
        while (i < normalized.Length)
        {
            var c = normalized[i];
            if (char.IsDigit(c))
            {
                var j = i;
                while (j < normalized.Length && char.IsDigit(normalized[j])) j++;
                if (long.TryParse(normalized[i..j], out var num))
                    tokens.Add(new VersionToken(num, normalized[i..j]));
                i = j;
            }
            else if (char.IsLetter(c))
            {
                var j = i;
                while (j < normalized.Length && char.IsLetter(normalized[j])) j++;
                tokens.Add(new VersionToken(null, normalized[i..j].ToLowerInvariant()));
                i = j;
            }
            else
            {
                i++; // разделители . - _ и прочее не участвуют в сравнении
            }
        }
        return tokens;
    }

    /// <summary>
    /// Сравнение версий: >0 — a новее b, &lt;0 — a старее b, 0 — равны.
    /// Нормализация (см. <see cref="NormalizeVersion"/>), попозиционное сравнение токенов:
    /// числа — численно, «число &lt; буквы», при общем префиксе хвоста:
    /// rc/alpha/beta/pre/dev — версия с таким хвостом старше (короче новее), иначе длиннее новее.
    /// </summary>
    public static int CompareVersions(string? a, string? b)
    {
        var ta = TokenizeVersion(NormalizeVersion(a));
        var tb = TokenizeVersion(NormalizeVersion(b));
        var common = Math.Min(ta.Count, tb.Count);

        for (var i = 0; i < common; i++)
        {
            var (x, y) = (ta[i], tb[i]);
            int c;
            if (x.Num is not null && y.Num is not null)
                c = x.Num.Value.CompareTo(y.Num.Value);
            else if (x.Num is not null)
                c = -1; // число < буквы
            else if (y.Num is not null)
                c = 1;
            else
                c = string.CompareOrdinal(x.Text, y.Text);
            if (c != 0) return c;
        }

        if (ta.Count == tb.Count) return 0;

        // Общий префикс исчерпан: сравниваем хвосты
        var aLonger = ta.Count > tb.Count;
        var firstTail = (aLonger ? ta : tb)[common];
        var isPrereleaseTail = firstTail.Num is null &&
                               firstTail.Text is "rc" or "alpha" or "beta" or "pre" or "dev";
        if (isPrereleaseTail)
            return aLonger ? -1 : 1; // пререлиз старше релиза
        return aLonger ? 1 : -1;     // иначе более длинная версия новее
    }

    /// <summary>
    /// Тег установленной версии приложения: SHA-256 текущего exe среди загруженных релизов
    /// (с побочным эффектом — запоминает тег), иначе сохранённый тег при согласованном хэше,
    /// иначе метка релиза сборки <see cref="AppInfo.ReleaseTag"/>. Без фолбэка на «последний релиз».
    /// </summary>
    public static string ResolveInstalledTag(IReadOnlyList<AppReleaseInfo> loaded)
    {
        var currentSha = GetCurrentExeSha256();
        var settings = App.Settings.Current;

        if (!string.IsNullOrWhiteSpace(currentSha))
        {
            var byHash = loaded.FirstOrDefault(r =>
                !string.IsNullOrWhiteSpace(r.AssetSha256) &&
                string.Equals(r.AssetSha256, currentSha, StringComparison.OrdinalIgnoreCase));
            if (byHash is not null)
            {
                if (!string.Equals(settings.InstalledReleaseTag, byHash.TagName, StringComparison.Ordinal) ||
                    !string.Equals(settings.InstalledExeSha256, currentSha, StringComparison.OrdinalIgnoreCase))
                {
                    settings.InstalledReleaseTag = byHash.TagName;
                    settings.InstalledExeSha256 = currentSha;
                    App.Settings.SaveSoon();
                }
                return byHash.TagName;
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.InstalledReleaseTag))
        {
            var hashMatchesOrUnknown = string.IsNullOrWhiteSpace(settings.InstalledExeSha256) ||
                                       string.IsNullOrWhiteSpace(currentSha) ||
                                       string.Equals(settings.InstalledExeSha256, currentSha, StringComparison.OrdinalIgnoreCase);
            if (hashMatchesOrUnknown)
                return settings.InstalledReleaseTag!;
        }

        return AppInfo.ReleaseTag;
    }

    /// <summary>Аккуратное форматирование Markdown-описания релиза с GitHub для чтения в карточке.</summary>
    public static string FormatReleaseNotes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return L.T("S0276");

        var normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var lines = normalized.Split('\n');
        var sb = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("### ", StringComparison.Ordinal))
                line = trimmed[4..].Trim();
            else if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                line = trimmed[3..].Trim();
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                line = trimmed[2..].Trim();
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                line = "• " + trimmed[2..].Trim();

            sb.AppendLine(line);
        }

        var result = sb.ToString().Trim();
        return result.Length == 0 ? L.T("S0276") : result;
    }

    /// <summary>
    /// Скачать релиз во временный файл с отчётом прогресса и проверкой целостности.
    /// Возвращает путь к готовому .exe и его SHA-256.
    /// </summary>
    public static async Task<(string ExePath, string Sha256)> DownloadReleaseAsync(
        AppReleaseInfo release,
        Action<double, long, long>? onProgress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(release.DownloadUrl))
            throw new InvalidOperationException(L.T("S0277"));

        var workDir = GetStagingDirectory();
        Directory.CreateDirectory(workDir);

        var id = Guid.NewGuid().ToString("N")[..8];
        var isZip = release.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var downloadPath = Path.Combine(workDir, isZip ? $"VibeTun-update-{id}.zip" : $"VibeTun-update-{id}.exe");

        try
        {
            Exception? lastError = null;
            var downloaded = false;

            foreach (var useProxy in new[] { true, false })
            {
                try
                {
                    using var client = CreateClient(useProxy, TimeSpan.FromMinutes(15));
                    using var resp = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();

                    var total = resp.Content.Headers.ContentLength ?? (release.AssetSize > 0 ? release.AssetSize : -1L);
                    await using (var netStream = await resp.Content.ReadAsStreamAsync(ct))
                    await using (var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                    {
                        var buffer = new byte[65536];
                        long received = 0;
                        var lastReport = Stopwatch.StartNew();

                        onProgress?.Invoke(0, 0, total);

                        int read;
                        while ((read = await netStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                        {
                            received += read;
                            // сервер не должен отдать больше разумного для релиза: иначе «обновление» забьёт диск
                            if (received > MaxDownloadBytes)
                                throw new InvalidDataException(L.F("S1074", MaxDownloadBytes / (1024 * 1024)));

                            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);

                            if (lastReport.ElapsedMilliseconds >= 35 || (total > 0 && received >= total))
                            {
                                lastReport.Restart();
                                var percent = total > 0
                                    ? Math.Clamp(received * 100.0 / total, 0, 100)
                                    : 50.0;
                                onProgress?.Invoke(percent, received, total);
                            }
                        }

                        await fileStream.FlushAsync(ct);
                        onProgress?.Invoke(100, received, total > 0 ? total : received);
                    }

                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (InvalidDataException)
                {
                    // файл отклонён по размеру: повтор напрямую отдаст то же самое
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    try { if (File.Exists(downloadPath)) File.Delete(downloadPath); } catch { /* ignore */ }
                }
            }

            if (!downloaded)
                throw lastError ?? new IOException(L.T("S0278"));

            // Хэш ассета GitHub считает по самому скачиваемому файлу (для архива — по .zip),
            // поэтому сверяем его сразу, до распаковки и до любых обращений к содержимому.
            VerifyAssetSha256(downloadPath, release.AssetSha256);

            string finalExePath;
            if (isZip)
            {
                var extractDir = Path.Combine(workDir, $"VibeTun-extract-{id}");
                Directory.CreateDirectory(extractDir);
                try
                {
                    // архив проверяется до распаковки: сигнатура, число записей, суммарный размер,
                    // степень сжатия и пути записей (zip-бомба, zip-slip) — см. ZipGuard
                    ZipGuard.ExtractToDirectory(downloadPath, extractDir, ct);
                    var foundExe = Directory.EnumerateFiles(extractDir, "*.exe", SearchOption.AllDirectories)
                        .FirstOrDefault(f => Path.GetFileName(f).Equals("VibeTun.exe", StringComparison.OrdinalIgnoreCase))
                        ?? Directory.EnumerateFiles(extractDir, "*.exe", SearchOption.AllDirectories)
                            .FirstOrDefault(f => !Path.GetFileName(f).Contains("sing-box", StringComparison.OrdinalIgnoreCase));

                    if (foundExe is null)
                        throw new InvalidOperationException(L.T("S0279"));

                    finalExePath = Path.Combine(workDir, $"VibeTun-update-{id}.exe");
                    File.Move(foundExe, finalExePath, overwrite: true);
                }
                finally
                {
                    try { File.Delete(downloadPath); } catch { /* ignore */ }
                    try { Directory.Delete(extractDir, recursive: true); } catch { /* ignore */ }
                }
            }
            else
            {
                finalExePath = downloadPath;
            }

            // Проверяем, что получился исполняемый PE-файл Windows ("MZ"). Хэш ассета уже сверен выше;
            // здесь считается SHA-256 самого .exe — он сохраняется как отпечаток установленной версии.
            ValidateExecutable(finalExePath, out var sha256);
            return (finalExePath, sha256);
        }
        catch
        {
            try { if (File.Exists(downloadPath)) File.Delete(downloadPath); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>
    /// Сверяет SHA-256 скачанного ассета с тем, что прислал GitHub (поле <c>digest</c> релиза).
    /// Хэша может не быть: старые релизы и резервная лента его не содержат — тогда проверка пропускается.
    /// </summary>
    internal static void VerifyAssetSha256(string path, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexString(SHA256.HashData(fs));
        if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("S0282"));
    }

    private static void ValidateExecutable(string exePath, out string actualSha256)
    {
        var info = new FileInfo(exePath);
        if (!info.Exists || info.Length < 1024 * 100)
            throw new InvalidDataException(L.T("S0280"));

        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var b0 = fs.ReadByte();
        var b1 = fs.ReadByte();
        if (b0 != 'M' || b1 != 'Z')
            throw new InvalidDataException(L.T("S0281"));

        fs.Position = 0;
        var hash = SHA256.HashData(fs);
        actualSha256 = Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Заменить текущий исполняемый файл скачанным релизом и перезапустить приложение,
    /// сохранив все пользовательские настройки, профили и данные.
    /// </summary>
    public static void ApplyUpdateAndRestart(
        string downloadedExePath,
        AppReleaseInfo targetRelease,
        string downloadedSha256,
        Action requestRestart,
        Action requestExit)
    {
        var currentExe = AppPaths.ExecutablePath;
        if (string.IsNullOrWhiteSpace(currentExe))
            throw new InvalidOperationException(L.T("S0283"));

        // Сохраняем сведения об установленном релизе и текущие настройки приложения в текущей папке данных,
        // чтобы после перезапуска все настройки, профили, маршруты, панели и SSH остались на месте
        var settings = App.Settings.Current;
        settings.InstalledReleaseTag = targetRelease.TagName;
        settings.InstalledExeSha256 = downloadedSha256;
        settings.LastUpdateCheck = DateTime.UtcNow;
        App.Settings.SaveNow();

        var backupExe = currentExe + ".old";

        // Способ 1: атомарное переименование запущенного .exe (поддерживается NTFS в Windows)
        // и подстановка нового .exe на то же место до выхода из процесса
        try
        {
            if (File.Exists(backupExe))
            {
                try { File.Delete(backupExe); } catch { /* может быть занят прошлым процессом */ }
            }

            File.Move(currentExe, backupExe, overwrite: true);
            try
            {
                File.Move(downloadedExePath, currentExe, overwrite: true);
            }
            catch
            {
                // Если не удалось положить новый файл — обязательно возвращаем старый на место
                try { File.Move(backupExe, currentExe, overwrite: true); } catch { /* ignore */ }
                throw;
            }

            _cachedExeSha256 = downloadedSha256;

            // Запускаем тихую фоновую очистку .old после завершения текущего процесса
            StartBackgroundOldCleanup(Environment.ProcessId, backupExe);

            // Штатный перезапуск через MainWindow -> ShutdownAsync -> App.StartNewInstance()
            requestRestart();
            return;
        }
        catch (Exception)
        {
            // Если переименовать работающий .exe не удалось — переходим к фоновому скрипту замены после выхода
        }

        // Способ 2 (резервный): внешний cmd-скрипт дожидается завершения текущего PID,
        // заменяет исполняемый файл и запускает приложение с флагом --restarted
        StartDeferredReplaceAndRestart(Environment.ProcessId, downloadedExePath, currentExe, backupExe);
        requestExit();
    }

    private static void StartBackgroundOldCleanup(int pid, string backupExe)
    {
        try
        {
            var cmdArgs = $"/c \"for /L %i in (1,1,25) do @(tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL && timeout /t 1 /nobreak >NUL || (del /f /q \"{backupExe}\" >NUL 2>&1 & exit /b 0))\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmdArgs)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch
        {
            /* не критично: .old также удаляется при следующем старте приложения */
        }
    }

    private static void StartDeferredReplaceAndRestart(int pid, string downloadedExe, string targetExe, string backupExe)
    {
        var workDir = Path.GetDirectoryName(targetExe) ?? AppContext.BaseDirectory;
        var scriptPath = Path.Combine(Path.GetTempPath(), $"vibetun-updater-{Guid.NewGuid():N}.cmd");

        var script = string.Join("\r\n", new[]
        {
            "@echo off",
            "chcp 65001 >NUL",
            $"set \"PID={pid}\"",
            $"set \"SRC={downloadedExe}\"",
            $"set \"DST={targetExe}\"",
            $"set \"BAK={backupExe}\"",
            $"set \"WORKDIR={workDir}\"",
            "for /L %%i in (1,1,40) do (",
            "  tasklist /FI \"PID eq %PID%\" 2>NUL | find \"%PID%\" >NUL",
            "  if errorlevel 1 goto :replace",
            "  timeout /t 1 /nobreak >NUL",
            ")",
            ":replace",
            "for /L %%i in (1,1,15) do (",
            "  move /y \"%SRC%\" \"%DST%\" >NUL 2>&1 && goto :launch",
            "  copy /y \"%SRC%\" \"%DST%\" >NUL 2>&1 && (del /f /q \"%SRC%\" >NUL 2>&1 & goto :launch)",
            "  timeout /t 1 /nobreak >NUL",
            ")",
            ":launch",
            "del /f /q \"%BAK%\" >NUL 2>&1",
            "cd /d \"%WORKDIR%\"",
            "start \"\" \"%DST%\" --restarted",
            "del /f /q \"%~f0\" >NUL 2>&1"
        });

        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var needsElevation = !IsDirectoryWritable(workDir) && !AdminHelper.IsElevated;
        var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{scriptPath}\"\"")
        {
            WorkingDirectory = workDir,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = !needsElevation,
            UseShellExecute = needsElevation
        };
        if (needsElevation) psi.Verb = "runas";

        Process.Start(psi);
    }

    /// <summary>Удалить оставшиеся после обновления временные файлы (.old / .download) при запуске.</summary>
    public static void CleanupOldExecutable()
    {
        try
        {
            var exe = AppPaths.ExecutablePath;
            if (string.IsNullOrWhiteSpace(exe)) return;

            foreach (var suffix in new[] { ".old", ".download", ".update" })
            {
                var path = exe + suffix;
                if (File.Exists(path))
                {
                    try { File.Delete(path); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }
    }

    private static string GetStagingDirectory()
    {
        var exeDir = Path.GetDirectoryName(AppPaths.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(exeDir) && IsDirectoryWritable(exeDir))
            return AppPaths.RuntimeDir;
        return Path.GetTempPath();
    }

    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".upd-probe-" + Guid.NewGuid().ToString("N")[..6]);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
