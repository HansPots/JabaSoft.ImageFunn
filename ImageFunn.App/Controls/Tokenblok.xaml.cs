using System.Globalization;
using System.Windows.Controls;
using System.Windows.Threading;
using Jabasoft.Base.AiBroker;

namespace ImageFunn.App.Controls;

/// <summary>
/// Interaction logic for Tokenblok.xaml - see that file for what it looks like.
///
/// Haalt de cijfers bij de broker op, niet bij de database: die verbinding
/// ligt op één plek en dat blijft zo.
///
/// Ververst op dezelfde REFRESH TIME als de meters in de footer. De tijd
/// wordt bij ELKE tik opnieuw gelezen, zodat het schuiven van die
/// instelling meteen aankomt zonder dat deze kaart iets van het
/// instellingenscherm hoeft te weten.
/// </summary>
public partial class Tokenblok : UserControl
{
    /// <summary>Waar op teruggevallen wordt als de instelling er (nog) niet is - gelijk aan Setting-03's standaard.</summary>
    private const double FallbackIntervalSeconds = 5;

    private readonly IAiBrokerClient _broker = AiBrokerClient.CreateDefault();
    private readonly DispatcherTimer _timer = new();

    /// <summary>Voorkomt dat een trage vraag door een volgende tik ingehaald wordt.</summary>
    private bool _bezig;

    public Tokenblok()
    {
        InitializeComponent();

        _timer.Tick += async (_, _) => await VerversAsync();

        Loaded += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromSeconds(ReadIntervalSeconds());
            _timer.Start();
            await VerversAsync();
        };

        // Uit als het blok niet in beeld is: dan hoeft er ook niets
        // opgehaald te worden.
        Unloaded += (_, _) => _timer.Stop();
    }

    private async Task VerversAsync()
    {
        _timer.Interval = TimeSpan.FromSeconds(ReadIntervalSeconds());

        if (_bezig)
        {
            return;
        }

        _bezig = true;
        try
        {
            var modellen = await _broker.GetUsageModelsAsync(CancellationToken.None);
            Toon(modellen);
        }
        finally
        {
            _bezig = false;
        }
    }

    /// <summary>Zet het totaal en de drie zwaarste modellen neer.</summary>
    public void Toon(IReadOnlyList<TokenUsageModel> modellen)
    {
        ArgumentNullException.ThrowIfNull(modellen);

        TotaalText.Text = modellen.Sum(model => model.TotalTokens).ToString("N0", CultureInfo.CurrentCulture);

        // De lijst komt al gesorteerd binnen (zwaarste eerst); hier alleen
        // de eerste drie over de drie vaste regels verdelen.
        ZetRegel(0, modellen, Model1, Waarde1);
        ZetRegel(1, modellen, Model2, Waarde2);
        ZetRegel(2, modellen, Model3, Waarde3);
    }

    private static void ZetRegel(int plaats, IReadOnlyList<TokenUsageModel> modellen, TextBlock naam, TextBlock waarde)
    {
        if (plaats >= modellen.Count)
        {
            naam.Text = string.Empty;
            waarde.Text = string.Empty;
            return;
        }

        naam.Text = modellen[plaats].Model;
        waarde.Text = modellen[plaats].TotalTokens.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>De ververstijd uit de resources, gezet door de REFRESH TIME-kaart (Controls/Setting-03).</summary>
    private double ReadIntervalSeconds() =>
        TryFindResource("RefreshIntervalSeconds") is double seconds && seconds > 0
            ? seconds
            : FallbackIntervalSeconds;
}
