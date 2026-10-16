using System.Collections.ObjectModel;
using System.Diagnostics;
using VibeTun.Models;
using VibeTun.Services;

namespace VibeTun.ViewModels;

/// <summary>
/// Раздел «DPI»: сервисы, для которых нужен обход блокировок, и профили настроек обхода.
///
/// Список сервисов устроен как список DNS в разделе «DNS»: строка со значком сайта, именем, доменом,
/// профилем и пингом; сервисы можно добавлять, править, удалять, закреплять и перетаскивать.
/// Кнопка «Пинг» меряет доступность сервиса (ответ сервера на ClientHello — напрямую или через обход).
///
/// Настройки сервиса собираются в окне <c>DpiServiceWindow</c>: домен, приёмы обхода и профили
/// (создать, переименовать, удалить, экспортировать, импортировать), там же кнопки «Тест» и
/// «Подобрать». Подбор создаёт профиль «auto» с найденным набором приёмов.
/// </summary>
public sealed class DpiViewModel : ObservableObject
{
    /// <summary>Сколько сервисов проверяем одновременно.</summary>
    private const int PingParallelism = 6;

    private readonly IDialogService _dialogs;

    /// <summary>Порт работающего обхода (0 — не поднят): тогда пинг меряет сервис напрямую.</summary>
    private readonly Func<int> _enginePort;

    private readonly SemaphoreSlim _pingGate = new(PingParallelism);

    public DpiViewModel(IDialogService dialogs, Func<int>? enginePort = null)
    {
        _dialogs = dialogs;
        _enginePort = enginePort ?? (() => 0);

        AddServiceCommand = new RelayCommand(_ => AddService());
        EditServiceCommand = new RelayCommand(p => EditService(p as DpiServiceVm));
        RemoveServiceCommand = new RelayCommand(p => RemoveService(p as DpiServiceVm));
        ToggleFavoriteCommand = new RelayCommand(p => ToggleFavorite(p as DpiServiceVm));
        ToggleEnabledCommand = new RelayCommand(p => ToggleEnabled(p as DpiServiceVm));
        PingCommand = new RelayCommand(p => _ = PingAsync(p as DpiServiceVm));
        PingAllCommand = new AsyncRelayCommand(PingAllAsync);
        OpenSiteCommand = new RelayCommand(p => OpenSite(p as DpiServiceVm));
        RefreshIconCommand = new RelayCommand(p => _ = RefreshIconAsync(p as DpiServiceVm));
        EditGeneralCommand = new RelayCommand(_ => EditGeneral());
        RestartElevatedCommand = new RelayCommand(_ => RestartElevated(), _ => NeedsAdmin);

        // переключатель «Общих»: приёмы применяются ко всему трафику или только к сервисам
        General.Toggled = () =>
        {
            App.Settings.SaveSoon();
            OnChanged();
            GeneralChanged?.Invoke();
        };

        DpiServiceCatalog.EnsureSeeded(App.Settings.Current);
        Load();
    }

    /// <summary>Сервисы в порядке пользователя.</summary>
    public ObservableCollection<DpiServiceVm> Services { get; } = new();

    /// <summary>«Общие»: набор приёмов для всего трафика, стоит над списком сервисов.</summary>
    public DpiGeneralVm General { get; } = new();

    /// <summary>Открыть настройки «Общих» — тем же окном, что у сервиса.</summary>
    public RelayCommand EditGeneralCommand { get; }

    public RelayCommand AddServiceCommand { get; }
    public RelayCommand EditServiceCommand { get; }
    public RelayCommand RemoveServiceCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand ToggleEnabledCommand { get; }
    public RelayCommand PingCommand { get; }
    public AsyncRelayCommand PingAllCommand { get; }
    public RelayCommand OpenSiteCommand { get; }
    public RelayCommand RefreshIconCommand { get; }

    /// <summary>Перезапуск приложения с правами администратора: без них не работает инъекция пакетов.</summary>
    public RelayCommand RestartElevatedCommand { get; }

