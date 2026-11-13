using System.Diagnostics;
using System.IO;
using System.Text;

namespace VibeTun.Services;

/// <summary>
/// Очистка остатков TUN-режима. Адаптер wintun создаётся с новым именем и новым device
/// instance GUID на каждый старт (см. SingBoxConfigBuilder); после Kill процесса он остаётся
/// в системе, а в Диспетчере устройств выглядит как «sing-tun Tunnel» — так драйвер подписывает
/// TunnelType (описание устройства, а не имя подключения). Скрытые узлы Get-NetAdapter без
/// -IncludeHidden не возвращает, поэтому раньше они не чистились и копились.
/// </summary>
public static class TunCleanup
{
    /// <summary>
    /// Полная очистка перед стартом TUN: наши подключения VibeTun* (видимые и скрытые),
    /// чужие скрытые TUN/TAP-адаптеры в состоянии не «Up» и фантомы PnP (not-present узлы
    /// с описанием tun/tap). Живые туннели других приложений (Status = Up) не трогаются.
    /// </summary>
    public static string? PrepareForTun() => RunPowerShell(CleanupScript(), 20000);

    /// <summary>
    /// То же при запуске приложения: убрать накопившиеся скрытые TUN-адаптеры и фантомы
    /// Device Manager сразу, не дожидаясь первого TUN-старта. Вызывать с правами админа —
    /// без них удаление ничего не сделает.
    /// </summary>
    public static string? CleanupHiddenTunAdapters() => RunPowerShell(CleanupScript(), 20000);

    /// <summary>Есть ли в конфиге TUN-inbound (по тегу, который всегда пишет SingBoxConfigBuilder).</summary>
    public static bool ConfigHasTun(string configJson) => configJson.Contains("tun-in", StringComparison.Ordinal);

    private static string CleanupScript()
    {
        // 1) сетевые адаптеры, видимые и скрытые: наши VibeTun* — всегда (кроме живых 'Up':
        //    так на старте не убивается адаптер усыновлённого демона после падения приложения);
        //    чужие TUN/TAP — только скрытые (нет в обычном Get-NetAdapter) и не 'Up'.
        // 2) фантомы PnP: not-present узлы с описанием/именем tun/tap — их и видит
        //    Диспетчер устройств в «показать скрытые».
        return """
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            $visible = @((Get-NetAdapter -ErrorAction SilentlyContinue).InterfaceIndex)
            Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object {
                $_.Status -ne 'Up' -and (
                    $_.Name -like 'VibeTun*' -or
                    (($_.InterfaceDescription -like '*tun*' -or $_.InterfaceDescription -like '*tap*') -and
                     $_.InterfaceIndex -notin $visible))
            } | Remove-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue
            Get-PnpDevice -Class Net -ErrorAction SilentlyContinue | Where-Object {
                -not $_.Present -and (
                    $_.FriendlyName -like '*tun*' -or $_.FriendlyName -like '*tap*' -or
                    $_.InstanceId -like '*Wintun*' -or $_.InstanceId -like '*tap*')
            } | ForEach-Object {
                try { Remove-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -ErrorAction Stop }
                catch { pnputil /remove-device $_.InstanceId 2>$null | Out-Null }
            }
            exit 0
            """;
    }

    private static string? RunPowerShell(string script, int timeoutMs = 10000)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "vibetun_tun_cleanup.ps1");
        File.WriteAllText(tmp, script, new UTF8Encoding(false));
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tmp}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p is null) return "powershell не запустился";
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { /* ignore */ }
                return $"таймаут {timeoutMs} мс";
            }
            var stdout = stdoutTask.Result;
            var stderr = stderrTask.Result;
            return p.ExitCode == 0 ? null : string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }
}
