using System.Collections.Generic;
using VibeTun.Models;

namespace VibeTun.Services;

public enum SubscriptionImportAction
{
    Cancel,
    AddSubscription,
    AddProfiles,
    AddProfilesToGroup
}

public sealed record SubscriptionGroupOption(string Id, string Name);

public sealed record SubscriptionImportChoice(
    SubscriptionImportAction Action,
    string? GroupId = null,
    string? NewGroupName = null);

/// <summary>
/// Что показать в окне сервиса раздела «DPI»: сам сервис (копия — правки не применяются, пока
/// не нажали «Сохранить»), признак «новый» и копии профилей настроек обхода.
/// </summary>
public sealed record DpiServiceEditRequest(
    DpiService Service,
    bool IsNew,
    IReadOnlyList<DpiStrategyProfile> Profiles);

/// <summary>
/// Чем закончилось окно сервиса: итоговый сервис, список профилей и признак «нажали Выбрать DNS»
/// (тогда раздел уводит пользователя в «DNS» и после выбора возвращает его в «DPI»).
/// </summary>
public sealed record DpiServiceEditResult(
    DpiService Service,
    List<DpiStrategyProfile> Profiles,
    bool PickDns = false);

/// <summary>Диалоги приложения (реализуются во view-слое).</summary>
public interface IDialogService
{
    /// <summary>Редактор профиля. true — если пользователь сохранил.</summary>
    bool EditProfile(ProxyProfile profile, LatencyService latency);

    /// <summary>Импорт: возвращает разобранные профили (или пустой список, если отменили).</summary>
    List<ProxyProfile> ImportProfiles(string? prefill = null);

    string? PromptText(string title, string caption, string initial = "");

    /// <summary>
    /// Окно «Свой DNS»: IP-адрес и название (добавление и правка). Адрес уже проверен и приведён
    /// к виду для конфига; null — если пользователь отменил.
    /// </summary>
    (string Name, string Address)? EditDnsServer(string title, string okText, string name = "", string address = "");

    /// <summary>
    /// Окно сервиса раздела «DPI»: домен, приёмы обхода и профили настроек, кнопки «Тест»,
    /// «Подобрать», «Отмена» и «Сохранить». null — если пользователь закрыл окно без сохранения.
    /// </summary>
    DpiServiceEditResult? EditDpiService(DpiServiceEditRequest request);

    bool Confirm(string title, string text, string? okText = null, bool danger = true);

    ProfileGroupDeleteChoice ChooseProfileGroupDeletion(string groupName, int profileCount);

    SubscriptionImportChoice ChooseSubscriptionImport(
        string subscriptionName, int profileCount, IReadOnlyList<SubscriptionGroupOption> groups);

    void Info(string title, string text);

    void Error(string title, string text);

    /// <summary>Выбор приложений: из запущенных процессов и/или по .exe. Возвращает список путей к .exe.</summary>
    List<RunningApp> PickApplications();

    string? OpenFile(string filter, string? initialDirectory = null);

    string? SaveFile(string filter, string defaultName);

    /// <summary>Предложить перезапуск с правами администратора.</summary>
    bool ConfirmElevate(string title, string text);

    void ShowCoreLog(string logText);

    /// <summary>Выбор папки. null — если пользователь отменил выбор.</summary>
    string? PickFolder(string description, string? initialDirectory = null);
}
