using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeTun.Services;
using VibeTun.ViewModels;
using VibeTun.Views.Controls;
using ShapePath = System.Windows.Shapes.Path;

namespace VibeTun.Views;

public partial class ProfilesView : UserControl
{
    private const double LogSlideDistance = 24;
    private const double ProfileWheelScrollStep = 48;

    private bool _logFollow = true;
    private ProfilesViewModel? _subscribed;
    private Point _dragStart;
    private ProfileItemVm? _dragProfile;
    private ProfileGroupVm? _dragGroup;
    private ProfileItemVm? _contextProfile;
    private bool _dragCandidate;
    private bool _groupHeaderPressed;
    private bool _dragging;
    private bool _dragChanged;
    private ScrollViewer? _listScroll;
    private AdornerLayer? _dragAdornerLayer;
    private DragPreviewAdorner? _dragPreview;
    private FrameworkElement? _dragSourceElement;
    private Point _dragGrabOffset;
    private object? _lastLiveTarget;
    private bool _lastLiveAfter;
    private bool _hasLiveTarget;

    private readonly record struct LayoutVisual(FrameworkElement Element, Point Position, ProfileGroupVm? ParentGroup);
    private readonly record struct LiveMoveTarget(object Item, bool IntoGroup);

    private sealed class DragPreviewAdorner : Adorner
    {
        private readonly Brush _sourceBrush;
        private Point _pointer;
        private readonly Point _grabOffset;
        private readonly Size _size;

        public DragPreviewAdorner(UIElement adornedElement, FrameworkElement source, Point grabOffset)
            : base(adornedElement)
        {
            IsHitTestVisible = false;
            Opacity = 0.9;
            _sourceBrush = CaptureBrush(source);
            _grabOffset = new Point(
                Math.Clamp(grabOffset.X, 0, source.ActualWidth),
                Math.Clamp(grabOffset.Y, 0, source.ActualHeight));
            _size = source.RenderSize;
        }

        public void MoveTo(Point pointer)
        {
            _pointer = pointer;
            InvalidateVisual();
        }

