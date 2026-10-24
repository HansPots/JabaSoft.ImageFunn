using System.Windows.Automation;
using System.Windows.Controls;
using Jabasoft.Base;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Health;
using Jabasoft.Base.Logging;
using ImageFunn.App.Controls;
using InhoudSchermen = ImageFunn.App.Regios.Inhoud;

namespace ImageFunn.App;

/// <summary>
/// ImageFunn als onderdeel van Jabasoft: de hele paginaschil van deze app,
/// die Jabasoft in zijn eigen venster zet. Was eerst een eigen venster in
/// een eigen proces; nu blijft alles in dezelfde applicatie.
///
/// Zelfde opbouw als Jabasoft.App/MainWindow.xaml.cs: header, footer, menu
/// en actierail werken hier op precies dezelfde manier. Wat bij een venster
/// hoort (maximaliseren, venstermaat onthouden) regelt Jabasoft.
/// </summary>
public partial class ImageFunnView : UserControl
{
    /// <summary>
    /// Gaat af als er op Home geklikt is. Deze view weet niet waar Home
    /// naartoe leidt - dat bepaalt Jabasoft, die deze view in zijn venster
    /// heeft gezet.
    /// </summary>
    public event EventHandler? HomeRequested;

    /// <summary>Het startscherm van deze app. Eén keer gemaakt en hergebruikt, zodat wat je erin invult niet weg is als je even naar iets anders kijkt.</summary>
    private readonly InhoudSchermen.Startscherm _startscherm = new();

    /// <summary>De verbinding met de broker: levert de AI-instelling en de lijst met aanwezige modellen aan de gezondheidscontrole en aan het beeldmodel.</summary>
    private readonly IAiBrokerClient _broker = AiBrokerClient.CreateDefault();

    /// <summary>Het scherm waar een afbeelding omgezet wordt naar tekst.</summary>
    private readonly InhoudSchermen.BeeldNaarTekst _beeldNaarTekst;

    /// <summary>Het scherm waar uit tekst een beeld gemaakt wordt.</summary>
    private readonly InhoudSchermen.TekstNaarBeeld _tekstNaarBeeld;

    /// <summary>Het scherm waar een beeld tot een korte video geanimeerd wordt.</summary>
    private readonly InhoudSchermen.BeeldNaarVideo _beeldNaarVideo;

    /// <summary>Het scherm waar uit tekst direct een video gemaakt wordt.</summary>
    private readonly InhoudSchermen.TekstNaarVideo _tekstNaarVideo;

    /// <summary>Het scherm waar een langere film uit scènes op een tijdlijn gemaakt wordt.</summary>
    private readonly InhoudSchermen.FilmMaker _filmMaker;

    /// <summary>De bewaking van deze app. Eén per applicatie: de pil in de footer kijkt hiernaar.</summary>
    private HealthMonitor? _monitor;

    /// <summary>Het menu zelf, om te kunnen zeggen welke pagina open staat.</summary>
    private Navigatiemenu? _menu;

    public ImageFunnView()
    {
        InitializeComponent();

        _beeldNaarTekst = new InhoudSchermen.BeeldNaarTekst(_broker);
        _tekstNaarBeeld = new InhoudSchermen.TekstNaarBeeld(_broker);
        _beeldNaarVideo = new InhoudSchermen.BeeldNaarVideo(_broker);
        _tekstNaarVideo = new InhoudSchermen.TekstNaarVideo(_broker);
        _filmMaker = new InhoudSchermen.FilmMaker(_broker);
        _tekstNaarBeeld.NaarFilm += (_, item) => _filmMaker.NeemCastOver(item);
        _tekstNaarBeeld.AnimatieDoorgeven += (_, pad) =>
        {
            _beeldNaarVideo.ZetBron(pad);
            ShowContent(NavigatiemenuItem.BeeldNaarVideo);
        };

        // De twee schermen kunnen elkaar iets doorgeven: een gelezen prompt
        // wordt een nieuw beeld, een gemaakt beeld wordt opnieuw beschreven.
        _beeldNaarTekst.PromptDoorgeven += (_, prompt) =>
        {
            _tekstNaarBeeld.ZetPrompt(prompt);
            ShowContent(NavigatiemenuItem.TekstNaarBeeld);
        };
        _tekstNaarBeeld.BeeldDoorgeven += (_, pad) =>
        {
            _beeldNaarTekst.Laad(pad);
            ShowContent(NavigatiemenuItem.BeeldNaarTekst);
        };

        BuildMenu();
        WireActionRail();
        ShowContent(NavigatiemenuItem.Home);
        ActivityLog.Shared.Add("app", "ImageFunn geopend");
        StartHealthCheck();
    }

