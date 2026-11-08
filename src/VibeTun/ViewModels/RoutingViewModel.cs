using System.Collections.ObjectModel;
using System.Windows.Threading;
using VibeTun.Models;
using VibeTun.Services;
using VibeTun.Utils;

namespace VibeTun.ViewModels;

/// <summary>Вкладка «Маршрутизация»: профили входа/TUN, наборы правил и приложения.</summary>
public sealed class RoutingViewModel : ObservableObject
{
    private readonly RouteStore _store;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _saveTimer;

    private RouteProfileItemVm? _selectedProfile;
    private RouteSetItemVm? _selectedSet;
    private string _newRulePattern = "";
    private MatchKind _newRuleKind = MatchKind.Domain;
    private RouteAction _newRuleAction = RouteAction.Proxy;
    private string _newAdBlockPattern = "";
    private MatchKind _newAdBlockKind = MatchKind.Domain;
    private string _appSearch = "";

    public RoutingViewModel(RouteStore store, IDialogService dialogs)
    {
        _store = store;
        _dialogs = dialogs;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Persist();
            RulesPersisted?.Invoke();
        };

        AddRuleCommand = new RelayCommand(_ => AddRule(), _ => !string.IsNullOrWhiteSpace(NewRulePattern));
        AddAdBlockRuleCommand = new RelayCommand(_ => AddAdBlockRule(), _ => !string.IsNullOrWhiteSpace(NewAdBlockPattern));
        RemoveRuleCommand = new RelayCommand(p => RemoveRule(p as RuleItemVm));
        AddAppFromExeCommand = new AsyncRelayCommand(AddAppFromExeAsync);
        AddAppFromProcessesCommand = new AsyncRelayCommand(AddAppsFromProcessesAsync);
        RemoveAppCommand = new RelayCommand(p => RemoveApp(p as AppRuleItemVm));
        DuplicateSetCommand = new RelayCommand(_ => DuplicateSet(), _ => SelectedSet is not null);
        NewSetCommand = new RelayCommand(_ => NewSet());
        DeleteSetCommand = new RelayCommand(_ => DeleteSet(), _ => SelectedSet is { BuiltIn: false });
        ImportSetCommand = new RelayCommand(_ => ImportSet());
        ExportSetCommand = new RelayCommand(_ => ExportSet(), _ => SelectedSet is not null);
        EnableTunCommand = new RelayCommand(_ => EnableTun());
    }

    public ObservableCollection<RouteProfileItemVm> Profiles { get; } = new();
    public ObservableCollection<RouteSetItemVm> Sets { get; } = new();
    public ObservableCollection<RuleItemVm> Rules { get; } = new();
    public ObservableCollection<RuleItemVm> AdBlockRules { get; } = new();
    public ObservableCollection<AppRuleItemVm> Apps { get; } = new();
    public ObservableCollection<AppRuleItemVm> FilteredApps { get; } = new();

    public event Action<string, bool>? Notify;
    public event Action? SettingsChanged;
    /// <summary>Наборы правил/приложений сохранены на диск. Подписчик (MainViewModel)
    /// переподключает ядро, если прокси активен, — чтобы правила вступили в силу сразу.</summary>
    public event Action? RulesPersisted;

    public RelayCommand AddRuleCommand { get; }
    public RelayCommand AddAdBlockRuleCommand { get; }
    public RelayCommand RemoveRuleCommand { get; }
    public AsyncRelayCommand AddAppFromExeCommand { get; }
    public AsyncRelayCommand AddAppFromProcessesCommand { get; }
    public RelayCommand RemoveAppCommand { get; }
    public RelayCommand DuplicateSetCommand { get; }
    public RelayCommand NewSetCommand { get; }
    public RelayCommand DeleteSetCommand { get; }
    public RelayCommand ImportSetCommand { get; }
    public RelayCommand ExportSetCommand { get; }
    public RelayCommand EnableTunCommand { get; }

    public RouteProfileItemVm? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            foreach (var p in Profiles) p.IsSelected = ReferenceEquals(p, value);
            Raise(nameof(ShowTunHint));
        }
    }

    public RouteSetItemVm? SelectedSet
    {
        get => _selectedSet;
        set
        {
            if (!SetProperty(ref _selectedSet, value)) return;
            // выбор набора живёт в профиле — иначе после перезапуска набор сбросится
            // на записанный в профиле (см. Load). Changed тянет за собой ScheduleSave.
            if (SelectedProfile is not null) SelectedProfile.RouteSetId = value?.Id;
            LoadSetContents();
            DuplicateSetCommand.RaiseCanExecuteChanged();
            DeleteSetCommand.RaiseCanExecuteChanged();
            ExportSetCommand.RaiseCanExecuteChanged();
        }
    }

    public string NewRulePattern
    {
        get => _newRulePattern;
        set
        {
            if (!SetProperty(ref _newRulePattern, value)) return;
            AddRuleCommand.RaiseCanExecuteChanged();

            // тип записи определяется автоматически по введённому тексту
            if (!string.IsNullOrWhiteSpace(value))
            {
                var (_, kind) = Text.NormalizeUserPattern(value);
                if (_newRuleKind != kind)
                {
                    _newRuleKind = kind;
                    Raise(nameof(NewRuleKind));
                    Raise(nameof(NewRuleKindLabel));
                }
            }
        }
    }

    public MatchKind NewRuleKind
    {
        get => _newRuleKind;
        private set
        {
            if (!SetProperty(ref _newRuleKind, value)) return;
            Raise(nameof(NewRuleKindLabel));
        }
    }

    public string NewRuleKindLabel => _newRuleKind switch
    {
        MatchKind.Domain => L.T("S0270"),
        MatchKind.Ip => "IP",
        MatchKind.Regex => "regex",
        MatchKind.Keyword => L.T("S0272"),
        _ => "—"
    };

    public string NewAdBlockPattern
    {
        get => _newAdBlockPattern;
        set
        {
            if (!SetProperty(ref _newAdBlockPattern, value)) return;
            AddAdBlockRuleCommand.RaiseCanExecuteChanged();

            // тип записи определяется автоматически по введённому тексту
            if (!string.IsNullOrWhiteSpace(value))
            {
                var (_, kind) = Text.NormalizeUserPattern(value);
                if (_newAdBlockKind != kind)
                {
                    _newAdBlockKind = kind;
                    Raise(nameof(NewAdBlockKind));
                    Raise(nameof(NewAdBlockKindLabel));
                }
            }
        }
    }

    public MatchKind NewAdBlockKind
    {
        get => _newAdBlockKind;
        private set
        {
            if (!SetProperty(ref _newAdBlockKind, value)) return;
            Raise(nameof(NewAdBlockKindLabel));
        }
    }

    public string NewAdBlockKindLabel => _newAdBlockKind switch
    {
        MatchKind.Domain => L.T("S0270"),
        MatchKind.Ip => "IP",
        MatchKind.Regex => "regex",
        MatchKind.Keyword => L.T("S0272"),
        _ => "—"
    };

    public RouteAction NewRuleAction
    {
        get => _newRuleAction;
        set
        {
            if (!SetProperty(ref _newRuleAction, value)) return;
            Raise(nameof(NewRuleIsProxy));
            Raise(nameof(NewRuleIsDirect));
            Raise(nameof(NewRuleIsBlock));
        }
    }

    public bool NewRuleIsProxy
    {
        get => _newRuleAction == RouteAction.Proxy;
        set
        {
            if (value) NewRuleAction = RouteAction.Proxy;
            else Raise(nameof(NewRuleIsProxy));
        }
    }

    public bool NewRuleIsDirect
    {
        get => _newRuleAction == RouteAction.Direct;
        set
        {
            if (value) NewRuleAction = RouteAction.Direct;
            else Raise(nameof(NewRuleIsDirect));
        }
    }

    public bool NewRuleIsBlock
    {
        get => _newRuleAction == RouteAction.Block;
        set
        {
            if (value) NewRuleAction = RouteAction.Block;
            else Raise(nameof(NewRuleIsBlock));
        }
    }

    public string AppSearch
    {
        get => _appSearch;
        set
        {
            if (SetProperty(ref _appSearch, value)) ApplyAppFilter();
        }
    }

    public bool ShowTunHint => Apps.Count > 0 && SelectedProfile?.TunEnabled == false;

    private string _routingTab = "domains";

    /// <summary>Открытая вкладка: «domains», «apps» или независимая «adblock».</summary>
    public string RoutingTab
    {
        get => _routingTab;
        set
        {
            if (!SetProperty(ref _routingTab, value)) return;
            Raise(nameof(IsDomainsTab));
            Raise(nameof(IsAppsTab));
            Raise(nameof(IsAdBlockTab));
        }
    }

    /// <summary>Привязка переключателя: выбрать можно только вкладку, снять выбор нельзя.</summary>
    public bool IsDomainsTab
    {
        get => _routingTab == "domains";
        set
        {
            if (value) RoutingTab = "domains";
            else Raise(nameof(IsDomainsTab));
        }
    }

    public bool IsAppsTab
    {
        get => _routingTab == "apps";
        set
        {
            if (value) RoutingTab = "apps";
            else Raise(nameof(IsAppsTab));
        }
    }

    public bool IsAdBlockTab
    {
        get => _routingTab == "adblock";
        set
        {
            if (value) RoutingTab = "adblock";
            else Raise(nameof(IsAdBlockTab));
        }
    }

    public string RulesCountText => L.F("S1050", Rules.Count);

    /// <summary>Счётчик вкладки «Приложения» — список глобальный, не относится к выбранному набору.</summary>
    public string AppsCountText => L.F("S1051", Apps.Count);

    // ------------------------------------------------------------------ загрузка
    private RouteSet? _adBlockSet;
    private RouteSet? _appsSet;

    public void Load()
    {
        _store.SeedBuiltInsIfNeeded();

        var storedSets = _store.LoadSets();
        var legacyAdBlockIds = storedSets
            .Where(set => set.BuiltIn && set.Name == RouteStore.SetAdBlock)
            .Select(set => set.Id)
            .ToHashSet(StringComparer.Ordinal);
        // Скрытое хранилище глобального списка приложений — в списке наборов не показываем.
        var appsIds = storedSets
            .Where(set => set.BuiltIn && set.Name == RouteStore.SetApps)
            .Select(set => set.Id)
            .ToHashSet(StringComparer.Ordinal);

        Sets.Clear();
        foreach (var set in storedSets.Where(set => !legacyAdBlockIds.Contains(set.Id) && !appsIds.Contains(set.Id)))
            Sets.Add(new RouteSetItemVm(set));

        // Старый встроенный набор становится скрытым хранилищем настроек отдельной функции.
        // Благодаря этому пользовательские правки рекламных правил не теряются при обновлении.
        _adBlockSet = storedSets.FirstOrDefault(set => legacyAdBlockIds.Contains(set.Id))
                      ?? RouteStore.AdBlockSet();
        AdBlockRules.Clear();
        foreach (var rule in _adBlockSet.Rules)
        {
            var vm = new RuleItemVm(rule);
            vm.Changed += ScheduleSave;
            AdBlockRules.Add(vm);
        }

        // Приложения глобальные, не привязаны к наборам: живут в скрытом служебном наборе.
        // Списки приложений внутри обычных наборов больше не используются — очищаем (начинаем с чистого листа).
        _appsSet = storedSets.FirstOrDefault(set => appsIds.Contains(set.Id))
                   ?? RouteStore.AppsSet();
        Apps.Clear();
        foreach (var app in _appsSet.Apps)
        {
            var vm = new AppRuleItemVm(app);
            vm.Changed += ScheduleSave;
            Apps.Add(vm);
        }
        foreach (var set in Sets.Where(s => s.Model.Apps.Count > 0))
        {
            set.Model.Apps.Clear();
            _store.Save(set.Model);
            set.Refresh();
        }

        Profiles.Clear();
        foreach (var profile in _store.LoadProfiles())
        {
            // Раньше блокировка была отдельным набором. Сохраняем её состояние при переходе
            // на независимый переключатель, а обычную маршрутизацию возвращаем к «Обходу РФ».
            if (profile.RouteSetId is { } routeSetId && legacyAdBlockIds.Contains(routeSetId))
            {
                profile.BlockAds = true;
                profile.RouteSetId = null;
            }
            var vm = new RouteProfileItemVm(profile);
            vm.Changed += ScheduleSave;
            Profiles.Add(vm);
        }

        _store.Normalize(Profiles.Select(p => p.Model).ToList(), Sets.Select(s => s.Model).ToList());

        var activeId = App.Settings.Current.ActiveRouteProfileId;
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == activeId) ?? Profiles.FirstOrDefault();
        SelectedSet = Sets.FirstOrDefault(s => s.Id == SelectedProfile?.RouteSetId) ?? Sets.FirstOrDefault();
        Raise(nameof(RulesCountText));
        Raise(nameof(ShowTunHint));
    }

    public RouteProfile ActiveProfile => SelectedProfile?.Model
        ?? Profiles.FirstOrDefault()?.Model
        ?? new RouteProfile();

    public RouteSet? ActiveSet => SelectedSet?.Model;

    public IReadOnlyList<RouteRule>? ActiveAdBlockRules => _adBlockSet?.Rules;

    /// <summary>Глобальный список приложений — вкладка «Приложения» не зависит от выбранного набора.</summary>
    public IReadOnlyList<AppRule>? ActiveApps => _appsSet?.Apps;

    private void LoadSetContents()
    {
        Rules.Clear();
        var set = SelectedSet?.Model;
        if (set is not null)
        {
            foreach (var rule in set.Rules)
            {
                var vm = new RuleItemVm(rule);
                vm.Changed += ScheduleSave;
                Rules.Add(vm);
            }
        }
        ApplyAppFilter();
        Raise(nameof(RulesCountText));
        Raise(nameof(ShowTunHint));
    }

    private void ApplyAppFilter()
    {
        FilteredApps.Clear();
        var q = _appSearch.Trim();
        foreach (var app in Apps)
        {
            if (q.Length == 0 ||
                app.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                app.ProcessName.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                app.Path.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                FilteredApps.Add(app);
        }
    }

    // --------------------------------------------------------------- сохранение
    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        Raise(nameof(RulesCountText));
        Raise(nameof(AppsCountText));
        Raise(nameof(ShowTunHint));
        SettingsChanged?.Invoke();
    }

    public void Persist()
    {
        if (SelectedSet is not null)
        {
            var set = SelectedSet.Model;
            set.Rules = Rules.Select(r => r.Model).ToList();
            _store.Save(set);
            SelectedSet.Refresh();
        }
        if (_adBlockSet is not null)
        {
            _adBlockSet.Rules = AdBlockRules.Select(rule => rule.Model).ToList();
            _store.Save(_adBlockSet);
        }
        if (_appsSet is not null)
        {
            _appsSet.Apps = Apps.Select(app => app.Model).ToList();
            _store.Save(_appsSet);
        }
        _store.SaveProfiles(Profiles.Select(p => p.Model).ToList());

        var settings = App.Settings.Current;
        settings.ActiveRouteProfileId = SelectedProfile?.Id;
        App.Settings.SaveSoon();
    }

    // -------------------------------------------------------------------- rules
    private void AddRule()
    {
        var pattern = NewRulePattern.Trim();
        if (pattern.Length == 0) return;

        var model = new RouteRule
        {
            Pattern = pattern,
            Kind = NewRuleKind,
            Action = NewRuleAction
        };
        var vm = new RuleItemVm(model);
        vm.Changed += ScheduleSave;

        Rules.Insert(0, vm);
        NewRulePattern = "";
        ScheduleSave();
    }

    private void AddAdBlockRule()
    {
        var pattern = NewAdBlockPattern.Trim();
        if (pattern.Length == 0) return;

        var model = new RouteRule
        {
            Pattern = pattern,
            Kind = NewAdBlockKind,
            Action = RouteAction.Block
        };
        var vm = new RuleItemVm(model);
        vm.Changed += ScheduleSave;

        AdBlockRules.Insert(0, vm);
        NewAdBlockPattern = "";
        ScheduleSave();
    }

    private void RemoveRule(RuleItemVm? rule)
    {
        if (rule is null) return;
        Rules.Remove(rule);
        ScheduleSave();
    }

    // --------------------------------------------------------------------- apps
    private Task AddAppFromExeAsync()
    {
        var path = _dialogs.OpenFile(L.T("S0665"));
        if (path is null) return Task.CompletedTask;
        AddAppPath(path);
        return Task.CompletedTask;
    }

    private Task AddAppsFromProcessesAsync()
    {
        var picked = _dialogs.PickApplications();
        foreach (var app in picked)
            AddAppPath(app.Path, app.Title);
        return Task.CompletedTask;
    }

    public void AddAppPath(string exePath, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return;
        if (Apps.Any(a => string.Equals(a.Path, exePath, StringComparison.OrdinalIgnoreCase))) return;

        var (processName, title) = ProcessScanner.Describe(exePath);
        var model = new AppRule
        {
            Name = displayName is { Length: > 0 } ? displayName! : title,
            Path = exePath,
            ProcessName = processName,
            Action = RouteAction.Proxy
        };
        var vm = new AppRuleItemVm(model);
        vm.Changed += ScheduleSave;
        Apps.Add(vm);
        ApplyAppFilter();
        ScheduleSave();
        Notify?.Invoke(L.F("S0820", vm.Name), false);
    }

    private void RemoveApp(AppRuleItemVm? app)
    {
        if (app is null) return;
        Apps.Remove(app);
        ApplyAppFilter();
        ScheduleSave();
    }

    // ---------------------------------------------------------------- наборы
    private void NewSet()
    {
        var name = _dialogs.PromptText(L.T("S0824"), L.T("S0027"), L.T("S0825") + (Sets.Count + 1));
        if (string.IsNullOrWhiteSpace(name)) return;

        var set = new RouteSet { Name = name!, Description = L.T("S0826") };
        _store.Save(set);
        var vm = new RouteSetItemVm(set);
        Sets.Add(vm);
        SelectedSet = vm;
        Notify?.Invoke(L.F("S0827", set.Name), false);
    }

    private void DuplicateSet()
    {
        if (SelectedSet is null) return;
        var name = _dialogs.PromptText(L.T("S0828"), L.T("S0027"), SelectedSet.Name + L.T("S0813"));
        if (string.IsNullOrWhiteSpace(name)) return;

        var copy = _store.Duplicate(SelectedSet.Model, name!);
        var vm = new RouteSetItemVm(copy);
        Sets.Add(vm);
        SelectedSet = vm;
        Notify?.Invoke(L.T("S0829"), false);
    }

    private void DeleteSet()
    {
        if (SelectedSet is null || SelectedSet.BuiltIn) return;
        if (!_dialogs.Confirm(L.T("S0830"), L.F("S0823", SelectedSet.Name), L.T("S0083"))) return;

        _store.Delete(SelectedSet.Model);
        Sets.Remove(SelectedSet);
        SelectedSet = Sets.FirstOrDefault();
        Notify?.Invoke(L.T("S0831"), false);
    }

    private void ImportSet()
    {
        var path = _dialogs.OpenFile(L.T("S0832"));
        if (path is null) return;
        try
        {
            var set = _store.Import(path);
            var vm = new RouteSetItemVm(set);
            Sets.Add(vm);
            SelectedSet = vm;
            Notify?.Invoke(L.F("S0833", set.Name), false);
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.T("S0834"), ex.Message);
        }
    }

    private void ExportSet()
    {
        if (SelectedSet is null) return;
        var path = _dialogs.SaveFile(L.T("S0835"), AppPaths.Slug(SelectedSet.Name) + ".json");
        if (path is null) return;
        try
        {
            _store.Export(SelectedSet.Model, path);
            Notify?.Invoke(L.T("S0836"), false);
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.T("S0837"), ex.Message);
        }
    }

    private void EnableTun()
    {
        if (SelectedProfile is null) return;
        SelectedProfile.TunEnabled = true;
        Raise(nameof(ShowTunHint));
        Notify?.Invoke(L.T("S0838"), false);
        ScheduleSave();
    }
}
