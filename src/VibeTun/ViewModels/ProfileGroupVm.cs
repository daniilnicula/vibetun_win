using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using VibeTun.Models;

namespace VibeTun.ViewModels;

/// <summary>Группа профилей на вкладке профилей, со своей коллекцией и контекстными действиями.</summary>
public sealed class ProfileGroupVm : ObservableObject
{
    private readonly ProfilesViewModel _owner;
    private string _name;
    private bool _isDragging;

    public ProfileGroupVm(ProfileGroupDefinition model, ProfilesViewModel owner)
    {
        Model = model;
        _owner = owner;
        _name = model.Name;
        Profiles.CollectionChanged += OnProfilesChanged;

        VisibleProfiles = CollectionViewSource.GetDefaultView(Profiles);
        VisibleProfiles.Filter = item => item is ProfileItemVm profile &&
                                        (_owner.IsGroupNameSearchMatch(this) || _owner.MatchesSearch(profile));

        RenameCommand = new RelayCommand(_ => _owner.RenameGroup(this));
        DissolveCommand = new RelayCommand(_ => _owner.DissolveGroup(this));
        DeleteCommand = new RelayCommand(_ => _owner.DeleteGroup(this));
        RefreshSubscriptionCommand = new AsyncRelayCommand(
            () => _owner.RefreshSubscriptionAsync(this),
            () => IsSubscription && !string.IsNullOrWhiteSpace(SubscriptionUrl));
    }

    public ProfileGroupDefinition Model { get; }
    public string Id => Model.Id;
    public bool IsSubscription => Model.IsSubscription;
    public string? SubscriptionUrl => Model.SubscriptionUrl;

    public string Name
    {
        get => _name;
        set
        {
            var normalized = value?.Trim() ?? "";
            if (!SetProperty(ref _name, normalized)) return;
            Model.Name = normalized;
            _owner.OnLayoutChanged();
        }
    }

    public ObservableCollection<ProfileItemVm> Profiles { get; } = new();
    public ICollectionView VisibleProfiles { get; }
    public int ProfileCount => Profiles.Count;
    public bool HasVisibleProfiles => Profiles.Any(profile =>
        _owner.IsGroupNameSearchMatch(this) || _owner.MatchesSearch(profile));
    public bool IsActive => Profiles.Any(profile => profile.IsActive);

    public bool IsExpanded
    {
        get => Model.IsExpanded;
        set
        {
            if (Model.IsExpanded == value) return;
            Model.IsExpanded = value;
            Raise();
            _owner.OnLayoutChanged();
        }
    }

    public bool IsDragging
    {
        get => _isDragging;
        set => SetProperty(ref _isDragging, value);
    }

    public RelayCommand RenameCommand { get; }
    public RelayCommand DissolveCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public AsyncRelayCommand RefreshSubscriptionCommand { get; }

    private void OnProfilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ProfileItemVm profile in e.OldItems)
                profile.PropertyChanged -= OnProfilePropertyChanged;
        if (e.NewItems is not null)
            foreach (ProfileItemVm profile in e.NewItems)
                profile.PropertyChanged += OnProfilePropertyChanged;

        Raise(nameof(ProfileCount));
        Raise(nameof(HasVisibleProfiles));
        Raise(nameof(IsActive));
    }

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileItemVm.IsActive)) Raise(nameof(IsActive));
    }

    public void RefreshFilter()
    {
        VisibleProfiles.Refresh();
        Raise(nameof(HasVisibleProfiles));
    }
}
