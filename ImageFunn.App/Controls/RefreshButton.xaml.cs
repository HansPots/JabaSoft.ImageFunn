using System.Windows.Controls;

namespace ImageFunn.App.Controls;

/// <summary>Interaction logic for RefreshButton.xaml - see that file for what it looks like.</summary>
public partial class RefreshButton : UserControl
{
    public RefreshButton()
    {
        InitializeComponent();

        RefreshAction.Click += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gaat af als er op de knop geklikt is. De knop doet zelf niets: wat
    /// er ververst moet worden weet alleen de pagina eromheen - net als bij
    /// Navigatiemenu.
    /// </summary>
    public event EventHandler? Clicked;
}
