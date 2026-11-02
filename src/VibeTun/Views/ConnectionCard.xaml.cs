using System.Windows;
using System.Windows.Controls;

namespace VibeTun.Views;

/// <summary>Карточка «Proxy / DPI bypass»: статус, быстрое включение и выключение, трафик.</summary>
public partial class ConnectionCard : UserControl
{
    public static readonly DependencyProperty GlassProperty = DependencyProperty.Register(
        nameof(Glass), typeof(bool), typeof(ConnectionCard), new PropertyMetadata(false));

    public ConnectionCard()
    {
        InitializeComponent();
    }

    /// <summary>Полупрозрачный слой поверх размытой левой панели (только в главном окне).</summary>
    public bool Glass
    {
        get => (bool)GetValue(GlassProperty);
        set => SetValue(GlassProperty, value);
    }
}
