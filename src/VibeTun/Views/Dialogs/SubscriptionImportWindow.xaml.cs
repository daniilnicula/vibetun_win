using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using VibeTun.Services;

namespace VibeTun.Views.Dialogs;

public partial class SubscriptionImportWindow : DialogWindow
{
    public SubscriptionImportWindow(string subscriptionName, string defaultGroupName, int profileCount,
        IReadOnlyList<SubscriptionGroupOption> groups)
    {
        InitializeComponent();
        // здесь группа обязательна: кнопка ниже кладёт профили именно в группу
        TargetGroup.SetGroups(groups, optional: false);
        TargetGroup.NewName = defaultGroupName;
        Message.Text = L.F("S0978", subscriptionName, profileCount);
    }

    public SubscriptionImportChoice Choice { get; private set; } = new(SubscriptionImportAction.Cancel);

    private void OnAddSubscription(object sender, RoutedEventArgs e) => Finish(SubscriptionImportAction.AddSubscription);

    private void OnAddProfiles(object sender, RoutedEventArgs e) => Finish(SubscriptionImportAction.AddProfiles);

    private void OnAddToGroup(object sender, RoutedEventArgs e)
    {
        var (groupId, newGroupName) = TargetGroup.Selection;
        if (groupId is null && newGroupName is null)
        {
            TargetGroup.ShowError();
            return;
        }

        Choice = new SubscriptionImportChoice(SubscriptionImportAction.AddProfilesToGroup, groupId, newGroupName);
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
