using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using VibeTun.Models;
using VibeTun.Services;
using VibeTun.Utils;

namespace VibeTun.ViewModels;

/// <summary>Профили, их порядок и пользовательские группы.</summary>
public sealed class ProfilesViewModel : ObservableObject
{
    private readonly ProfileStore _store;
    private readonly ProfileLayoutStore _layoutStore;
    private readonly SubscriptionService _subscriptionService;
    private readonly LatencyService _latency;
    private readonly IDialogService _dialogs;
    private string _searchText = "";
    private ProfileItemVm? _selected;
    private object? _selectedEntry;
    private readonly SemaphoreSlim _pingGate = new(8);
    private readonly Dictionary<string, Task> _pendingPings = new();

    public ProfilesViewModel(ProfileStore store, LatencyService latency, IDialogService dialogs, LogFeed log)
    {
        _store = store;
        _layoutStore = new ProfileLayoutStore();
        _subscriptionService = new SubscriptionService();
        _latency = latency;
        _dialogs = dialogs;
        Log = log;
        ClearLogCommand = new RelayCommand(_ => Log.Clear());

        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = FilterEntry;

        AddCommand = new AsyncRelayCommand(() => AddAsync());
        AddFromClipboardCommand = new AsyncRelayCommand(() => AddFromClipboardAsync());
        ImportFileCommand = new AsyncRelayCommand(ImportFromFileAsync);
        CreateGroupCommand = new RelayCommand(_ => CreateGroup());
        CreateSubscriptionCommand = new AsyncRelayCommand(CreateSubscriptionAsync);
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

    /// <summary>Все профили, включая находящиеся внутри групп.</summary>
    public ObservableCollection<ProfileItemVm> Items { get; } = new();

    /// <summary>Корневые элементы списка: отдельные профили и группы, в сохранённом порядке.</summary>
    public ObservableCollection<object> Entries { get; } = new();

    public ICollectionView EntriesView { get; }

    /// <summary>Общий журнал: панель «Логи» на вкладке «Профили».</summary>
    public LogFeed Log { get; }

    public RelayCommand ClearLogCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            RefreshDisplay();
        }
    }

    /// <summary>Профиль, для которого доступны команды строки и контекстного меню.</summary>
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

    /// <summary>Выбранная верхнеуровневая строка ListBox (это может быть группа).</summary>
    public object? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!SetProperty(ref _selectedEntry, value)) return;
            Selected = value as ProfileItemVm;
        }
    }

    public bool IsEmpty => Items.Count == 0 && !Entries.OfType<ProfileGroupVm>().Any();
    public bool HasSearchResults => EntriesView.Cast<object>().Any();
    public bool IsSearchNoResults => !string.IsNullOrWhiteSpace(_searchText) && !HasSearchResults;

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
    public RelayCommand CreateGroupCommand { get; }
    public AsyncRelayCommand CreateSubscriptionCommand { get; }
    public AsyncRelayCommand AddCommand { get; }
    public AsyncRelayCommand AddFromClipboardCommand { get; }
    public AsyncRelayCommand ImportFileCommand { get; }

    /// <summary>Создание профиля вручную: выбор протокола из подменю ПКМ.</summary>
    public RelayCommand CreateManualCommand { get; }

    // ------------------------------------------------------------------ загрузка
    public void Load()
    {
        Items.Clear();
        Entries.Clear();
        Selected = null;
        SelectedEntry = null;

        var profilesById = new Dictionary<string, ProfileItemVm>(StringComparer.Ordinal);
        foreach (var profile in _store.LoadAll())
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || profilesById.ContainsKey(profile.Id)) continue;
            var item = new ProfileItemVm(profile);
            profilesById.Add(item.Id, item);
            Items.Add(item);
        }

        var layout = _layoutStore.Load();
        layout.Entries ??= new List<ProfileLayoutEntry>();
        layout.Groups ??= new List<ProfileGroupDefinition>();

        var groupsById = new Dictionary<string, ProfileGroupVm>(StringComparer.Ordinal);
        foreach (var definition in layout.Groups)
        {
            if (definition is null) continue;
            if (string.IsNullOrWhiteSpace(definition.Id)) definition.Id = Guid.NewGuid().ToString("N");
            if (groupsById.ContainsKey(definition.Id)) continue;
            if (string.IsNullOrWhiteSpace(definition.Name))
                definition.Name = L.F("S0957", groupsById.Count + 1);
            definition.ProfileIds ??= new List<string>();
            groupsById.Add(definition.Id, new ProfileGroupVm(definition, this));
        }

        var placedProfiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groupsById.Values)
        {
            var normalizedIds = new List<string>();
            foreach (var id in group.Model.ProfileIds)
            {
                if (string.IsNullOrWhiteSpace(id) ||
                    !profilesById.TryGetValue(id, out var profile) || !placedProfiles.Add(id)) continue;
                group.Profiles.Add(profile);
                normalizedIds.Add(id);
            }
            group.Model.ProfileIds = normalizedIds;
        }

        var placedGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in layout.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id)) continue;
            if (entry.Kind == ProfileLayoutEntryKind.Group)
            {
                if (groupsById.TryGetValue(entry.Id, out var group) && placedGroups.Add(group.Id))
                    Entries.Add(group);
            }
            else if (entry.Kind == ProfileLayoutEntryKind.Profile &&
                     profilesById.TryGetValue(entry.Id, out var profile) && placedProfiles.Add(profile.Id))
            {
                Entries.Add(profile);
            }
        }

        // Group definitions absent from the root order, and profiles newly added outside the layout,
        // are appended without disturbing existing order or group membership.
        foreach (var group in groupsById.Values)
            if (placedGroups.Add(group.Id)) Entries.Add(group);
        foreach (var profile in Items)
            if (placedProfiles.Add(profile.Id)) Entries.Add(profile);

        RefreshDisplay();
        OnLayoutChanged();
        PingAllCommand.RaiseCanExecuteChanged();
    }

    private bool FilterEntry(object item) => item switch
    {
        ProfileItemVm profile => MatchesSearch(profile),
        ProfileGroupVm group => string.IsNullOrWhiteSpace(_searchText) ||
                               IsGroupNameSearchMatch(group) || group.Profiles.Any(MatchesSearch),
        _ => false
    };

    internal bool MatchesSearch(ProfileItemVm profile)
    {
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        var query = _searchText.Trim();
        return profile.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || profile.Address.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || profile.ProtocolLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    internal bool IsGroupNameSearchMatch(ProfileGroupVm group) =>
        !string.IsNullOrWhiteSpace(_searchText) &&
        group.Name.Contains(_searchText.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private void RefreshDisplay()
    {
        foreach (var group in Entries.OfType<ProfileGroupVm>()) group.RefreshFilter();
        EntriesView.Refresh();
        Raise(nameof(IsEmpty));
        Raise(nameof(HasSearchResults));
        Raise(nameof(IsSearchNoResults));
    }

    // -------------------------------------------------------------------- CRUD
    private Task AddAsync()
    {
        var parsed = _dialogs.ImportProfiles();
        AddParsed(parsed);
        return Task.CompletedTask;
    }

    private async Task AddFromClipboardAsync()
    {
        string text;
        try { text = System.Windows.Clipboard.GetText(); }
        catch { text = ""; }

        if (string.IsNullOrWhiteSpace(text))
        {
            Notify?.Invoke(L.T("S0800"), true);
            return;
        }

        if (TryGetSubscriptionUrl(text, out var subscriptionUrl))
        {
            await ImportSubscriptionFromClipboardAsync(subscriptionUrl);
            return;
        }

        // обычные ссылки/конфиги из буфера добавляем без промежуточного диалога;
        // удалённая HTTP(S)-подписка обрабатывается отдельным путём выше.
        var result = LinkParser.ParseMany(text);
        if (result.Profiles.Count == 0)
        {
            var hint = result.Errors.FirstOrDefault();
            Notify?.Invoke(string.IsNullOrEmpty(hint)
                ? L.T("S0801")
                : L.T("S0802") + hint, true);
            return;
        }

        AddParsed(result.Profiles);
        if (result.Errors.Count > 0)
            Notify?.Invoke(L.F("S0803", result.Errors.First()), true);
    }

    private static bool TryGetSubscriptionUrl(string text, out string url)
    {
        url = text.Trim();
        if (url.Any(char.IsWhiteSpace) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrWhiteSpace(uri.Host))
            return false;

        // http(s)://user:pass@proxy:port is also a supported single HTTP-proxy URI.
        // A subscription URL (e.g. 3x-ui /<sub-path>/<sub-id>) has a non-root path.
        return uri.AbsolutePath.Trim('/').Length > 0;
    }

    private async Task ImportSubscriptionFromClipboardAsync(string url)
    {
        try
        {
            var subscription = await _subscriptionService.FetchAsync(url);
            var groups = Entries.OfType<ProfileGroupVm>()
                .Where(group => !group.IsSubscription)
                .Select(group => new SubscriptionGroupOption(group.Id, group.Name))
                .ToList();
            var choice = _dialogs.ChooseSubscriptionImport(subscription.Name, subscription.Profiles.Count, groups);
            if (choice.Action == SubscriptionImportAction.Cancel) return;

            switch (choice.Action)
            {
                case SubscriptionImportAction.AddSubscription:
                    AddSubscriptionGroup(url, subscription);
                    break;
                case SubscriptionImportAction.AddProfiles:
                    AddParsed(subscription.Profiles);
                    break;
                case SubscriptionImportAction.AddProfilesToGroup:
                    var group = ResolvePasteTargetGroup(choice);
                    if (group is null)
                    {
                        Notify?.Invoke(L.T("S0985"), true);
                        return;
                    }
                    var added = AddProfilesToGroupCore(subscription.Profiles, group);
                    OnLayoutChanged();
                    RefreshDisplay();
                    PingAllCommand.RaiseCanExecuteChanged();
                    Notify?.Invoke(L.F("S0988", added, group.Name), false);
                    break;
            }

            if (subscription.Errors.Count > 0)
                Notify?.Invoke(L.F("S0989", subscription.Errors[0]), true);
        }
        catch (Exception ex)
        {
            Notify?.Invoke(L.F("S0990", ex.Message), true);
        }
    }

    private ProfileGroupVm? ResolvePasteTargetGroup(SubscriptionImportChoice choice)
    {
        if (!string.IsNullOrWhiteSpace(choice.GroupId))
        {
            var selected = Entries.OfType<ProfileGroupVm>().FirstOrDefault(group =>
                !group.IsSubscription && string.Equals(group.Id, choice.GroupId, StringComparison.Ordinal));
            if (selected is not null) return selected;
        }

        if (string.IsNullOrWhiteSpace(choice.NewGroupName)) return null;
        var name = choice.NewGroupName.Trim();
        var existing = Entries.OfType<ProfileGroupVm>().FirstOrDefault(group =>
            !group.IsSubscription && string.Equals(group.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null) return existing;

        var created = new ProfileGroupVm(new ProfileGroupDefinition { Name = MakeUniqueGroupName(name) }, this);
        Entries.Add(created);
        return created;
    }

    private async Task CreateSubscriptionAsync()
    {
        var address = _dialogs.PromptText(L.T("S0975"), L.T("S0976"));
        if (string.IsNullOrWhiteSpace(address)) return;

        try
        {
            var url = address.Trim();
            var subscription = await _subscriptionService.FetchAsync(url);
            AddSubscriptionGroup(url, subscription);
            if (subscription.Errors.Count > 0)
                Notify?.Invoke(L.F("S0989", subscription.Errors[0]), true);
        }
        catch (Exception ex)
        {
            Notify?.Invoke(L.F("S0990", ex.Message), true);
        }
    }

    internal async Task RefreshSubscriptionAsync(ProfileGroupVm group)
    {
        var url = group.SubscriptionUrl;
        if (!Entries.Contains(group) || !group.IsSubscription || string.IsNullOrWhiteSpace(url)) return;
        try
        {
            var subscription = await _subscriptionService.FetchAsync(url);
            if (!Entries.Contains(group) || !group.IsSubscription ||
                !string.Equals(group.SubscriptionUrl, url, StringComparison.Ordinal)) return;
            var (added, updated) = ApplySubscriptionUpdate(group, subscription.Profiles);
            Notify?.Invoke(L.F("S0987", group.Name, added, updated), false);
            if (subscription.Errors.Count > 0)
                Notify?.Invoke(L.F("S0989", subscription.Errors[0]), true);
        }
        catch (Exception ex)
        {
            Notify?.Invoke(L.F("S0991", group.Name, ex.Message), true);
        }
    }

    private void AddSubscriptionGroup(string url, SubscriptionFetchResult subscription)
    {
        var normalizedUrl = NormalizeSubscriptionUrl(url);
        var existing = Entries.OfType<ProfileGroupVm>().FirstOrDefault(group =>
            group.IsSubscription && string.Equals(NormalizeSubscriptionUrl(group.SubscriptionUrl), normalizedUrl,
                StringComparison.Ordinal));
        if (existing is not null)
        {
            var (addedExisting, updatedExisting) = ApplySubscriptionUpdate(existing, subscription.Profiles);
            Notify?.Invoke(L.F("S0987", existing.Name, addedExisting, updatedExisting), false);
            return;
        }

        var group = new ProfileGroupVm(new ProfileGroupDefinition
        {
            Name = MakeUniqueGroupName(subscription.Name),
            IsSubscription = true,
            SubscriptionUrl = url.Trim(),
        }, this);
        var added = AddProfilesToGroupCore(subscription.Profiles, group);
        Entries.Add(group);
        OnLayoutChanged();
        RefreshDisplay();
        PingAllCommand.RaiseCanExecuteChanged();
        Notify?.Invoke(L.F("S0992", group.Name, added), false);
    }

    private (int Added, int Updated) ApplySubscriptionUpdate(ProfileGroupVm group, IEnumerable<ProxyProfile> incomingProfiles)
    {
        var existing = group.Profiles.ToList();
        var matched = new HashSet<ProfileItemVm>();
        var added = 0;
        var updated = 0;

        foreach (var incoming in incomingProfiles)
        {
            var key = SubscriptionProfileKey(incoming);
            var match = existing.FirstOrDefault(item => !matched.Contains(item) &&
                string.Equals(SubscriptionProfileKey(item.Profile), key, StringComparison.Ordinal));
            if (match is null)
            {
                AddProfilesToGroupCore(new[] { incoming }, group);
                added++;
                continue;
            }

            matched.Add(match);
            UpdateSubscriptionProfile(match, incoming);
            updated++;
        }

        // Intentionally keep profiles that disappeared from the remote response, as requested.
        OnLayoutChanged();
        RefreshDisplay();
        PingAllCommand.RaiseCanExecuteChanged();
        return (added, updated);
    }

    private int AddProfilesToGroupCore(IEnumerable<ProxyProfile> profiles, ProfileGroupVm group)
    {
        var added = 0;
        foreach (var profile in profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || Items.Any(item => item.Id == profile.Id))
                profile.Id = Guid.NewGuid().ToString("N");
            _store.Save(profile);
            var item = new ProfileItemVm(profile);
            Items.Add(item);
            group.Profiles.Add(item);
            added++;
        }
        return added;
    }

    private void UpdateSubscriptionProfile(ProfileItemVm item, ProxyProfile incoming)
    {
        var profile = item.Profile;
        profile.Name = incoming.Name;
        profile.Protocol = incoming.Protocol;
        profile.Uri = incoming.Uri;
        profile.Node = incoming.Node?.Clone() ?? new ProxyNode();
        profile.RawJson = incoming.RawJson;
        profile.Group = incoming.Group;
        _store.Save(profile);
        item.Refresh();
    }

    private static string SubscriptionProfileKey(ProxyProfile profile)
    {
        var node = profile.Node ?? new ProxyNode();
        var protocol = profile.Protocol.ToLowerInvariant();
        var uuid = node.Uuid;
        if (!string.IsNullOrWhiteSpace(uuid) && (protocol is "vless" or "vmess" or "tuic"))
            return string.Join("\u001f", protocol, uuid.ToLowerInvariant(), protocol == "tuic" ? node.Password : "");
        var wgPeerKey = node.WgPeerPublicKey;
        if (protocol == "wireguard" && !string.IsNullOrWhiteSpace(wgPeerKey))
            return protocol + "\u001f" + wgPeerKey;
        var mtSecret = node.MtSecret;
        if (protocol == "mtproto" && !string.IsNullOrWhiteSpace(mtSecret))
            return protocol + "\u001f" + mtSecret;

        var key = string.Join("\u001f", protocol, node.Server.ToLowerInvariant(), node.Port,
            node.Username, node.Password, node.Method, node.Uuid, node.WgPrivateKey);
        if (!string.IsNullOrWhiteSpace(node.Username) || !string.IsNullOrWhiteSpace(node.Password) ||
            !string.IsNullOrWhiteSpace(node.Method) || !string.IsNullOrWhiteSpace(node.Uuid) ||
            !string.IsNullOrWhiteSpace(node.WgPrivateKey)) return key;

        var uri = profile.Uri ?? "";
        var fragment = uri.IndexOf('#');
        if (fragment >= 0) uri = uri[..fragment];
        return key + "\u001f" + uri + "\u001f" + profile.Name;
    }

    private string MakeUniqueGroupName(string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? L.T("S0993") : name.Trim();
        var existing = Entries.OfType<ProfileGroupVm>().Select(group => group.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!existing.Contains(baseName)) return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!existing.Contains(candidate)) return candidate;
        }
        return baseName + " " + Guid.NewGuid().ToString("N")[..4];
    }

    private static string NormalizeSubscriptionUrl(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return (address ?? "").Trim();
        return uri.Scheme.ToLowerInvariant() + "://" + uri.Authority.ToLowerInvariant() + uri.PathAndQuery;
    }

    private Task ImportFromFileAsync()
    {
        var path = _dialogs.OpenFile(L.T("S0804"));
        if (path is null) return Task.CompletedTask;
        try
        {
            var text = File.ReadAllText(path);
            var parsed = _dialogs.ImportProfiles(text);
            AddParsed(parsed);
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.T("S0805"), ex.Message);
        }
        return Task.CompletedTask;
    }

    /// <summary>Добавление уже разобранных профилей (используется диалогом импорта); всегда вне групп.</summary>
    public int AddParsed(IEnumerable<ProxyProfile> parsed)
    {
        var added = 0;
        foreach (var profile in parsed)
        {
            if (Items.Any(item => item.Id == profile.Id))
                profile.Id = Guid.NewGuid().ToString("N");
            _store.Save(profile);
            var item = new ProfileItemVm(profile);
            Items.Add(item);
            Entries.Add(item);
            added++;
        }
        if (added > 0)
        {
            OnLayoutChanged();
            RefreshDisplay();
            PingAllCommand.RaiseCanExecuteChanged();
            Notify?.Invoke(L.F("S0806", added), false);
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
            RefreshDisplay();
            Notify?.Invoke(L.T("S0807"), false);
        }
    }

    private void Remove(ProfileItemVm? item)
    {
        if (item is null) return;
        if (!_dialogs.Confirm(L.T("S0165"), L.F("S0808", item.Name), L.T("S0083"))) return;

        _store.Delete(item.Profile);
        Items.Remove(item);
        Entries.Remove(item);
        FindContainingGroup(item)?.Profiles.Remove(item);
        if (Selected == item) Selected = null;
        if (ReferenceEquals(SelectedEntry, item)) SelectedEntry = null;
        OnLayoutChanged();
        RefreshDisplay();
        PingAllCommand.RaiseCanExecuteChanged();
        Notify?.Invoke(L.T("S0809"), false);
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

        AddCreatedProfile(profile);
        Notify?.Invoke(L.F("S0810", profile.Name), false);
    }

    private void Duplicate()
    {
        if (Selected is null) return;
        var name = _dialogs.PromptText(L.T("S0811"), L.T("S0812"), Selected.Name + L.T("S0813"));
        if (string.IsNullOrWhiteSpace(name)) return;

        var copy = _store.Duplicate(Selected.Profile, name!);
        AddCreatedProfile(copy);
        Notify?.Invoke(L.T("S0814"), false);
    }

    private void AddCreatedProfile(ProxyProfile profile)
    {
        var item = new ProfileItemVm(profile);
        _store.Save(profile);
        Items.Add(item);
        Entries.Add(item);
        OnLayoutChanged();
        RefreshDisplay();
        PingAllCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ группы
    private void CreateGroup()
    {
        var nextName = L.F("S0957", Entries.OfType<ProfileGroupVm>().Count() + 1);
        var name = _dialogs.PromptText(L.T("S0952"), L.T("S0958"), nextName);
        if (string.IsNullOrWhiteSpace(name)) return;

        var definition = new ProfileGroupDefinition { Name = name.Trim() };
        var group = new ProfileGroupVm(definition, this);
        Entries.Add(group);
        OnLayoutChanged();
        RefreshDisplay();
        Notify?.Invoke(L.F("S0960", group.Name), false);
    }

    public void RenameGroup(ProfileGroupVm group)
    {
        if (!Entries.Contains(group)) return;
        var name = _dialogs.PromptText(L.T("S0953"), L.T("S0958"), group.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        group.Name = name.Trim();
        RefreshDisplay();
        Notify?.Invoke(L.F("S0961", group.Name), false);
    }

    public void DissolveGroup(ProfileGroupVm group) => RemoveGroupPreservingProfiles(group, "S0962");

    private void RemoveGroupPreservingProfiles(ProfileGroupVm group, string notificationKey)
    {
        var groupIndex = Entries.IndexOf(group);
        if (groupIndex < 0) return;

        var profiles = group.Profiles.ToList();
        Entries.RemoveAt(groupIndex);
        var insertionIndex = groupIndex;
        foreach (var profile in profiles)
        {
            group.Profiles.Remove(profile);
            Entries.Insert(insertionIndex++, profile);
        }

        if (ReferenceEquals(SelectedEntry, group)) SelectedEntry = null;
        OnLayoutChanged();
        RefreshDisplay();
        Notify?.Invoke(L.F(notificationKey, group.Name), false);
    }

    public void DeleteGroup(ProfileGroupVm group)
    {
        if (!Entries.Contains(group)) return;
        var choice = _dialogs.ChooseProfileGroupDeletion(group.Name, group.ProfileCount);
        if (choice == ProfileGroupDeleteChoice.Cancel) return;
        if (choice == ProfileGroupDeleteChoice.KeepProfiles)
        {
            RemoveGroupPreservingProfiles(group, "S0973");
            return;
        }

        foreach (var profile in group.Profiles.ToList())
        {
            group.Profiles.Remove(profile);
            Items.Remove(profile);
            _store.Delete(profile.Profile);
            if (Selected == profile) Selected = null;
            if (ReferenceEquals(SelectedEntry, profile)) SelectedEntry = null;
        }
        Entries.Remove(group);
        if (ReferenceEquals(SelectedEntry, group)) SelectedEntry = null;

        OnLayoutChanged();
        RefreshDisplay();
        PingAllCommand.RaiseCanExecuteChanged();
        Notify?.Invoke(L.F("S0963", group.Name), false);
    }

    /// <summary>Persist the current root order and group membership.</summary>
    public void OnLayoutChanged()
    {
        var layout = new ProfileLayout();
        foreach (var entry in Entries)
        {
            switch (entry)
            {
                case ProfileItemVm profile:
                    layout.Entries.Add(new ProfileLayoutEntry { Kind = ProfileLayoutEntryKind.Profile, Id = profile.Id });
                    break;
                case ProfileGroupVm group:
                    group.Model.ProfileIds = group.Profiles.Select(profile => profile.Id).ToList();
                    layout.Groups.Add(group.Model);
                    layout.Entries.Add(new ProfileLayoutEntry { Kind = ProfileLayoutEntryKind.Group, Id = group.Id });
                    break;
            }
        }
        _layoutStore.Save(layout);
        Raise(nameof(IsEmpty));
    }

    /// <summary>Переместить профиль до/после другой карточки; группа определяется карточкой назначения.</summary>
    public bool ReorderProfile(ProfileItemVm profile, ProfileItemVm target, bool after, bool persist = true)
    {
        if (ReferenceEquals(profile, target) || !Items.Contains(profile) || !Items.Contains(target)) return false;
        var sourceGroup = FindContainingGroup(profile);
        var targetGroup = FindContainingGroup(target);
        if ((sourceGroup?.IsSubscription == true || targetGroup?.IsSubscription == true) &&
            !ReferenceEquals(sourceGroup, targetGroup)) return false;

        if (ReferenceEquals(sourceGroup, targetGroup))
        {
            var reordered = sourceGroup is null
                ? ReorderWithin(Entries, profile, target, after)
                : ReorderWithin(sourceGroup.Profiles, profile, target, after);
            if (!reordered) return false;
        }
        else
        {
            RemoveFromPlacement(profile, sourceGroup);
            if (targetGroup is null)
            {
                var targetIndex = Entries.IndexOf(target);
                if (targetIndex < 0) Entries.Add(profile);
                else Entries.Insert(targetIndex + (after ? 1 : 0), profile);
            }
            else
            {
                var targetIndex = targetGroup.Profiles.IndexOf(target);
                if (targetIndex < 0) targetGroup.Profiles.Add(profile);
                else targetGroup.Profiles.Insert(targetIndex + (after ? 1 : 0), profile);
            }
        }

        if (persist) PersistPlacementChange();
        return true;
    }

    public bool PutProfileInGroup(ProfileItemVm profile, ProfileGroupVm group, bool persist = true)
    {
        if (!Items.Contains(profile) || !Entries.Contains(group)) return false;
        var sourceGroup = FindContainingGroup(profile);
        if ((group.IsSubscription || sourceGroup?.IsSubscription == true) && !ReferenceEquals(sourceGroup, group))
            return false;
        if (ReferenceEquals(sourceGroup, group))
        {
            if (group.Profiles.IndexOf(profile) == group.Profiles.Count - 1) return false;
            group.Profiles.Remove(profile);
            group.Profiles.Add(profile);
        }
        else
        {
            RemoveFromPlacement(profile, sourceGroup);
            group.Profiles.Add(profile);
        }
        if (persist) PersistPlacementChange();
        return true;
    }

    public bool PutProfileAtEnd(ProfileItemVm profile, bool persist = true) =>
        PutProfileAtRootPosition(profile, target: null, after: true, persist: persist);

    /// <summary>Вывести профиль на верхний уровень и поставить до/после корневой карточки или группы.</summary>
    public bool PutProfileAtRootPosition(ProfileItemVm profile, object? target, bool after, bool persist = true)
    {
        if (!Items.Contains(profile)) return false;
        var sourceGroup = FindContainingGroup(profile);
        if (sourceGroup?.IsSubscription == true) return false;
        var sourceIndex = sourceGroup is null ? Entries.IndexOf(profile) : -1;
        if (sourceGroup is null && sourceIndex < 0) return false;

        var rootTarget = target is null ? null : RootEntryFor(target);
        if (target is not null && rootTarget is null) return false;
        if (ReferenceEquals(rootTarget, profile)) return false;
        var targetIndex = rootTarget is null ? -1 : Entries.IndexOf(rootTarget);
        if (rootTarget is not null && targetIndex < 0) return false;
        if (sourceGroup is null && rootTarget is null && sourceIndex == Entries.Count - 1) return false;
        if (sourceGroup is null && rootTarget is not null &&
            ((after && sourceIndex == targetIndex + 1) ||
             (!after && sourceIndex + 1 == targetIndex))) return false;

        RemoveFromPlacement(profile, sourceGroup);
        if (rootTarget is null)
        {
            Entries.Add(profile);
        }
        else
        {
            targetIndex = Entries.IndexOf(rootTarget);
            Entries.Insert(targetIndex + (after ? 1 : 0), profile);
        }

        if (persist) PersistPlacementChange();
        return true;
    }

    public bool ReorderGroup(ProfileGroupVm group, object? target, bool after, bool persist = true)
    {
        var sourceIndex = Entries.IndexOf(group);
        if (sourceIndex < 0) return false;
        var rootTarget = target is null ? null : RootEntryFor(target);
        if (ReferenceEquals(group, rootTarget)) return false;
        if (rootTarget is null && sourceIndex == Entries.Count - 1) return false;

        var targetIndexBeforeRemoval = rootTarget is null ? -1 : Entries.IndexOf(rootTarget);
        if (targetIndexBeforeRemoval < 0 && rootTarget is not null) return false;
        if (rootTarget is not null &&
            ((after && sourceIndex == targetIndexBeforeRemoval + 1) ||
             (!after && sourceIndex + 1 == targetIndexBeforeRemoval))) return false;

        var destinationIndex = rootTarget is null
            ? Entries.Count - 1
            : targetIndexBeforeRemoval + (after ? 1 : 0) - (sourceIndex < targetIndexBeforeRemoval ? 1 : 0);
        if (destinationIndex == sourceIndex) return false;

        // Emit a single collection Move rather than Remove + Insert. Besides preserving the
        // ListBox item container, this avoids a transient intermediate layout during group drags.
        Entries.Move(sourceIndex, destinationIndex);

        if (persist) PersistPlacementChange();
        return true;
    }

    private void PersistPlacementChange()
    {
        OnLayoutChanged();
        RefreshDisplay();
    }

    /// <summary>Для drop по вложенному профилю возвращает его верхнеуровневую группу.</summary>
    public object? RootEntryFor(object item)
    {
        if (item is ProfileGroupVm groupVm)
            return Entries.Contains(groupVm) ? groupVm : null;
        if (item is not ProfileItemVm profile) return null;
        var group = FindContainingGroup(profile);
        if (group is not null) return group;
        return Entries.Contains(profile) ? (object)profile : null;
    }

    private void RemoveFromPlacement(ProfileItemVm profile, ProfileGroupVm? group)
    {
        if (group is null) Entries.Remove(profile);
        else group.Profiles.Remove(profile);
    }

    private ProfileGroupVm? FindContainingGroup(ProfileItemVm profile) =>
        Entries.OfType<ProfileGroupVm>().FirstOrDefault(group => group.Profiles.Contains(profile));

    private static bool ReorderWithin<T>(ObservableCollection<T> collection, T moving, T target, bool after)
    {
        var movingIndex = collection.IndexOf(moving);
        var targetIndex = collection.IndexOf(target);
        if (movingIndex < 0 || targetIndex < 0) return false;
        if ((after && movingIndex == targetIndex + 1) ||
            (!after && movingIndex + 1 == targetIndex)) return false;

        collection.RemoveAt(movingIndex);
        targetIndex = collection.IndexOf(target);
        collection.Insert(targetIndex + (after ? 1 : 0), moving);
        return true;
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
        Notify?.Invoke(L.T("S0815"), false);
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
        RefreshDisplay();
    }

    private void CopyLink(ProfileItemVm? item)
    {
        if (item is null) return;
        try
        {
            System.Windows.Clipboard.SetText(_store.Export(item.Profile));
            Notify?.Invoke(L.T("S0816"), false);
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.T("S0817"), ex.Message);
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
