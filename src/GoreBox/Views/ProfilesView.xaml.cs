using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GoreBox.Services;
using GoreBox.ViewModels;
using GoreBox.Views.Controls;

namespace GoreBox.Views;

public partial class ProfilesView : UserControl
{
    private static readonly TimeSpan HighlightTime = TimeSpan.FromMilliseconds(280);

    /// <summary>С какого сдвига влево выезжает новая строка журнала, px.</summary>
    private const double LogSlideDistance = 24;

    private bool _logFollow = true;
    private ProfilesViewModel? _subscribed;

    public ProfilesView()
    {
        InitializeComponent();
        // высота панели логов запоминается между запусками
        LogRow.Height = new GridLength(Math.Clamp(App.Settings.Current.ProfilesLogHeight, 100, 600));
        LogScroll.ScrollChanged += OnLogScrollChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribed is not null)
        {
            _subscribed.ActiveChanged -= MoveActiveHighlight;
            _subscribed.Log.Updated -= ScrollLogToEnd;
        }
        _subscribed = e.NewValue as ProfilesViewModel;
        if (_subscribed is not null)
        {
            _subscribed.ActiveChanged += MoveActiveHighlight;
            _subscribed.Log.Updated += ScrollLogToEnd;
        }
    }

    /// <summary>
    /// Рамка активного профиля переезжает: на старой строке гаснет, уезжая в сторону новой,
    /// на новой появляется, приезжая со старой позиции. Если строки не нарисованы — просто появляется.
    /// </summary>
    private void MoveActiveHighlight(string? fromId, string? toId)
    {
        var from = RowOf(fromId);
        var to = RowOf(toId);
        var shift = from is not null && to is not null ? TopOf(from) - TopOf(to) : 0;
        if (to is not null) AnimateHighlight(to, shift, 0, appear: true);
        if (from is not null) AnimateHighlight(from, 0, -shift, appear: false);
    }

    private ListBoxItem? RowOf(string? id)
    {
        if (id is null || Vm is null) return null;
        var item = Vm.Find(id);
        return item is null ? null : List.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
    }

    private double TopOf(ListBoxItem row) => row.TranslatePoint(new Point(0, 0), List).Y;

    private static void AnimateHighlight(ListBoxItem row, double fromY, double toY, bool appear)
    {
        row.ApplyTemplate();
        if (row.Template?.FindName("hl", row) is not Border highlight) return;

        var move = Motion.WritableTransform<TranslateTransform>(highlight);
        Motion.Run(move, TranslateTransform.YProperty, fromY, toY, HighlightTime, EasingMode.EaseInOut);
        Motion.Run(highlight, UIElement.OpacityProperty, appear ? 0 : 1, appear ? 1 : 0, HighlightTime);
    }

    private ProfilesViewModel? Vm => DataContext as ProfilesViewModel;

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        Vm?.ConnectCommand.Execute(null);
    }

    /// <summary>ПКМ по элементу списка должен выделять его — тогда меню работает по нужному профилю.</summary>
    private void OnPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is not null) item.IsSelected = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed) return typed;
            // TextElement (Run внутри TextBlock) не является Visual — VisualTreeHelper.GetParent
            // падает с «not a Visual or Visual3D»; для них идём по логическому дереву.
            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>Разделитель отпустили: запоминаем новую высоту панели логов.</summary>
    private void OnLogSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        var rowHeight = LogPanel.ActualHeight + LogPanel.Margin.Top + LogPanel.Margin.Bottom;
        App.Settings.Current.ProfilesLogHeight = Math.Round(rowHeight);
        App.Settings.SaveSoon();
    }

    /// <summary>
    /// Автопрокрутка журнала: следим, у конца ли пользователь. Если он прокрутил вверх,
    /// новые строки его не сдвигают.
    /// </summary>
    /// <summary>
    /// Автопрокрутка журнала: следим, у конца ли пользователь. Если он прокрутил вверх,
    /// новые строки его не сдвигают.
    /// </summary>
    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0)
            _logFollow = LogScroll.VerticalOffset >= LogScroll.ScrollableHeight - 4;
    }

    /// <summary>Строки журнала изменились: если пользователь у конца — прокручиваем вниз после раскладки.</summary>
    private void ScrollLogToEnd()
    {
        if (!_logFollow) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => LogScroll.ScrollToEnd()));
    }

    /// <summary>
    /// Новая строка журнала появляется плавно: выезжает слева и проявляется.
    /// Скорость — общая для анимаций (настройка «Скорость анимаций»). Уже показанные строки не анимируются.
    /// </summary>
    private void OnLogLineLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border row || row.DataContext is not LogLine { IsFresh: true } line) return;
        line.IsFresh = false;
        var move = Motion.WritableTransform<TranslateTransform>(row);

        Motion.Run(move, TranslateTransform.XProperty, -LogSlideDistance, 0, Motion.Normal, EasingMode.EaseOut);
        Motion.Run(row, UIElement.OpacityProperty, 0, 1, Motion.Normal, EasingMode.EaseOut);
    }

    private void OnLogCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Vm?.Log.Snapshot() ?? "");
        }
        catch (Exception)
        {
            // буфер обмена занят другим приложением — пропускаем
        }
    }
}
