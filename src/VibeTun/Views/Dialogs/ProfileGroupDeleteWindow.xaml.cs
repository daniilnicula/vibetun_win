using System.Windows;
using System.Windows.Input;
using VibeTun.Services;

namespace VibeTun.Views.Dialogs;

public partial class ProfileGroupDeleteWindow : DialogWindow
{
    public ProfileGroupDeleteWindow(string groupName, int profileCount)
    {
        InitializeComponent();
        Message.Text = L.F("S0959", groupName, profileCount);
        if (profileCount <= 0)
        {
            KeepButton.Visibility = Visibility.Collapsed;
            DeleteTitle.Text = L.T("S0955");
            DeleteDescription.Text = L.T("S0972");
        }
    }

    public ProfileGroupDeleteChoice Choice { get; private set; } = ProfileGroupDeleteChoice.Cancel;

    private void OnKeepProfiles(object sender, RoutedEventArgs e)
    {
        Choice = ProfileGroupDeleteChoice.KeepProfiles;
        DialogResult = true;
        Close();
    }

    private void OnDeleteGroupAndProfiles(object sender, RoutedEventArgs e)
    {
        Choice = ProfileGroupDeleteChoice.DeleteGroupAndProfiles;
        DialogResult = true;
        Close();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = ProfileGroupDeleteChoice.Cancel;
        DialogResult = false;
        Close();
    }
}
