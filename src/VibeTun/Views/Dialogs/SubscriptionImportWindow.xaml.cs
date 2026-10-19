using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using VibeTun.Services;

namespace VibeTun.Views.Dialogs;

public partial class SubscriptionImportWindow : DialogWindow
{
    private readonly IReadOnlyList<SubscriptionGroupOption> _groups;

    public SubscriptionImportWindow(string subscriptionName, int profileCount, IReadOnlyList<SubscriptionGroupOption> groups)
    {
        InitializeComponent();
        _groups = groups;
        GroupSelector.ItemsSource = groups;
        GroupSelector.SelectedIndex = -1;
        GroupSelector.SelectionChanged += (_, _) => GroupError.Visibility = Visibility.Collapsed;
        if (groups.Count == 0) NewGroupName.Text = subscriptionName;
        NewGroupName.TextChanged += (_, _) => GroupError.Visibility = Visibility.Collapsed;
        Message.Text = L.F("S0978", subscriptionName, profileCount);
    }

    public SubscriptionImportChoice Choice { get; private set; } = new(SubscriptionImportAction.Cancel);

    private void OnAddSubscription(object sender, RoutedEventArgs e) => Finish(SubscriptionImportAction.AddSubscription);

    private void OnAddProfiles(object sender, RoutedEventArgs e) => Finish(SubscriptionImportAction.AddProfiles);

    private void OnAddToGroup(object sender, RoutedEventArgs e)
    {
        var name = NewGroupName.Text?.Trim() ?? "";
        if (name.Length > 0)
        {
            var matchingGroup = _groups.FirstOrDefault(group =>
                string.Equals(group.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (matchingGroup is not null)
            {
                Choice = new SubscriptionImportChoice(SubscriptionImportAction.AddProfilesToGroup, matchingGroup.Id);
            }
            else
            {
                Choice = new SubscriptionImportChoice(SubscriptionImportAction.AddProfilesToGroup, NewGroupName: name);
            }
        }
        else if (GroupSelector.SelectedItem is SubscriptionGroupOption selected)
        {
            Choice = new SubscriptionImportChoice(SubscriptionImportAction.AddProfilesToGroup, selected.Id);
        }
        else
        {
            GroupError.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Finish(SubscriptionImportAction action)
    {
        Choice = new SubscriptionImportChoice(action);
        DialogResult = true;
        Close();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = new SubscriptionImportChoice(SubscriptionImportAction.Cancel);
        DialogResult = false;
        Close();
    }
}