        private static Brush CaptureBrush(FrameworkElement source)
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(source);
                var width = Math.Max(1, (int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX));
                var height = Math.Max(1, (int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY));
                var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                    PixelFormats.Pbgra32);
                bitmap.Render(source);
                if (bitmap.CanFreeze) bitmap.Freeze();
                return new ImageBrush(bitmap) { Stretch = Stretch.Fill };
            }
            catch (InvalidOperationException)
            {
                return new VisualBrush(source) { Stretch = Stretch.Fill };
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (_size.Width <= 0 || _size.Height <= 0) return;
            var topLeft = new Point(_pointer.X - _grabOffset.X, _pointer.Y - _grabOffset.Y);
            drawingContext.DrawRectangle(_sourceBrush, null, new Rect(topLeft, _size));
        }
    }

    public ProfilesView()
    {
        InitializeComponent();
        List.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnCollectionPreviewMouseDown), handledEventsToo: true);
        LogRow.Height = new GridLength(Math.Clamp(App.Settings.Current.ProfilesLogHeight, 100, 600));
        LogScroll.ScrollChanged += OnLogScrollChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribed is not null) _subscribed.Log.Updated -= ScrollLogToEnd;
        _subscribed = e.NewValue as ProfilesViewModel;
        if (_subscribed is not null) _subscribed.Log.Updated += ScrollLogToEnd;
    }

    private ProfilesViewModel? Vm => DataContext as ProfilesViewModel;

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var profile = FindDataContext<ProfileItemVm>(e.OriginalSource as DependencyObject);
        if (profile is not null) Vm?.ConnectCommand.Execute(profile);
    }

    /// <summary>ПКМ по профилю выбирает именно его; на пустом месте не оставляем старую команду выбранной.</summary>
    private void OnPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        _contextProfile = FindDataContext<ProfileItemVm>(e.OriginalSource as DependencyObject);
        if (Vm is not null) Vm.Selected = _contextProfile;
    }

    private void OnProfileContextMenuOpened(object sender, RoutedEventArgs e)
    {
        // An inner profile is hosted by a top-level group ListBoxItem; restore its selection
        // after WPF's right-click selection handling so menu commands target the profile.
        if (Vm is not null) Vm.Selected = _contextProfile;
    }

    private void OnCollectionPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) _groupHeaderPressed = false;
    }

    private void OnDragCandidateMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Vm is null) return;
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<ButtonBase>(source) is not null || FindAncestor<TextBox>(source) is not null)
        {
            ClearDragCandidate();
            return;
        }

        _dragProfile = FindDataContext<ProfileItemVm>(source);
        _dragGroup = _dragProfile is null ? FindDataContext<ProfileGroupVm>(source) : null;
        _groupHeaderPressed = _dragGroup is not null;
        _dragCandidate = _dragProfile is not null || _dragGroup is not null;
        _dragChanged = false;
        ResetLiveTarget();
        _dragSourceElement = _dragProfile is not null
            ? FindProfileCardAncestor(source, _dragProfile)
            : FindNamedBorderAncestor(source, "GroupHeader");
        if (_dragSourceElement is not null) _dragGrabOffset = e.GetPosition(_dragSourceElement);

        if (_dragProfile is not null)
        {
            Vm.Selected = _dragProfile;
            if (FindAncestor<ListBoxItem>(source)?.DataContext is ProfileGroupVm)
            {
                // Nested profiles must not select their parent group; handle their double-click here.
                e.Handled = true;
                if (e.ClickCount >= 2) Vm.ConnectCommand.Execute(_dragProfile);
            }
        }
        if (_dragCandidate) _dragStart = e.GetPosition(List);
    }

    private void OnCollectionPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (_dragging) FinishDrag();
            else ClearDragCandidate();
            return;
        }
        if (!_dragCandidate || Vm is null) return;
        if (!string.IsNullOrWhiteSpace(Vm.SearchText))
        {
            if (_dragging) FinishDrag();
            return;
        }

        var position = e.GetPosition(List);
        if (!_dragging)
        {
            if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            BeginDrag();
        }

        AutoScroll(e);
        List.UpdateLayout();
        position = e.GetPosition(List);
        _dragPreview?.MoveTo(position);
        var hit = List.InputHitTest(position) as DependencyObject;
        ApplyLiveMove(hit, position);
    }

    private void OnCollectionPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var wasDragging = _dragging;
        if (wasDragging) e.Handled = true;
        FinishDrag();
    }

    private void OnCollectionLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragging && !List.IsMouseCaptured) FinishDrag();
    }

    private void OnCollectionLoaded(object sender, RoutedEventArgs e)
    {
        List.ApplyTemplate();
        _listScroll = FindVisualDescendant<ScrollViewer>(List);
    }

    private void OnProfilesPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_listScroll is null) return;
        _listScroll.ScrollToVerticalOffset(_listScroll.VerticalOffset - e.Delta / 120.0 * ProfileWheelScrollStep);
        e.Handled = true;
    }

    private void OnGroupContentLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not SlideHost slide || slide.DataContext is not ProfileGroupVm group) return;
        slide.SelectedKey = group.IsExpanded ? "expanded" : "collapsed";
        var row = FindAncestor<ListBoxItem>(slide);
        var chevron = FindVisualDescendant<ShapePath>(row, path => path.Name == "GroupChevron");
        if (chevron is not null)
            Motion.WritableTransform<RotateTransform>(chevron).Angle = group.IsExpanded ? 180 : 0;
    }

    private void OnGroupHeaderMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_groupHeaderPressed) return;
        _groupHeaderPressed = false;
        if (e.ChangedButton != MouseButton.Left ||
            FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null) return;
        if (sender is not FrameworkElement header || header.DataContext is not ProfileGroupVm group) return;
        var position = e.GetPosition(header);
        if (position.X < 0 || position.Y < 0 || position.X > header.ActualWidth || position.Y > header.ActualHeight) return;
        ToggleGroup(group, header);
        e.Handled = true;
    }

    private void OnGroupToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button && button.DataContext is ProfileGroupVm group)
            ToggleGroup(group, button);
    }

    private void ToggleGroup(ProfileGroupVm group, FrameworkElement visualAnchor)
    {
        var row = FindAncestor<ListBoxItem>(visualAnchor);
        var slide = FindVisualDescendant<SlideHost>(row);
        if (slide is null) return;

        group.IsExpanded = !group.IsExpanded;
        slide.SelectedKey = group.IsExpanded ? "expanded" : "collapsed";
        var chevron = FindVisualDescendant<ShapePath>(row, path => path.Name == "GroupChevron");
        if (chevron?.RenderTransform is RotateTransform currentRotation)
        {
            var from = currentRotation.IsFrozen ? (group.IsExpanded ? 0 : 180) : currentRotation.Angle;
            var rotation = Motion.WritableTransform<RotateTransform>(chevron);
            Motion.Run(rotation, RotateTransform.AngleProperty, from, group.IsExpanded ? 180 : 0,
                Motion.Normal, EasingMode.EaseOut);
        }
    }

    private void BeginDrag()
    {
        _dragging = true;
        _dragChanged = false;
        ResetLiveTarget();
        if (_dragSourceElement is not null && AdornerLayer.GetAdornerLayer(List) is { } layer)
        {
            _dragAdornerLayer = layer;
            _dragPreview = new DragPreviewAdorner(List, _dragSourceElement, _dragGrabOffset);
            layer.Add(_dragPreview);
            _dragPreview.MoveTo(_dragStart);
        }
        if (_dragProfile is not null) _dragProfile.IsDragging = true;
        if (_dragGroup is not null) _dragGroup.IsDragging = true;
        List.CaptureMouse();
    }

    private void FinishDrag()
    {
        var wasDragging = _dragging;
        var layoutChanged = _dragChanged;
        if (wasDragging) _groupHeaderPressed = false;
        if (_dragProfile is not null) _dragProfile.IsDragging = false;
        if (_dragGroup is not null) _dragGroup.IsDragging = false;
        _dragging = false;
        _dragChanged = false;
        RemoveDragPreview();
        if (List.IsMouseCaptured) List.ReleaseMouseCapture();
        ClearDragCandidate();
        if (wasDragging && layoutChanged) Vm?.OnLayoutChanged();
    }

    private bool ApplyLiveMove(DependencyObject? hit, Point position)
    {
        var vm = Vm;
        if (vm is null || hit is null || !IsWithin(hit, List)) return false;

        if (_dragProfile is { } movingProfile)
        {
            var targetProfile = FindDataContext<ProfileItemVm>(hit);
            if (targetProfile is not null)
            {
                if (ReferenceEquals(targetProfile, movingProfile))
                {
                    ResetLiveTarget();
                    return false;
                }
                var targetCard = FindProfileCardAncestor(hit, targetProfile);
                if (targetCard is null) return false;
                var after = position.Y >= CenterY(targetCard);
                return RunLiveMove(targetProfile, after,
                    () => vm.ReorderProfile(movingProfile, targetProfile, after, persist: false));
            }

            var targetGroup = FindDataContext<ProfileGroupVm>(hit);
            if (targetGroup is not null)
            {
                if (List.ItemContainerGenerator.ContainerFromItem(targetGroup) is FrameworkElement groupRow)
                {
                    const double rootDropEdge = 14;
                    var rowTop = groupRow.TranslatePoint(new Point(0, 0), List).Y;
                    var rowBottom = rowTop + groupRow.ActualHeight;
                    if (position.Y <= rowTop + rootDropEdge)
                        return RunLiveMove(targetGroup, after: false,
                            () => vm.PutProfileAtRootPosition(movingProfile, targetGroup, after: false, persist: false));
                    if (position.Y >= rowBottom - rootDropEdge)
                        return RunLiveMove(targetGroup, after: true,
                            () => vm.PutProfileAtRootPosition(movingProfile, targetGroup, after: true, persist: false));
                }

                return RunLiveMove(targetGroup, after: false,
                    () => vm.PutProfileInGroup(movingProfile, targetGroup, persist: false), intoGroup: true);
            }

            if (FindAncestor<ScrollBar>(hit) is not null) return false;
            var (rootTarget, placeAfter) = FindNearestRootTarget(position.Y);
            if (rootTarget is null)
                return RunLiveMove(List, after: true,
                    () => vm.PutProfileAtEnd(movingProfile, persist: false));
            return RunLiveMove(rootTarget, placeAfter,
                () => vm.PutProfileAtRootPosition(movingProfile, rootTarget, placeAfter, persist: false));
        }

        if (_dragGroup is { } movingGroup)
        {
            if (FindAncestor<ScrollBar>(hit) is not null) return false;
            var (target, after) = FindNearestRootTarget(position.Y, movingGroup);
            if (target is null && vm.Entries.Count > 1) return false;
            var key = target ?? (object)List;
            return RunLiveMove(key, after,
                () => vm.ReorderGroup(movingGroup, target, after, persist: false));
        }

        return false;
    }

    private (object? Target, bool After) FindNearestRootTarget(double y, object? excluded = null)
    {
        if (Vm is not { } vm) return (null, true);
        object? nearest = null;
        var nearestDistance = double.PositiveInfinity;
        var after = true;

        foreach (var entry in vm.Entries)
        {
            if (ReferenceEquals(entry, excluded) ||
                List.ItemContainerGenerator.ContainerFromItem(entry) is not FrameworkElement row) continue;
            // Use the row's layout slot, not its FLIP-animated visual position. Otherwise
            // the animated midpoint can pass the stationary pointer after a reorder and
            // immediately request the inverse move (most visible with short, collapsed groups).
            var top = LayoutPositionWithoutMotion(row).Y;
            var bottom = top + row.ActualHeight;
            var distance = y < top ? top - y : y > bottom ? y - bottom : 0;
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearest = entry;
            after = y >= top + row.ActualHeight / 2;
        }

        return (nearest, after);
    }

    private bool RunLiveMove(object target, bool after, Func<bool> move, bool intoGroup = false)
    {
        object liveTarget = intoGroup ? new LiveMoveTarget(target, IntoGroup: true) : target;
        if (!RememberLiveTarget(liveTarget, after)) return false;
        // Snapshot the actual on-screen positions before changing the collection. If a prior
        // FLIP animation is still running, starting from the unanimated layout slot makes rows snap.
        var before = CaptureLayoutVisuals(includeMotion: true);
        if (!move()) return false;
        _dragChanged = true;
        List.UpdateLayout();
        AnimateReflow(before, CaptureLayoutVisuals(includeMotion: false));
        return true;
    }

    private bool RememberLiveTarget(object target, bool after)
    {
        if (_hasLiveTarget && object.Equals(_lastLiveTarget, target) && _lastLiveAfter == after) return false;
        _lastLiveTarget = target;
        _lastLiveAfter = after;
        _hasLiveTarget = true;
        return true;
    }

    private void ResetLiveTarget()
    {
        _lastLiveTarget = null;
        _lastLiveAfter = false;
        _hasLiveTarget = false;
    }

    private Point LayoutPositionWithoutMotion(FrameworkElement element, ProfileGroupVm? parentGroup = null)
    {
        var point = element.TranslatePoint(new Point(0, 0), List);
        if (element.RenderTransform is TranslateTransform elementMove)
        {
            point.X -= elementMove.X;
            point.Y -= elementMove.Y;
        }
        if (parentGroup is not null &&
            List.ItemContainerGenerator.ContainerFromItem(parentGroup) is FrameworkElement parentRow &&
            parentRow.RenderTransform is TranslateTransform parentMove)
        {
            point.X -= parentMove.X;
            point.Y -= parentMove.Y;
        }
        return point;
    }

    private Dictionary<object, LayoutVisual> CaptureLayoutVisuals(bool includeMotion)
    {
        var result = new Dictionary<object, LayoutVisual>();
        if (Vm is not { } vm) return result;

        Point PositionOf(FrameworkElement element, ProfileGroupVm? parentGroup = null) => includeMotion
            ? element.TranslatePoint(new Point(0, 0), List)
            : LayoutPositionWithoutMotion(element, parentGroup);

        foreach (var entry in vm.Entries)
        {
            if (List.ItemContainerGenerator.ContainerFromItem(entry) is not FrameworkElement row) continue;
            result[entry] = new LayoutVisual(row, PositionOf(row), null);
            if (entry is not ProfileGroupVm group) continue;

            foreach (var profile in group.Profiles)
            {
                var card = FindProfileCardWithin(row, profile);
                if (card is not null)
                    result[profile] = new LayoutVisual(card, PositionOf(card, group), group);
            }
        }
        return result;
    }

    private void AnimateReflow(Dictionary<object, LayoutVisual> before, Dictionary<object, LayoutVisual> after)
    {
        foreach (var pair in after)
        {
            if (!before.TryGetValue(pair.Key, out var oldVisual)) continue;
            var newVisual = pair.Value;
            var parentDelta = new Vector();
            if (newVisual.ParentGroup is { } parent &&
                before.TryGetValue(parent, out var oldParent) && after.TryGetValue(parent, out var newParent))
            {
                parentDelta = oldParent.Position - newParent.Position;
            }

            var dx = oldVisual.Position.X - newVisual.Position.X - parentDelta.X;
            var dy = oldVisual.Position.Y - newVisual.Position.Y - parentDelta.Y;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) continue;

            var move = Motion.WritableTransform<TranslateTransform>(newVisual.Element);
            Motion.Run(move, TranslateTransform.XProperty, dx, 0, Motion.Scaled(190), EasingMode.EaseOut);
            Motion.Run(move, TranslateTransform.YProperty, dy, 0, Motion.Scaled(190), EasingMode.EaseOut);
        }
    }

    private void AutoScroll(MouseEventArgs e)
    {
        if (_listScroll is null || _listScroll.ViewportHeight <= 0) return;
        const double edge = 28;
        const double step = 14;
        var y = e.GetPosition(_listScroll).Y;
        if (y < edge) _listScroll.ScrollToVerticalOffset(_listScroll.VerticalOffset - step);
        else if (y > _listScroll.ViewportHeight - edge)
            _listScroll.ScrollToVerticalOffset(_listScroll.VerticalOffset + step);
    }

    private double CenterY(FrameworkElement element) =>
        element.TranslatePoint(new Point(0, element.ActualHeight / 2), List).Y;

    private static Border? FindProfileCardAncestor(DependencyObject? current, ProfileItemVm profile)
    {
        while (current is not null)
        {
            if (current is Border { Name: "ProfileCard" } border && ReferenceEquals(border.DataContext, profile)) return border;
            current = ParentOf(current);
        }
        return null;
    }

    private static Border? FindProfileCardWithin(DependencyObject root, ProfileItemVm profile) =>
        FindVisualDescendant<Border>(root, border => border.Name == "ProfileCard" && ReferenceEquals(border.DataContext, profile));

    private static Border? FindNamedBorderAncestor(DependencyObject? current, string name)
    {
        while (current is not null)
        {
            if (current is Border border && border.Name == name) return border;
            current = ParentOf(current);
        }
        return null;
    }

    private static T? FindVisualDescendant<T>(DependencyObject? root, Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        if (root is null) return null;
        if (root is T match && (predicate is null || predicate(match))) return match;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindVisualDescendant<T>(VisualTreeHelper.GetChild(root, i), predicate);
            if (found is not null) return found;
        }
        return null;
    }

    private static T? FindDataContext<T>(DependencyObject? current) where T : class
    {
        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is T data) return data;
            if (current is FrameworkContentElement content && content.DataContext is T contentData) return contentData;
            current = ParentOf(current);
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed) return typed;
            current = ParentOf(current);
        }
        return null;
    }

    private static bool IsWithin(DependencyObject? current, DependencyObject ancestor)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = ParentOf(current);
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject current) => current is Visual or Visual3D
        ? VisualTreeHelper.GetParent(current)
        : LogicalTreeHelper.GetParent(current);

    private void RemoveDragPreview()
    {
        if (_dragAdornerLayer is not null && _dragPreview is not null)
            _dragAdornerLayer.Remove(_dragPreview);
        _dragAdornerLayer = null;
        _dragPreview = null;
    }

    private void ClearDragCandidate()
    {
        _dragProfile = null;
        _dragGroup = null;
        _dragCandidate = false;
        _dragSourceElement = null;
        _dragGrabOffset = default;
        ResetLiveTarget();
    }

    private void OnLogSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        var rowHeight = LogPanel.ActualHeight + LogPanel.Margin.Top + LogPanel.Margin.Bottom;
        App.Settings.Current.ProfilesLogHeight = Math.Round(rowHeight);
        App.Settings.SaveSoon();
    }

    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0)
            _logFollow = LogScroll.VerticalOffset >= LogScroll.ScrollableHeight - 4;
    }

    private void ScrollLogToEnd()
    {
        if (!_logFollow) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => LogScroll.ScrollToEnd()));
    }

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
