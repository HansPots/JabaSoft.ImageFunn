using System.Windows;
using System.Windows.Media;
using Jabasoft.Base.Settings;
using Stylebook.Components.Theming;

namespace ImageFunn.App.Layout;

/// <summary>
/// De voorkeuren van ImageFunn: thema, lettertype, hoeveel ruimte en rand,
/// hoe vaak de meters verversen, en waar het venster stond.
///
/// Geen taalkeuze zoals Jabasoft en AI Studio hebben: ImageFunn heeft nog
/// geen instellingenscherm en dus geen TAAL-kaart om die te zetten. Komt er
/// later een eigen Taal/Strings-woordenboek bij, dan hoort die keuze hier
/// terug - tot die tijd zou een taalsleutel zonder scherm die hem zet dode
/// code zijn.
///
/// Deze klasse kent de SLEUTELS; het bewaren zelf zit in
/// <see cref="SettingsStore"/> in Jabasoft.Base, zodat elke applicatie het
/// op dezelfde manier doet.
///
/// <see cref="Apply"/> zet alles in één keer neer bij het opstarten, vóór
/// het venster verschijnt.
/// </summary>
public static class Preferences
{
    private static readonly SettingsStore Store = new("ImageFunn.App");

    private const string ThemaSleutel = "thema";
    private const string LettertypeSleutel = "lettertype";
    private const string TekstgrootteSleutel = "tekstgrootte";
    private const string TussenruimteSleutel = "tussenruimte";
    private const string KaderrandSleutel = "kaderrand";
    private const string VerverstijdSleutel = "ververstijd";
    private const string VensterLinksSleutel = "venster.links";
    private const string VensterBovenSleutel = "venster.boven";
    private const string VensterBreedteSleutel = "venster.breedte";
    private const string VensterHoogteSleutel = "venster.hoogte";
    private const string VensterMaxSleutel = "venster.gemaximaliseerd";
    private const string BeeldmodelSleutel = "beeldservice.model";
    private const string OntladenNaSleutel = "beeldservice.ontladen-na";

    /// <summary>Waar een verse installatie mee begint.</summary>
    public const string StandaardLettertype = "Segoe UI";

    public const double StandaardTekstgrootte = 16;

    public const double StandaardVerverstijd = 5;

    /// <summary>
    /// Het standaard beeldmodel van de beeldservice ("realvis", "juggernaut",
    /// "sdxl" of "flux") - gaat als IMAGEFUNN_MODEL mee bij het starten (zie
    /// ImageServiceLauncher). Leeg = nooit gekozen: dan beslist de service zelf
    /// (of een omgevingsvariabele die al op de pc staat).
    /// </summary>
    public static string Beeldmodel => Store.GetString(BeeldmodelSleutel, string.Empty);

    public static void BewaarBeeldmodel(string model) => Store.Set(BeeldmodelSleutel, model);

    /// <summary>
    /// Na hoeveel seconden zonder opdracht de beeldservice zijn modellen
    /// loslaat en stopt (0 = nooit) - gaat als IMAGEFUNN_ONTLAAD_NA mee. Kleiner
    /// dan 0 = nooit gekozen, zelfde afspraak als bij <see cref="Beeldmodel"/>.
    /// </summary>
    public static double OntladenNaSeconden => Store.GetDouble(OntladenNaSleutel, -1);

    public static void BewaarOntladenNaSeconden(double seconden) => Store.Set(OntladenNaSleutel, seconden);

    /// <summary>
    /// Staat "automatisch vertalen en verbeteren" aan op deze pagina? Per pagina
    /// onthouden (Tekst naar beeld, Tekst naar video, Beeld naar video), en
    /// standaard UIT: je typt dan zelf de prompt zoals het model hem krijgt.
    /// </summary>
    public static bool Verbeteren(string pagina) => Store.GetBool($"verbeteren.{pagina}", false);

    public static void BewaarVerbeteren(string pagina, bool aan) => Store.Set($"verbeteren.{pagina}", aan);

    public static Theme Thema =>
        Enum.TryParse<Theme>(Store.GetString(ThemaSleutel, nameof(Theme.Lcars)), out var thema)
            ? thema
            : Theme.Lcars;

    public static string Lettertype => Store.GetString(LettertypeSleutel, StandaardLettertype);

    public static double Tekstgrootte => Store.GetDouble(TekstgrootteSleutel, StandaardTekstgrootte);

