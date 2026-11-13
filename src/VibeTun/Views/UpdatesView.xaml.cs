using System.Windows.Controls;
using System.Windows.Input;
using VibeTun.ViewModels;

namespace VibeTun.Views;

public partial class UpdatesView : UserControl
{
    public UpdatesView()
    {
        InitializeComponent();
        // прокрутка до конца списка догружает следующую порцию релизов (ScrollChanged всплывает от ScrollViewer'а)
        ReleaseList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScrollChanged),
            handledEventsToo: true);
    }

    private UpdatesViewModel? Vm => DataContext as UpdatesViewModel;

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // у самого низа (с запасом под футер загрузки), но только когда в списке уже есть содержимое
        if (e.ExtentHeight <= 0 || e.ViewportHeight <= 0) return;
        if (e.ExtentHeight - (e.VerticalOffset + e.ViewportHeight) >= 24) return;
        Vm?.LoadMoreCommand.Execute(null);
    }
}
