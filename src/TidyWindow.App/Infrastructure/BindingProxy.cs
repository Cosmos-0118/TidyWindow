using System.Windows;

namespace TidyWindow.App.Infrastructure;

/// <summary>
/// Provides a binding-friendly proxy so StaticResource consumers inside templates can access data context values.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
