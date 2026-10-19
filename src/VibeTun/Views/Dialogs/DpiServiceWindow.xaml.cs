using System.Windows;
using System.Windows.Input;
using VibeTun.Services;
using VibeTun.ViewModels;

namespace VibeTun.Views.Dialogs;

/// <summary>
/// Окно сервиса раздела «DPI»: домен, приёмы обхода, профили настроек, журнал проверки и кнопки
/// «Тест», «Подобрать», «Отмена», «Сохранить». Всё состояние живёт в
/// <see cref="DpiServiceEditorViewModel"/>; здесь — только окно и прокрутка журнала.
/// </summary>
public partial class DpiServiceWindow : DialogWindow
{
    private readonly DpiServiceEditorViewModel _vm;
    private bool _done;

    public DpiServiceWindow(DpiServiceEditRequest request, IDialogService dialogs)
    {
        InitializeComponent();

        _vm = new DpiServiceEditorViewModel(request, dialogs);
        DataContext = _vm;
        TitleText.Text = _vm.Title;

        // журнал всегда показывает последние строки — как панель логов на вкладке «Профили»
        _vm.Log.Updated += ScrollLogToEnd;
    }

    /// <summary>Что вернёт окно после «Сохранить».</summary>
    public DpiServiceEditResult Result => _vm.Build();

    private void OnLoaded(object sender, RoutedEventArgs e) => ScrollLogToEnd();

    /// <summary>Журнал прокручивается к последней записи: видно, что происходит прямо сейчас.</summary>
    private void ScrollLogToEnd() => LogScroll.ScrollToEnd();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_done || !_vm.CanSave) return;
        _done = true;

        _vm.Cancel();   // незаконченный тест гасим: его результат уже не нужен
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_done) return;
        _done = true;

        _vm.Cancel();
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// «Выбрать DNS…»: сохраняем сервис и уходим в раздел «DNS» — там выбирают DNS так же, как для
    /// домена и приложения. После выбора раздел вернётся в «DPI» (см. DpiViewModel.DnsPickRequested).
    /// </summary>
    private void OnPickDns(object sender, RoutedEventArgs e)
    {
        if (_done || !_vm.CanSave) return;
        _done = true;

        _vm.Cancel();   // незаконченный тест гасим: его результат уже не нужен
        _vm.RequestPickDns();
        DialogResult = true;
        Close();
    }

    private void OnStop(object sender, RoutedEventArgs e) => _vm.StopCommand.Execute(null);

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    /// <summary>Esc закрывает окно, где бы ни был фокус.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        OnCancel(sender, e);
    }

    private void OnLogCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = _vm.Log.Snapshot();
            if (text.Length > 0) Clipboard.SetText(text);
        }
        catch { /* буфер обмена занят другим приложением */ }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Log.Updated -= ScrollLogToEnd;
        _vm.Cancel();
        base.OnClosed(e);
    }
}
