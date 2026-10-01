using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace ImageFunn.App.Controls;

/// <summary>
/// De standaard app-schil - header, footer, linker menu en rechter
/// actierail rond een middenvak - zie Basis.xaml voor de volledige
/// toelichting op de vijf content-slots.
///
/// Geen van de vijf slots heet "Content": die overgeërfde
/// ContentControl-eigenschap is al intern in gebruik (InitializeComponent
/// hangt Basis.xaml's eigen rij/kolom-Grid daaraan op), dus een
/// MainContent-achtige waarde zonder eigen naam zou de hele indeling
/// stilletjes vervangen.
/// [ContentProperty(nameof(MainContent))] stuurt impliciete XAML-inhoud
/// (&lt;controls:Basis&gt;...&lt;/controls:Basis&gt;) naar het middenvak.
/// </summary>
[ContentProperty(nameof(MainContent))]
public partial class Basis : UserControl
{
    public static readonly DependencyProperty HeaderContentProperty = DependencyProperty.Register(
        nameof(HeaderContent), typeof(object), typeof(Basis), new PropertyMetadata(null));

    public static readonly DependencyProperty MenuContentProperty = DependencyProperty.Register(
        nameof(MenuContent), typeof(object), typeof(Basis), new PropertyMetadata(null));

    public static readonly DependencyProperty MainContentProperty = DependencyProperty.Register(
        nameof(MainContent), typeof(object), typeof(Basis), new PropertyMetadata(null));

    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent), typeof(object), typeof(Basis), new PropertyMetadata(null));

    public static readonly DependencyProperty FooterContentProperty = DependencyProperty.Register(
        nameof(FooterContent), typeof(object), typeof(Basis), new PropertyMetadata(null));

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public object? MenuContent
    {
        get => GetValue(MenuContentProperty);
        set => SetValue(MenuContentProperty, value);
    }

    public object? MainContent
    {
        get => GetValue(MainContentProperty);
        set => SetValue(MainContentProperty, value);
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    public Basis()
    {
        InitializeComponent();
    }
}
