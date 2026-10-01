using System.Windows.Controls;

namespace ImageFunn.App.Controls;

/// <summary>Interaction logic for LogButton.xaml - see that file for what it looks like.</summary>
public partial class LogButton : UserControl
{
    public LogButton()
    {
        InitializeComponent();

        LogAction.Click += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Gaat af als er op de knop geklikt is (ook als hij daarmee AAN of UIT gaat). MainWindow bepaalt wat dat voor de actieve pagina betekent.</summary>
    public event EventHandler? Clicked;

    /// <summary>Of het logpaneel van de huidige pagina open staat - MainWindow zet dit, niet de knop zelf.</summary>
    public bool AanVoorDezePagina
    {
        get => LogAction.IsChecked == true;
        set => LogAction.IsChecked = value;
    }

    /// <summary>Of de huidige pagina überhaupt met een model praat (zie IHeeftLog) - anders staat de knop grijs.</summary>
    public bool BeschikbaarVoorDezePagina
    {
        get => LogAction.IsEnabled;
        set => LogAction.IsEnabled = value;
    }
}
