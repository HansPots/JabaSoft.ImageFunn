using System.Windows;
using System.Windows.Controls;

namespace ImageFunn.App.Controls;

/// <summary>Interaction logic for Footer-03.xaml - see that file for what it looks like.</summary>
public partial class Footer03 : UserControl
{
    public Footer03()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Zet de waarde en de stand van het balkje. De vulling loopt via de
    /// sterverhouding van de twee kolommen: die klopt bij elke pilbreedte,
    /// in tegenstelling tot een vaste breedte in pixels.
    ///
    /// <paramref name="fraction"/> is 0 tot 1. Buiten dat bereik wordt
    /// geklemd, zodat een rare meting het balkje niet uit zijn pil duwt.
    /// </summary>
    public void Show(string value, double fraction)
    {
        ValueText.Text = value;

        var filled = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        BarFill.Width = new GridLength(filled, GridUnitType.Star);
        BarRest.Width = new GridLength(1 - filled, GridUnitType.Star);
    }
}
