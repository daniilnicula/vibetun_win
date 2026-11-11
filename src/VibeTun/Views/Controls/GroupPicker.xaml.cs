using System.Windows;
using System.Windows.Controls;
using VibeTun.Services;

namespace VibeTun.Views.Controls;

/// <summary>
/// Блок «в какую группу положить профили»: список существующих групп и поле имени новой.
/// Поля взаимоисключающие — выбор в списке очищает имя, ввод имени снимает выбор.
/// <para>
/// Два режима. Обязательный (окно импорта подписки): группу нужно указать, иначе показывается
/// ошибка. Необязательный (окно «Добавить подключение»): первым пунктом списка идёт «Без группы»,
/// он выбран по умолчанию, и профили тогда добавляются вне групп, как раньше.
/// </para>
/// </summary>
public partial class GroupPicker : UserControl
{
    /// <summary>Пункт «Без группы» в списке (только необязательный режим).</summary>
    private const string NoGroupId = "";

    private IReadOnlyList<SubscriptionGroupOption> _groups = Array.Empty<SubscriptionGroupOption>();
    private bool _optional;
    private bool _syncing;

    public GroupPicker()
    {
        InitializeComponent();
        ApplyCaptions();
        GroupSelector.SelectionChanged += (_, _) => OnGroupPicked();
        NewGroupName.TextChanged += (_, _) => OnNameTyped();
    }

    /// <summary>Выбор изменился (в том числе сброшен).</summary>
    public event Action? Changed;

    /// <summary>
    /// Заполнить список и сбросить выбор. <paramref name="groups"/> — обычные группы пользователя
    /// (подписочные сюда не передаются: их состав ведёт сервер подписки). <paramref name="optional"/> —
    /// группу можно не указывать.
    /// </summary>
    public void SetGroups(IReadOnlyList<SubscriptionGroupOption> groups, bool optional)
    {
        _groups = groups;
        _optional = optional;

        _syncing = true;
        try
        {
            var items = new List<SubscriptionGroupOption>(groups.Count + 1);
            if (optional && groups.Count > 0) items.Add(new SubscriptionGroupOption(NoGroupId, L.T("S1089")));
            items.AddRange(groups);
            GroupSelector.ItemsSource = items;
            GroupSelector.SelectedIndex = optional && groups.Count > 0 ? 0 : -1;
            NewGroupName.Text = "";
        }
        finally
        {
            _syncing = false;
        }

        ApplyCaptions();
        HideError();
    }

    /// <summary>Имя новой группы в поле ввода (предзаполнение и чтение).</summary>
    public string NewName
    {
        get => NewGroupName.Text?.Trim() ?? "";
        set => NewGroupName.Text = value ?? "";
    }

    /// <summary>
    /// Итог выбора: идентификатор существующей группы либо имя новой; оба null — группа не выбрана.
    /// Введённое имя, совпавшее с существующей группой, означает эту группу, а не её двойник.
    /// </summary>
    public (string? GroupId, string? NewGroupName) Selection
    {
        get
        {
            var name = NewName;
            if (name.Length > 0)
            {
                var same = _groups.FirstOrDefault(group =>
                    string.Equals(group.Name, name, StringComparison.CurrentCultureIgnoreCase));
                return same is not null ? (same.Id, null) : (null, name);
            }

            return GroupSelector.SelectedItem is SubscriptionGroupOption picked && picked.Id.Length > 0
                ? (picked.Id, null)
                : (null, null);
        }
    }

    /// <summary>true — указана существующая группа или имя новой.</summary>
    public bool HasSelection
    {
        get
        {
            var (groupId, newGroupName) = Selection;
            return groupId is not null || newGroupName is not null;
        }
    }

    /// <summary>Показать «выберите группу или введите имя» (обязательный режим, когда ничего не указано).</summary>
    public void ShowError() => GroupError.Visibility = Visibility.Visible;

    private void HideError() => GroupError.Visibility = Visibility.Collapsed;

    /// <summary>Подписи полей и видимость списка: без единой группы список не показывается вовсе.</summary>
    private void ApplyCaptions()
    {
        var hasGroups = _groups.Count > 0;
        var list = hasGroups ? Visibility.Visible : Visibility.Collapsed;
        ExistingCaption.Visibility = list;
        GroupSelector.Visibility = list;

        ExistingCaption.Text = L.T(_optional ? "S1088" : "S0982");
        NewCaption.Text = hasGroups ? L.T("S0983") : L.T(_optional ? "S0040" : "S1090");
        NewCaption.Margin = new Thickness(0, hasGroups ? 12 : 0, 0, 0);
    }

    private void OnGroupPicked()
    {
        HideError();
        if (_syncing) return;
        if (GroupSelector.SelectedItem is not null)
        {
            // выбор в списке (в том числе «Без группы») отменяет имя новой группы
            _syncing = true;
            try { NewGroupName.Text = ""; }
            finally { _syncing = false; }
        }
        Changed?.Invoke();
    }

    private void OnNameTyped()
    {
        HideError();
        if (_syncing) return;

        _syncing = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(NewGroupName.Text))
            {
                // ручной ввод означает новую группу, а не выбранную в списке
                GroupSelector.SelectedIndex = -1;
            }
            else if (_optional && _groups.Count > 0 && GroupSelector.SelectedIndex < 0)
            {
                // имя стёрли — список возвращается к «Без группы», а не остаётся пустым
                GroupSelector.SelectedIndex = 0;
            }
        }
        finally
        {
            _syncing = false;
        }
        Changed?.Invoke();
    }
}