    /// <summary>Строка состояния приложения.</summary>
    public event Action<string, bool>? Notify;

    /// <summary>Сервисы или профили изменились — движок обхода надо перезапустить.</summary>
    public event Action? Changed;

    /// <summary>«Общие» включили или выключили: приёмы применяются ко всему или только к сервисам.</summary>
    public event Action? GeneralChanged;

    /// <summary>
    /// В окне сервиса нажали «Выбрать DNS»: раздел уводит пользователя в «DNS» (там выбирают DNS
    /// так же, как для домена и приложения) и после выбора возвращает его в «DPI».
    /// </summary>
    public event Action<DpiService>? DnsPickRequested;

    public bool HasServices => Services.Count > 0;

    /// <summary>
    /// Приёмы с фейк-пакетом работают в полную силу только с правами администратора: Windows не даёт
    /// обычному процессу raw-сокет, а без него «фейк» не попадает в тот же поток, что и настоящий
    /// ClientHello, и DPI его не видит. Разрыв, мультисплит и disorder работают и без прав.
    /// </summary>
    public bool NeedsAdmin => !AdminHelper.IsElevated && !DpiBypassEngine.InjectionAvailable;

    /// <summary>Показывать ли подсказку про права (только пока есть чему мешать).</summary>
    public bool ShowAdminHint => NeedsAdmin;

    /// <summary>Состояние честной инъекции — для «Копировать диагностику».</summary>
    public string InjectionText => AdminHelper.IsElevated
        ? L.T("dpi.admin.elevated")
        : DpiBypassEngine.InjectionAvailable
            ? DpiRawFlow.LearnedOnce ? L.T("dpi.admin.raw") : L.T("dpi.admin.rawNoSeq")
            : L.T("dpi.admin.none");

    /// <summary>Сколько сервисов и профилей в разделе — подпись под заголовком.</summary>
    public string SummaryText
    {
        get
        {
            var section = App.Settings.Current.DpiServices;
            return L.F("dpi.summary", Services.Count, section.Profiles.Count);
        }
    }

    /// <summary>
    /// Сервисы и их наборы приёмов одной строкой — для «Копировать диагностику»: по ней видно,
    /// что именно применяется к какому домену.
    /// </summary>
    public string DiagnosticsText()
    {
        if (Services.Count == 0) return "";

        var parts = Services.Select(service => L.F("dpi.diag.line",
            service.Name,
            service.DomainText,
            service.Enabled ? DpiTester.Describe(service.Model.Settings) : L.T("dpi.disabled")));

        return L.F("dpi.diag.title", Services.Count) + string.Join("; ", parts) + Environment.NewLine +
               L.F("dpi.diag.injection", InjectionText) + Environment.NewLine;
    }

    /// <summary>Профиль, который применяется к домену (для движка и для строки списка).</summary>
    public DpiStrategyProfile? ProfileFor(DpiService service) =>
        DpiServiceCatalog.FindProfile(App.Settings.Current.DpiServices, service.ProfileId);

    /// <summary>Вошли в раздел: подтягиваем значки сервисов, которые ещё не скачивали.</summary>
    public void OnOpened() => _ = LoadIconsAsync();

    internal override void RefreshLanguage()
    {
        foreach (var service in Services) service.ShowProfile(ProfileFor(service.Model));
        General.Refresh();
        Raise(nameof(SummaryText));
        base.RefreshLanguage();
    }

    /// <summary>Перезапуск с правами администратора: приём с фейком в том же потоке без них не работает.</summary>
    private void RestartElevated()
    {
        if (AdminHelper.RestartElevated())
        {
            // окно закрывает сама программа при выходе; пользователю говорим, что дальше
            Notify?.Invoke(L.T("dpi.admin.restarting"), false);
            return;
        }

        Notify?.Invoke(L.T("dpi.admin.restartFailed"), true);
    }

    // ------------------------------------------------------------------- список

