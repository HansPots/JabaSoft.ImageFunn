using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Media;
using System.Windows.Shapes;
using ImageFunn.App.Ai;
using ImageFunn.App.ImageService;
using Jabasoft.Base.AiBroker;
using Microsoft.Win32;

namespace ImageFunn.App.Regios.Inhoud;

/// <summary>
/// Interaction logic for FilmMaker.xaml - see that file for what it looks like.
///
/// Houdt de scènes van de film bij als een gewone lijst, tekent daar de
/// tijdlijn van en stuurt de hele film als één opdracht naar de beeldservice.
/// De service meldt per scène de stand terug; scènes die klaar zijn krijgen
/// hun bestand, zodat een volgende run ze hergebruikt in plaats van opnieuw
/// te maken.
/// </summary>
public partial class FilmMaker : UserControl
{
    private static readonly TimeSpan PeilInterval = TimeSpan.FromSeconds(2);

    /// <summary>Grove schatting voor de melding vooraf: een scène kost ongeveer twee minuten.</summary>
    private const int MinutenPerScene = 2;

    private static readonly JsonSerializerOptions JsonOpties = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly ImageServiceClient _service = new();

    private readonly Verhaalschrijver _schrijver;

    private List<FilmScene> _scenes = [];

    private int _gekozen = -1;

    /// <summary>True terwijl de velden vanuit de code gevuld worden, zodat dat niet als een wijziging van de gebruiker telt.</summary>
    private bool _vullen;

    private bool _galerijVullen;

    private bool _bezig;

    private string? _jobId;

    private string? _filmBestand;

    private string? _spelerBestand;

    /// <summary>Het sleutelbeeld dat nu als voorbeeld getoond wordt, om niet elke peiling opnieuw te laden.</summary>
    private string? _voorbeeldPad;

    /// <summary>De scènes waarvan de clip al getoond is, zodat een clip maar één keer start als hij klaar komt.</summary>
    private HashSet<int> _klaarGezien = [];

    /// <summary>Is er in deze run al een gemaakte clip afgespeeld? Daarvóór tonen we de sleutelbeelden.</summary>
    private bool _clipInRun;

    /// <summary>Heeft de gebruiker zelf iets gekozen om te bekijken? Dan neemt een nieuwe klare scène het scherm niet meer over.</summary>
    private bool _handmatig;

    /// <summary>Speelt de speler alle klare scènes achter elkaar af ("Speel wat klaar is")?</summary>
    private bool _afspeellijst;

    private int _lijstIndex = -1;

    public FilmMaker(IAiBrokerClient broker)
    {
        InitializeComponent();
        _schrijver = new Verhaalschrijver(broker);

        _scenes.Add(NieuweScene());
        Loaded += async (_, _) =>
        {
            ToonTab(_tab);
            Selecteer(_gekozen < 0 ? 0 : _gekozen);
            await VulGalerijAsync();
        };
    }

    private static FilmScene NieuweScene() => new() { Titel = "Nieuwe scène", Seconden = 4 };

    private static string TagVan(ComboBox keuze) => (string)((ComboBoxItem)keuze.SelectedItem).Tag;

    private static void KiesTag(ComboBox keuze, string tag)
    {
        foreach (ComboBoxItem item in keuze.Items)
        {
            if ((string)item.Tag == tag)
            {
                keuze.SelectedItem = item;
                return;
            }
        }
    }

    // ---------------------------------------------------------------- scènes bewerken

    private FilmScene? Gekozen => _gekozen >= 0 && _gekozen < _scenes.Count ? _scenes[_gekozen] : null;

    private void Selecteer(int index)
    {
        _gekozen = _scenes.Count == 0 ? -1 : Math.Clamp(index, 0, _scenes.Count - 1);
        ToonScene();
        Teken();
    }

    private void ToonScene()
    {
        _vullen = true;
        try
        {
            var scene = Gekozen;
            SceneEditor.IsEnabled = scene is not null && !_bezig;
            SceneKop.Text = scene is null ? "Scène" : $"Scène {_gekozen + 1} van {_scenes.Count}";
            TitelBox.Text = scene?.Titel ?? string.Empty;
            GebeurtBox.Text = scene?.Gebeurt ?? string.Empty;
            BeeldBox.Text = scene?.Beeld ?? string.Empty;
            BewegingBox.Text = scene?.Beweging ?? string.Empty;
            KiesTag(DuurKeuze, (scene?.Seconden ?? 4).ToString());
            ToonSceneCast(scene);
        }
        finally
        {
            _vullen = false;
        }
    }

    /// <summary>Een aangepaste scène moet opnieuw gemaakt worden: het eerder gemaakte bestand geldt niet meer.</summary>
    private static void Maak_ongeldig(FilmScene scene, bool sleutelOok = false)
    {
        scene.Bestand = null;
        scene.Status = "wachtend";

        // Een nieuwe omschrijving, speler of plek hoort bij een nieuw beginbeeld.
        if (sleutelOok)
        {
            scene.Sleutel = null;
        }
    }

