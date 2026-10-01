using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using Jabasoft.Base;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Health;
using Jabasoft.Base.Logging;
using ImageFunn.App.Controls;
using InhoudSchermen = ImageFunn.App.Regios.Inhoud;

namespace ImageFunn.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
///
/// Zelfde opbouw als Jabasoft.App/MainWindow.xaml.cs: header, footer, menu
/// en actierail werken hier op precies dezelfde manier.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>De naam in appsettings.json van de app die de Home-knop start.</summary>
    private const string JabasoftAppName = "Jabasoft";

    private readonly List<AppEntry> _apps = LoadApps();

    /// <summary>Het startscherm van deze app. Eén keer gemaakt en hergebruikt, zodat wat je erin invult niet weg is als je even naar iets anders kijkt.</summary>
    private readonly InhoudSchermen.Startscherm _startscherm = new();

    /// <summary>De verbinding met de broker: levert de AI-instelling en de lijst met aanwezige modellen aan de gezondheidscontrole en aan het beeldmodel.</summary>
    private readonly IAiBrokerClient _broker = AiBrokerClient.CreateDefault();

    /// <summary>Het scherm waar een afbeelding omgezet wordt naar tekst.</summary>
    private readonly InhoudSchermen.BeeldNaarTekst _beeldNaarTekst;

    /// <summary>Het scherm waar uit tekst een beeld gemaakt wordt.</summary>
    private readonly InhoudSchermen.TekstNaarBeeld _tekstNaarBeeld;

    /// <summary>De bewaking van deze app. Eén per applicatie: de pil in de footer kijkt hiernaar.</summary>
    private HealthMonitor? _monitor;

    /// <summary>Het menu zelf, om te kunnen zeggen welke pagina open staat.</summary>
    private Navigatiemenu? _menu;

    private BrokerLogReader? _brokerLog;

    public MainWindow()
    {
        InitializeComponent();

        _beeldNaarTekst = new InhoudSchermen.BeeldNaarTekst(_broker);
        _tekstNaarBeeld = new InhoudSchermen.TekstNaarBeeld(_broker);

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

        // Hoort bij de WindowChrome in MainWindow.xaml: zonder titelbalk
        // maximaliseert Windows dit venster over de taakbalk heen.
        Layout.MaximizeBounds.Apply(this);

        // Lettertype en venstermaat kunnen pas als er een venster is; de
        // rest is in App.OnStartup al gezet.
        Layout.Preferences.ApplyToWindow(this);

        // Waar het venster stond onthouden we bij het sluiten.
        Closing += (_, _) => Layout.Preferences.BewaarVenster(this);

        BuildMenu();
        WireActionRail();
        ShowContent(NavigatiemenuItem.Home);
        StartActivityLog();
        StartHealthCheck();
    }

    /// <summary>
    /// Leest de Apps-lijst uit appsettings.json naast de exe. Voor deze app
    /// staat er één entry in: Jabasoft, waar de Home-knop naartoe gaat.
    /// </summary>
    private static List<AppEntry> LoadApps()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var apps = document.RootElement.GetProperty("Apps").Deserialize<List<AppEntry>>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return apps ?? [];
    }

    /// <summary>
    /// Zet het menu van deze app in het Menu-vak. Het menu meldt alleen
    /// WELKE knop is aangeklikt; wat daarop gebeurt staat hier.
    /// </summary>
    private void BuildMenu()
    {
        _menu = new Navigatiemenu();

        // Uit de gebouwde assembly en niet overgetypt in de XAML: dan kan het
        // getal op het scherm niet afwijken van de code die draait. Ophogen
        // gebeurt in <Version> in ImageFunn.App.csproj.
        _menu.Version = $"V {AppVersion.Current}";

        AutomationProperties.SetName(_menu, "Hoofdmenu");
        _menu.ItemSelected += (_, item) => Navigate(item);
        AppShell.MenuContent = _menu;
    }

    /// <summary>
    /// Vult het Activity-blok: de eigen regels van deze app komen
    /// rechtstreeks in de gedeelde opvang, en BrokerLogReader haalt die van
    /// de AI-broker erbij.
    /// </summary>
    private void StartActivityLog()
    {
        ActivityLog.Shared.Add("app", "ImageFunn gestart");

        _brokerLog = new BrokerLogReader(ActivityLog.Shared);
        _brokerLog.Start();
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

        // Niet awaiten: het venster mag meteen verschijnen, de pil vult
        // zichzelf bij terwijl de controle loopt.
        _ = _monitor.RunAsync();
    }

    private void Navigate(NavigatiemenuItem item)
    {
        // Home is geen scherm van deze app maar de weg terug naar Jabasoft;
        // ImageFunn gaat nooit vanzelf naar een andere applicatie - dat
        // gebeurt alleen als je er zelf op klikt.
        if (item == NavigatiemenuItem.Home)
        {
            GaNaarApp(JabasoftAppName);
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

    /// <summary>
    /// Geeft het scherm over aan een andere applicatie van de familie: die
    /// komt op DEZELFDE plek en grootte te staan, en dit venster verdwijnt
    /// van het scherm. Zo voelt het als één venster dat van inhoud wisselt.
    /// </summary>
    private void GaNaarApp(string naam)
    {
        if (_apps.FirstOrDefault(app => app.Name == naam) is not { Available: true } doel)
        {
            ActivityLog.Shared.Add("app", $"{naam} staat niet (of niet als beschikbaar) in appsettings.json.");
            return;
        }

        // De venstermaat nog even bewaren: de andere applicatie neemt hem
        // over, en zo staat hij ook in onze eigen voorkeuren als we straks
        // weer tevoorschijn komen.
        Layout.Preferences.BewaarVenster(this);

        var handvat = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (!AppHandover.SwitchTo(doel.ExecutablePath, handvat))
        {
            ActivityLog.Shared.Add("app", $"{naam} kon niet geopend worden - het venster is niet gevonden.");
        }
    }
}
