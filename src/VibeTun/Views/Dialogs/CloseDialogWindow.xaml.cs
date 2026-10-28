using System.Windows;
using System.Windows.Input;

namespace VibeTun.Views.Dialogs;

/// <summary>
/// Диалог закрытия: «Скрыть или выйти из приложения?».
/// DialogResult: true — скрыть в трей, false — выйти полностью, null — отмена (Esc).
/// </summary>
public partial class CloseDialogWindow : DialogWindow
{
    public CloseDialogWindow()
    {
        InitializeComponent();
    }

    /// <summary>Отметка «Запомнить мой выбор» — вызывающий код сохраняет выбранный режим.</summary>
    public bool RememberMyChoice => RememberBox.IsChecked == true;

    /// <summary>Явный выбор пользователя: true — скрыть, false — выйти, null — только закрыть диалог.</summary>
    public bool? CloseChoice { get; private set; }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnHide(object sender, RoutedEventArgs e)
    {
        CloseChoice = true;
        DialogResult = true;
        Close();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        CloseChoice = false;
        DialogResult = false;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CloseChoice = null;
        Close();   // Крестик не выбирает ни «Скрыть», ни «Закрыть приложение».
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); return; }   // DialogResult остаётся null — отмена
        base.OnKeyDown(e);
    }
}