    public static double Tussenruimte => Store.GetDouble(TussenruimteSleutel, LayoutManager.DefaultGap);

    public static double Kaderrand => Store.GetDouble(KaderrandSleutel, LayoutManager.DefaultBorder);

    public static double Ververstijd => Store.GetDouble(VerverstijdSleutel, StandaardVerverstijd);

    public static void BewaarThema(Theme thema) => Store.Set(ThemaSleutel, thema.ToString());

    public static void BewaarLettertype(string naam, double grootte) => Store.SetMany(
    [
        new(LettertypeSleutel, naam),
        new(TekstgrootteSleutel, grootte.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
    ]);

    public static void BewaarTussenruimte(double pixels) => Store.Set(TussenruimteSleutel, pixels);

    public static void BewaarKaderrand(double pixels) => Store.Set(KaderrandSleutel, pixels);

    public static void BewaarVerverstijd(double seconden) => Store.Set(VerverstijdSleutel, seconden);

    /// <summary>
    /// Zet alle voorkeuren neer. Aanroepen vóórdat het venster verschijnt,
    /// anders zie je het eerst in het verkeerde thema opkomen.
    /// </summary>
    public static void Apply(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var resources = app.Resources;

        ThemeManager.Apply(Thema, resources);
        LayoutManager.ApplyRegionGap(Tussenruimte, resources);
        LayoutManager.ApplyRegionBorder(Kaderrand, resources);

        resources["RefreshIntervalSeconds"] = Ververstijd;
    }

    /// <summary>
    /// Het lettertype gaat niet via een resource maar via overerving, dus
    /// dat kan pas als er een venster is.
    /// </summary>
    public static void ApplyToWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.FontFamily = new FontFamily(Lettertype);
        window.FontSize = Tekstgrootte;

        HerstelVenster(window);
    }

    /// <summary>
    /// Zet het venster terug waar het stond. Alleen als er iets bewaard is
    /// én het nog op een scherm past: een venster dat terugkomt op een
    /// beeldscherm dat je niet meer hebt, is onvindbaar.
    /// </summary>
    private static void HerstelVenster(Window window)
    {
        if (!Store.Has(VensterBreedteSleutel))
        {
            return;
        }

        var breedte = Store.GetDouble(VensterBreedteSleutel, window.Width);
        var hoogte = Store.GetDouble(VensterHoogteSleutel, window.Height);
        var links = Store.GetDouble(VensterLinksSleutel, double.NaN);
        var boven = Store.GetDouble(VensterBovenSleutel, double.NaN);

        if (breedte < 400 || hoogte < 300 || double.IsNaN(links) || double.IsNaN(boven))
        {
            return;
        }

        // Past de linkerbovenhoek nog ergens op het bureaublad? Zo niet, dan
        // laten we WPF hem gewoon in het midden zetten.
        var bureaublad = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        if (!bureaublad.Contains(new Point(links + 40, boven + 40)))
        {
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = links;
        window.Top = boven;
        window.Width = breedte;
        window.Height = hoogte;

        if (Store.GetBool(VensterMaxSleutel, false))
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Onthoudt waar het venster staat. Aanroepen bij het afsluiten.
    ///
    /// Bij een gemaximaliseerd venster worden RestoreBounds bewaard en niet
    /// de maat van nu: anders komt het terug op schermgrootte en weet je
    /// niet meer hoe groot het "eigenlijk" was.
    /// </summary>
    public static void BewaarVenster(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var gemaximaliseerd = window.WindowState == WindowState.Maximized;
        var plek = gemaximaliseerd ? window.RestoreBounds : new Rect(window.Left, window.Top, window.Width, window.Height);

        if (double.IsNaN(plek.Left) || double.IsNaN(plek.Top) || plek.Width <= 0 || plek.Height <= 0)
        {
            return;
        }

        var cultuur = System.Globalization.CultureInfo.InvariantCulture;

        Store.SetMany(
        [
            new(VensterLinksSleutel, plek.Left.ToString("R", cultuur)),
            new(VensterBovenSleutel, plek.Top.ToString("R", cultuur)),
            new(VensterBreedteSleutel, plek.Width.ToString("R", cultuur)),
            new(VensterHoogteSleutel, plek.Height.ToString("R", cultuur)),
            new(VensterMaxSleutel, gemaximaliseerd ? "true" : "false"),
        ]);
    }
}
