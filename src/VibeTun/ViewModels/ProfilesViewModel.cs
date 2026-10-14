using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using VibeTun.Models;
using VibeTun.Services;
using VibeTun.Utils;

namespace VibeTun.ViewModels;

/// <summary>Список профилей: добавление, редактирование, пинг, подключение.</summary>
public sealed class ProfilesViewModel : ObservableObject
{
    private readonly ProfileStore _store;
    private readonly LatencyService _latency;
    private readonly IDialogService _dialogs;
    private string _searchText = "";
    private ProfileItemVm? _selected;
    private readonly SemaphoreSlim _pingGate = new(8);
    private readonly Dictionary<string, Task> _pendingPings = new();

    public ProfilesViewModel(ProfileStore store, LatencyService latency, IDialogService dialogs, LogFeed log)
    {
        _store = store;
        _latency = latency;
        _dialogs = dialogs;
        Log = log;
        ClearLogCommand = new RelayCommand(_ => Log.Clear());

        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Filter;
        View.SortDescriptions.Add(new SortDescription(nameof(ProfileItemVm.Favorite), ListSortDirection.Descending));
        View.SortDescriptions.Add(new SortDescription(nameof(ProfileItemVm.Name), ListSortDirection.Ascending));

        AddCommand = new AsyncRelayCommand(() => AddAsync());
        AddFromClipboardCommand = new AsyncRelayCommand(() => AddFromClipboardAsync());
        ImportFileCommand = new AsyncRelayCommand(ImportFromFileAsync);
        EditCommand = new RelayCommand(p => Edit(p as ProfileItemVm ?? Selected),
            p => (p as ProfileItemVm ?? Selected) is not null);
        RemoveCommand = new RelayCommand(p => Remove(p as ProfileItemVm ?? Selected),
            p => (p as ProfileItemVm ?? Selected) is not null);
        DuplicateCommand = new RelayCommand(_ => Duplicate(), _ => Selected is not null);
        PingCommand = new AsyncRelayCommand(async p => await PingAsync(p as ProfileItemVm ?? Selected), _ => Selected is not null);
        PingAllCommand = new AsyncRelayCommand(PingAllAsync, () => Items.Count > 0);
        ConnectCommand = new RelayCommand(p => Connect(p as ProfileItemVm ?? Selected), _ => Selected is not null);
        ToggleFavoriteCommand = new RelayCommand(p => ToggleFavorite(p as ProfileItemVm ?? Selected));
        CopyLinkCommand = new RelayCommand(p => CopyLink(p as ProfileItemVm ?? Selected), _ => Selected is not null);
        CreateManualCommand = new RelayCommand(p => CreateManual(p as string));
    }

    public ObservableCollection<ProfileItemVm> Items { get; } = new();

    public ICollectionView View { get; }

    /// <summary>Общий журнал: панель «Логи» на вкладке «Профили».</summary>
    public LogFeed Log { get; }

