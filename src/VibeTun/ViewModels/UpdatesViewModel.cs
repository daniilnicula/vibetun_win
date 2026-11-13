using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using VibeTun.Models;
using VibeTun.Services;
using VibeTun.Utils;

namespace VibeTun.ViewModels;

/// <summary>Сравнение релиза с установленной текущей версией.</summary>
public enum ReleaseRelation
{
    Current,
    Newer,
    Older
}

/// <summary>Элемент списка релизов во вкладке «Обновление».</summary>
public sealed class ReleaseItemVm : ObservableObject
{
    private ReleaseRelation _relation;
    private bool _suppressBadges;

    public ReleaseItemVm(AppReleaseInfo release, ReleaseRelation relation)
    {
        Release = release;
        _relation = relation;
    }

    public AppReleaseInfo Release { get; }

    public string TagName => Release.TagName;

    public string Title => Release.Title;

    /// <summary>Короткая дата под названием в левом списке релизов.</summary>
    public string DateText => Release.PublishedAt.ToLocalTime().ToString("d MMMM yyyy", L.Culture);

    /// <summary>Полная дата под названием в правой части.</summary>
    public string DateFullText
    {
        get
        {
            var date = Release.PublishedAt.ToLocalTime().ToString(L.T("S0891"), L.Culture);
            return Release.AssetSize > 0 ? $"{date} · {Text.Bytes(Release.AssetSize)}" : date;
        }
    }

    /// <summary>Описание с GitHub — что изменилось.</summary>
    public string DescriptionText => Release.FormattedBody;

    /// <summary>Заглушка без версии («Ядро не установлено»): плашки статуса не показываем.</summary>
    public bool SuppressBadges
    {
        get => _suppressBadges;
        set => SetProperty(ref _suppressBadges, value);
    }

    public ReleaseRelation Relation
    {
        get => _relation;
        set
        {
            if (!SetProperty(ref _relation, value)) return;
            Raise(nameof(IsCurrent));
            Raise(nameof(IsNewer));
            Raise(nameof(IsOlder));
            Raise(nameof(BadgeText));
            Raise(nameof(HasBadge));
        }
    }

    public bool IsCurrent => _relation == ReleaseRelation.Current;
    public bool IsNewer => _relation == ReleaseRelation.Newer;
    public bool IsOlder => _relation == ReleaseRelation.Older;

    public string BadgeText => _relation switch
    {
        ReleaseRelation.Current => L.T("S0258"),
        ReleaseRelation.Newer => L.T("S0892"),
        _ => ""
    };

    public bool HasBadge => _relation != ReleaseRelation.Older;
}

/// <summary>
/// Вкладка «Обновление»: две секции — приложение (<see cref="TabApp"/>) и ядро sing-box-lx
/// (<see cref="TabCore"/>). У каждой секции своё состояние (список, выбор, текущая версия,
/// ошибка, пагинация), общий визуальный список перезаполняется при переключении вкладки.
/// </summary>
public sealed class UpdatesViewModel : ObservableObject
{
    public const string TabApp = "app";
    public const string TabCore = "core";

    /// <summary>Состояние одной секции (приложение / ядро).</summary>
    private sealed class SectionState
    {
        public List<ReleaseItemVm> Items { get; } = new();
        public ReleaseItemVm? Selected;
        public ReleaseItemVm? Current;
        public string? InstalledTag;
        public bool Loaded;
        public int NextOffset;
        public bool HasMore;
        public string Error = "";
        public bool Loading;
        public bool LoadingMore;
    }

    private readonly IDialogService _dialogs;
    private readonly CoreService _core;
    private readonly Func<Task> _stopProxy;
    private readonly Action _coreUpdated;

    private readonly SectionState _appSection = new();
    private readonly SectionState _coreSection = new();
    private string _tab = TabApp;
    private bool _switching;            // идёт перезаполнение общего списка — выбор не трогаем
    private ReleaseItemVm? _currentRelease;
    private ReleaseItemVm? _selectedRelease;
    private bool _isDownloading;
    private double _downloadProgress;
    private string _downloadStatusText = "";
    private CancellationTokenSource? _downloadCts;
    private bool _hasAppUpdate;
    private bool _hasCoreUpdate;
    private DispatcherTimer? _autoTimer;

