using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageFunn.App.Ai;
using ImageFunn.App.ImageService;

namespace ImageFunn.App.Regios.Inhoud;

/// <summary>
/// Het derde deel van FilmMaker: de cast, een lijst met spelers, plekken en belangrijke voorwerpen, elk met een
/// eigen referentiebeeld. Bij het maken van de sleutelbeelden gaan het beeld van de speler, van de plek en van het
/// voorwerp van die scène als voorbeeld mee, zodat een personage er overal hetzelfde uitziet en een plek herkenbaar
/// blijft. Optioneel: zonder het vinkje werkt alles als voorheen.
/// </summary>
public partial class FilmMaker
{
    private sealed record CastKaart(CastItem Item, Image Beeld, CheckBox Opnieuw);

    private List<CastItem> _cast = [];

    private readonly List<CastKaart> _castKaarten = [];

    // ---------------------------------------------------------------- stijl en sterkte

    private Beeldstijl HuidigeStijl() => Beeldstijl.Van(TagVan(BeeldstijlKeuze));

    /// <summary>De stijl zoals de service hem krijgt: altijd met de gekozen stijltekst ervoor.</summary>
    private string StijlVoorService() => HuidigeStijl().MetStijl(StijlBox.Text.Trim());

    /// <summary>Wisselt je de beeldstijl, dan gaat de stijltekst in het vak mee (als hij met een bekende stijl begint).</summary>
    private void BeeldstijlKeuze_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StijlBox is null || BeeldstijlKeuze.SelectedItem is null)
        {
            return;
        }

        StijlBox.Text = HuidigeStijl().Wissel(StijlBox.Text);
    }

    /// <summary>Hoe sterk een speler, een plek en een voorwerp op hun referentiebeeld moeten lijken, afgeleid van de keuze 'Gelijkenis'.</summary>
    private (double Speler, double Plek, double Voorwerp) CastSterkte() => GelijkenisKeuze.SelectedIndex switch
    {
        0 => (0.35, 0.35, 0.15),
        2 => (0.6, 0.5, 0.35),
        _ => (0.5, 0.45, 0.25),
    };

    // ---------------------------------------------------------------- kopie naar de service

    /// <summary>Een kopie van de scène met de paden van de referentiebeelden van haar spelers, plek en voorwerp erin.</summary>
    private FilmScene KopieVoorService(FilmScene scene)
    {
        var kopie = Kopie(scene);
        if (CastGebruikBox.IsChecked != true)
        {
            return kopie;
        }

        string? Pad(string soort, string? naam) =>
            _cast.FirstOrDefault(c => c.Soort == soort && c.Naam == naam)?.Referentie is { } p && File.Exists(p) ? p : null;

        kopie.SpelerRefs = scene.Spelers.Select(n => Pad("speler", n)).Where(p => p is not null).Select(p => p!).ToList();
        kopie.PlekRef = Pad("plek", scene.Plek);
        kopie.VoorwerpRef = scene.Voorwerpen.Select(n => Pad("voorwerp", n)).FirstOrDefault(p => p is not null);
        return kopie;
    }

    // ---------------------------------------------------------------- spelers, plek en voorwerpen per scène

    private void CastGebruik_Changed(object sender, RoutedEventArgs e)
    {
        if (CastScenePaneel is null || CastInhoud is null)
        {
            return;
        }

        var aan = CastGebruikBox.IsChecked == true;
        CastInhoud.IsEnabled = aan;
        CastInhoud.Visibility = aan ? Visibility.Visible : Visibility.Collapsed;
        CastUit.Visibility = aan ? Visibility.Collapsed : Visibility.Visible;
        CastScenePaneel.Visibility = aan ? Visibility.Visible : Visibility.Collapsed;
        ToonSceneCast(Gekozen);
        VernieuwStappen();
    }

    private void ToonSceneCast(FilmScene? scene)
    {
        if (CastScenePaneel is null)
        {
            return;
        }

        var oud = _vullen;
        _vullen = true;
        try
        {
            VulVinkjes(SpelersLijst, "speler", scene?.Spelers, SpelersVanSceneBijwerken, "Nog geen spelers - maak ze op de tab Cast");
            VulVinkjes(VoorwerpenLijst, "voorwerp", scene?.Voorwerpen, VoorwerpenVanSceneBijwerken, "Nog geen voorwerpen - maak ze op de tab Cast");

            PlekKeuze.Items.Clear();
            PlekKeuze.Items.Add("(geen vaste plek)");
            foreach (var plek in _cast.Where(c => c.Soort == "plek"))
            {
                PlekKeuze.Items.Add(plek.Naam);
            }

            PlekKeuze.SelectedIndex = scene?.Plek is { } naam && PlekKeuze.Items.Contains(naam) ? PlekKeuze.Items.IndexOf(naam) : 0;
        }
        finally
        {
            _vullen = oud;
        }
    }

    private void VulVinkjes(WrapPanel paneel, string soort, List<string>? gekozen, Action bijwerken, string leegTekst)
    {
        paneel.Children.Clear();
        foreach (var item in _cast.Where(c => c.Soort == soort))
        {
            var vak = new CheckBox
            {
                Content = item.Naam,
                IsChecked = gekozen?.Contains(item.Naam) == true,
                Margin = new Thickness(0, 0, 14, 4),
                FontSize = 12,
            };
            vak.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            vak.Checked += (_, _) => bijwerken();
            vak.Unchecked += (_, _) => bijwerken();
            paneel.Children.Add(vak);
        }

        if (!_cast.Any(c => c.Soort == soort))
        {
            paneel.Children.Add(new TextBlock
            {
                Text = leegTekst,
                FontSize = 12,
                Foreground = (Brush)FindResource("TextMutedBrush"),
            });
        }
    }

    private void SpelersVanSceneBijwerken()
    {
        if (_vullen || Gekozen is not { } scene)
        {
            return;
        }

        scene.Spelers = SpelersLijst.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToList();
        Maak_ongeldig(scene, true);
        Teken();
    }

    private void VoorwerpenVanSceneBijwerken()
    {
        if (_vullen || Gekozen is not { } scene)
        {
            return;
        }

        scene.Voorwerpen = VoorwerpenLijst.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToList();
        Maak_ongeldig(scene, true);
        Teken();
    }

    private void PlekKeuze_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vullen || PlekKeuze is null || Gekozen is not { } scene || PlekKeuze.SelectedItem is null)
        {
            return;
        }

        scene.Plek = PlekKeuze.SelectedIndex > 0 ? (string)PlekKeuze.SelectedItem : null;
        Maak_ongeldig(scene, true);
        Teken();
    }

    // ---------------------------------------------------------------- de cast-tab

    private void CastBezig(bool bezig)
    {
        CastToevoegSpelerButton.IsEnabled = !bezig;
        CastToevoegPlekButton.IsEnabled = !bezig;
        CastToevoegVoorwerpButton.IsEnabled = !bezig;
        CastLijstButton.IsEnabled = !bezig;
        CastMaakButton.IsEnabled = !bezig;
        CastStopButton.IsEnabled = bezig;
        foreach (var kaart in _castKaarten)
        {
            kaart.Opnieuw.IsEnabled = !bezig;
        }
    }

    private static BitmapImage? Laad(string pad, int breedte)
    {
        try
        {
            var beeld = new BitmapImage();
            beeld.BeginInit();
            beeld.CacheOption = BitmapCacheOption.OnLoad;
            beeld.DecodePixelWidth = breedte;
            beeld.UriSource = new Uri(pad);
            beeld.EndInit();
            beeld.Freeze();
            return beeld;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int SoortIndex(string soort) => soort switch { "plek" => 1, "voorwerp" => 2, _ => 0 };

    private static string SoortNaam(int index) => index switch { 1 => "plek", 2 => "voorwerp", _ => "speler" };

    private void BouwCastKaarten()
    {
        _castKaarten.Clear();
        CastLijst.Children.Clear();

        foreach (var item in _cast)
        {
            var naam = new TextBox { Text = item.Naam, FontWeight = FontWeights.SemiBold, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 2, 4, 2) };
            naam.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            naam.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimaryBrush");
            naam.LostFocus += (_, _) => HernoemCast(item, naam.Text.Trim());

            var soort = new ComboBox { Margin = new Thickness(0, 4, 0, 4) };
            soort.Items.Add("Speler");
            soort.Items.Add("Plek");
            soort.Items.Add("Voorwerp");
            soort.SelectedIndex = SoortIndex(item.Soort);
            soort.SelectionChanged += (_, _) => item.Soort = SoortNaam(soort.SelectedIndex);

            var beschrijving = new TextBox
            {
                Text = item.Beschrijving,
                Height = 60,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2),
                FontSize = 12,
                ToolTip = "Beschrijf alleen hoe de speler of het voorwerp eruitziet, of hoe de plek eruitziet (zonder personen). Hoe preciezer, hoe beter het blijft kloppen.",
            };
            beschrijving.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            beschrijving.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimaryBrush");

            var beeld = new Image { Height = item.Soort == "speler" ? 190 : 130, Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 0, 4) };
            RenderOptions.SetBitmapScalingMode(beeld, BitmapScalingMode.HighQuality);

            var opnieuw = new CheckBox { Content = "Opnieuw maken", FontSize = 12, Margin = new Thickness(0, 2, 0, 2), IsChecked = item.Opnieuw };
            opnieuw.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            opnieuw.Checked += (_, _) => item.Opnieuw = true;
            opnieuw.Unchecked += (_, _) => item.Opnieuw = false;

            // Een andere omschrijving betekent een nieuw beeld: de Engelse tekst wordt opnieuw afgeleid.
            beschrijving.TextChanged += (_, _) =>
            {
                if (item.Beschrijving == beschrijving.Text)
                {
                    return;
                }

                item.Beschrijving = beschrijving.Text;
                item.Beeld = string.Empty;
                if (item.Referentie is not null)
                {
                    opnieuw.IsChecked = true;
                }
            };

            var verwijder = new Button { Content = "Verwijderen", Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            verwijder.Click += (_, _) => VerwijderCast(item);

            var inhoud = new StackPanel();
            inhoud.Children.Add(Omrand(naam));
            inhoud.Children.Add(soort);
            inhoud.Children.Add(Omrand(beschrijving));
            inhoud.Children.Add(beeld);
            inhoud.Children.Add(opnieuw);
            inhoud.Children.Add(verwijder);

            var kaart = new Border
            {
                Width = 250,
                Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Child = inhoud,
            };
            kaart.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            kaart.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            CastLijst.Children.Add(kaart);

            _castKaarten.Add(new CastKaart(item, beeld, opnieuw));
            VernieuwCastBeeld(item);
        }

        CastStatus.Text = _cast.Count == 0
            ? "Nog geen spelers, plekken of voorwerpen. Laat het chatmodel een lijst maken uit je verhaal, of voeg ze zelf toe."
            : "Controleer de lijst. Maak daarna de referentiebeelden en kijk of het is wat je bedoelt.";
    }

    private static Border Omrand(UIElement kind)
    {
        var rand = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = kind };
        rand.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
        rand.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return rand;
    }

    private void VernieuwCastBeeld(CastItem item)
    {
        var kaart = _castKaarten.FirstOrDefault(k => ReferenceEquals(k.Item, item));
        if (kaart is not null && item.Referentie is { } pad && File.Exists(pad) && Laad(pad, 500) is { } beeld)
        {
            kaart.Beeld.Source = beeld;
        }
    }

    private void HernoemCast(CastItem item, string nieuw)
    {
        if (nieuw.Length == 0 || nieuw == item.Naam || _cast.Any(c => !ReferenceEquals(c, item) && c.Naam == nieuw))
        {
            return;
        }

        var oud = item.Naam;
        item.Naam = nieuw;
        foreach (var scene in _scenes)
        {
            scene.Spelers = scene.Spelers.Select(n => n == oud ? nieuw : n).ToList();
            scene.Voorwerpen = scene.Voorwerpen.Select(n => n == oud ? nieuw : n).ToList();
            if (scene.Plek == oud)
            {
                scene.Plek = nieuw;
            }
        }
    }

    private void VerwijderCast(CastItem item)
    {
        _cast.Remove(item);
        foreach (var scene in _scenes)
        {
            scene.Spelers = scene.Spelers.Where(n => n != item.Naam).ToList();
            scene.Voorwerpen = scene.Voorwerpen.Where(n => n != item.Naam).ToList();
            if (scene.Plek == item.Naam)
            {
                scene.Plek = null;
            }
        }

        BouwCastKaarten();
        VernieuwStappen();
    }

    private string UniekeNaam(string basis)
    {
        var naam = basis;
        for (var n = 2; _cast.Any(c => c.Naam == naam); n++)
        {
            naam = $"{basis} {n}";
        }

        return naam;
    }

    private void CastToevoegSpeler_Click(object sender, RoutedEventArgs e) => VoegCastToe("speler", "Nieuwe speler");

    private void CastToevoegPlek_Click(object sender, RoutedEventArgs e) => VoegCastToe("plek", "Nieuwe plek");

    private void CastToevoegVoorwerp_Click(object sender, RoutedEventArgs e) => VoegCastToe("voorwerp", "Nieuw voorwerp");

    private void VoegCastToe(string soort, string naam)
    {
        _cast.Add(new CastItem { Naam = UniekeNaam(naam), Soort = soort });
        BouwCastKaarten();
        VernieuwStappen();
    }

    // ---------------------------------------------------------------- lijst laten schrijven

    private async void CastLijst_Click(object sender, RoutedEventArgs e)
    {
        if (_scenes.All(s => string.IsNullOrWhiteSpace(s.Gebeurt) && string.IsNullOrWhiteSpace(s.Beeld)))
        {
            CastStatus.Text = "Schrijf eerst het verhaal (de scènes), dan kan het chatmodel zien wie en wat erin voorkomt.";
            return;
        }

        if (_cast.Count > 0 &&
            MessageBox.Show("De huidige lijst wordt vervangen door een nieuwe. Doorgaan?", "ImageFunn",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        CastBezig(true);
        CastStatus.Text = "Het chatmodel haalt de spelers, plekken en voorwerpen uit het verhaal… (dit kan een minuut duren)";

        try
        {
            var (voorstel, fout) = await _schrijver.MaakCastAsync(StijlBox.Text, _scenes, CancellationToken.None);
            if (voorstel is null)
            {
                CastStatus.Text = $"Lijst maken mislukt: {fout}";
                return;
            }

            // Een bestaand referentiebeeld blijft staan als de naam hetzelfde is gebleven.
            foreach (var nieuw in voorstel.Cast)
            {
                if (_cast.FirstOrDefault(c => c.Naam == nieuw.Naam && c.Soort == nieuw.Soort && c.Beschrijving == nieuw.Beschrijving) is { } oud)
                {
                    nieuw.Referentie = oud.Referentie;
                }
            }

            _cast = voorstel.Cast;
            for (var i = 0; i < _scenes.Count; i++)
            {
                if (voorstel.Scenes.TryGetValue(i + 1, out var wie))
                {
                    _scenes[i].Spelers = wie.Spelers;
                    _scenes[i].Plek = wie.Plek;
                    _scenes[i].Voorwerpen = wie.Voorwerpen;
                    Maak_ongeldig(_scenes[i], true);
                }
            }

            BouwCastKaarten();
            CastStatus.Text = $"{_cast.Count(c => c.Soort == "speler")} spelers, {_cast.Count(c => c.Soort == "plek")} plekken en " +
                              $"{_cast.Count(c => c.Soort == "voorwerp")} voorwerpen gevonden. Pas aan wat je wilt en maak de referentiebeelden.";
            Teken();
        }
        catch (Exception fout)
        {
            CastStatus.Text = $"Kon het chatmodel niet bereiken: {fout.Message}";
        }
        finally
        {
            CastBezig(false);
        }
    }

    // ---------------------------------------------------------------- referentiebeelden maken

    private async void CastMaak_Click(object sender, RoutedEventArgs e)
    {
        if (_cast.Count == 0)
        {
            CastStatus.Text = "Er zijn nog geen spelers, plekken of voorwerpen.";
            return;
        }

        CastBezig(true);
        var klok = Stopwatch.StartNew();

        try
        {
            await RunCastAsync(klok);
        }
        catch (Exception fout)
        {
            CastStatus.Text = $"Kon de beeldservice of het chatmodel niet bereiken: {fout.Message}";
        }
        finally
        {
            _jobId = null;
            CastBezig(false);
            ZetTaakbalk(null, false);
            VernieuwStappen();
        }
    }

    private async void CastStopKnop_Click(object sender, RoutedEventArgs e) => await StopHuidigeTaakAsync();

    private async Task StopHuidigeTaakAsync()
    {
        if (_jobId is null)
        {
            return;
        }

        try
        {
            await _service.StopJobAsync(_jobId);
        }
        catch (Exception)
        {
            // Mislukt stoppen is niet erg: de taak loopt gewoon af.
        }
    }

    /// <summary>
    /// Maakt de referentiebeelden die nog ontbreken of opnieuw gemaakt moeten worden. Scènes die een speler, plek of
    /// voorwerp met een nieuw beeld gebruiken krijgen daarna een nieuw sleutelbeeld, want dat hoort bij het oude voorbeeld.
    /// </summary>
    private async Task<bool> RunCastAsync(Stopwatch klok)
    {
        var nodig = _cast.Where(c => c.Opnieuw || c.Referentie is not { } p || !File.Exists(p)).ToList();
        if (nodig.Count == 0)
        {
            CastStatus.Text = "Alle referentiebeelden zijn al gemaakt.";
            return true;
        }

        // De beeldmodellen lezen alleen Engels: de omschrijving eerst laten omzetten.
        foreach (var item in nodig.Where(c => string.IsNullOrWhiteSpace(c.Beeld)))
        {
            if (string.IsNullOrWhiteSpace(item.Beschrijving))
            {
                CastStatus.Text = $"'{item.Naam}' heeft nog geen omschrijving.";
                return false;
            }

            CastStatus.Text = $"Omschrijving van '{item.Naam}' omzetten voor het beeldmodel…";
            StatusText.Text = CastStatus.Text;
            var (beeld, fout) = await _schrijver.LeidCastBeeldAsync(item.Soort, StijlVoorService(), item.Beschrijving, CancellationToken.None);
            if (beeld is null)
            {
                CastStatus.Text = $"Omzetten mislukt voor '{item.Naam}': {fout}";
                return false;
            }

            item.Beeld = beeld;
        }

        var formaat = TagVan(FormaatKeuze).Split('x');
        var opdracht = new BeeldOpdracht
        {
            Soort = "cast",
            Breedte = int.Parse(formaat[0]),
            Hoogte = int.Parse(formaat[1]),
            Stijl = StijlVoorService(),
            Negatief = HuidigeStijl().Negatief,
            Prompt = IdeeBox.Text.Trim(),
            Seed = Random.Shared.Next(),
            Cast = _cast,
        };

        KopText.Text = "Referentiebeelden maken";
        ProcentText.Text = string.Empty;
        _jobId = await _service.StartJobAsync(opdracht);
        CastStopButton.IsEnabled = true;
        StopButton.IsEnabled = true;

        while (true)
        {
            var status = await _service.GetStatusAsync(_jobId);

            if (status.Cast.Count == _cast.Count)
            {
                for (var i = 0; i < _cast.Count; i++)
                {
                    _cast[i].Referentie = status.Cast[i].Referentie;
                }
            }

            CastStatus.Text = status.Stap;
            CastVoortgang.Value = status.Voortgang;
            StatusText.Text = status.Stap;
            Voortgang.Value = status.Voortgang;
            ProcentText.Text = $"{status.Voortgang:P0}";
            TijdText.Text = $"Verstreken {klok.Elapsed:h\\:mm\\:ss}";
            ZetTaakbalk(status.Voortgang, false);

            foreach (var item in nodig)
            {
                VernieuwCastBeeld(item);
            }

            if (status.IsKlaar)
            {
                foreach (var item in nodig)
                {
                    item.Opnieuw = false;
                    foreach (var scene in _scenes.Where(s => s.Plek == item.Naam || s.Spelers.Contains(item.Naam) || s.Voorwerpen.Contains(item.Naam)))
                    {
                        Maak_ongeldig(scene, true);
                    }
                }

                foreach (var kaart in _castKaarten)
                {
                    kaart.Opnieuw.IsChecked = false;
                }

                CastVoortgang.Value = 1;
                CastStatus.Text = "Referentiebeelden klaar. Controleer ze: past het niet, pas de omschrijving aan en vink 'Opnieuw maken' aan.";
                Teken();
                return true;
            }

            if (status.IsMislukt)
            {
                CastStatus.Text = status.Foutmelding ?? "Referentiebeelden maken mislukt.";
                return false;
            }

            await Task.Delay(PeilInterval);
        }
    }
}
