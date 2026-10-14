using System.Reflection;

namespace GoreBox.Utils;

/// <summary>
/// Метка сборки приложения.
///
/// Меняется в каждом коммите, который влияет на поведение: первая строка app.log
/// («GoreBox 1.2.0 (neko-grpc) · ядро: …») сразу показывает, какой код запущен.
/// Без неё легко принять старый бинарник за новую сборку и чинить уже починенное.
/// </summary>
public static class AppInfo
{
    /// <summary>Короткая метка кода. Обновлять вместе с изменениями в логике подключения.</summary>
    public const string BuildStamp = "log-compact";

    /// <summary>Тег релиза на GitHub (GoreSense/GoreBox), которому соответствует эта сборка по умолчанию.</summary>
    public const string DefaultReleaseTag = "26.11.14L";

    public static string ReleaseTag
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var clean = info.Split('+')[0].Trim();
                if (clean.Length > 0 && clean != Version && clean != "1.0.0")
                    return clean;
            }
            return DefaultReleaseTag;
        }
    }

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static string Full => Version + " (" + BuildStamp + ")";
}