    public UpdatesViewModel(IDialogService dialogs, CoreService core, Func<Task> stopProxy, Action coreUpdated)
    {
        _dialogs = dialogs;
        _core = core;
        _stopProxy = stopProxy;
        _coreUpdated = coreUpdated;

        // До первой загрузки с GitHub у секции приложения есть карточка текущей версии
        _appSection.Current = MakeInitialCurrent(forCore: false);

        RefreshCommand = new AsyncRelayCommand(() => LoadReleasesAsync(force: true), () => !IsLoading && !IsDownloading);
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !IsLoading && !IsDownloading);
        LoadMoreCommand = new RelayCommand(() => _ = LoadMoreAsync());
        ApplyReleaseCommand = new AsyncRelayCommand(ApplySelectedReleaseAsync, () => !IsDownloading && ShowActionButton);
        CancelDownloadCommand = new RelayCommand(CancelDownload, () => IsDownloading);
        OpenGitHubCommand = new RelayCommand(OpenGitHubReleases);
    }

    public ObservableCollection<ReleaseItemVm> Releases { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand CheckCommand { get; }
    public RelayCommand LoadMoreCommand { get; }
    public AsyncRelayCommand ApplyReleaseCommand { get; }
    public RelayCommand CancelDownloadCommand { get; }
    public RelayCommand OpenGitHubCommand { get; }

    public event Action<string, bool>? Notify;
    public event Action? RestartRequested;
    public event Action? ExitRequested;

    private SectionState Active => _tab == TabCore ? _coreSection : _appSection;

    // ------------------------------------------------------------------ вкладки
    /// <summary>Открытая секция: <see cref="TabApp"/> — приложение, <see cref="TabCore"/> — ядро sing-box-lx.</summary>
    public string Tab
    {
        get => _tab;
        set
        {
            if (value is not (TabApp or TabCore) || value == _tab || IsDownloading) return;
            SwitchSection(value);
        }
    }

    /// <summary>Привязки переключателя: выбрать можно только вкладку, снять выбор нельзя.</summary>
    public bool IsAppTab
    {
        get => _tab == TabApp;
        set
        {
            if (value) Tab = TabApp;
            else Raise();
        }
    }

    public bool IsCoreTab
    {
        get => _tab == TabCore;
        set
        {
            if (value) Tab = TabCore;
            else Raise();
        }
    }

    /// <summary>Заголовок левого списка: зависит от секции.</summary>
    public string ListCaption => _tab == TabCore ? L.T("S1092") : L.T("S0257");

    /// <summary>Текст кнопки действия: пока ядро не установлено — «Установить ядро».</summary>
    public string ActionText => _tab == TabCore && !_core.Info.Exists ? L.T("S0142") : L.T("S1098");

    private void SwitchSection(string value)
    {
        _tab = value;
        var section = Active;

        // секция ядра ещё не загружана — карточку текущей версии строим по свежему состоянию ядра
        if (!section.Loaded && ReferenceEquals(section, _coreSection))
            section.Current = MakeInitialCurrent(forCore: true);

        RefillVisible(section);

        Raise(nameof(Tab));
        Raise(nameof(IsAppTab));
        Raise(nameof(IsCoreTab));
        Raise(nameof(ListCaption));
        Raise(nameof(ActionText));
        RaiseVisibleState();
        RefreshCommand.RaiseCanExecuteChanged();
        CheckCommand.RaiseCanExecuteChanged();

        if (!section.Loaded) _ = EnsureLoadedAsync();
    }

    // ------------------------------------------------------------------ карточки
    /// <summary>Карточка текущей версии до первой загрузки (или когда версия не найдена в списке).</summary>
    private static ReleaseItemVm MakePlaceholder(string? installedTag, bool coreMissing, bool loading)
    {
        var release = new AppReleaseInfo
        {
            TagName = coreMissing ? "—" : installedTag ?? "",
            Name = coreMissing ? L.T("S0841") : installedTag ?? "",
            TargetCommitish = "",
            PublishedAt = DateTime.UtcNow,
            Body = coreMissing ? L.T("S0856") : loading ? L.T("S0893") : ""
        };
        var vm = new ReleaseItemVm(release, ReleaseRelation.Current);
        if (coreMissing) vm.SuppressBadges = true;
        return vm;
    }

    private ReleaseItemVm MakeInitialCurrent(bool forCore)
    {
        if (forCore)
        {
            return _core.Info.Exists
                ? MakePlaceholder(AppUpdateService.NormalizeVersion(_core.Info.VersionNumber),
                    coreMissing: false, loading: true)
                : MakePlaceholder(null, coreMissing: true, loading: false);
        }

        var tag = !string.IsNullOrWhiteSpace(App.Settings.Current.InstalledReleaseTag)
            ? App.Settings.Current.InstalledReleaseTag!
            : AppInfo.ReleaseTag;
        return MakePlaceholder(tag, coreMissing: false, loading: true);
    }

    /// <summary>Отношение релиза к установленному: null installedTag (нет ядра) — всё новее.</summary>
    private static ReleaseRelation RelationOf(string tag, string? installedTag)
    {
        if (installedTag is null) return ReleaseRelation.Newer;
        var cmp = AppUpdateService.CompareVersions(tag, installedTag);
        return cmp > 0 ? ReleaseRelation.Newer : cmp == 0 ? ReleaseRelation.Current : ReleaseRelation.Older;
    }

    private static ReleaseItemVm MakeVm(AppReleaseInfo rel, string? installedTag) =>
        new(rel, RelationOf(rel.TagName, installedTag));

    // ------------------------------------------------------------------ загрузка
    /// <summary>Подгрузить релизы активной секции при открытии вкладки, если ещё не загружены.</summary>
    public Task EnsureLoadedAsync()
    {
        var section = Active;
        if (section.Loaded) return Task.CompletedTask;
        return LoadReleasesAsync(force: false);
    }

    public async Task LoadReleasesAsync(bool force = false)
    {
        var section = Active;
        if (section.Loading || IsDownloading) return;
        if (!force && section.Loaded) return;

        var forCore = ReferenceEquals(section, _coreSection);
        SetLoading(section, true);
        SetError(section, "");
        try
        {
            var repo = forCore ? ConstantsService.CoreRepo : AppUpdateService.Repo;
            var fetched = await AppUpdateService.FetchReleasesPageAsync(repo, 0, 20, forCore: forCore);

            var installedTag = forCore
                ? (_core.Info.Exists ? AppUpdateService.NormalizeVersion(_core.Info.VersionNumber) : null)
                : AppUpdateService.ResolveInstalledTag(fetched);

            var selectedTagBefore = section.Selected?.TagName;

            ReleaseItemVm? resolvedCurrent = null;
            var vms = new List<ReleaseItemVm>(fetched.Count);
            foreach (var rel in fetched)
            {
                var vm = MakeVm(rel, installedTag);
                vms.Add(vm);
                if (resolvedCurrent is null && vm.IsCurrent) resolvedCurrent = vm;
            }

            section.Items.Clear();
            section.Items.AddRange(vms);
            section.InstalledTag = installedTag;
            section.Current = resolvedCurrent ??
                              MakePlaceholder(installedTag, coreMissing: forCore && installedTag is null, loading: false);
            section.NextOffset = fetched.Count;
            section.HasMore = fetched.Count == 20;
            section.Loaded = true;
            section.Selected = selectedTagBefore is null
                ? null
                : section.Items.FirstOrDefault(i =>
                    string.Equals(i.TagName, selectedTagBefore, StringComparison.OrdinalIgnoreCase));

            if (ReferenceEquals(section, Active)) RefillVisible(section);

            // Флаг для точки в навигации = свежайший стабильный релиз (первый в
            // загруженном списке — GitHub отдаёт релизы по дате создания) новее
            // установленного. Any(IsNewer) нельзя: «день в теге» может быть больше
            // релиз ложно зажигал обновление, хотя свежайший уже установлен.
            var latestStable = section.Items.FirstOrDefault(i => !i.Release.IsPrerelease);
            var hasNewer = latestStable is not null &&
                           (string.IsNullOrEmpty(section.InstalledTag) ||
                            AppUpdateService.CompareVersions(latestStable.TagName, section.InstalledTag) > 0);
            if (forCore) HasCoreUpdate = hasNewer;
            else HasAppUpdate = hasNewer;
            Raise(nameof(HasAnyUpdate));

            App.Settings.Current.LastUpdateCheck = DateTime.UtcNow;
            App.Settings.SaveSoon();

            if (force) Notify?.Invoke(L.F("S0894", section.Items.Count), false);
        }
        catch (Exception ex)
        {
            SetError(section, L.T("S0895") + ex.Message);

            // карточка всё ещё показывает «Загрузка описания…» — подменяем на текст ошибки
            var cur = section.Current;
            if (cur is not null && cur.DescriptionText.StartsWith(L.T("S0896"), StringComparison.Ordinal))
            {
                var old = cur.Release;
                section.Current = new ReleaseItemVm(new AppReleaseInfo
                {
                    TagName = old.TagName,
                    Name = cur.Title,
                    TargetCommitish = old.TargetCommitish,
                    PublishedAt = old.PublishedAt,
                    Body = L.T("S0897")
                }, ReleaseRelation.Current) { SuppressBadges = cur.SuppressBadges };
                if (ReferenceEquals(section, Active))
                {
                    Raise(nameof(CurrentRelease));
                    RaiseDisplayedState();
                }
            }

            if (force) Notify?.Invoke(section.Error, true);
        }
        finally
        {
            SetLoading(section, false);
        }
    }

    /// <summary>Проверить обновления обеих секций (точка в навигации) и перезагрузить активный список.</summary>
    private async Task CheckAsync()
    {
        await LightCheckAsync();
        await LoadReleasesAsync(force: true);
    }

    /// <summary>
    /// Лёгкая проверка по свежим релизам каждого репозитория: только флаги
    /// <see cref="HasAppUpdate"/>/<see cref="HasCoreUpdate"/>; без ядра флаг ядра всегда true.
    /// Пререлизы (rc/alpha) значок не зажигают — сравнение только со стабильным релизом.
    /// </summary>
    private async Task LightCheckAsync()
    {
        try
        {
            var list = await AppUpdateService.FetchReleasesPageAsync(AppUpdateService.Repo, 0, 5);
            var installed = AppUpdateService.ResolveInstalledTag(list);
            var latest = list.FirstOrDefault(r => !r.IsPrerelease);
            HasAppUpdate = latest is not null &&
                           AppUpdateService.CompareVersions(latest.TagName, installed) > 0;
        }
        catch
        {
            /* сеть недоступна — оставляем прежнее значение */
        }

        if (!_core.Info.Exists)
        {
            HasCoreUpdate = true;
        }
        else
        {
            try
            {
                var list = await AppUpdateService.FetchReleasesPageAsync(
                    ConstantsService.CoreRepo, 0, 5, forCore: true);
                var latest = list.FirstOrDefault(r => !r.IsPrerelease);
                HasCoreUpdate = latest is not null &&
                                AppUpdateService.CompareVersions(latest.TagName, _core.Info.VersionNumber) > 0;
            }
            catch
            {
                /* оставляем прежнее значение */
            }
        }

        Raise(nameof(HasAnyUpdate));
        App.Settings.Current.LastUpdateCheck = DateTime.UtcNow;
        App.Settings.SaveSoon();
    }

    /// <summary>Автопроверка при запуске (если включена) и каждые 30 минут.</summary>
    public void StartAutoChecks()
    {
        if (_autoTimer is not null) return;
        if (App.Settings.Current.CheckUpdatesOnStart) _ = LightCheckAsync();
        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _autoTimer.Tick += (_, _) => _ = LightCheckAsync();
        _autoTimer.Start();
    }

    /// <summary>Догрузка следующей порции релизов активной секции (вызывается прокруткой списка).</summary>
    private async Task LoadMoreAsync()
    {
        var section = Active;
        if (!section.Loaded || !section.HasMore || section.Loading || section.LoadingMore || IsDownloading)
            return;

        var forCore = ReferenceEquals(section, _coreSection);
        SetLoadingMore(section, true);
        try
        {
            var repo = forCore ? ConstantsService.CoreRepo : AppUpdateService.Repo;
            var fetched = await AppUpdateService.FetchReleasesPageAsync(
                repo, section.NextOffset, 10, forCore: forCore);

            var known = section.Items.Select(i => i.TagName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = new List<ReleaseItemVm>();
            foreach (var rel in fetched)
            {
                if (!known.Add(rel.TagName)) continue;
                var vm = MakeVm(rel, section.InstalledTag);
                section.Items.Add(vm);
                added.Add(vm);
            }

            if (ReferenceEquals(section, Active))
                foreach (var vm in added)
                    Releases.Add(vm);

            section.NextOffset += fetched.Count;
            section.HasMore = fetched.Count == 10;

            // как и при полной загрузке: флаг считает только свежайший стабильный
            // релиз против установленного (см. LoadReleasesAsync — Any(IsNewer)
            // зажигал точку из-за старых релизов с большим «днём» в теге)
            var latestStable = section.Items.FirstOrDefault(i => !i.Release.IsPrerelease);
            var anyNewer = latestStable is not null &&
                           (string.IsNullOrEmpty(section.InstalledTag) ||
                            AppUpdateService.CompareVersions(latestStable.TagName, section.InstalledTag) > 0);
            if (forCore) HasCoreUpdate = anyNewer;
            else HasAppUpdate = anyNewer;
            Raise(nameof(HasAnyUpdate));
        }
        catch (Exception ex)
        {
            Notify?.Invoke(L.T("S0895") + ex.Message, true);
        }
        finally
        {
            SetLoadingMore(section, false);
        }
    }

    // ------------------------------------------------------------------ состояние
    /// <summary>Перезаполнить общий список из состояния секции (выбор восстанавливается из секции).</summary>
    private void RefillVisible(SectionState section)
    {
        _switching = true;
        try
        {
            Releases.Clear();
            foreach (var vm in section.Items) Releases.Add(vm);
            _selectedRelease = section.Selected;
            _currentRelease = section.Current;
        }
        finally
        {
            _switching = false;
        }
        Raise(nameof(SelectedRelease));
        Raise(nameof(CurrentRelease));
        Raise(nameof(IsEmpty));
    }

    private void RaiseVisibleState()
    {
        Raise(nameof(SelectedRelease));
        Raise(nameof(CurrentRelease));
        Raise(nameof(IsEmpty));
        Raise(nameof(ErrorText));
        Raise(nameof(HasError));
        Raise(nameof(IsLoading));
        Raise(nameof(IsLoadingMore));
        RaiseDisplayedState();
    }

    private void SetError(SectionState section, string value)
    {
        section.Error = value;
        if (!ReferenceEquals(section, Active)) return;
        Raise(nameof(ErrorText));
        Raise(nameof(HasError));
    }

    private void SetLoading(SectionState section, bool value)
    {
        if (section.Loading == value) return;
        section.Loading = value;
        if (!ReferenceEquals(section, Active)) return;
        Raise(nameof(IsLoading));
        Raise(nameof(IsEmpty));
        RefreshCommand.RaiseCanExecuteChanged();
        CheckCommand.RaiseCanExecuteChanged();
    }

    private void SetLoadingMore(SectionState section, bool value)
    {
        if (section.LoadingMore == value) return;
        section.LoadingMore = value;
        if (ReferenceEquals(section, Active)) Raise(nameof(IsLoadingMore));
    }

    /// <summary>Релиз, выбранный пользователем в левом списке (по умолчанию null — справа текущая версия).</summary>
    public ReleaseItemVm? SelectedRelease
    {
        get => _selectedRelease;
        set
        {
            if (_switching || IsDownloading) return;   // перезаполнение списка и скачивание выбор не меняют
            if (!SetProperty(ref _selectedRelease, value)) return;
            Active.Selected = value;
            RaiseDisplayedState();
        }
    }

    /// <summary>Текущая установленная версия активной секции.</summary>
    public ReleaseItemVm? CurrentRelease => _currentRelease;

    /// <summary>Какой релиз показывать справа: выбранный, иначе текущая версия.</summary>
    public ReleaseItemVm? DisplayedRelease => _selectedRelease ?? _currentRelease;

    public bool HasDisplayedRelease => DisplayedRelease is not null;

    /// <summary>Выбран релиз новее текущего — кнопка «Обновить».</summary>
    public bool CanUpdate => DisplayedRelease is { IsNewer: true };

    /// <summary>Выбран релиз старее текущего — кнопка «Откатить».</summary>
    public bool CanRollback => DisplayedRelease is { IsOlder: true };

    /// <summary>Показывать ли кнопку действия снизу (для текущей версии кнопка скрыта).</summary>
    public bool ShowActionButton => CanUpdate || CanRollback;

    /// <summary>Плашка «Текущая версия» справа (у заглушки «Ядро не установлено» её нет).</summary>
    public bool IsCurrentDisplayed => DisplayedRelease is { IsCurrent: true, SuppressBadges: false };

    public bool IsLoading => Active.Loading;

    public bool IsLoadingMore => Active.LoadingMore;

    public bool IsEmpty => !IsLoading && Releases.Count == 0;

    public string ErrorText => Active.Error;

    public bool HasError => !string.IsNullOrWhiteSpace(Active.Error);

    // ------------------------------------------------------------------ флаги обновлений
    public bool HasAppUpdate
    {
        get => _hasAppUpdate;
        private set
        {
            if (SetProperty(ref _hasAppUpdate, value)) Raise(nameof(HasAnyUpdate));
        }
    }

    public bool HasCoreUpdate
    {
        get => _hasCoreUpdate;
        private set
        {
            if (SetProperty(ref _hasCoreUpdate, value)) Raise(nameof(HasAnyUpdate));
        }
    }

    /// <summary>Хотя бы в одной секции есть новее — пульсирующая точка в навигации.</summary>
    public bool HasAnyUpdate => _hasAppUpdate || _hasCoreUpdate;

    // ------------------------------------------------------------------ скачивание
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (!SetProperty(ref _isDownloading, value)) return;
            RefreshCommand.RaiseCanExecuteChanged();
            CheckCommand.RaiseCanExecuteChanged();
            ApplyReleaseCommand.RaiseCanExecuteChanged();
            CancelDownloadCommand.RaiseCanExecuteChanged();
        }
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            if (!SetProperty(ref _downloadProgress, value)) return;
            Raise(nameof(DownloadPercentText));
        }
    }

    public string DownloadPercentText => $"{DownloadProgress:0}%";

    public string DownloadStatusText
    {
        get => _downloadStatusText;
        private set => SetProperty(ref _downloadStatusText, value);
    }

    private async Task ApplySelectedReleaseAsync()
    {
        var target = DisplayedRelease;
        if (target is null || target.IsCurrent || IsDownloading) return;

        if (Tab == TabCore)
        {
            await InstallCoreReleaseAsync(target);
            return;
        }

        await InstallAppReleaseAsync(target);
    }

    // ------------------------------------------------------------------ обновление ядра
    /// <summary>
    /// Установка/откат ядра из секции «sing-box-lx»: при запущенном прокси сначала останавливаем
    /// ядро (с подтверждением), затем архив ставится через <see cref="CoreInstaller"/> — как в настройках.
    /// </summary>
    private async Task InstallCoreReleaseAsync(ReleaseItemVm target)
    {
        var verb = target.IsOlder ? L.T("Extra960") : L.T("Extra961");

        if (_core.IsRunning)
        {
            if (!_dialogs.Confirm(L.T("S1097"), L.T("S1093"), L.T("S0230"))) return;
            try { await _stopProxy(); } catch { /* не критично: проверяем состояние ниже */ }

            // StopProxyAsync может быть заблокирован (Busy) — ждём фактической остановки, но не дольше 10 с
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (_core.State is (ConnectionState.Running or ConnectionState.Starting or ConnectionState.Stopping) &&
                   DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }

            if (_core.State is (ConnectionState.Running or ConnectionState.Starting or ConnectionState.Stopping))
            {
                SetError(Active, L.T("S1095"));
                Notify?.Invoke(L.T("S1095"), true);
                return;
            }
        }

        _downloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadStatusText = L.F("S0900", target.Title);
        SetError(Active, "");
        var reloadNeeded = false;

        try
        {
            var status = new Progress<string>(s => DownloadStatusText = s);
            var percent = new Progress<double>(p => DownloadProgress = p);
            DownloadStatusText = L.F("S0288", target.Release.AssetName);

            var info = await CoreInstaller.InstallFromUrlAsync(
                _core,
                target.Release.DownloadUrl,
                status,
                cts.Token,
                App.Settings.Current.CoreFlavor,
                new CoreSourceInfo
                {
                    Kind = CoreSourceInfo.KindGitHub,
                    Repo = ConstantsService.CoreRepo,
                    Tag = target.TagName,
                    Asset = target.Release.AssetName
                },
                target.Release.AssetSha256,
                percent);

            if (info is not { Exists: true }) throw new InvalidOperationException(L.T("S0848"));

            // как в SettingsViewModel.InstallCoreAsync: путь/версия в настройки, точные пробы, обновить UI
            App.Settings.Current.CorePath = info.Path;
            App.Settings.Current.CoreVersion = info.Version;
            App.Settings.SaveSoon();
            await _core.ProbeAmneziaSupportAsync(force: true);
            _ = _core.ProbeOutboundTypesAsync();
            _coreUpdated();
            Raise(nameof(ActionText));

            Notify?.Invoke(L.F("S1094", info.Version), false);
            reloadNeeded = true;
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText = "";
            DownloadProgress = 0;
            Notify?.Invoke(L.T("S0905"), false);
        }
        catch (Exception ex)
        {
            DownloadStatusText = "";
            DownloadProgress = 0;
            SetError(Active, L.F("S0906", verb, ex.Message));
            Notify?.Invoke(ErrorText, true);
            _dialogs.Error(L.T("S0847"), ex.Message + "\n\n" + CoreInstaller.ManualHint);
        }
        finally
        {
            IsDownloading = false;
            if (reloadNeeded)
            {
                _coreSection.Loaded = false;
                if (ReferenceEquals(Active, _coreSection)) _ = LoadReleasesAsync(force: true);
            }
        }
    }

    // ------------------------------------------------------------------ обновление приложения
    private async Task InstallAppReleaseAsync(ReleaseItemVm target)
    {
        var isRollback = target.IsOlder;
        var actionVerb = isRollback ? L.T("S0898") : L.T("S0899");

        _downloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadStatusText = L.F("S0900", target.Title);
        SetError(Active, "");

        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            var (exePath, sha256) = await AppUpdateService.DownloadReleaseAsync(
                target.Release,
                (percent, received, total) =>
                {
                    void UpdateUi()
                    {
                        DownloadProgress = percent;
                        DownloadStatusText = total > 0
                            ? L.F("S0901", target.Title, Text.Bytes(received), Text.Bytes(total))
                            : L.F("S0902", target.Title, Text.Bytes(received));
                    }

                    if (dispatcher is not null && !dispatcher.CheckAccess())
                        dispatcher.BeginInvoke((Action)UpdateUi);
                    else
                        UpdateUi();
                },
                cts.Token);

            DownloadProgress = 100;
            DownloadStatusText = L.F("S0903", actionVerb, target.Title);
            Notify?.Invoke(L.F("S0904", actionVerb, target.Title), false);

            await Task.Delay(250, cts.Token);

            AppUpdateService.ApplyUpdateAndRestart(
                exePath,
                target.Release,
                sha256,
                () => RestartRequested?.Invoke(),
                () => ExitRequested?.Invoke());
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText = "";
            DownloadProgress = 0;
            Notify?.Invoke(L.T("S0905"), false);
        }
        catch (Exception ex)
        {
            DownloadStatusText = "";
            DownloadProgress = 0;
            SetError(Active, L.F("S0906", isRollback ? L.T("Extra960") : L.T("Extra961"), ex.Message));
            Notify?.Invoke(ErrorText, true);
            _dialogs.Error(isRollback ? L.T("S0907") : L.T("S0908"), ex.Message);
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private void CancelDownload()
    {
        try { _downloadCts?.Cancel(); } catch { /* ignore */ }
    }

    private void OpenGitHubReleases()
    {
        var repo = Tab == TabCore ? ConstantsService.CoreRepo : AppUpdateService.Repo;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://github.com/{repo}/releases",
                UseShellExecute = true
            });
        }
        catch { /* ignore */ }
    }

    private void RaiseDisplayedState()
    {
        Raise(nameof(DisplayedRelease));
        Raise(nameof(HasDisplayedRelease));
        Raise(nameof(CanUpdate));
        Raise(nameof(CanRollback));
        Raise(nameof(ShowActionButton));
        Raise(nameof(IsCurrentDisplayed));
        ApplyReleaseCommand.RaiseCanExecuteChanged();
    }
}