    /// <summary>
    /// Zet het menu van deze app in het Menu-vak. Het menu meldt alleen
    /// WELKE knop is aangeklikt; wat daarop gebeurt staat hier.
    /// </summary>
    private void BuildMenu()
    {
        _menu = new Navigatiemenu();

        // Het nummer van DEZE bibliotheek en niet van de exe waar hij in
        // draait (dat is Jabasoft): elk onderdeel telt zijn eigen
        // wijzigingen. Ophogen gebeurt in <Version> in ImageFunn.App.csproj.
        _menu.Version = $"V {AppVersion.Of(typeof(ImageFunnView).Assembly)}";

        AutomationProperties.SetName(_menu, "Hoofdmenu");
        _menu.ItemSelected += (_, item) => Navigate(item);
        AppShell.MenuContent = _menu;
    }

    /// <summary>
    /// Zet de SYSTEM HEALTH-pil aan het werk - zelfde vier controles als
    /// Jabasoft en AI Studio, zodat de pil overal hetzelfde betekent.
    /// </summary>
    private void StartHealthCheck()
    {
        if (AppShell.Footer is not { } footer)
        {
            return;
        }

        if (_monitor is null)
        {
            _monitor = new HealthMonitor(
            [
                new AiBrokerHealthCheck(),
                new AiServerHealthCheck(_broker),
                new AiModelsHealthCheck(_broker),
                new DatabaseHealthCheck(_broker),
            ]);

            footer.Observe(_monitor);

            _monitor.Changed += (_, status) => ActivityLog.Shared.Add("app", status.Message);
        }

        // Niet awaiten: het scherm mag meteen verschijnen, de pil vult
        // zichzelf bij terwijl de controle loopt.
        _ = _monitor.RunAsync();
    }

    private void Navigate(NavigatiemenuItem item)
    {
        // Home is geen scherm van deze app maar de weg terug naar Jabasoft.
        if (item == NavigatiemenuItem.Home)
        {
            HomeRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        ShowContent(item);
    }

    /// <summary>
    /// Zet het inhoudsvak op één van de schermen.
    /// </summary>
    private void ShowContent(NavigatiemenuItem item)
    {
        AppShell.MainContent = item switch
        {
            NavigatiemenuItem.BeeldNaarTekst => _beeldNaarTekst,
            NavigatiemenuItem.TekstNaarBeeld => _tekstNaarBeeld,
            NavigatiemenuItem.BeeldNaarVideo => _beeldNaarVideo,
            NavigatiemenuItem.TekstNaarVideo => _tekstNaarVideo,
            NavigatiemenuItem.FilmMaken => _filmMaker,
            _ => _startscherm,
        };

        // Het menu markeert de knop van de pagina die nu open staat.
        if (_menu is not null)
        {
            _menu.Active = item;
        }

        // ImageFunn heeft nog geen logpaneel, dus de
        // Log-knop in de rail blijft uitgeschakeld - het mechanisme zelf
        // (Controls.IHeeftLog) staat al wel klaar voor de eerste pagina die
        // dat wel doet.
        if (AppShell.Actie is { } rail)
        {
            rail.LogBeschikbaar = false;
        }
    }

    /// <summary>
    /// De actierail zit ingebakken in de paginaschil, dus hij hoeft hier
    /// niet opgebouwd te worden - alleen aangesloten.
    /// </summary>
    private void WireActionRail()
    {
        if (AppShell.Actie is not { } rail)
        {
            return;
        }

        rail.RefreshRequested += (_, _) => AppShell.ResetSplitters();
    }
}