    private void TitelBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vullen || Gekozen is not { } scene)
        {
            return;
        }

        scene.Titel = TitelBox.Text;
        Teken();
    }

    private void GebeurtBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vullen || Gekozen is not { } scene)
        {
            return;
        }

        // Nieuwe omschrijving: de Engelse tekst hoort daar niet meer bij en wordt bij het starten opnieuw afgeleid.
        scene.Gebeurt = GebeurtBox.Text;
        scene.Beeld = string.Empty;
        scene.Beweging = string.Empty;
        Maak_ongeldig(scene, true);

        _vullen = true;
        BeeldBox.Text = string.Empty;
        BewegingBox.Text = string.Empty;
        _vullen = false;
        Teken();
    }

    private void BeeldBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vullen || Gekozen is not { } scene)
        {
            return;
        }

        scene.Beeld = BeeldBox.Text;
        Maak_ongeldig(scene, true);
        Teken();
    }

    private void BewegingBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vullen || Gekozen is not { } scene)
        {
            return;
        }

        scene.Beweging = BewegingBox.Text;
        Maak_ongeldig(scene);
        Teken();
    }

    private void DuurKeuze_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vullen || Gekozen is not { } scene || DuurKeuze.SelectedItem is null)
        {
            return;
        }

        scene.Seconden = int.Parse(TagVan(DuurKeuze));
        Maak_ongeldig(scene);
        Teken();
    }

    private void EngelsToon_Changed(object sender, RoutedEventArgs e) =>
        EngelsPaneel.Visibility = EngelsToon.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void VoegToe_Click(object sender, RoutedEventArgs e)
    {
        var plek = _gekozen < 0 ? _scenes.Count : _gekozen + 1;
        _scenes.Insert(plek, NieuweScene());
        Selecteer(plek);
    }

    private void Verwijder_Click(object sender, RoutedEventArgs e)
    {
        if (Gekozen is null)
        {
            return;
        }

        _scenes.RemoveAt(_gekozen);
        if (_scenes.Count == 0)
        {
            _scenes.Add(NieuweScene());
        }

        Selecteer(_gekozen);
    }

    private void Links_Click(object sender, RoutedEventArgs e) => Verplaats(-1);

    private void Rechts_Click(object sender, RoutedEventArgs e) => Verplaats(1);

    private void Verplaats(int stap)
    {
        var naar = _gekozen + stap;
        if (Gekozen is null || naar < 0 || naar >= _scenes.Count)
        {
            return;
        }

        (_scenes[_gekozen], _scenes[naar]) = (_scenes[naar], _scenes[_gekozen]);

        // Bij "doorlopend" begint elke scène op het einde van de vorige: een andere volgorde betekent opnieuw maken.
        if (TagVan(ModusKeuze) == "doorlopend")
        {
            for (var i = Math.Min(_gekozen, naar); i < _scenes.Count; i++)
            {
                Maak_ongeldig(_scenes[i]);
            }
        }

        Selecteer(naar);
    }

    private void Opnieuw_Click(object sender, RoutedEventArgs e)
    {
        if (Gekozen is null)
        {
            return;
        }

        // Doorlopend: de volgende scènes beginnen op het einde van deze, dus die moeten mee opnieuw.
        var tot = TagVan(ModusKeuze) == "doorlopend" ? _scenes.Count : _gekozen + 1;
        for (var i = _gekozen; i < tot; i++)
        {
            Maak_ongeldig(_scenes[i]);
        }

        Teken();
        StatusText.Text = tot - _gekozen > 1
            ? $"Scène {_gekozen + 1} t/m {tot} worden bij 'Maak film' opnieuw gemaakt (doorlopend)."
            : $"Scène {_gekozen + 1} wordt bij 'Maak film' opnieuw gemaakt.";
    }

    // ---------------------------------------------------------------- tijdlijn tekenen

    private void TijdlijnScroll_SizeChanged(object sender, SizeChangedEventArgs e) => Teken();

    private Brush Kleur(string sleutel) => TryFindResource(sleutel) as Brush ?? Brushes.Gray;

    private void Teken()
    {
        if (Tijdlijn is null)
        {
            return;
        }

        Tijdlijn.Children.Clear();

        var totaal = Math.Max(1, _scenes.Sum(s => s.Seconden));
        var open = _scenes.Count(s => string.IsNullOrEmpty(s.Bestand));
        TijdlijnKop.Text =
            $"Tijdlijn - {_scenes.Count} scènes, {totaal / 60}:{totaal % 60:00} - " +
            (open == 0 ? "alles gemaakt" : $"nog {open} te maken, ±{open * MinutenPerScene} min rekentijd");

        const double marge = 12;
        var beschikbaar = Math.Max(300, TijdlijnScroll.ViewportWidth - 2 * marge);
        var schaal = Math.Max(14, beschikbaar / totaal);
        Tijdlijn.Width = totaal * schaal + 2 * marge;

        // Liniaal: een streepje per seconde, een label per 5 of 10 seconden.
        var labelStap = schaal >= 28 ? 5 : 10;
        for (var t = 0; t <= totaal; t++)
        {
            var x = marge + t * schaal;
            var groot = t % 5 == 0;
            Tijdlijn.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = groot ? 6 : 12,
                Y2 = 20,
                Stroke = Kleur("TextMutedBrush"),
                StrokeThickness = 1,
            });

            if (t % labelStap == 0)
            {
                var label = new TextBlock
                {
                    Text = $"{t / 60}:{t % 60:00}",
                    FontSize = 11,
                    Foreground = Kleur("TextMutedBrush"),
                };
                Canvas.SetLeft(label, x + 3);
                Canvas.SetTop(label, 0);
                Tijdlijn.Children.Add(label);
            }
        }

        // De scènes als blokken, in de kleur van hun stand.
        double begin = 0;
        for (var i = 0; i < _scenes.Count; i++)
        {
            var scene = _scenes[i];
            var breedte = scene.Seconden * schaal;
            var gekozen = i == _gekozen;

            var (achtergrond, tekst) = scene.Status switch
            {
                "bezig" => ("BackgroundBrush", "TextPrimaryBrush"),
                "klaar" => ("HealthOkBrush", "AccentForegroundBrush"),
                "mislukt" => ("HealthErrorBrush", "AccentForegroundBrush"),
                _ => ("BackgroundBrush", "TextPrimaryBrush"),
            };

            var inhoud = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
            inhoud.Children.Add(new TextBlock
            {
                Text = $"{i + 1}. {scene.Titel}",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = Kleur(tekst),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            inhoud.Children.Add(new TextBlock
            {
                Text = scene.Status == "bezig" ? $"{scene.Seconden} sec · {scene.Voortgang:P0}" : $"{scene.Seconden} sec",
                FontSize = 11,
                Foreground = Kleur(tekst),
            });

            // Een scène die bezig is vult zich van links naar rechts naarmate hij vordert.
            UIElement blokInhoud = inhoud;
            if (scene.Status == "bezig")
            {
                var raster = new Grid();
                raster.Children.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(0, (breedte - 8) * Math.Clamp(scene.Voortgang, 0, 1)),
                    Background = Kleur("HealthBusyBrush"),
                    CornerRadius = new CornerRadius(4),
                    Opacity = 0.55,
                });
                raster.Children.Add(inhoud);
                blokInhoud = raster;
            }

            var blok = new Border
            {
                Width = Math.Max(4, breedte - 2),
                Height = 62,
                Background = Kleur(achtergrond),
                BorderBrush = Kleur(gekozen ? "AccentBrush" : "BorderBrush"),
                BorderThickness = new Thickness(gekozen ? 3 : 1),
                CornerRadius = new CornerRadius(6),
                Child = blokInhoud,
                ClipToBounds = true,
                ToolTip = $"{scene.Titel}\n{scene.Gebeurt}".Trim(),
                Tag = i,
                Cursor = Cursors.Hand,
            };
            Canvas.SetLeft(blok, marge + begin * schaal + 1);
            Canvas.SetTop(blok, 26);
            Tijdlijn.Children.Add(blok);

            begin += scene.Seconden;
        }

        VernieuwStappen();
    }

    private void Tijdlijn_Klik(object sender, MouseButtonEventArgs e)
    {
        var plek = e.GetPosition(Tijdlijn);
        foreach (var kind in Tijdlijn.Children.OfType<Border>())
        {
            var links = Canvas.GetLeft(kind);
            if (kind.Tag is int index && plek.X >= links && plek.X <= links + kind.Width && plek.Y >= 26 && plek.Y <= 88)
            {
                Selecteer(index);

                // Een gemaakte scène laat zich meteen bekijken.
                if (_scenes[index].Bestand is { } bestand && File.Exists(bestand))
                {
                    _afspeellijst = false;
                    _handmatig = true;
                    Speel(bestand, $"Scène {index + 1}: {_scenes[index].Titel}");
                }

                return;
            }
        }
    }

    // ---------------------------------------------------------------- tijdlijn schrijven

    private async void TijdlijnButton_Click(object sender, RoutedEventArgs e)
    {
        var idee = IdeeBox.Text.Trim();
        if (idee.Length == 0)
        {
            StatusText.Text = "Typ eerst een verhaalidee.";
            return;
        }

        if (_scenes.Any(s => !string.IsNullOrWhiteSpace(s.Gebeurt) || s.Bestand is not null) &&
            MessageBox.Show("De huidige scènes worden vervangen door een nieuwe tijdlijn. Doorgaan?", "ImageFunn",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var seconden = int.Parse(TagVan(LengteKeuze));
        TijdlijnButton.IsEnabled = false;
        StatusText.Text = "Het chatmodel schrijft de tijdlijn… (dit kan een minuut duren)";

        try
        {
            var (voorstel, fout) = await _schrijver.MaakTijdlijnAsync(idee, seconden, HuidigeStijl().Prefix, CancellationToken.None);
            if (voorstel is null)
            {
                StatusText.Text = $"Tijdlijn maken mislukt: {fout}";
                return;
            }

            _scenes = voorstel.Scenes;
            StijlBox.Text = voorstel.Stijl;
            Selecteer(0);
            StatusText.Text = $"Tijdlijn met {_scenes.Count} scènes klaar. Pas aan wat je wilt.";
            ToonTab(1);
        }
        catch (Exception fout)
        {
            StatusText.Text = $"Kon het chatmodel niet bereiken: {fout.Message}";
        }
        finally
        {
            TijdlijnButton.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- film maken

    private async void MaakButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scenes.Any(s => string.IsNullOrWhiteSpace(s.Gebeurt) && string.IsNullOrWhiteSpace(s.Beeld)))
        {
            StatusText.Text = "Elke scène heeft een omschrijving nodig ('Wat gebeurt er?' op tab 2).";
            return;
        }

        ZetBezig(true);
        var klok = Stopwatch.StartNew();

        try
        {
            if (!await LeidAfMissendeAsync())
            {
                return;
            }

            // Met een cast eerst de referentiebeelden, als die er nog niet zijn.
            if (CastGebruikBox.IsChecked == true && _cast.Count > 0 && _cast.Any(c => c.Referentie is not { } p || !File.Exists(p)) && !await RunCastAsync(klok))
            {
                return;
            }

            // Zijn de sleutelbeelden nog niet gemaakt en wil je ze controleren, dan doe je dat eerst op tab 4.
            if (ControleerBox.IsChecked == true && NodigeSleutelbeelden().Count > 0)
            {
                StatusText.Text = "De sleutelbeelden zijn nog niet gemaakt. Maak en controleer ze eerst op tab 4 (Beelden), of zet 'Sleutelbeelden eerst laten controleren' uit op tab 1.";
                ToonTab(3);
                return;
            }

            await RunFilmAsync(klok);
        }
        catch (Exception fout)
        {
            StatusText.Text = $"Kon de beeldservice niet bereiken: {fout.Message}";
        }
        finally
        {
            _jobId = null;
            ZetBezig(false);
            VernieuwStappen();
        }
    }

    /// <summary>Stuurt de hele film naar de service en volgt de voortgang. Al goedgekeurde sleutelbeelden en gemaakte scènes worden hergebruikt.</summary>
    private async Task RunFilmAsync(Stopwatch klok)
    {
        var formaat = TagVan(FormaatKeuze).Split('x');
        var (referentie, eerste, sterkte) = Consistentie();
        var opdracht = new BeeldOpdracht
        {
            Soort = "film",
            Breedte = int.Parse(formaat[0]),
            Hoogte = int.Parse(formaat[1]),
            Videomodel = TagVan(VideomodelKeuze),
            Modus = TagVan(ModusKeuze),
            Overgang = TagVan(OvergangKeuze),
            Stijl = StijlVoorService(),
            Beeldmodel = TagVan(BeeldmodelKeuze),
            Negatief = NegatiefVoorService(),
            Prompt = IdeeBox.Text.Trim(),
            Referentie = referentie,
            EersteAlsReferentie = eerste,
            ReferentieSterkte = sterkte,
            SpelerSterkte = CastSterkte().Speler,
            PlekSterkte = CastSterkte().Plek,
            VoorwerpSterkte = CastSterkte().Voorwerp,
            Scenes = _scenes.Select(KopieVoorService).ToList(),
        };

        StatusText.Text = "Film starten…";
        KopText.Text = "Film starten";
        _jobId = await _service.StartJobAsync(opdracht);
        StopButton.IsEnabled = true;

        var teDoen = _scenes.Count(s => string.IsNullOrEmpty(s.Bestand));
        _klaarGezien = _scenes.Select((s, i) => (s, i)).Where(x => !string.IsNullOrEmpty(x.s.Bestand)).Select(x => x.i).ToHashSet();
        _clipInRun = false;
        _handmatig = false;
        _afspeellijst = false;
        _voorbeeldPad = null;
        var sceneKlok = new Stopwatch();
        var klaarBegin = _scenes.Count - teDoen;

        while (true)
        {
            var status = await _service.GetStatusAsync(_jobId);
            Overnemen(status);
            SpeelKlaarButton.IsEnabled = _scenes.Any(s => !string.IsNullOrEmpty(s.Bestand));
            ToonVoortgang(status, klok, sceneKlok, klaarBegin, teDoen);
            Teken();

            if (status.IsKlaar && status.Bestand is { } film)
            {
                _filmBestand = film;
                _afspeellijst = false;
                Speel(film);
                StatusText.Text = $"Film klaar in {klok.Elapsed:h\\:mm\\:ss}.";
                await VulGalerijAsync();
                break;
            }

            if (status.IsMislukt)
            {
                StatusText.Text = $"{status.Foutmelding} Druk op 'Maak film' om verder te gaan; gemaakte scènes worden hergebruikt.";
                break;
            }

            await Task.Delay(PeilInterval);
        }
    }

    private static FilmScene Kopie(FilmScene s) => new()
    {
        Titel = s.Titel,
        Gebeurt = s.Gebeurt,
        Beeld = s.Beeld,
        Beweging = s.Beweging,
        Seconden = s.Seconden,
        Bestand = s.Bestand,
        Status = s.Status,
        Seed = s.Seed,
        Voortgang = s.Voortgang,
        Sleutel = s.Sleutel,
        Opnieuw = s.Opnieuw,
        Spelers = [.. s.Spelers],
        Voorwerpen = [.. s.Voorwerpen],
        VoorwerpRef = s.VoorwerpRef,
        Plek = s.Plek,
        SpelerRefs = [.. s.SpelerRefs],
        PlekRef = s.PlekRef,
    };

    /// <summary>Neemt de stand per scène over van wat de service meldt.</summary>
    private void Overnemen(BeeldJobStatus status)
    {
        if (status.Scenes.Count != _scenes.Count)
        {
            return;
        }

        for (var i = 0; i < _scenes.Count; i++)
        {
            _scenes[i].Status = status.Scenes[i].Status;
            _scenes[i].Bestand = status.Scenes[i].Bestand;
            _scenes[i].Seed = status.Scenes[i].Seed;
            _scenes[i].Voortgang = status.Scenes[i].Voortgang;
            _scenes[i].Sleutel = status.Scenes[i].Sleutel;
        }
    }

    private void ZetBezig(bool bezig)
    {
        _bezig = bezig;
        ZetBewerkbaar(!bezig);
        MaakButton.IsEnabled = !bezig;
        StopButton.IsEnabled = false;
        Voortgang.IsIndeterminate = false;
        if (bezig)
        {
            Voortgang.Value = 0;
            ProcentText.Text = string.Empty;
            TijdText.Text = string.Empty;
        }
        else
        {
            ZetTaakbalk(null, false);
        }

        ToonFilmButton.IsEnabled = !bezig && _filmBestand is not null;
        SpeelKlaarButton.IsEnabled = _scenes.Any(s => !string.IsNullOrEmpty(s.Bestand));
        OpslaanButton.IsEnabled = !bezig && _filmBestand is not null;
        ToonScene();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_jobId is null)
        {
            return;
        }

        StopButton.IsEnabled = false;
        StatusText.Text = "Stoppen na de huidige stap… (de al gemaakte scènes blijven bewaard)";
        try
        {
            await _service.StopJobAsync(_jobId);
        }
        catch (Exception fout)
        {
            StatusText.Text = $"Stoppen mislukt: {fout.Message}";
        }
    }

    // ---------------------------------------------------------------- afspelen en bewaren

    private void Speel(string bestand, string? label = null)
    {
        _spelerBestand = bestand;
        _voorbeeldPad = null;
        SleutelBeeld.Visibility = Visibility.Collapsed;
        SleutelLabelTekst.Text = label ?? string.Empty;
        SleutelLabel.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;
        LegeTekst.Visibility = Visibility.Collapsed;
        Speler.Source = new Uri(bestand);
        Speler.Play();
    }

    /// <summary>Het vlak boven de speler: laat een sleutelbeeld zien zolang er nog geen gemaakte clip is.</summary>
    private void ToonVoorbeeld(string pad, string label)
    {
        if (_voorbeeldPad == pad || !File.Exists(pad))
        {
            return;
        }

        try
        {
            var beeld = new BitmapImage();
            beeld.BeginInit();
            beeld.CacheOption = BitmapCacheOption.OnLoad;
            beeld.UriSource = new Uri(pad);
            beeld.EndInit();
            beeld.Freeze();

            Speler.Stop();
            SleutelBeeld.Source = beeld;
            SleutelBeeld.Visibility = Visibility.Visible;
            SleutelLabelTekst.Text = label;
            SleutelLabel.Visibility = Visibility.Visible;
            LegeTekst.Visibility = Visibility.Collapsed;
            _voorbeeldPad = pad;
        }
        catch (Exception)
        {
            // Een voorbeeld dat niet laadt is geen reden om de voortgang te verstoren.
        }
    }

    private void ZetFase(int actief)
    {
        void Zet(TextBlock blok, bool aan)
        {
            blok.SetResourceReference(TextBlock.ForegroundProperty, aan ? "AccentBrush" : "TextMutedBrush");
            blok.FontWeight = aan ? FontWeights.Bold : FontWeights.Normal;
        }

        Zet(FaseEen, actief == 1);
        Zet(FaseTwee, actief == 2);
        Zet(FaseDrie, actief == 3);
    }

    /// <summary>Het voortgangsbalkje op het taakbalkpictogram, zodat je de voortgang ook ziet als je met iets anders bezig bent.</summary>
    private void ZetTaakbalk(double? waarde, bool onbepaald)
    {
        if (Window.GetWindow(this) is not { } venster)
        {
            return;
        }

        venster.TaskbarItemInfo ??= new TaskbarItemInfo();
        venster.TaskbarItemInfo.ProgressState = waarde is null
            ? TaskbarItemProgressState.None
            : onbepaald ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.Normal;
        venster.TaskbarItemInfo.ProgressValue = waarde ?? 0;
    }

    private static int Minuten(TimeSpan duur) => Math.Max(1, (int)Math.Round(duur.TotalMinutes));

    /// <summary>
    /// Zet de stand van de service op de voortgangskaart: fase, scène, percentage,
    /// verstreken tijd en een schatting van de rest. De schatting begint pas als
    /// er genoeg van de eerste scène af is - daarvoor is er niets om op te rekenen.
    /// </summary>
    private void ToonVoortgang(BeeldJobStatus status, Stopwatch klok, Stopwatch sceneKlok, int klaarBegin, int teDoen)
    {
        var n = _scenes.Count;
        var klaarNu = _scenes.Count(s => s.Status == "klaar");
        var klaarInRun = Math.Max(0, klaarNu - klaarBegin);
        var actief = _scenes.FindIndex(s => s.Status == "bezig");
        var fase = status.Fase;

        if (fase == "scenes" && !sceneKlok.IsRunning && (actief >= 0 || klaarInRun > 0))
        {
            sceneKlok.Start();
        }

        ZetFase(fase == "sleutelbeelden" ? 1 : fase is "model" or "scenes" ? 2 : fase is "samenvoegen" or "klaar" ? 3 : 0);

        KopText.Text = fase switch
        {
            "sleutelbeelden" => "Sleutelbeelden maken",
            "model" => "Videomodel laden",
            "scenes" when actief >= 0 => $"Scène {actief + 1} van {n}: {_scenes[actief].Titel}",
            "scenes" => $"Scène {Math.Min(n, klaarNu + 1)} van {n}",
            "samenvoegen" => "Film samenvoegen",
            "klaar" => "Film klaar",
            _ => "Bezig…",
        };

        ProcentText.Text = $"{status.Voortgang:P0}";
        var onbepaald = fase == "model" || (fase == "scenes" && actief >= 0 && _scenes[actief].Voortgang <= 0);
        Voortgang.IsIndeterminate = onbepaald;
        if (!onbepaald)
        {
            Voortgang.Value = status.Voortgang;
        }

        ZetTaakbalk(status.Voortgang, onbepaald);
        StatusText.Text = status.Stap;

        var tijd = $"Verstreken {klok.Elapsed:h\\:mm\\:ss}";
        if (fase == "scenes" && sceneKlok.IsRunning)
        {
            var gedaan = klaarInRun + (actief >= 0 ? Math.Clamp(_scenes[actief].Voortgang, 0, 1) : 0);
            if (gedaan >= 0.1)
            {
                var perScene = sceneKlok.Elapsed / gedaan;
                var rest = (perScene * Math.Max(0, teDoen - gedaan)) + TimeSpan.FromSeconds(60);
                tijd += $"  ·  nog ±{Minuten(rest)} min  ·  klaar rond {DateTime.Now + rest:HH:mm}";
            }
            else
            {
                tijd += "  ·  tijdschatting volgt zodra de eerste scène op gang is";
            }
        }
        else if (fase == "samenvoegen")
        {
            tijd += "  ·  bijna klaar";
        }
        else if (fase is "sleutelbeelden" or "model")
        {
            tijd += $"  ·  daarna {teDoen} scène(s) animeren (±{teDoen * MinutenPerScene} min)";
        }

        TijdText.Text = tijd;

        // Een scène die klaar komt speelt meteen; tot de eerste klaar is tonen we de sleutelbeelden zodra ze er zijn.
        for (var i = 0; i < n; i++)
        {
            if (_scenes[i].Status == "klaar" && _scenes[i].Bestand is { } clip && _klaarGezien.Add(i))
            {
                _clipInRun = true;
                if (!_handmatig)
                {
                    Speel(clip, $"Scène {i + 1} is klaar");
                }
            }
        }

        if (!_clipInRun && !_handmatig)
        {
            var laatste = actief >= 0 && !string.IsNullOrEmpty(_scenes[actief].Sleutel)
                ? actief
                : _scenes.FindLastIndex(s => !string.IsNullOrEmpty(s.Sleutel));
            if (laatste >= 0)
            {
                ToonVoorbeeld(_scenes[laatste].Sleutel!, $"Beginbeeld van scène {laatste + 1}");
            }
        }
    }

    /// <summary>Speelt alle scènes die al klaar zijn achter elkaar; nieuwe klare scènes komen bij de volgende ronde vanzelf mee.</summary>
    private void SpeelKlaar_Click(object sender, RoutedEventArgs e)
    {
        _afspeellijst = true;
        _handmatig = true;
        _lijstIndex = -1;
        VolgendeVanLijst();
    }

    private void VolgendeVanLijst()
    {
        var klaar = _scenes
            .Select((s, i) => (s, i))
            .Where(x => x.s.Bestand is { } f && File.Exists(f))
            .ToList();

        if (klaar.Count == 0)
        {
            _afspeellijst = false;
            StatusText.Text = "Er is nog geen scène klaar.";
            return;
        }

        _lijstIndex = (_lijstIndex + 1) % klaar.Count;
        var (scene, nummer) = klaar[_lijstIndex];
        Speel(scene.Bestand!, $"Scène {nummer + 1} van {_scenes.Count}: {scene.Titel}");
    }

    private void Speler_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_afspeellijst)
        {
            VolgendeVanLijst();
            return;
        }

        Speler.Position = TimeSpan.Zero;
        Speler.Play();
    }

    private void Speler_MediaFailed(object sender, ExceptionRoutedEventArgs e) =>
        StatusText.Text = $"De video kon niet afgespeeld worden: {e.ErrorException.Message}";

    private void ToonFilm_Click(object sender, RoutedEventArgs e)
    {
        if (_filmBestand is not null && File.Exists(_filmBestand))
        {
            _afspeellijst = false;
            _handmatig = true;
            Speel(_filmBestand);
        }
    }

    private void FilmOpslaan_Click(object sender, RoutedEventArgs e)
    {
        if (_filmBestand is null || !File.Exists(_filmBestand))
        {
            return;
        }

        var dialoog = new SaveFileDialog { Filter = "MP4-video (*.mp4)|*.mp4", FileName = "film.mp4" };
        if (dialoog.ShowDialog() == true)
        {
            File.Copy(_filmBestand, dialoog.FileName, overwrite: true);
            StatusText.Text = $"Opgeslagen: {dialoog.FileName}";
        }
    }

    // ---------------------------------------------------------------- galerij

    private async Task VulGalerijAsync()
    {
        _galerijVullen = true;
        try
        {
            var lijst = await _service.ListJobsAsync("film");
            Galerij.Items.Clear();
            foreach (var taak in lijst.Where(t => t.Bestand is not null && File.Exists(t.Bestand)))
            {
                var tekst = taak.Prompt.Length > 40 ? taak.Prompt[..40] + "…" : taak.Prompt;
                Galerij.Items.Add(new ListBoxItem
                {
                    Content = GalerijPrullenbak.MaakRegel($"{taak.GemaaktOp.LocalDateTime:dd-MM HH:mm}  {taak.Scenes.Count} scènes  {tekst}", () => VerwijderFilmAsync(taak)),
                    Tag = taak,
                    ToolTip = taak.Prompt,
                    Margin = new Thickness(0, 0, 8, 0),
                });
            }
        }
        catch (Exception)
        {
            // Service (nog) niet bereikbaar: geen galerij, geen ruzie.
        }
        finally
        {
            _galerijVullen = false;
        }
    }

    /// <summary>
    /// Verwijdert een eerder gemaakte film met zijn hele map: de film, alle scènes en de sleutelbeelden. Scènes op de
    /// tijdlijn die naar die bestanden wezen worden weer "nog te maken", en wat er nu speelt gaat eerst uit omdat de
    /// speler het bestand open houdt.
    /// </summary>
    private async Task VerwijderFilmAsync(BeeldJobStatus taak)
    {
        if (_bezig)
        {
            StatusText.Text = "Wacht tot het maken klaar is voor je een film verwijdert.";
            return;
        }

        if (!GalerijPrullenbak.Bevestig("film", taak.Prompt, "De film en alle bijbehorende scènes en sleutelbeelden worden verwijderd."))
        {
            return;
        }

        var map = System.IO.Path.GetDirectoryName(taak.Bestand);

        if (GalerijPrullenbak.ZitIn(_spelerBestand, map))
        {
            Speler.Stop();
            Speler.Close();
            Speler.Source = null;
            _spelerBestand = null;
            _afspeellijst = false;
            LegeTekst.Visibility = Visibility.Visible;
            SleutelBeeld.Visibility = Visibility.Collapsed;
            SleutelLabel.Visibility = Visibility.Collapsed;
            await Task.Delay(300);
        }

        if (GalerijPrullenbak.ZitIn(_filmBestand, map))
        {
            _filmBestand = null;
            ToonFilmButton.IsEnabled = false;
            OpslaanButton.IsEnabled = false;
        }

        try
        {
            await _service.DeleteJobAsync(taak.JobId);
        }
        catch (Exception fout)
        {
            StatusText.Text = $"Verwijderen mislukt: {fout.Message}";
            return;
        }

        foreach (var scene in _scenes)
        {
            if (GalerijPrullenbak.ZitIn(scene.Bestand, map))
            {
                scene.Bestand = null;
                scene.Status = "wachtend";
            }

            if (GalerijPrullenbak.ZitIn(scene.Sleutel, map))
            {
                scene.Sleutel = null;
            }
        }

        Teken();
        await VulGalerijAsync();
        StatusText.Text = "Film verwijderd.";
    }

    /// <summary>Een eerder gemaakte film openen: de film speelt en de scènes komen terug op de tijdlijn.</summary>
    private void Galerij_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_galerijVullen || _bezig || Galerij.SelectedItem is not ListBoxItem { Tag: BeeldJobStatus taak })
        {
            return;
        }

        if (taak.Bestand is not null)
        {
            _filmBestand = taak.Bestand;
            ToonFilmButton.IsEnabled = true;
            OpslaanButton.IsEnabled = true;
            _afspeellijst = false;
            Speel(taak.Bestand);
        }

        if (_scenes.Any(s => !string.IsNullOrWhiteSpace(s.Gebeurt) || s.Bestand is not null) &&
            MessageBox.Show("Ook de scènes van deze film op de tijdlijn zetten? De huidige scènes worden dan vervangen.",
                "ImageFunn", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        if (taak.Scenes.Count > 0)
        {
            _scenes = taak.Scenes.Select(Kopie).ToList();
            StijlBox.Text = taak.Stijl;
            IdeeBox.Text = taak.Prompt;
            if (taak.Modus.Length > 0)
            {
                KiesTag(ModusKeuze, taak.Modus);
            }

            if (taak.Overgang.Length > 0)
            {
                KiesTag(OvergangKeuze, taak.Overgang);
            }

            KiesTag(FormaatKeuze, $"{taak.Breedte}x{taak.Hoogte}");
            Selecteer(0);
        }
    }

    // ---------------------------------------------------------------- verhaal bewaren

    private sealed class Verhaal
    {
        public string Idee { get; set; } = string.Empty;

        public string Stijl { get; set; } = string.Empty;

        public string Modus { get; set; } = "sleutelbeelden";

        public string Overgang { get; set; } = "zacht";

        public string Videomodel { get; set; } = "ltx";

        public string Beeldmodel { get; set; } = "juggernaut";

        public string Formaat { get; set; } = "704x480";

        public string Beeldstijl { get; set; } = "realistisch";

        public bool CastGebruik { get; set; }

        public List<CastItem> Cast { get; set; } = [];

        public List<FilmScene> Scenes { get; set; } = [];
    }

    private void VerhaalOpslaan_Click(object sender, RoutedEventArgs e)
    {
        var dialoog = new SaveFileDialog { Filter = "ImageFunn-verhaal (*.json)|*.json", FileName = "verhaal.json" };
        if (dialoog.ShowDialog() != true)
        {
            return;
        }

        var verhaal = new Verhaal
        {
            Idee = IdeeBox.Text,
            Stijl = StijlBox.Text,
            Modus = TagVan(ModusKeuze),
            Overgang = TagVan(OvergangKeuze),
            Videomodel = TagVan(VideomodelKeuze),
            Beeldmodel = TagVan(BeeldmodelKeuze),
            Formaat = TagVan(FormaatKeuze),
            Beeldstijl = TagVan(BeeldstijlKeuze),
            CastGebruik = CastGebruikBox.IsChecked == true,
            Cast = _cast,
            Scenes = _scenes.Select(Kopie).ToList(),
        };
        File.WriteAllText(dialoog.FileName, JsonSerializer.Serialize(verhaal, JsonOpties));
        StatusText.Text = $"Verhaal opgeslagen: {dialoog.FileName}";
    }

    private void VerhaalOpenen_Click(object sender, RoutedEventArgs e)
    {
        var dialoog = new OpenFileDialog { Filter = "ImageFunn-verhaal (*.json)|*.json" };
        if (dialoog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var verhaal = JsonSerializer.Deserialize<Verhaal>(File.ReadAllText(dialoog.FileName), JsonOpties);
            if (verhaal is null || verhaal.Scenes.Count == 0)
            {
                StatusText.Text = "Dit bestand bevat geen scènes.";
                return;
            }

            IdeeBox.Text = verhaal.Idee;
            StijlBox.Text = verhaal.Stijl;
            KiesTag(ModusKeuze, verhaal.Modus);
            KiesTag(OvergangKeuze, verhaal.Overgang);
            KiesTag(VideomodelKeuze, verhaal.Videomodel);
            KiesTag(BeeldmodelKeuze, verhaal.Beeldmodel);
            KiesTag(FormaatKeuze, verhaal.Formaat);
            KiesTag(BeeldstijlKeuze, verhaal.Beeldstijl);
            _cast = verhaal.Cast;
            foreach (var item in _cast.Where(c => c.Referentie is not null && !File.Exists(c.Referentie)))
            {
                item.Referentie = null;
            }

            CastGebruikBox.IsChecked = verhaal.CastGebruik;

            // Een gemaakte scène telt alleen nog als het bestand er nog is.
            foreach (var scene in verhaal.Scenes.Where(s => s.Bestand is not null && !File.Exists(s.Bestand)))
            {
                Maak_ongeldig(scene);
            }

            _scenes = verhaal.Scenes;
            Selecteer(0);
            StatusText.Text = $"Verhaal geopend: {_scenes.Count} scènes.";
        }
        catch (Exception fout)
        {
            StatusText.Text = $"Openen mislukt: {fout.Message}";
        }
    }
}
