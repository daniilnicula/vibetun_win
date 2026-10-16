using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
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
    public string AssetName { get; init; } = "VibeTun.exe";
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
    public const string Repo = "daniilnicula/vibetun_win";
    private const string ApiReleasesUrl = $"https://api.github.com/repos/{Repo}/releases?per_page=100";
    private const string WebReleasesUrl = $"https://github.com/{Repo}/releases";
    private const string AtomReleasesUrl = $"https://github.com/{Repo}/releases.atom";

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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VibeTun-Updater/1.2");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// Загрузить список релизов под Windows с https://github.com/daniilnicula/vibetun_win/releases
    /// (от самого нового к старым).
    /// </summary>
    public static async Task<List<AppReleaseInfo>> FetchWindowsReleasesAsync(CancellationToken ct = default)
    {
        Exception? lastError = null;

        // Пробуем через текущие системные настройки прокси и напрямую (если прокси недоступен или наоборот нужен обход)
        foreach (var useProxy in new[] { true, false })
        {
            try
            {
                using var client = CreateClient(useProxy, TimeSpan.FromSeconds(20));
                using var resp = await client.GetAsync(ApiReleasesUrl, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    var list = ParseApiReleases(json);
                    if (list.Count > 0) return list;
                }
                else if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    // Если лимит GitHub API исчерпан для IP (актуально на общем VPN), берём из веб-страницы + Atom-ленты
                    var fallback = await FetchFallbackReleasesAsync(client, ct);
                    if (fallback.Count > 0) return fallback;
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

    private static List<AppReleaseInfo> ParseApiReleases(string json)
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
                                   !aName.Contains("android", StringComparison.OrdinalIgnoreCase) &&
                                   !aName.Contains("linux", StringComparison.OrdinalIgnoreCase) &&
                                   !aName.Contains("darwin", StringComparison.OrdinalIgnoreCase) &&
                                   !aName.Contains("macos", StringComparison.OrdinalIgnoreCase);

                    if (!isExe && !isWinZip) continue;

                    // Предпочитаем VibeTun.exe или любой .exe перед .zip
                    var betterMatch = downloadUrl is null ||
                                      (isExe && !assetName!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ||
                                      aName.Equals("VibeTun.exe", StringComparison.OrdinalIgnoreCase);
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

            var isAndroidBranch = commitish.Equals("Android", StringComparison.OrdinalIgnoreCase);
            var isWindowsBranch = commitish.Equals("Windows", StringComparison.OrdinalIgnoreCase);

            // Релиз считается Windows-релизом, если в нём есть Windows-ассет (.exe/.zip)
            // или он выпущен из ветки Windows (и не является чисто Android-релизом)
            if (downloadUrl is null)
            {
                if (isAndroidBranch || hasOnlyAndroidAssets) continue;
                if (!isWindowsBranch && totalAssets > 0) continue;

                assetName = "VibeTun.exe";
                downloadUrl = $"https://github.com/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/VibeTun.exe";
            }

            result.Add(new AppReleaseInfo
            {
                Id = id,
                TagName = tag,
                Name = string.IsNullOrWhiteSpace(name) ? tag : name,
                TargetCommitish = commitish,
                PublishedAt = publishedAt,
                Body = body,
                AssetName = assetName ?? "VibeTun.exe",
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

    /// <summary>Резервный парсинг через страницу релизов и Atom-ленту, если GitHub API вернул лимит 403.</summary>
    private static async Task<List<AppReleaseInfo>> FetchFallbackReleasesAsync(HttpClient client, CancellationToken ct)
    {
        var html = await client.GetStringAsync(WebReleasesUrl, ct);
        var releaseTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(html, @"/daniilnicula/vibetun_win/releases/tag/([^""'<>?#\s]+)"))
        {
            var tag = Uri.UnescapeDataString(m.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(tag)) releaseTags.Add(tag);
        }

        var atomXml = await client.GetStringAsync(AtomReleasesUrl, ct);
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

            list.Add(new AppReleaseInfo
            {
                TagName = tag,
                Name = title,
                TargetCommitish = "Windows",
                PublishedAt = publishedAt,
                Body = plainBody,
                AssetName = "VibeTun.exe",
                DownloadUrl = $"https://github.com/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/VibeTun.exe"
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

    private static string? ParseSha256Digest(string? digest)
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
    /// Определяет, какой релиз из списка сейчас установлен у пользователя.
    /// 1) По точному совпадению SHA-256 текущего VibeTun.exe с ассетом на GitHub;
    /// 2) По сохранённому тегу <see cref="VibeTun.Models.AppSettings.InstalledReleaseTag"/> (если хэш exe совпадает);
    /// 3) По метке релиза сборки <see cref="AppInfo.ReleaseTag"/>;
    /// 4) Иначе — самый свежий релиз в списке.
    /// </summary>
    public static AppReleaseInfo? ResolveCurrentRelease(IReadOnlyList<AppReleaseInfo> releases)
    {
        if (releases.Count == 0) return null;

        var currentSha = GetCurrentExeSha256();
        var settings = App.Settings.Current;

        // 1. Точное совпадение бинарника по SHA-256 с релизом на GitHub
        if (!string.IsNullOrWhiteSpace(currentSha))
        {
            var byHash = releases.FirstOrDefault(r =>
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
                return byHash;
            }
        }

        // 2. Сохранённый тег после обновления через приложение
        if (!string.IsNullOrWhiteSpace(settings.InstalledReleaseTag))
        {
            var hashMatchesOrUnknown = string.IsNullOrWhiteSpace(settings.InstalledExeSha256) ||
                                       string.IsNullOrWhiteSpace(currentSha) ||
                                       string.Equals(settings.InstalledExeSha256, currentSha, StringComparison.OrdinalIgnoreCase);
            if (hashMatchesOrUnknown)
            {
                var bySavedTag = FindByTagOrName(releases, settings.InstalledReleaseTag);
                if (bySavedTag is not null) return bySavedTag;
            }
        }

        // 3. Тег релиза, зашитый в сборку (AppInfo.ReleaseTag)
        var byAppInfo = FindByTagOrName(releases, AppInfo.ReleaseTag);
        if (byAppInfo is not null) return byAppInfo;

        // 4. По умолчанию — последний доступный релиз
        return releases[0];
    }

    private static AppReleaseInfo? FindByTagOrName(IReadOnlyList<AppReleaseInfo> releases, string? tagOrName)
    {
        if (string.IsNullOrWhiteSpace(tagOrName)) return null;
        var key = tagOrName.Trim();
        var keyNoV = key.TrimStart('v', 'V');

        return releases.FirstOrDefault(r =>
            string.Equals(r.TagName.Trim(), key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Title, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.TagName.Trim().TrimStart('v', 'V'), keyNoV, StringComparison.OrdinalIgnoreCase));
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
                            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                            received += read;

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
                catch (Exception ex)
                {
                    lastError = ex;
                    try { if (File.Exists(downloadPath)) File.Delete(downloadPath); } catch { /* ignore */ }
                }
            }

            if (!downloaded)
                throw lastError ?? new IOException(L.T("S0278"));

            string finalExePath;
            if (isZip)
            {
                var extractDir = Path.Combine(workDir, $"VibeTun-extract-{id}");
                Directory.CreateDirectory(extractDir);
                try
                {
                    ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
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

            // Проверяем, что скачанный файл действительно исполняемый PE-файл Windows ("MZ") и его хэш сходится
            ValidateExecutable(finalExePath, isZip ? null : release.AssetSha256, out var sha256);
            return (finalExePath, sha256);
        }
        catch
        {
            try { if (File.Exists(downloadPath)) File.Delete(downloadPath); } catch { /* ignore */ }
            throw;
        }
    }

    private static void ValidateExecutable(string exePath, string? expectedSha256, out string actualSha256)
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

        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(L.T("S0282"));
        }
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