    private void Load()
    {
        var section = App.Settings.Current.DpiServices;
        Services.Clear();

        foreach (var model in section.Services)
        {
            var vm = new DpiServiceVm(model);
            vm.Icon = DpiIconService.FromCache(DpiServiceCatalog.Primary(model.Domain));
            vm.ShowProfile(ProfileFor(model));
            vm.Toggled = () => { App.Settings.SaveSoon(); OnChanged(); };
            Services.Add(vm);
        }

        General.Refresh();

        Raise(nameof(HasServices));
        Raise(nameof(SummaryText));
    }

    /// <summary>Значки сервисов в фоне: сеть не должна задерживать открытие раздела.</summary>
    private async Task LoadIconsAsync()
    {
        foreach (var service in Services.ToList())
        {
            if (service.HasIcon || service.IconRequested || service.Model.Domain.Length == 0) continue;
            service.IconRequested = true;

            try
            {
                var icon = await DpiIconService.GetAsync(DpiServiceCatalog.Primary(service.Model.Domain));
                if (icon is not null) service.Icon = icon;
            }
            catch { /* значок — украшение: без него строка живёт с общим значком */ }
        }
    }

    private async Task RefreshIconAsync(DpiServiceVm? service)
    {
        if (service is null || service.Model.Domain.Length == 0) return;

        DpiIconService.Forget(DpiServiceCatalog.Primary(service.Model.Domain));
        var icon = await DpiIconService.GetAsync(DpiServiceCatalog.Primary(service.Model.Domain), force: true);
        service.Icon = icon;
        Notify?.Invoke(icon is null ? L.T("dpi.iconFailed") : L.T("dpi.iconDone"), icon is null);
    }

    /// <summary>Клик по строке: открываем окно сервиса — как карандаш справа.</summary>
    public void Activate(DpiServiceVm? service)
    {
        if (service is null) return;
        EditService(service);
    }

    /// <summary>Строку перетащили на новое место.</summary>
    public void MoveEntry(int from, int to)
    {
        var section = App.Settings.Current.DpiServices;
        if (from < 0 || to < 0 || from >= Services.Count || to >= Services.Count) return;

        var vm = Services[from];
        Services.Move(from, to);

        var model = section.Services.FirstOrDefault(s => s.Id == vm.Id);
        if (model is null) return;
        section.Services.Remove(model);
        section.Services.Insert(Math.Min(to, section.Services.Count), model);
    }

    /// <summary>Перетаскивание закончилось — порядок запоминаем.</summary>
    public void CommitOrder()
    {
        App.Settings.SaveSoon();
        Raise(nameof(SummaryText));
    }

    // ------------------------------------------------------------------- правка

    private void AddService()
    {
        var section = App.Settings.Current.DpiServices;
        var draft = new DpiService { Name = NextName(), Domain = "" };

        // в окно уходят копии: пока не нажали «Сохранить», в настройках ничего не меняется
        var result = _dialogs.EditDpiService(new DpiServiceEditRequest(
            draft.Clone(), IsNew: true, ProfileCopies(section)));
        if (result is null) return;

        ApplyProfiles(result.Profiles, null);
        section.Services.Add(result.Service);
        App.Settings.SaveSoon();

        Load();
        OnChanged();

        // «Выбрать DNS» в окне: сервис уже сохранён, дальше пользователь выбирает DNS в разделе «DNS»
        if (result.PickDns)
        {
            DnsPickRequested?.Invoke(result.Service);
            return;
        }

        Notify?.Invoke(L.F("dpi.serviceAdded", result.Service.Name), false);
    }

