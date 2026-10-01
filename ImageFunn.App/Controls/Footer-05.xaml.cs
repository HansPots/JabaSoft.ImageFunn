using System.Windows;
using System.Windows.Controls;

namespace ImageFunn.App.Controls;

/// <summary>Interaction logic for Footer-05.xaml - see that file for what it looks like.</summary>
public partial class Footer05 : UserControl
{
    public Footer05()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Zet de waarde en de stand van het balkje - zelfde opzet als
    /// Footer02.Show. <paramref name="fraction"/> is 0 tot 1, buiten dat
    /// bereik geklemd.
    /// </summary>
    public void Show(string value, double fraction)
    {
        ValueText.Text = value;

        var filled = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        BarFill.Width = new GridLength(filled, GridUnitType.Star);
        BarRest.Width = new GridLength(1 - filled, GridUnitType.Star);
    }

    /// <summary>Geen vraag (meer) aan het lopen, of de actieve server geeft dit niet door (Ollama) - de pil terug naar leeg.</summary>
    public void ToonInactief() => Show(string.Empty, 0);
}
