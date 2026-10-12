using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using GoreBox.Services;
using GoreBox.Utils;

namespace GoreBox.ViewModels;

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
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");

    private ReleaseRelation _relation;

    public ReleaseItemVm(AppReleaseInfo release, ReleaseRelation relation)
    {
        Release = release;
        _relation = relation;
    }

    public AppReleaseInfo Release { get; }

    public string TagName => Release.TagName;

    public string Title => Release.Title;

    /// <summary>Короткая дата под названием в левом списке релизов.</summary>
    public string DateText => Release.PublishedAt.ToLocalTime().ToString("d MMMM yyyy", RuCulture);

    /// <summary>Полная дата под названием в правой части.</summary>
    public string DateFullText
    {
        get
        {
            var date = Release.PublishedAt.ToLocalTime().ToString("d MMMM yyyy г., HH:mm", RuCulture);
            return Release.AssetSize > 0 ? $"{date} · {Text.Bytes(Release.AssetSize)}" : date;
        }
    }

    /// <summary>Описание с GitHub — что изменилось.</summary>
    public string DescriptionText => Release.FormattedBody;

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
        ReleaseRelation.Current => "Текущая",
        ReleaseRelation.Newer => "Новее",
        _ => ""
    };

    public bool HasBadge => _relation != ReleaseRelation.Older;
}