    /// <summary>
    /// Окно «Общих»: набор приёмов для всего трафика. Имя и домены в нём не меняются, зато есть
    /// транспортные поля (порт, задержка, режим выхода, системный прокси) и выбор DNS — раньше
    /// это жило в «Настройках» → «Обход DPI».
    /// </summary>
    private void EditGeneral()
    {
        var section = App.Settings.Current.DpiServices;
        var draft = new DpiService
        {
            Id = DpiServices.GeneralId,
            Name = L.T("dpi.general"),
            Domain = "",
            Enabled = section.GeneralEnabled,
            Settings = App.Settings.Current.Dpi.Clone(),
            Dns = section.GeneralDns.Clone()
        };

        var result = _dialogs.EditDpiService(new DpiServiceEditRequest(draft, IsNew: false, ProfileCopies(section)));
        if (result is null) return;

        ApplyProfiles(result.Profiles, null);

        // набор приёмов «Общих» — это блок обхода в настройках: забираем форму целиком, но
        // признак «обход включён» оставляем свой — им управляет кнопка на карточке подключения
        var settings = result.Service.Settings.Clone();
        settings.Enabled = App.Settings.Current.Dpi.Enabled;
        App.Settings.Current.Dpi = settings;

        section.GeneralDns = result.Service.Dns.Clone();
        section.GeneralDns.Id = DpiServices.GeneralId;
        App.Settings.SaveSoon();

        General.Refresh();
        Raise(nameof(SummaryText));
        GeneralChanged?.Invoke();

        if (result.PickDns)
        {
            // разделу «DNS» отдаём ту же запись DNS, что легла в настройки: выбранный сервер
            // он записывает прямо в неё, поэтому копия потеряла бы выбор
            DnsPickRequested?.Invoke(new DpiService
            {
                Id = DpiServices.GeneralId,
                Name = L.T("dpi.general"),
                Dns = section.GeneralDns,
                Settings = App.Settings.Current.Dpi
            });
            return;
        }

        Notify?.Invoke(L.T("dpi.generalSaved"), false);
    }

    /// <summary>Из раздела «DNS» вернулись после выбора DNS для сервиса: обновляем подписи строк.</summary>
    public void OnDnsPicked(string id)
    {
        General.Refresh();
        Raise(nameof(SummaryText));
    }

    private void EditService(DpiServiceVm? service)
    {
        if (service is null) return;

        var section = App.Settings.Current.DpiServices;
        var result = _dialogs.EditDpiService(new DpiServiceEditRequest(
            service.Model.Clone(), IsNew: false, ProfileCopies(section)));
        if (result is null) return;

        var updated = result.Service;

        // Окно сервиса возвращает копию: без этой строки все правки (домен, наборы приёмов,
        // выбранный профиль) оставались в копии и не попадали в настройки — сервис «не
        // сохранялся». Кладём правку на место оригинала по Id.
        var index = section.Services.FindIndex(s => s.Id == updated.Id);
        if (index >= 0) section.Services[index] = updated;

        ApplyProfiles(result.Profiles, updated.Id);

        // сервис мог быть переименован — строка пересоздаётся целиком
        App.Settings.SaveSoon();
        Load();

        var vm = Services.FirstOrDefault(s => s.Id == updated.Id);
        if (vm is not null)
        {
            vm.ClearPing();
            vm.IconRequested = false;
            _ = LoadIconsAsync();
        }

        OnChanged();

        if (result.PickDns)
        {
            DnsPickRequested?.Invoke(updated);
            return;
        }

        Notify?.Invoke(L.F("dpi.serviceSaved", updated.Name), false);
    }

    /// <summary>
    /// Профили из окна сервиса: список приходит целиком (в нём могли появиться, переименоваться или
    /// исчезнуть записи). Порядок сохраняем, профиль подбора «auto» остаётся в разделе.
    /// </summary>
    private void ApplyProfiles(List<DpiStrategyProfile> profiles, string? serviceId)
    {
        var section = App.Settings.Current.DpiServices;
        section.Profiles = profiles;

        foreach (var model in section.Services)
        {
            if (model.ProfileId is not null && DpiServiceCatalog.FindProfile(section, model.ProfileId) is null)
                model.ProfileId = null;   // профиль удалили — сервис возвращается к общему набору
        }

        // у переименованных профилей обновляем подписи строк
        foreach (var vm in Services) vm.ShowProfile(ProfileFor(vm.Model));
        if (serviceId is not null) OnChanged();

        App.Settings.SaveSoon();
        Raise(nameof(SummaryText));
    }

