using System.Windows;
using System.Windows.Controls;

namespace ImageFunn.App.Controls;

/// <summary>Welke knop in het menu is aangeklikt - zie <see cref="Navigatiemenu.ItemSelected"/>.</summary>
public enum NavigatiemenuItem
{
    /// <summary>Terug naar Jabasoft. Geen scherm van deze app, maar een andere applicatie.</summary>
    Home,

    /// <summary>Een afbeelding laten beschrijven, uitlezen of omzetten naar een prompt.</summary>
    BeeldNaarTekst,

    /// <summary>Een afbeelding genereren uit een omschrijving.</summary>
    TekstNaarBeeld,

    /// <summary>Een gemaakt beeld laten bewegen tot een korte video.</summary>
    BeeldNaarVideo,

    /// <summary>Een video maken uit alleen een omschrijving.</summary>
    TekstNaarVideo,

    /// <summary>Een langere film maken uit scènes op een tijdlijn.</summary>
    FilmMaken,
}

/// <summary>
/// Interaction logic for Navigatiemenu.xaml - see that file for what it looks like.
///
/// Een eigen menu van deze applicatie, niet gedeeld: elke app heeft zijn
/// eigen knoppen en moet los aan te passen zijn.
/// </summary>
public partial class Navigatiemenu : UserControl
{
    /// <summary>
    /// Gaat af als er op een menuknop geklikt is. Het menu doet zelf NIETS
    /// met die klik: het weet niet welke schermen deze app heeft en hoort
    /// dat ook niet te weten - zie MainWindow.xaml.cs.
    /// </summary>
    public event EventHandler<NavigatiemenuItem>? ItemSelected;

    public Navigatiemenu()
    {
        InitializeComponent();

        ToonActief();
    }

    /// <summary>De versietekst onderin de balk - de app zet hier zijn eigen versienummer in.</summary>
    public string Version
    {
        get => VersionText.Text;
        set => VersionText.Text = value;
    }

    private NavigatiemenuItem _actief = NavigatiemenuItem.Home;

    /// <summary>
    /// Welke pagina er nu open staat. De applicatie zet dit.
    ///
    /// Home staat er bewust niet tussen als blijvende stand: die knop brengt
    /// je naar Jabasoft en verandert het inhoudsvak niet.
    /// </summary>
    public NavigatiemenuItem Active
    {
        get => _actief;
        set
        {
            if (value == NavigatiemenuItem.Home || _actief == value)
            {
                return;
            }

            _actief = value;
            ToonActief();
        }
    }

    /// <summary>
    /// De actieve knop wordt omgekeerd: zwart vlak met een accentrand en
    /// accenttekst, in plaats van een vol accentvlak met zwarte tekst.
    /// </summary>
    private void ToonActief()
    {
        // Home is een handeling, geen bestemming: nooit gemarkeerd.
        Zet(HomeButton, false);
        Zet(BeeldNaarTekstButton, _actief == NavigatiemenuItem.BeeldNaarTekst);
        Zet(TekstNaarBeeldButton, _actief == NavigatiemenuItem.TekstNaarBeeld);
        Zet(BeeldNaarVideoButton, _actief == NavigatiemenuItem.BeeldNaarVideo);
        Zet(TekstNaarVideoButton, _actief == NavigatiemenuItem.TekstNaarVideo);
        Zet(FilmButton, _actief == NavigatiemenuItem.FilmMaken);
    }

    private static void Zet(Button knop, bool actief)
    {
        knop.SetResourceReference(BackgroundProperty, actief ? "BackgroundBrush" : "AccentBrush");
        knop.SetResourceReference(ForegroundProperty, actief ? "AccentBrush" : "AccentForegroundBrush");
        knop.BorderThickness = new Thickness(actief ? 2 : 0);
    }

    private void MenuItem_Click(object sender, RoutedEventArgs e)
    {
        var item = sender switch
        {
            _ when ReferenceEquals(sender, BeeldNaarTekstButton) => NavigatiemenuItem.BeeldNaarTekst,
            _ when ReferenceEquals(sender, TekstNaarBeeldButton) => NavigatiemenuItem.TekstNaarBeeld,
            _ when ReferenceEquals(sender, BeeldNaarVideoButton) => NavigatiemenuItem.BeeldNaarVideo,
            _ when ReferenceEquals(sender, TekstNaarVideoButton) => NavigatiemenuItem.TekstNaarVideo,
            _ when ReferenceEquals(sender, FilmButton) => NavigatiemenuItem.FilmMaken,
            _ => NavigatiemenuItem.Home,
        };

        ItemSelected?.Invoke(this, item);
    }
}