/// <summary>Вкладка «Обновление»: список релизов под Windows, описание изменений, скачивание и обновление/откат.</summary>
public sealed class UpdatesViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;

    private ReleaseItemVm? _currentRelease;
    private ReleaseItemVm? _selectedRelease;
    private bool _isLoading;
    private bool _loadedOnce;
    private string _errorText = "";
    private bool _isDownloading;
    private double _downloadProgress;
    private string _downloadStatusText = "";
    private CancellationTokenSource? _downloadCts;

    public UpdatesViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;

        // До первой загрузки с GitHub создаём карточку текущей версии по умолчанию,
        // чтобы правая часть сразу показывала текущую версию
        var initialTag = !string.IsNullOrWhiteSpace(App.Settings.Current.InstalledReleaseTag)
            ? App.Settings.Current.InstalledReleaseTag!
            : AppInfo.ReleaseTag;

        _currentRelease = new ReleaseItemVm(new AppReleaseInfo
        {
            TagName = initialTag,
            Name = initialTag,
            TargetCommitish = "Windows",
            PublishedAt = DateTime.UtcNow,
            Body = "Загрузка описания изменений с GitHub…"
        }, ReleaseRelation.Current);

        RefreshCommand = new AsyncRelayCommand(() => LoadReleasesAsync(force: true), () => !IsLoading && !IsDownloading);
        ApplyReleaseCommand = new AsyncRelayCommand(ApplySelectedReleaseAsync, () => !IsDownloading && ShowActionButton);
        CancelDownloadCommand = new RelayCommand(CancelDownload, () => IsDownloading);
        OpenGitHubCommand = new RelayCommand(OpenGitHubReleases);
    }

    public ObservableCollection<ReleaseItemVm> Releases { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ApplyReleaseCommand { get; }
    public RelayCommand CancelDownloadCommand { get; }
    public RelayCommand OpenGitHubCommand { get; }

    public event Action<string, bool>? Notify;
    public event Action? RestartRequested;
    public event Action? ExitRequested;

    /// <summary>Текущая установленная версия приложения.</summary>
    public ReleaseItemVm? CurrentRelease
    {
        get => _currentRelease;
        private set
        {
            if (!SetProperty(ref _currentRelease, value)) return;
            RaiseDisplayedState();
        }
    }

    /// <summary>
    /// Релиз, выбранный пользователем в левом списке.
    /// По умолчанию null (пока пользователь не нажал ни на один релиз — справа показывается <see cref="CurrentRelease"/>).
    /// </summary>
    public ReleaseItemVm? SelectedRelease
    {
        get => _selectedRelease;
        set
        {
            if (IsDownloading) return;   // во время скачивания выбор не меняем
            if (!SetProperty(ref _selectedRelease, value)) return;
            RaiseDisplayedState();
        }
    }

    /// <summary>
    /// Какой релиз показывать в правой части: выбранный в списке, а если ничего не нажимали — текущую версию.
    /// </summary>
    public ReleaseItemVm? DisplayedRelease => _selectedRelease ?? _currentRelease;

    public bool HasDisplayedRelease => DisplayedRelease is not null;

    /// <summary>Выбран релиз новее текущего — показываем кнопку «Обновить» в стиле приложения.</summary>
    public bool CanUpdate => DisplayedRelease is { IsNewer: true };

    /// <summary>Выбран релиз старее текущего — показываем оранжевую кнопку «Откатить».</summary>
    public bool CanRollback => DisplayedRelease is { IsOlder: true };

    /// <summary>Показывать ли кнопку действия снизу (для текущей версии кнопка скрыта).</summary>
    public bool ShowActionButton => CanUpdate || CanRollback;

    public bool IsCurrentDisplayed => DisplayedRelease is { IsCurrent: true };

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value)) return;
            Raise(nameof(IsEmpty));
            RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsEmpty => !IsLoading && Releases.Count == 0;

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (!SetProperty(ref _errorText, value)) return;
            Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorText);

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (!SetProperty(ref _isDownloading, value)) return;
            RefreshCommand.RaiseCanExecuteChanged();
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

    /// <summary>Подгрузить релизы при открытии вкладки, если ещё не загружены.</summary>
    public Task EnsureLoadedAsync()
    {
        if (_loadedOnce && Releases.Count > 0) return Task.CompletedTask;
        return LoadReleasesAsync(force: false);
    }

    public async Task LoadReleasesAsync(bool force = false)
    {
        if (IsLoading || IsDownloading) return;
        if (!force && _loadedOnce && Releases.Count > 0) return;

        IsLoading = true;
        ErrorText = "";
        try
        {
            var fetched = await AppUpdateService.FetchWindowsReleasesAsync();
            var currentInfo = AppUpdateService.ResolveCurrentRelease(fetched);

            var selectedTagBefore = _selectedRelease?.TagName;

            Releases.Clear();
            var currentIndex = currentInfo is null
                ? -1
                : fetched.FindIndex(r => string.Equals(r.TagName, currentInfo.TagName, StringComparison.OrdinalIgnoreCase));

            ReleaseItemVm? resolvedCurrentVm = null;
            ReleaseItemVm? restoredSelectionVm = null;

            for (var i = 0; i < fetched.Count; i++)
            {
                var rel = fetched[i];
                ReleaseRelation relation;
                if (currentIndex >= 0)
                {
                    relation = i == currentIndex
                        ? ReleaseRelation.Current
                        : i < currentIndex
                            ? ReleaseRelation.Newer
                            : ReleaseRelation.Older;
                }
                else
                {
                    relation = i == 0 ? ReleaseRelation.Current : ReleaseRelation.Older;
                }

                var vm = new ReleaseItemVm(rel, relation);
                Releases.Add(vm);

                if (relation == ReleaseRelation.Current)
                    resolvedCurrentVm = vm;

                if (!string.IsNullOrWhiteSpace(selectedTagBefore) &&
                    string.Equals(rel.TagName, selectedTagBefore, StringComparison.OrdinalIgnoreCase))
                {
                    restoredSelectionVm = vm;
                }
            }

            _currentRelease = resolvedCurrentVm ?? Releases.FirstOrDefault() ?? _currentRelease;
            _selectedRelease = restoredSelectionVm;   // если пользователь ничего не выбирал, остаётся null -> справа текущая версия
            _loadedOnce = true;

            App.Settings.Current.LastUpdateCheck = DateTime.UtcNow;
            App.Settings.SaveSoon();

            Raise(nameof(CurrentRelease));
            Raise(nameof(SelectedRelease));
            Raise(nameof(IsEmpty));
            RaiseDisplayedState();

            if (force)
                Notify?.Invoke($"Список релизов обновлён ({Releases.Count})", false);
        }
        catch (Exception ex)
        {
            ErrorText = "Не удалось загрузить релизы с GitHub: " + ex.Message;
            if (_currentRelease is not null && _currentRelease.DescriptionText.StartsWith("Загрузка", StringComparison.Ordinal))
            {
                _currentRelease = new ReleaseItemVm(new AppReleaseInfo
                {
                    TagName = _currentRelease.TagName,
                    Name = _currentRelease.Title,
                    TargetCommitish = "Windows",
                    PublishedAt = _currentRelease.Release.PublishedAt,
                    Body = "Не удалось загрузить описание изменений с GitHub. Проверьте подключение к интернету и нажмите «Обновить список»."
                }, ReleaseRelation.Current);
                Raise(nameof(CurrentRelease));
                RaiseDisplayedState();
            }

            if (force)
                Notify?.Invoke(ErrorText, true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ApplySelectedReleaseAsync()
    {
        var target = DisplayedRelease;
        if (target is null || target.IsCurrent || IsDownloading) return;

        var isRollback = target.IsOlder;
        var actionVerb = isRollback ? "Откат до" : "Обновление до";

        _downloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadStatusText = $"Подготовка к скачиванию {target.Title}…";
        ErrorText = "";

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
                            ? $"Скачивание {target.Title}… {Text.Bytes(received)} из {Text.Bytes(total)}"
                            : $"Скачивание {target.Title}… {Text.Bytes(received)}";
                    }

                    if (dispatcher is not null && !dispatcher.CheckAccess())
                        dispatcher.BeginInvoke((Action)UpdateUi);
                    else
                        UpdateUi();
                },
                cts.Token);

            DownloadProgress = 100;
            DownloadStatusText = $"{actionVerb} {target.Title}: замена файла и перезапуск…";
            Notify?.Invoke($"{actionVerb} {target.Title} — перезапуск приложения…", false);

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
            Notify?.Invoke("Скачивание отменено", false);
        }
        catch (Exception ex)
        {
            DownloadStatusText = "";
            DownloadProgress = 0;
            ErrorText = $"Не удалось выполнить {(isRollback ? "откат" : "обновление")}: {ex.Message}";
            Notify?.Invoke(ErrorText, true);
            _dialogs.Error(isRollback ? "Ошибка отката" : "Ошибка обновления", ex.Message);
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

    private static void OpenGitHubReleases()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://github.com/{AppUpdateService.Repo}/releases",
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