    private void RemoveService(DpiServiceVm? service)
    {
        if (service is null) return;
        if (!_dialogs.Confirm(L.T("dpi.title"), L.F("dpi.removeText", service.Name), L.T("S0083"))) return;

        var section = App.Settings.Current.DpiServices;
        section.Services.RemoveAll(s => s.Id == service.Id);
        Services.Remove(service);

        App.Settings.SaveSoon();

        Raise(nameof(HasServices));
        Raise(nameof(SummaryText));
        OnChanged();
        Notify?.Invoke(L.F("dpi.serviceRemoved", service.Name), false);
    }

    private void ToggleFavorite(DpiServiceVm? service)
    {
        if (service is null) return;
        service.Favorite = !service.Favorite;
        App.Settings.SaveSoon();
    }

    private void ToggleEnabled(DpiServiceVm? service)
    {
        if (service is null) return;
        service.Enabled = !service.Enabled;
        App.Settings.SaveSoon();
        OnChanged();
    }

    private void OpenSite(DpiServiceVm? service)
    {
        var domain = DpiServiceCatalog.NormalizeHost(service?.Model.Domain ?? "");
        if (domain.Length == 0) return;

        try
        {
            Process.Start(new ProcessStartInfo("https://" + domain) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify?.Invoke(ex.Message, true);
        }
    }

    private string NextName()
    {
        for (var i = 1; ; i++)
        {
            var name = L.F("dpi.serviceName", i);
            if (Services.All(s => !string.Equals(s.Name, name, StringComparison.Ordinal))) return name;
        }
    }

    /// <summary>Копии профилей для окна сервиса: правки в окне не трогают настройки до «Сохранить».</summary>
    private static List<DpiStrategyProfile> ProfileCopies(DpiServices section) =>
        section.Profiles.Select(p => p.Clone()).ToList();

    private void OnChanged()
    {
        Raise(nameof(SummaryText));
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------- пинг

    private async Task PingAsync(DpiServiceVm? service)
    {
        if (service is null || service.IsPinging) return;

        service.ShowPingPending();
        await _pingGate.WaitAsync();
        try
        {
            var port = _enginePort();
            var result = await DpiServiceTester.PingAsync(service.Model.Domain, port);

            service.ShowPing(result);
            service.Model.LastPingMs = result.Answered ? result.Ms : -1;
            service.Model.CheckedAt = DateTime.UtcNow;
            service.Model.LastNote = result.Detail;
            App.Settings.SaveSoon();
        }
        catch (Exception ex)
        {
            service.ShowPing(new DpiServiceTest(false, false, false, -1, ex.Message));
        }
        finally
        {
            _pingGate.Release();
        }
    }

    /// <summary>Пинг всех сервисов — как «Пинг всех» в разделе «DNS».</summary>
    private async Task PingAllAsync()
    {
        var pending = Services.ToList();
        if (pending.Count == 0) return;

        var port = _enginePort();
        var tasks = pending.Select(async service =>
        {
            service.ShowPingPending();
            await _pingGate.WaitAsync();
            try
            {
                var result = await DpiServiceTester.PingAsync(service.Model.Domain, port);
                service.ShowPing(result);
                service.Model.LastPingMs = result.Answered ? result.Ms : -1;
                service.Model.CheckedAt = DateTime.UtcNow;
                service.Model.LastNote = result.Detail;
            }
            catch (Exception ex)
            {
                service.ShowPing(new DpiServiceTest(false, false, false, -1, ex.Message));
            }
            finally
            {
                _pingGate.Release();
            }
        });

        await Task.WhenAll(tasks);
        App.Settings.SaveSoon();

        var answered = pending.Count(s => s.PingMs >= 0);
        Notify?.Invoke(L.F("dpi.pingDone", answered, pending.Count), answered == 0);
    }
}
