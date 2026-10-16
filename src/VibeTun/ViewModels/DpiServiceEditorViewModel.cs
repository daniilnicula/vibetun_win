using System.Collections.ObjectModel;
using System.IO;
using VibeTun.Models;
using VibeTun.Services;

namespace VibeTun.ViewModels;

/// <summary>
/// Окно сервиса раздела «DPI»: домен, приёмы обхода (все ручки движка) и профили настроек —
/// создать, переименовать, удалить, экспортировать, импортировать. Кнопка «Тест» проверяет обмен
/// пакетами с сервисом и пишет ход в журнал, «Подобрать» перебирает приёмы и создаёт профиль «auto».
///
/// Все правки идут в копии: пока не нажали «Сохранить», в настройках ничего не меняется.
/// </summary>
public sealed class DpiServiceEditorViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly DpiServiceEditRequest _request;

    private CancellationTokenSource? _task;
    private string _name = "";
    private string _domain = "";
    private bool _busy;
    private string _progress = "";
    private string _status = "";
    private bool _statusIsError;
    private DpiProfileVm? _selectedProfile;
    private DpiDnsChoice _dns = new();
    private string _port = "";
    private bool _exitDirect = true;
    private bool _exitProxy;
    private bool _useSystemProxy = true;
    private bool _pickDns;

    public DpiServiceEditorViewModel(DpiServiceEditRequest request, IDialogService dialogs)
    {
        _request = request;
        _dialogs = dialogs;

        _name = request.Service.Id == DpiServices.GeneralId ? L.T("dpi.general") : request.Service.Name;
        _domain = request.Service.Domain;

        Log = new LogFeed(200);
        ClearLogCommand = new RelayCommand(_ => Log.Clear());

        // Команды создаём ДО загрузки профилей и выбора профиля: сеттер SelectedProfile
        // пересчитывает доступность «Профиль → обновить», и если он сработает раньше,
        // окно падало с NullReferenceException (правка сервиса с уже выбранным профилем).
        NewProfileCommand = new RelayCommand(_ => NewProfile(), _ => !Busy);
        UpdateProfileCommand = new RelayCommand(_ => UpdateProfile(), _ => !Busy && _selectedProfile is not null);
        RenameProfileCommand = new RelayCommand(p => RenameProfile(p as DpiProfileVm), _ => !Busy);
        DeleteProfileCommand = new RelayCommand(p => DeleteProfile(p as DpiProfileVm), _ => !Busy);
        ExportProfileCommand = new RelayCommand(p => ExportProfile(p as DpiProfileVm), _ => !Busy);
        ImportProfileCommand = new RelayCommand(_ => ImportProfile(), _ => !Busy);
        UseCommonCommand = new RelayCommand(_ => UseCommon(), _ => !Busy);
        TestCommand = new AsyncRelayCommand(TestAsync, () => !Busy);
        AutoCommand = new AsyncRelayCommand(AutoAsync, () => !Busy);
        StopCommand = new RelayCommand(_ => StopTask(), _ => Busy);
        AddRelatedCommand = new RelayCommand(_ => AddRelatedDomains(), _ => !Busy && HasRelatedDomains);
        ClearDnsCommand = new RelayCommand(_ => ClearDns(), _ => !Busy && HasDns);

        _dns = request.Service.Dns.Clone();
        _dns.Id = request.Service.Id;

        var transport = App.Settings.Current.Dpi;
        _port = transport.ListenPort.ToString();
        _exitDirect = transport.ExitMode != DpiExitMode.Proxy;
        _useSystemProxy = transport.UseSystemProxy;

        foreach (var profile in request.Profiles) Profiles.Add(new DpiProfileVm(profile));
        RefreshUsage();

        Strategy = new DpiStrategyFormVm();
        var ownSettings = request.Service.Settings;
        var preset = Profiles.FirstOrDefault(p => p.Id == request.Service.ProfileId)?.Model.Settings;

        // у сервиса уже свои настройки — показываем их; иначе набор профиля, а для нового
        // сервиса — значения по умолчанию из эталонного набора: фейк с TTL 8 и разрыв ClientHello
        // (те же, что у «Общих»), у ТСПУ этот приём работает чаще остальных
        Strategy.Load(DpiServiceCatalog.HasAnyStrategy(ownSettings)
            ? ownSettings
            : preset ?? new DpiBypassSettings());

        var existing = Profiles.FirstOrDefault(p => p.Id == request.Service.ProfileId);
        if (existing is not null) SelectedProfile = existing;

        Strategy.Changed += () => Raise(nameof(StrategySummary));

        // подсказка про служебные домены площадки должна быть видна сразу при открытии окна
        Validate();
    }

    public string Title => IsGeneral
        ? L.T("dpi.window.generalTitle")
        : _request.IsNew ? L.T("dpi.window.add") : L.T("dpi.window.edit");

    /// <summary>Заголовок карточки: у «Общих» — свой, у сервиса — как раньше.</summary>
    public string ServiceHeader => IsGeneral ? L.T("dpi.general") : L.T("dpi.window.service");

    /// <summary>Пояснение для «Общих»: домены им не нужны, приёмы применяются ко всему трафику.</summary>
    public string GeneralText => L.T("dpi.window.generalHint");

    /// <summary>
    /// Окно «Общих» — набора приёмов для всего трафика. Настроить его можно так же, как сервис,
    /// но переименовать и задать домены нельзя: это не сервис, а общий набор раздела. Здесь же
    /// живут транспортные поля, которые раньше были в «Настройках» → «Обход DPI».
    /// </summary>
    public bool IsGeneral => _request.Service.Id == DpiServices.GeneralId;

    /// <summary>Обычный сервис: имя, домены и подсказка про служебные домены площадки.</summary>
    public bool IsService => !IsGeneral;

    /// <summary>Порт локального прокси обхода («Общие»).</summary>
    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    /// <summary>Куда идти после десинхронизации: напрямую. Выбранный сегмент не снимается — как у темы.</summary>
    public bool ExitDirect
    {
        get => _exitDirect;
        set
        {
            if (!value || _exitDirect) { Raise(); return; }
            _exitDirect = true;
            _exitProxy = false;
            Raise();
            Raise(nameof(ExitProxy));
        }
    }

    /// <summary>Куда идти после десинхронизации: в активный профиль (VPN). Снять выбор нельзя.</summary>
    public bool ExitProxy
    {
        get => _exitProxy;
        set
        {
            if (!value || _exitProxy) { Raise(); return; }
            _exitProxy = true;
            _exitDirect = false;
            Raise();
            Raise(nameof(ExitDirect));
        }
    }

    /// <summary>Прописывать обход в системный прокси Windows, пока он включён.</summary>
    public bool UseSystemProxy
    {
        get => _useSystemProxy;
        set => SetProperty(ref _useSystemProxy, value);
    }

    // ------------------------------------------------------------------- DNS сервиса

    /// <summary>
    /// Выбранный DNS: каким сервером движок спрашивает адреса доменов сервиса. Выбирается в разделе
    /// «DNS» — там же, где DNS для домена и приложения, — поэтому в окне только подпись и кнопки.
    /// </summary>
    public string DnsText
    {
        get
        {
            var text = DpiServiceCatalog.DnsTextFor(_dns, App.Settings.Current.Dns);
            return text.Length > 0 ? text : L.T("dpi.dns.none");
        }
    }

    public bool HasDns => DpiServiceCatalog.DnsTextFor(_dns, App.Settings.Current.Dns).Length > 0;

    /// <summary>Куда вернёмся после выбора: раздел «DNS» вернёт пользователя туда, откуда пришёл.</summary>
    public string DnsPickHint => L.T("dpi.dns.pickHint");

    /// <summary>Свой DNS не выбран — адреса берутся как у обхода в целом.</summary>
    public RelayCommand ClearDnsCommand { get; }

    /// <summary>
    /// В окне нажали «Выбрать DNS»: сервис сохраняется, а раздел уводит пользователя в «DNS»
    /// и после выбора возвращает его в «DPI» (см. <c>DpiViewModel.DnsPickRequested</c>).
    /// </summary>
    public bool PickDns => _pickDns;

    /// <summary>
    /// «Выбрать DNS…»: окно сохраняет правки и закрывается, а раздел уводит пользователя в «DNS» —
    /// там он выбирает сервер и возвращается в «DPI» (см. <c>DpiViewModel.DnsPickRequested</c>).
    /// </summary>
    public void RequestPickDns()
    {
        if (!CanSave) return;
        _pickDns = true;
    }

    private void ClearDns()
    {
        _dns.Mode = "";
        _dns.EntryId = null;
        _dns.Server = null;
        RaiseDns();
    }

    private void RaiseDns()
    {
        Raise(nameof(DnsText));
        Raise(nameof(HasDns));
        ClearDnsCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Имя сервиса в списке.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value)) { Raise(nameof(CanSave)); Validate(); }
        }
    }

    /// <summary>Домен сервиса: «discord.com», «youtube.com», «*.discord.media».</summary>
    public string Domain
    {
        get => _domain;
        set
        {
            if (SetProperty(ref _domain, value)) { Raise(nameof(CanSave)); Validate(); }
        }
    }

    public bool CanSave => !Busy && _name.Trim().Length > 0 && DomainProblem is null;

    /// <summary>Что не так с доменом (пусто — всё в порядке).</summary>
    public string? DomainProblem { get; private set; }

    public bool HasDomainProblem => DomainProblem is not null;

    /// <summary>
    /// Служебные домены площадки, которых ещё нет в поле: у Discord это discordapp.com,
    /// discordapp.net, discord.media. Без них открывается сама страница, а картинки и вложения
    /// приходят с другого домена и в обход не попадают.
    /// </summary>
    public ObservableCollection<string> RelatedDomains { get; } = new();

    public bool HasRelatedDomains => RelatedDomains.Count > 0;

    public string RelatedText => L.F("dpi.window.related", string.Join(", ", RelatedDomains));

    /// <summary>Дописать служебные домены площадки в поле «домен».</summary>
    public RelayCommand AddRelatedCommand { get; }

    /// <summary>Набор приёмов обхода в форме — те же ручки, что у движка.</summary>
    public DpiStrategyFormVm Strategy { get; }

    /// <summary>Набор приёмов одной строкой: видно, что именно уйдёт в движок.</summary>
    public string StrategySummary => DpiTester.Describe(Strategy.Settings);

    public ObservableCollection<DpiProfileVm> Profiles { get; } = new();

    /// <summary>
    /// Выбранный профиль: его набор приёмов скопирован в форму ниже. Профиль — шаблон, поэтому
    /// правки в форме на сам профиль не влияют: они сохраняются вместе с сервисом.
    /// </summary>
    public DpiProfileVm? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value)) return;
            _selectedProfile = value;
            Raise();
            Raise(nameof(HasProfile));
            Raise(nameof(ProfileHint));

            // «?» — страховка на случай, если значение выставят до создания команд
            UpdateProfileCommand?.RaiseCanExecuteChanged();
            if (value is not null) Strategy.Load(value.Model.Settings);
            RefreshUsage();
        }
    }

    public bool HasProfile => _selectedProfile is not null;

    public string ProfileHint => _selectedProfile is null ? L.T("dpi.window.commonHint") : L.T("dpi.window.profileHint");

    /// <summary>Журнал окна — тот же вид и поведение, что у логов на вкладке «Профили».</summary>
    public LogFeed Log { get; }

    public RelayCommand ClearLogCommand { get; }
    public RelayCommand NewProfileCommand { get; }

    /// <summary>Перенести набор приёмов из формы в выбранный профиль.</summary>
    public RelayCommand UpdateProfileCommand { get; }
    public RelayCommand RenameProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand ExportProfileCommand { get; }
    public RelayCommand ImportProfileCommand { get; }

    /// <summary>Вернуть сервис на общий набор обхода (без своего профиля).</summary>
    public RelayCommand UseCommonCommand { get; }

    /// <summary>Тест обмена пакетами с сервисом: сначала напрямую, затем через обход.</summary>
    public AsyncRelayCommand TestCommand { get; }

    /// <summary>Подбор приёма: перебор стратегий и создание профиля «auto».</summary>
    public AsyncRelayCommand AutoCommand { get; }

    /// <summary>Прервать идущий тест или подбор.</summary>
    public RelayCommand StopCommand { get; }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            Raise(nameof(CanSave));
            Raise(nameof(NotBusy));
            StopCommand.RaiseCanExecuteChanged();
            UpdateProfileCommand.RaiseCanExecuteChanged();
            TestCommand.RaiseCanExecuteChanged();
            AutoCommand.RaiseCanExecuteChanged();
            NewProfileCommand.RaiseCanExecuteChanged();
            RenameProfileCommand.RaiseCanExecuteChanged();
            DeleteProfileCommand.RaiseCanExecuteChanged();
            ExportProfileCommand.RaiseCanExecuteChanged();
            ImportProfileCommand.RaiseCanExecuteChanged();
            UseCommonCommand.RaiseCanExecuteChanged();
        }
    }

    public bool NotBusy => !_busy;

    /// <summary>Ход теста: «проверяем 3 из 18: fake TTL 8».</summary>
    public string ProgressText
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string StatusText
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) Raise(nameof(HasStatus));
        }
    }

    public bool HasStatus => _status.Length > 0;

    /// <summary>Домены сервиса одной строкой — так видно, что проверяем площадку целиком.</summary>
    private string HostListText
    {
        get
        {
            var list = DpiServiceCatalog.DomainList(ProbeHosts);
            return list.Count == 0 ? ProbeHosts.Trim() : DpiServiceCatalog.Join(list);
        }
    }

    /// <summary>
    /// Что проверяют «Тест» и «Подобрать». У сервиса — его домены (первый и служебный: картинки,
    /// вложения), у «Общих» — площадки, которые режет DPI: общий набор должен открывать их все.
    /// </summary>
    private string ProbeHosts => IsGeneral ? string.Join(", ", DpiTester.BlockedHosts) : Domain;

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    /// <summary>
    /// Собрать результат для сохранения. Домен приводится к виду «хост»; доменов может быть
    /// несколько — обход применяется к площадке целиком (страница и её служебные домены).
    /// </summary>
    public DpiServiceEditResult Build()
    {
        var service = _request.Service.Clone();
        service.Name = Name.Trim();
        service.Domain = DpiServiceCatalog.Join(DpiServiceCatalog.DomainList(Domain));
        service.ProfileId = _selectedProfile?.Id;

        // набор приёмов из формы — свои настройки сервиса: профиль остаётся шаблоном,
        // поэтому правки одного сервиса не меняют поведение остальных
        var settings = service.Settings.Clone();
        Strategy.ApplyTo(settings);
        ApplyTransport(settings);   // порт, режим выхода и системный прокси — только у «Общих»
        service.Settings = settings;

        // выбор DNS сервиса («Общие» хранят его в разделе, поэтому их выбор вернётся отдельно)
        service.Dns = _dns.Clone();

        return new DpiServiceEditResult(service, Profiles.Select(p => p.Model).ToList(), _pickDns);
    }

    /// <summary>
    /// Набор приёмов для «Теста» и «Подбора»: форма плюс выбранный для сервиса DNS — проверки
    /// идут теми же адресами, что и живой обход (см. <see cref="DpiServiceCatalog.ApplyDns"/>).
    /// </summary>
    private DpiBypassSettings ProbeSettings()
    {
        var settings = Strategy.Settings;
        DpiServiceCatalog.ApplyDns(settings, _dns, App.Settings.Current.Dns);
        return settings;
    }

    /// <summary>
    /// Транспортные поля «Общих» — они живут в блоке обхода (<c>settings.Dpi</c>), а не в профиле:
    /// порт, задержка, режим выхода и системный прокси. Набор приёмов применяет раздел «DPI».
    /// </summary>
    public void ApplyTransport(DpiBypassSettings target)
    {
        if (!IsGeneral) return;

        target.ListenPort = int.TryParse(Port.Trim(), out var port) && port is > 0 and < 65536 ? port : target.ListenPort;
        target.ExitMode = ExitProxy ? DpiExitMode.Proxy : DpiExitMode.Direct;
        target.UseSystemProxy = UseSystemProxy;
    }

    /// <summary>Окно закрывают — гасим незаконченный тест и движок под ним.</summary>
    public void Cancel() => StopTask();

    // ------------------------------------------------------------------ профили

    private void NewProfile()
    {
        var name = _dialogs.PromptText(L.T("dpi.profile.title"), L.T("dpi.profile.name"), L.T("dpi.profile.new"));
        if (string.IsNullOrWhiteSpace(name)) return;

        var profile = new DpiStrategyProfile
        {
            Name = name.Trim(),
            Note = "dpi.profile.customNote",
            Settings = Strategy.Settings
        };

        var vm = new DpiProfileVm(profile);
        Profiles.Insert(0, vm);
        SelectedProfile = vm;
        Notify(L.F("dpi.profile.created", profile.Name));
    }

    /// <summary>
    /// Набор приёмов из формы уходит в выбранный профиль: профиль правят один раз, а дальше им
    /// пользуются сервисы (проще, чем заводить новый профиль на каждую правку).
    /// </summary>
    private void UpdateProfile()
    {
        var profile = _selectedProfile;
        if (profile is null) return;

        profile.Model.Settings = Strategy.Settings;
        profile.Model.UpdatedAt = DateTime.UtcNow;
        profile.Refresh();
        Notify(L.F("dpi.profile.saved", profile.Name));
    }

    private void RenameProfile(DpiProfileVm? profile)
    {
        if (profile is null) return;

        var name = _dialogs.PromptText(L.T("dpi.profile.title"), L.T("S0027"), profile.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        profile.Name = name;
        profile.Refresh();
        RefreshUsage();
        Notify(L.F("dpi.profile.renamed", name));
    }

    private void DeleteProfile(DpiProfileVm? profile)
    {
        if (profile is null) return;
        if (!_dialogs.Confirm(L.T("dpi.profile.title"), L.F("dpi.profile.removeText", profile.Name), L.T("S0083")))
            return;

        Profiles.Remove(profile);
        if (ReferenceEquals(_selectedProfile, profile)) SelectedProfile = null;
        RefreshUsage();
        Notify(L.F("dpi.profile.removed", profile.Name));
    }

    private void ExportProfile(DpiProfileVm? profile)
    {
        profile ??= _selectedProfile;
        if (profile is null) return;

        var path = _dialogs.SaveFile("JSON (*.json)|*.json", profile.Name + ".json");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            Json.Write(path, profile.Model);
            Notify(L.F("dpi.profile.exported", Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            Notify(ex.Message, error: true);
        }
    }

    private void ImportProfile()
    {
        var path = _dialogs.OpenFile("JSON (*.json)|*.json");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var loaded = Json.Read<DpiStrategyProfile>(path);
            if (loaded is null)
            {
                Notify(L.T("dpi.profile.importFailed"), error: true);
                return;
            }

            loaded.Id = Guid.NewGuid().ToString("N");
            loaded.BuiltIn = false;
            loaded.NameKey = null;
            loaded.UpdatedAt = DateTime.UtcNow;
            loaded.Name = UniqueName(string.IsNullOrWhiteSpace(loaded.Name)
                ? Path.GetFileNameWithoutExtension(path)
                : loaded.Name);

            var vm = new DpiProfileVm(loaded);
            Profiles.Insert(0, vm);
            SelectedProfile = vm;
            Notify(L.F("dpi.profile.imported", loaded.Name));
        }
        catch (Exception ex)
        {
            Notify(ex.Message, error: true);
        }
    }

    private void UseCommon()
    {
        SelectedProfile = null;
        Notify(L.T("dpi.window.commonHint"));
    }

    /// <summary>Сколько сервисов пользуется профилем: считаем по настройкам раздела.</summary>
    private void RefreshUsage()
    {
        var section = App.Settings.Current.DpiServices;
        foreach (var profile in Profiles)
            profile.SetUsage(section.Services.Count(s => s.ProfileId == profile.Id));
    }

    private string UniqueName(string wanted)
    {
        var name = wanted.Trim();
        if (name.Length == 0) name = L.T("dpi.profile.new");

        for (var i = 2; Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{wanted} ({i})";
        return name;
    }

    private void Notify(string text, bool error = false)
    {
        StatusText = text;
        StatusIsError = error;
    }

    // -------------------------------------------------------------------- тест

    private async Task TestAsync()
    {
        if (!CheckBeforeRun()) return;

        Log.Add(L.T("dpi.log.separator"));
        Log.Add(L.F("dpi.log.testHeader", HostListText));

        await RunAsync(async ct =>
        {
            var result = await DpiServiceTester.TestAsync(ProbeHosts, ProbeSettings(), Log.Add, ct);
            Notify(result.Detail, error: !result.Answered);
        });
    }

    private async Task AutoAsync()
    {
        if (!CheckBeforeRun()) return;

        Log.Add(L.T("dpi.log.separator"));
        Log.Add(L.F("dpi.log.autoHeader", HostListText));

        await RunAsync(async ct =>
        {
            var result = await DpiServiceTester.AutoAsync(
                ProbeHosts,
                ProbeSettings(),
                Log.Add,
                (index, total, label) => ProgressText = L.F("dpi.progress", index, total, label),
                ct);

            if (result.Working.Count == 0)
            {
                Notify(result.Detail, error: true);
                return;
            }

            var host = DpiServiceCatalog.Primary(Domain);

            // Порядок — по времени ответа: первой пойдёт самая быстрая стратегия,
            // остальные останутся запасными на случай, если провайдер поменяет правила.
            var ordered = result.Working.OrderBy(w => w.Ms).ToList();
            var existing = AutoProfiles(host);

            var saved = new List<DpiProfileVm>();
            for (var i = 0; i < ordered.Count; i++)
            {
                var found = ordered[i];
                var name = DpiServiceCatalog.AutoName(i);

                DpiProfileVm profile;
                if (i < existing.Count)
                {
                    profile = existing[i];   // тот же номер стратегии — переписываем набор приёмов
                }
                else
                {
                    profile = new DpiProfileVm(new DpiStrategyProfile
                    {
                        Name = name,
                        Auto = true,
                        Domain = host,
                        Rank = i,
                        Note = "dpi.p.auto.note"
                    });
                    Profiles.Insert(Math.Min(i, Profiles.Count), profile);
                }

                profile.Model.Auto = true;
                profile.Model.BuiltIn = false;
                profile.Model.NameKey = null;
                profile.Model.Name = name;
                profile.Model.Domain = host;
                profile.Model.Rank = i;
                profile.Model.Note = "dpi.p.auto.note";
                profile.Model.Settings = found.Settings;
                profile.Model.UpdatedAt = DateTime.UtcNow;
                profile.Refresh();

                saved.Add(profile);
                Log.Add(L.F("dpi.log.autoSaved", profile.Name, found.Label, found.Ms));
            }

            // Наборы прошлого подбора, которым не нашлось места, остаются запасными:
            // они когда-то работали, но идут после проверенных сейчас.
            for (var i = saved.Count; i < existing.Count; i++)
            {
                existing[i].Model.Rank = 100 + i;
                existing[i].Refresh();
            }

            if (existing.Count > saved.Count)
                Log.Add(L.F("dpi.log.autoKept", existing.Count - saved.Count));

            // Первая (самая быстрая) стратегия становится основной у сервиса, а число
            // найденных задаёт, сколько разрезов движок пробует на одном соединении.
            var primary = ordered[0].Settings.Clone();
            primary.FailoverCount = Math.Clamp(saved.Count, 1, 4);

            SelectedProfile = saved[0];
            Strategy.Load(primary);
            RefreshUsage();
            Notify(result.Detail);
        });
    }

    /// <summary>Профили подбора, уже сохранённые для этого домена: по порядку применения.</summary>
    private List<DpiProfileVm> AutoProfiles(string host) =>
        Profiles
            .Where(p => p.Model.Auto &&
                        string.Equals(DpiServiceCatalog.NormalizeHost(p.Model.Domain), host, StringComparison.Ordinal))
            .OrderBy(p => p.Model.Rank)
            .ThenBy(p => p.Model.UpdatedAt)
            .ToList();

    /// <summary>Общая обвязка теста и подбора: Busy, кнопка «Стоп», журнал ошибок.</summary>
    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (Busy) return;

        Busy = true;
        ProgressText = "";
        StatusText = "";
        _task = new CancellationTokenSource();

        try
        {
            await work(_task.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Add(L.T("dpi.log.cancelled"));
            Notify(L.T("dpi.test.cancelled"), error: true);
        }
        catch (Exception ex)
        {
            Log.Add(L.T("dpi.log.error") + ex.Message);
            Notify(ex.Message, error: true);
        }
        finally
        {
            ProgressText = "";
            Busy = false;
            _task?.Dispose();
            _task = null;
        }
    }

    private void StopTask()
    {
        try { _task?.Cancel(); }
        catch { /* уже завершилось */ }
    }

    /// <summary>Перед запуском теста проверяем имя и домен — без домена мерить нечего.</summary>
    private bool CheckBeforeRun()
    {
        if (_name.Trim().Length == 0)
        {
            Notify(L.T("dpi.window.needName"), error: true);
            return false;
        }

        if (DomainProblem is not null)
        {
            Notify(DomainProblem, error: true);
            return false;
        }

        return true;
    }

    private void Validate()
    {
        // «Общие» — не сервис: домены у них не задаются, приёмы применяются ко всему трафику
        if (IsGeneral)
        {
            if (DomainProblem is null) { RefreshRelated(); return; }
            DomainProblem = null;
            Raise(nameof(DomainProblem));
            Raise(nameof(HasDomainProblem));
            Raise(nameof(CanSave));
            RefreshRelated();
            return;
        }

        // доменов может быть несколько: проверяем каждый
        var hosts = DpiServiceCatalog.DomainList(Domain);
        string? problem = null;

        if (Domain.Trim().Length == 0) problem = L.T("dpi.window.needDomain");
        else if (hosts.Count == 0 || hosts.Any(h => !h.Contains('.') || h.StartsWith('.') || h.EndsWith('.')))
            problem = L.T("dpi.window.badDomain");

        if (DomainProblem == problem) { RefreshRelated(); return; }

        DomainProblem = problem;
        Raise(nameof(DomainProblem));
        Raise(nameof(HasDomainProblem));
        Raise(nameof(CanSave));
        RefreshRelated();
    }

    /// <summary>Пересчитать, каких служебных доменов площадки не хватает в поле.</summary>
    private void RefreshRelated()
    {
        // у «Общих» доменов нет — подсказывать нечего
        var missing = IsGeneral ? new List<string>() : DpiServiceCatalog.RelatedMissing(Domain);
        var same = missing.Count == RelatedDomains.Count && !missing.Except(RelatedDomains).Any();

        if (!same)
        {
            RelatedDomains.Clear();
            foreach (var host in missing) RelatedDomains.Add(host);
        }

        Raise(nameof(HasRelatedDomains));
        Raise(nameof(RelatedText));
        AddRelatedCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>Дописать служебные домены площадки в поле «домен» (с сохранением хвоста).</summary>
    private void AddRelatedDomains()
    {
        var list = DpiServiceCatalog.DomainList(Domain);
        foreach (var host in RelatedDomains)
        {
            if (list.Any(h => string.Equals(h, host, StringComparison.Ordinal))) continue;
            list.Add(host);
        }

        Domain = DpiServiceCatalog.Join(list);
        Log.Add(L.F("dpi.log.relatedAdded", DpiServiceCatalog.Join(RelatedDomains)));
        RefreshRelated();
    }
}
