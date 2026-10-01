using System.Windows.Controls;

namespace ImageFunn.App.Regios.Actie;

/// <summary>Interaction logic for Actiebalk.xaml - see that file for what it looks like.</summary>
public partial class Actiebalk : UserControl
{
    public Actiebalk()
    {
        InitializeComponent();

        RefreshAction.Clicked += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        LogAction.Clicked += (_, _) => LogRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>De verversknop is aangeklikt.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>De Log-knop is aangeklikt - MainWindow bepaalt wat dat voor de actieve pagina betekent.</summary>
    public event EventHandler? LogRequested;

    /// <summary>Staat het logpaneel van de huidige pagina open? MainWindow zet dit, niet de rail zelf.</summary>
    public bool LogAan
    {
        get => LogAction.AanVoorDezePagina;
        set => LogAction.AanVoorDezePagina = value;
    }

    /// <summary>Praat de huidige pagina met een model? Bepaalt of de Log-knop actief (of grijs) is.</summary>
    public bool LogBeschikbaar
    {
        get => LogAction.BeschikbaarVoorDezePagina;
        set => LogAction.BeschikbaarVoorDezePagina = value;
    }
}