    public RelayCommand ClearLogCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) View.Refresh();
        }
    }

    public ProfileItemVm? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                EditCommand.RaiseCanExecuteChanged();
                RemoveCommand.RaiseCanExecuteChanged();
                DuplicateCommand.RaiseCanExecuteChanged();
                PingCommand.RaiseCanExecuteChanged();
                ConnectCommand.RaiseCanExecuteChanged();
                CopyLinkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsEmpty => Items.Count == 0;

    public event Action<ProxyProfile>? ConnectRequested;
    public event Action<ProxyProfile, int>? PingUpdated;
    public event Action<string, bool>? Notify;   // (текст, ошибка?)

    public RelayCommand EditCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public AsyncRelayCommand PingCommand { get; }
    public AsyncRelayCommand PingAllCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand CopyLinkCommand { get; }
    public AsyncRelayCommand AddCommand { get; }
    public AsyncRelayCommand AddFromClipboardCommand { get; }
    public AsyncRelayCommand ImportFileCommand { get; }

    /// <summary>Создание профиля вручную: выбор протокола из подменю ПКМ.</summary>
    public RelayCommand CreateManualCommand { get; }

    // ------------------------------------------------------------------ загрузка
    public void Load()
    {
        Items.Clear();
        foreach (var profile in _store.LoadAll())
            Items.Add(new ProfileItemVm(profile));
        Raise(nameof(IsEmpty));
        PingAllCommand.RaiseCanExecuteChanged();
    }

    private bool Filter(object item)
    {
        if (item is not ProfileItemVm vm) return false;
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        var q = _searchText.Trim();
        return vm.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || vm.Address.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || vm.ProtocolLabel.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    // -------------------------------------------------------------------- CRUD
    private Task AddAsync()
    {
        var parsed = _dialogs.ImportProfiles();
        AddParsed(parsed);
        return Task.CompletedTask;
    }

    private Task AddFromClipboardAsync()
    {
        string text;
        try { text = System.Windows.Clipboard.GetText(); }
        catch { text = ""; }

        if (string.IsNullOrWhiteSpace(text))
        {
            Notify?.Invoke("Буфер обмена пуст", true);
            return Task.CompletedTask;
        }

        // вставка из буфера (Ctrl+V, контекстное меню) добавляет профили сразу, без окна подтверждения
        var result = LinkParser.ParseMany(text);
        if (result.Profiles.Count == 0)
        {
            var hint = result.Errors.FirstOrDefault();
            Notify?.Invoke(string.IsNullOrEmpty(hint)
                ? "Не удалось распознать профиль из буфера"
                : "Не распознано: " + hint, true);
            return Task.CompletedTask;
        }

        AddParsed(result.Profiles);
        if (result.Errors.Count > 0)
            Notify?.Invoke($"Часть строк не распознана: {result.Errors.First()}", true);
        return Task.CompletedTask;
    }

    private Task ImportFromFileAsync()
    {
        var path = _dialogs.OpenFile("Текст и конфиги (*.txt;*.json;*.conf;*.yaml)|*.txt;*.json;*.conf;*.yaml|Все файлы (*.*)|*.*");
        if (path is null) return Task.CompletedTask;
        try
        {
            var text = File.ReadAllText(path);
            var parsed = _dialogs.ImportProfiles(text);
            AddParsed(parsed);
        }
        catch (Exception ex)
        {
            _dialogs.Error("Не удалось прочитать файл", ex.Message);
        }
        return Task.CompletedTask;
    }

    /// <summary>Добавление уже разобранных профилей (используется диалогом импорта).</summary>
    public int AddParsed(IEnumerable<ProxyProfile> parsed)
    {
        var added = 0;
        foreach (var profile in parsed)
        {
            _store.Save(profile);
            Items.Add(new ProfileItemVm(profile));
            added++;
        }
        if (added > 0)
        {
            Raise(nameof(IsEmpty));
            PingAllCommand.RaiseCanExecuteChanged();
            Notify?.Invoke($"Добавлено профилей: {added}", false);
        }
        return added;
    }

    private void Edit(ProfileItemVm? item)
    {
        if (item is null) return;
        if (_dialogs.EditProfile(item.Profile, _latency))
        {
            _store.Save(item.Profile);
            item.Refresh();
            Notify?.Invoke("Профиль сохранён", false);
        }
    }

    private void Remove(ProfileItemVm? item)
    {
        if (item is null) return;
        if (!_dialogs.Confirm("Удалить профиль", $"Удалить «{item.Name}»? Действие необратимо.", "Удалить")) return;

        _store.Delete(item.Profile);
        Items.Remove(item);
        if (Selected == item) Selected = null;
        Raise(nameof(IsEmpty));
        PingAllCommand.RaiseCanExecuteChanged();
        Notify?.Invoke("Профиль удалён", false);
    }

    private void CreateManual(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol)) return;

        var profile = new ProxyProfile
        {
            Name = ProtocolIds.Display(protocol!),
            Protocol = protocol!
        };

        if (!_dialogs.EditProfile(profile, _latency)) return;

        _store.Save(profile);
        Items.Add(new ProfileItemVm(profile));
        View.Refresh();
        Raise(nameof(IsEmpty));
        PingAllCommand.RaiseCanExecuteChanged();
        Notify?.Invoke($"Профиль «{profile.Name}» создан", false);
    }

    private void Duplicate()
    {
        if (Selected is null) return;
        var name = _dialogs.PromptText("Дублировать профиль", "Новое имя", Selected.Name + " (копия)");
        if (string.IsNullOrWhiteSpace(name)) return;

        var copy = _store.Duplicate(Selected.Profile, name!);
        Items.Add(new ProfileItemVm(copy));
        View.Refresh();
        Notify?.Invoke("Профиль продублирован", false);
    }

    // -------------------------------------------------------------------- пинг
    // Все вызовы начинаются на UI-потоке: обновление списка, сохранение и событие
    // выполняются вместе. Повторный запрос того же профиля ждёт уже идущий замер.
    public async Task PingAsync(ProfileItemVm? item)
    {
        if (item is null) return;
        if (_pendingPings.TryGetValue(item.Id, out var pending))
        {
            await pending;
            return;
        }

        var task = MeasureAndPublishPingAsync(item);
        _pendingPings[item.Id] = task;
        try
        {
            await task;
        }
        finally
        {
            _pendingPings.Remove(item.Id);
        }
    }

    private async Task MeasureAndPublishPingAsync(ProfileItemVm item)
    {
        item.IsPinging = true;
        try
        {
            await _pingGate.WaitAsync();
            try
            {
                var ms = await _latency.MeasureAsync(item.Profile);
                // Удалённый во время замера профиль не должен появиться на диске снова.
                if (!Items.Contains(item)) return;
                item.PingMs = ms;
                item.Profile.LastPingMs = ms;
                PingUpdated?.Invoke(item.Profile, ms);
                _store.Save(item.Profile);
            }
            finally
            {
                _pingGate.Release();
            }
        }
        finally
        {
            item.IsPinging = false;
        }
    }

    public async Task PingAllAsync()
    {
        var targets = Items.ToList();
        if (targets.Count == 0) return;

        await Task.WhenAll(targets.Select(item => PingAsync(item)));
        Notify?.Invoke("Замер задержки завершён", false);
    }

    // ------------------------------------------------------------------- прочее
    private void Connect(ProfileItemVm? item)
    {
        if (item is null) return;
        ConnectRequested?.Invoke(item.Profile);
    }

    private void ToggleFavorite(ProfileItemVm? item)
    {
        if (item is null) return;
        item.Profile.Favorite = !item.Profile.Favorite;
        _store.Save(item.Profile);
        item.Refresh();
        View.Refresh();
    }

    private void CopyLink(ProfileItemVm? item)
    {
        if (item is null) return;
        try
        {
            System.Windows.Clipboard.SetText(_store.Export(item.Profile));
            Notify?.Invoke("Конфигурация скопирована в буфер", false);
        }
        catch (Exception ex)
        {
            _dialogs.Error("Не удалось скопировать", ex.Message);
        }
    }

    /// <summary>Активный профиль сменился (старый id, новый id) — список анимирует подсветку.</summary>
    public event Action<string?, string?>? ActiveChanged;

    /// <summary>Пометить активный профиль в списке.</summary>
    public void SetActive(string? profileId)
    {
        var previous = Items.FirstOrDefault(i => i.IsActive)?.Id;
        foreach (var item in Items)
            item.IsActive = item.Id == profileId;
        if (!string.Equals(previous, profileId, StringComparison.Ordinal))
            ActiveChanged?.Invoke(previous, profileId);
    }

    public ProfileItemVm? Find(string id) => Items.FirstOrDefault(i => i.Id == id);
}
