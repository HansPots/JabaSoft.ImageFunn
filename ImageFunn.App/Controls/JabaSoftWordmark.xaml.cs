using System.Windows;
using System.Windows.Controls;

namespace ImageFunn.App.Controls;

/// <summary>Interaction logic for JabaSoftWordmark.xaml - see that file for what it looks like.</summary>
public partial class JabaSoftWordmark : UserControl
{
    /// <summary>
    /// De naam onder "JabaSoft" - welke applicatie dit beeldmerk draagt.
    /// Een DependencyProperty en geen gewone property, zodat een app hem
    /// ook via een Binding kan vullen en het beeldmerk (en daarmee de
    /// zwarte balk eronder) zich vanzelf opnieuw meet als de naam wijzigt.
    /// </summary>
    public static readonly DependencyProperty ApplicationNameProperty =
        DependencyProperty.Register(
            nameof(ApplicationName),
            typeof(string),
            typeof(JabaSoftWordmark),
            new PropertyMetadata("the Application"));

    public string ApplicationName
    {
        get => (string)GetValue(ApplicationNameProperty);
        set => SetValue(ApplicationNameProperty, value);
    }

    public JabaSoftWordmark()
    {
        InitializeComponent();
    }
}
