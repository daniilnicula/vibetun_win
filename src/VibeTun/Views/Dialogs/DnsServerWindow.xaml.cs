using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VibeTun.Utils;

namespace VibeTun.Views.Dialogs;

/// <summary>Небольшое окно «Свой DNS» (кнопка «Добавить свой» и карандаш в строке): IP-адрес и название.</summary>
public partial class DnsServerWindow : DialogWindow
{
    private bool _done;

    public DnsServerWindow(string title, string okText, string name, string address)
    {
        InitializeComponent();
        TitleText.Text = title;
        OkButton.Content = okText;
        AddressBox.Text = address;
        NameBox.Text = name;
    }

    /// <summary>Название для списка; пустое поле — адрес.</summary>
    public string ServerName { get; private set; } = "";

    /// <summary>Проверенный адрес в виде для конфига ядра.</summary>
    public string Address { get; private set; } = "";

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnOk(sender, e);
    }

    /// <summary>Esc закрывает окно, где бы ни был фокус.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        OnCancel(sender, e);
    }

    /// <summary>Ошибка под полем пропадает, как только адрес начинают править.</summary>
    private void OnAddressChanged(object sender, TextChangedEventArgs e)
    {
        if (ErrorText is not null) ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_done) return;

        if (!DnsAddress.TryNormalize(AddressBox.Text, out var address, out var error))
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            AddressBox.Focus();
            AddressBox.SelectAll();
            return;
        }

        var name = (NameBox.Text ?? "").Trim();
        ServerName = name.Length > 0 ? name : address;
        Address = address;

        _done = true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_done) return;
        _done = true;
        DialogResult = false;
        Close();
    }
}
