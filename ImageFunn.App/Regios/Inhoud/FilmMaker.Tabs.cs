using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ImageFunn.App.Regios.Inhoud;

/// <summary>
/// Het vierde deel van FilmMaker: de vijf tabs (Verhaal, Scènes, Cast, Beelden, Film) in de volgorde waarin je
/// het instelt. Een tab is een Grid dat getoond of verborgen wordt; de balk bovenin laat zien welke stap klaar is
/// en welke je nog kunt doen. Wat nog niet kan (bijvoorbeeld beelden maken zonder scènes) staat uit.
/// </summary>
public partial class FilmMaker
{
    private static readonly string[] StapTitels = ["1  Verhaal", "2  Scènes", "3  Cast (optioneel)", "4  Beelden", "5  Film"];

    private int _tab;

    private Grid[] Tabs => [TabVerhaal, TabScenes, TabCast, TabBeelden, TabFilm];

    private Border[] StapRanden => [StapRand0, StapRand1, StapRand2, StapRand3, StapRand4];

    private Button[] StapKnoppen => [StapKnop0, StapKnop1, StapKnop2, StapKnop3, StapKnop4];

    private void Stap_Click(object sender, RoutedEventArgs e) => ToonTab(int.Parse((string)((Button)sender).Tag));

    /// <summary>De knoppen 'Volgende' en 'Vorige' onderaan een tab; hun Tag is de tab waar ze naartoe gaan.</summary>
    private void Volgende_Click(object sender, RoutedEventArgs e) => ToonTab(int.Parse((string)((Button)sender).Tag));

    private void ToonTab(int index)
    {
        _tab = Math.Clamp(index, 0, 4);

        for (var i = 0; i < Tabs.Length; i++)
        {
            Tabs[i].Visibility = i == _tab ? Visibility.Visible : Visibility.Collapsed;
        }

        // Een tab bouwt zijn lijsten op uit de stand van het verhaal zodra je hem opent.
        switch (_tab)
        {
            case 1:
                ToonScene();
                break;
            case 2:
                CastGebruik_Changed(this, new RoutedEventArgs());
                BouwCastKaarten();
                break;
            case 3:
                BouwBeoordelingKaarten();
                break;
        }

        VernieuwStappen();
    }

    /// <summary>
    /// Zet de stappenbalk goed: een vinkje bij wat klaar is, een streep onder de tab waar je bent, en uit wat nog niet kan.
    /// Wordt aangeroepen vanuit Teken(), dus bij elke wijziging van het verhaal.
    /// </summary>
    private void VernieuwStappen()
    {
        if (StapKnop0 is null)
        {
            return;
        }

        var heeftScenes = _scenes.Count > 0 && _scenes.All(s => !string.IsNullOrWhiteSpace(s.Gebeurt) || !string.IsNullOrWhiteSpace(s.Beeld));
        var castAan = CastGebruikBox?.IsChecked == true;
        var castKlaar = castAan && _cast.Count > 0 && _cast.All(c => c.Referentie is { } p && File.Exists(p));
        var beeldenKlaar = heeftScenes && BeoordeelbareScenes().All(i => _scenes[i].Sleutel is { } p && File.Exists(p));
        var filmKlaar = _filmBestand is not null;

        bool[] klaar = [heeftScenes, heeftScenes, castKlaar, beeldenKlaar, filmKlaar];

        // Cast is optioneel; maar als je hem aanzet moet hij eerst af zijn voordat de beelden kunnen.
        bool[] kan =
        [
            true,
            true,
            heeftScenes,
            heeftScenes && (!castAan || castKlaar),
            heeftScenes,
        ];

        for (var i = 0; i < StapKnoppen.Length; i++)
        {
            StapKnoppen[i].Content = (klaar[i] ? "✓  " : string.Empty) + StapTitels[i];
            StapKnoppen[i].IsEnabled = kan[i];
            StapRanden[i].BorderBrush = i == _tab ? (Brush)FindResource("AccentBrush") : Brushes.Transparent;
        }
    }

    /// <summary>Zet de bewerkbare tabs aan of uit; tijdens het maken van de film blijft alleen de filmtab bedienbaar.</summary>
    private void ZetBewerkbaar(bool aan)
    {
        TabVerhaal.IsEnabled = aan;
        TabScenes.IsEnabled = aan;
        TabCast.IsEnabled = aan;
        TabBeelden.IsEnabled = aan;
    }
}
