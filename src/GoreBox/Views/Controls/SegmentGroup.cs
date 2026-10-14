using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace GoreBox.Views.Controls;

/// <summary>
/// Сегментированный переключатель (ряд ToggleButton): подсветка выбранного сегмента переезжает
/// к новому, а не вспыхивает на нём. Включается на Grid-обёртке ряда:
/// <c>ctl:SegmentGroup.IsGroup="True"</c>, скругление — <c>ctl:SegmentGroup.Radius</c>.
/// Цвет подсветки — ключ кисти <c>SegmentGroup.Key</c>, его задают стили сегментов
/// (Accent / DangerSoft / BorderStrong). У сегментов внутри группы фон при выборе не задаётся шаблоном.
/// </summary>
public static class SegmentGroup
{
    public static readonly DependencyProperty IsGroupProperty = DependencyProperty.RegisterAttached(
        "IsGroup", typeof(bool), typeof(SegmentGroup), new PropertyMetadata(false, OnIsGroupChanged));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(SegmentGroup), new PropertyMetadata(7d));

    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(SegmentGroup), new PropertyMetadata("Accent"));

    public static bool GetIsGroup(DependencyObject element) => (bool)element.GetValue(IsGroupProperty);
    public static void SetIsGroup(DependencyObject element, bool value) => element.SetValue(IsGroupProperty, value);

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);
    public static void SetKey(DependencyObject element, string? value) => element.SetValue(KeyProperty, value);

    private static void OnIsGroupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid || e.NewValue is not true) return;
        if (grid.IsLoaded) Attach(grid);
        else grid.Loaded += (_, _) => Attach(grid);
    }

    /// <summary>Однократная настройка: подсветка, подписки на выбор сегментов и на изменение размера.</summary>
    private static void Attach(Grid grid)
    {
        if (grid.Tag is SlideMarker) return;

        var marker = new Border
        {
            CornerRadius = new CornerRadius(GetRadius(grid)),
        };
        grid.Children.Insert(0, marker);

        var toggles = new List<ToggleButton>();
        CollectToggles(grid, toggles);

        var slide = new SlideMarker(
            grid,
            marker,
            () => toggles.FirstOrDefault(t => t.IsChecked == true),
            toggle => GetKey(toggle) ?? "Accent");
        grid.Tag = slide;

        RoutedEventHandler refresh = (_, _) => grid.Dispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => slide.Sync(true)));
        foreach (var toggle in toggles)
        {
            toggle.Checked += refresh;
            toggle.Unchecked += refresh;
        }

        // Раскрытие страницы и смена размера приходят во время раскладки, когда размеры кнопок
        // внутри ряда ещё не выставлены. Поэтому пересинхронизируем отложенно, после раскладки.
        grid.SizeChanged += (_, _) => Reconcile(grid, slide);
        grid.IsVisibleChanged += (_, _) => Reconcile(grid, slide);
        slide.Sync(false);
        Reconcile(grid, slide);
    }

    private static void Reconcile(Grid grid, SlideMarker slide) =>
        grid.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => slide.Sync(false)));

    /// <summary>Все ToggleButton ряда; внутрь самих переключателей не заходим.</summary>
    private static void CollectToggles(DependencyObject node, List<ToggleButton> toggles)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is ToggleButton toggle) toggles.Add(toggle);
            else CollectToggles(child, toggles);
        }
    }
}
