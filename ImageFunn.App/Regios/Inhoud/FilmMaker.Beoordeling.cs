using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageFunn.App.ImageService;
using Microsoft.Win32;

namespace ImageFunn.App.Regios.Inhoud;

/// <summary>
/// Het tweede deel van FilmMaker: eerst alleen de sleutelbeelden maken, die laten
/// controleren en de opmerkingen van de gebruiker in het verhaal verwerken, en pas
/// daarna animeren. Zo kun je bijvoorbeeld zorgen dat het in elke scène dezelfde
/// robot is voordat er uren aan rekentijd in gaan zitten.
/// </summary>
public partial class FilmMaker
{
    /// <summary>Eén kaart in het controlescherm: het beeld van een scène met een opmerkingvak.</summary>
    private sealed record Kaart(int Index, Image Beeld, TextBox Opmerking, CheckBox Opnieuw);

    private readonly List<Kaart> _kaarten = [];

    private string? _eigenVoorbeeld;

    private void ConsistentieKeuze_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Tijdens het opbouwen van het scherm bestaan de andere elementen nog niet.
        if (VoorbeeldPaneel is null || ConsistentieKeuze.SelectedItem is null)
        {
            return;
        }

        VoorbeeldPaneel.Visibility = TagVan(ConsistentieKeuze) == "eigen" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void VoorbeeldButton_Click(object sender, RoutedEventArgs e)
    {
        var dialoog = new OpenFileDialog { Filter = "Afbeeldingen|*.png;*.jpg;*.jpeg;*.bmp;*.webp|Alle bestanden (*.*)|*.*" };
        if (dialoog.ShowDialog() == true)
        {
            _eigenVoorbeeld = dialoog.FileName;
            VoorbeeldTekst.Text = Path.GetFileName(dialoog.FileName);
        }
    }

    /// <summary>Wat de service nodig heeft om de personages op elkaar te laten lijken.</summary>
    private (string? Referentie, bool Eerste, double Sterkte) Consistentie()
    {
        var sterkte = double.Parse(TagVan(GelijkenisKeuze), System.Globalization.CultureInfo.InvariantCulture);
        return TagVan(ConsistentieKeuze) switch
        {
            "eigen" when _eigenVoorbeeld is not null && File.Exists(_eigenVoorbeeld) => (_eigenVoorbeeld, false, sterkte),
            "eerste" => (null, true, sterkte),
            _ => (null, false, sterkte),
        };
    }

    /// <summary>De scènes waarvan het beeld beoordeeld kan worden: bij "doorlopend" alleen de eerste, de rest begint op het einde van de vorige.</summary>
    private List<int> BeoordeelbareScenes() =>
        TagVan(ModusKeuze) == "doorlopend" ? [0] : Enumerable.Range(0, _scenes.Count).ToList();

    /// <summary>De scènes die nog geen sleutelbeeld hebben (of waarvan het bestand weg is).</summary>
    private List<int> NodigeSleutelbeelden() =>
        BeoordeelbareScenes().Where(i => _scenes[i].Sleutel is not { } pad || !File.Exists(pad)).ToList();

    private static BitmapImage? LaadMiniatuur(string pad)
    {
        try
        {
            var beeld = new BitmapImage();
            beeld.BeginInit();
            beeld.CacheOption = BitmapCacheOption.OnLoad;
            beeld.DecodePixelWidth = 640;
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

    // ---------------------------------------------------------------- engelse tekst afleiden

    /// <summary>De modellen lezen alleen Engels: scènes zonder Engelse tekst laten we eerst afleiden. False = mislukt.</summary>
    private async Task<bool> LeidAfMissendeAsync()
    {
        // Clean: geen vertaling. Wat je bij 'Wat gebeurt er?' typt wordt zelf het beeld en de beweging.
        if (Clean)
        {
            foreach (var scene in _scenes.Where(s => string.IsNullOrEmpty(s.Bestand)))
            {
                if (string.IsNullOrWhiteSpace(scene.Beeld))
                {
                    scene.Beeld = scene.Gebeurt.Trim();
                }

                if (string.IsNullOrWhiteSpace(scene.Beweging))
                {
                    scene.Beweging = scene.Gebeurt.Trim();
                }
            }

            ToonScene();
            return true;
        }

        var afleiden = _scenes.Where(s => string.IsNullOrEmpty(s.Bestand) && string.IsNullOrWhiteSpace(s.Beeld)).ToList();
        for (var n = 0; n < afleiden.Count; n++)
        {
            StatusText.Text = $"Engelse tekst afleiden voor scène {n + 1} van {afleiden.Count}…";
            KopText.Text = "Verhaal voorbereiden";
            var (beeld, beweging, fout) = await _schrijver.LeidAfAsync(StijlBox.Text, afleiden[n].Gebeurt, CancellationToken.None);
            if (beeld is null)
            {
                StatusText.Text = $"Afleiden mislukt voor '{afleiden[n].Titel}': {fout}";
                return false;
            }

            afleiden[n].Beeld = beeld;
            afleiden[n].Beweging = beweging ?? string.Empty;
        }

        ToonScene();
        return true;
    }

    // ---------------------------------------------------------------- sleutelbeelden maken

    /// <summary>
    /// Laat de service de sleutelbeelden van deze scènes (opnieuw) maken. Een scène waarvan het beeld
    /// opnieuw gemaakt wordt verliest zijn eerder gemaakte clip, die klopt dan niet meer.
    /// </summary>
    private async Task<bool> RunSleutelbeeldenAsync(IEnumerable<int> opnieuw, Stopwatch klok)
    {
        var set = opnieuw.ToHashSet();
        foreach (var i in set)
        {
            _scenes[i].Opnieuw = true;
            Maak_ongeldig(_scenes[i]);
        }

        Teken();

        var formaat = TagVan(FormaatKeuze).Split('x');
        var (referentie, eerste, sterkte) = Consistentie();
        var opdracht = new BeeldOpdracht
        {
            Soort = "sleutelbeelden",
            Breedte = int.Parse(formaat[0]),
            Hoogte = int.Parse(formaat[1]),
            Modus = TagVan(ModusKeuze),
            Stijl = StijlVoorService(),
            Beeldmodel = TagVan(BeeldmodelKeuze),
            Negatief = NegatiefVoorService(),
            Prompt = IdeeBox.Text.Trim(),
            Seed = Random.Shared.Next(),
            Referentie = referentie,
            EersteAlsReferentie = eerste,
            ReferentieSterkte = sterkte,
            SpelerSterkte = CastSterkte().Speler,
            PlekSterkte = CastSterkte().Plek,
            VoorwerpSterkte = CastSterkte().Voorwerp,
            Scenes = _scenes.Select(KopieVoorService).ToList(),
        };

        KopText.Text = "Sleutelbeelden maken";
        StatusText.Text = "Sleutelbeelden starten…";
        ZetFase(1);
        _jobId = await _service.StartJobAsync(opdracht);
        StopButton.IsEnabled = true;
        BeoordelingStopButton.IsEnabled = true;

        while (true)
        {
            var status = await _service.GetStatusAsync(_jobId);

            if (status.Scenes.Count == _scenes.Count)
            {
                for (var i = 0; i < _scenes.Count; i++)
                {
                    _scenes[i].Sleutel = status.Scenes[i].Sleutel;
                }
            }

            Voortgang.IsIndeterminate = status.Voortgang <= 0;
            Voortgang.Value = status.Voortgang;
            BeeldenVoortgang.Value = status.Voortgang;
            ProcentText.Text = $"{status.Voortgang:P0}";
            StatusText.Text = status.Stap;
            TijdText.Text = $"Verstreken {klok.Elapsed:h\\:mm\\:ss}";
            BeoordelingStatus.Text = status.Stap;
            ZetTaakbalk(status.Voortgang, status.Voortgang <= 0);

            // Een nieuw beeld laat zich meteen zien, ook voordat de rest klaar is.
            var nieuwste = set.Where(i => !string.IsNullOrEmpty(_scenes[i].Sleutel) && File.Exists(_scenes[i].Sleutel!))
                .DefaultIfEmpty(-1).Max();
            if (nieuwste >= 0)
            {
                ToonVoorbeeld(_scenes[nieuwste].Sleutel!, $"Beginbeeld van scène {nieuwste + 1}");
                VernieuwKaart(nieuwste);
            }

            if (status.IsKlaar)
            {
                foreach (var i in set)
                {
                    _scenes[i].Opnieuw = false;
                }

                StatusText.Text = $"Sleutelbeelden klaar in {klok.Elapsed:h\\:mm\\:ss}.";
                return true;
            }

            if (status.IsMislukt)
            {
                foreach (var i in set)
                {
                    _scenes[i].Opnieuw = false;
                }

                StatusText.Text = status.Foutmelding ?? "Sleutelbeelden maken mislukt.";
                return false;
            }

            await Task.Delay(PeilInterval);
        }
    }

    // ---------------------------------------------------------------- het controlescherm

    /// <summary>
    /// Bouwt de kaarten van de tab Beelden op uit de stand van het verhaal. Wat je al getypt of aangevinkt had
    /// blijft staan als je de tab opnieuw opent.
    /// </summary>
    private void BouwBeoordelingKaarten()
    {
        var oud = _kaarten.ToDictionary(k => k.Index, k => (Tekst: k.Opmerking.Text, Opnieuw: k.Opnieuw.IsChecked == true));
        _kaarten.Clear();
        BeoordelingLijst.Children.Clear();

        foreach (var i in BeoordeelbareScenes())
        {
            var beeld = new Image { Height = 180, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 6) };
            RenderOptions.SetBitmapScalingMode(beeld, BitmapScalingMode.HighQuality);

            var opmerking = new TextBox
            {
                Height = 56,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 12,
                ToolTip = "Wat moet er anders aan dit beeld?",
                Text = oud.TryGetValue(i, out var vorig) ? vorig.Tekst : string.Empty,
            };
            opmerking.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            opmerking.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimaryBrush");

            var opnieuwBox = new CheckBox
            {
                Content = "Opnieuw maken",
                Margin = new Thickness(0, 6, 0, 6),
                FontSize = 12,
                IsChecked = oud.TryGetValue(i, out var vorigVinkje) && vorigVinkje.Opnieuw,
            };
            opnieuwBox.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");

            var titel = new TextBlock
            {
                Text = $"{i + 1}. {_scenes[i].Titel}",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 4),
            };
            titel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var vak = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = opmerking };
            vak.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
            vak.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

            var inhoud = new StackPanel();
            inhoud.Children.Add(titel);
            inhoud.Children.Add(beeld);
            inhoud.Children.Add(opnieuwBox);
            inhoud.Children.Add(vak);

            var kaart = new Border
            {
                Width = 300,
                Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Child = inhoud,
            };
            kaart.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            kaart.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

            BeoordelingLijst.Children.Add(kaart);
            _kaarten.Add(new Kaart(i, beeld, opmerking, opnieuwBox));
            VernieuwKaart(i);
        }

        BeoordelingUitleg.Text = TagVan(ModusKeuze) == "doorlopend"
            ? "Bij 'doorlopend' begint elke volgende scène op het laatste beeld van de vorige; alleen het eerste beeld hoeft beoordeeld te worden. " +
              "Klopt het niet, typ dan wat anders moet of vink 'Opnieuw maken' aan."
            : "Elke scène begint met zo'n beeld. Klopt het niet, typ dan bij het beeld wat anders moet (bijvoorbeeld: andere kleur, andere vorm) " +
              "of vink 'Opnieuw maken' aan. Het chatmodel verwerkt je opmerkingen in het verhaal.";

        if (_jobId is null)
        {
            BeoordelingStatus.Text = NodigeSleutelbeelden().Count > 0
                ? "De sleutelbeelden zijn nog niet (allemaal) gemaakt. Druk op 'Maak sleutelbeelden'."
                : "Controleer de beelden. Tevreden? Dan kun je ze goedkeuren en gaat het animeren beginnen.";
        }
    }

    private void VernieuwKaart(int index)
    {
        var kaart = _kaarten.FirstOrDefault(k => k.Index == index);
        if (kaart is null || _scenes[index].Sleutel is not { } pad || !File.Exists(pad))
        {
            return;
        }

        if (LaadMiniatuur(pad) is { } miniatuur)
        {
            kaart.Beeld.Source = miniatuur;
        }
    }

    /// <summary>Maakt de sleutelbeelden die nog ontbreken (en eerst de cast als die gebruikt wordt en nog niet klaar is).</summary>
    private async void BeeldenMaak_Click(object sender, RoutedEventArgs e)
    {
        if (_scenes.Any(s => string.IsNullOrWhiteSpace(s.Gebeurt) && string.IsNullOrWhiteSpace(s.Beeld)))
        {
            BeoordelingStatus.Text = "Elke scène heeft een omschrijving nodig ('Wat gebeurt er?' op tab 2).";
            return;
        }

        if (NodigeSleutelbeelden().Count == 0)
        {
            BeoordelingStatus.Text = "Alle sleutelbeelden zijn al gemaakt. Wil je er een anders, typ dan een opmerking of vink 'Opnieuw maken' aan.";
            return;
        }

        BeoordelingBezig(true);
        var klok = Stopwatch.StartNew();

        try
        {
            if (!await LeidAfMissendeAsync())
            {
                BeoordelingStatus.Text = StatusText.Text;
                return;
            }

            if (CastGebruikBox.IsChecked == true && _cast.Count > 0 && _cast.Any(c => c.Referentie is not { } p || !File.Exists(p)))
            {
                BeoordelingStatus.Text = "Eerst de referentiebeelden van de cast maken…";
                if (!await RunCastAsync(klok))
                {
                    BeoordelingStatus.Text = CastStatus.Text;
                    return;
                }
            }

            BouwBeoordelingKaarten();
            var gelukt = await RunSleutelbeeldenAsync(NodigeSleutelbeelden(), klok);
            BeoordelingStatus.Text = gelukt
                ? "Sleutelbeelden klaar. Controleer ze: klopt iets niet, typ een opmerking en laat ze opnieuw maken."
                : $"Mislukt: {StatusText.Text}";
        }
        catch (Exception fout)
        {
            BeoordelingStatus.Text = $"Kon de beeldservice of het chatmodel niet bereiken: {fout.Message}";
        }
        finally
        {
            _jobId = null;
            BeoordelingBezig(false);
            Voortgang.IsIndeterminate = false;
            ZetTaakbalk(null, false);
            VernieuwStappen();
        }
    }

    private void BeoordelingBezig(bool bezig)
    {
        BeeldenMaakButton.IsEnabled = !bezig;
        BeoordelingNogmaalsButton.IsEnabled = !bezig;
        BeoordelingGoedButton.IsEnabled = !bezig;
        BeoordelingSluitButton.IsEnabled = !bezig;
        BeoordelingStopButton.IsEnabled = bezig;
        foreach (var kaart in _kaarten)
        {
            kaart.Opmerking.IsEnabled = !bezig;
            kaart.Opnieuw.IsEnabled = !bezig;
        }

        AlgemeneOpmerkingBox.IsEnabled = !bezig;
    }

    /// <summary>
    /// Verwerkt de opmerkingen in het verhaal en maakt de betrokken beelden opnieuw. Een opmerking voor het hele verhaal
    /// (of een gewijzigde personagebeschrijving) betekent dat alle beelden opnieuw gemaakt worden: anders lijken ze niet op elkaar.
    /// </summary>
    private async void BeoordelingNogmaals_Click(object sender, RoutedEventArgs e)
    {
        var algemeen = AlgemeneOpmerkingBox.Text.Trim();
        var opmerkingen = _kaarten
            .Where(k => k.Opmerking.Text.Trim().Length > 0)
            .ToDictionary(k => k.Index + 1, k => k.Opmerking.Text.Trim());
        var aangevinkt = _kaarten.Where(k => k.Opnieuw.IsChecked == true).Select(k => k.Index).ToList();

        if (algemeen.Length == 0 && opmerkingen.Count == 0 && aangevinkt.Count == 0)
        {
            BeoordelingStatus.Text = "Typ een opmerking, of vink bij een beeld 'Opnieuw maken' aan.";
            return;
        }

        BeoordelingBezig(true);
        var klok = Stopwatch.StartNew();

        try
        {
            var opnieuw = new HashSet<int>(aangevinkt);
            var alles = false;

            if (algemeen.Length > 0 || opmerkingen.Count > 0)
            {
                BeoordelingStatus.Text = "Het chatmodel verwerkt je opmerkingen in het verhaal…";
                var scenes = BeoordeelbareScenes().Select(i => (i + 1, _scenes[i].Titel, _scenes[i].Beeld)).ToList();
                var (stijl, beelden, fout) = await _schrijver.PasOpmerkingenToeAsync(
                    StijlBox.Text, scenes, algemeen, opmerkingen, CancellationToken.None);

                if (beelden is null)
                {
                    BeoordelingStatus.Text = $"Opmerkingen verwerken mislukt: {fout}";
                    return;
                }

                if (!string.IsNullOrWhiteSpace(stijl) && stijl != StijlBox.Text.Trim())
                {
                    StijlBox.Text = stijl;
                    alles = true; // de personagebeschrijving staat in elk beeld
                }

                foreach (var (nr, beeld) in beelden.Where(b => b.Key >= 1 && b.Key <= _scenes.Count))
                {
                    _scenes[nr - 1].Beeld = beeld;
                    opnieuw.Add(nr - 1);
                }

                foreach (var nr in opmerkingen.Keys.Where(n => n >= 1 && n <= _scenes.Count))
                {
                    opnieuw.Add(nr - 1);
                }

                alles |= algemeen.Length > 0;
            }

            // Zonder gelijke personagebeschrijving of voorbeeld zouden de andere beelden niet meer bij het nieuwe eerste beeld passen.
            if (TagVan(ConsistentieKeuze) == "eerste" && opnieuw.Contains(0) && TagVan(ModusKeuze) != "doorlopend")
            {
                alles = true;
            }

            if (alles)
            {
                opnieuw = BeoordeelbareScenes().ToHashSet();
            }

            BeoordelingStatus.Text = $"{opnieuw.Count} beeld(en) opnieuw maken…";
            var gelukt = await RunSleutelbeeldenAsync(opnieuw.OrderBy(i => i), klok);

            // Opmerkingen zijn verwerkt: de vakjes weer leeg, de nieuwe beelden staan er.
            AlgemeneOpmerkingBox.Text = gelukt ? string.Empty : algemeen;
            foreach (var kaart in _kaarten)
            {
                if (gelukt)
                {
                    kaart.Opmerking.Text = string.Empty;
                    kaart.Opnieuw.IsChecked = false;
                }

                VernieuwKaart(kaart.Index);
            }

            BeoordelingStatus.Text = gelukt
                ? "Nieuwe beelden staan klaar. Nog iets aan te passen, of goedkeuren?"
                : $"Mislukt: {StatusText.Text}";
        }
        catch (Exception fout)
        {
            BeoordelingStatus.Text = $"Kon de service of het chatmodel niet bereiken: {fout.Message}";
        }
        finally
        {
            _jobId = null;
            BeoordelingBezig(false);
            Voortgang.IsIndeterminate = false;
            ZetTaakbalk(null, false);
        }
    }

    private async void BeoordelingGoed_Click(object sender, RoutedEventArgs e)
    {
        ToonTab(4);
        ZetBezig(true);
        var klok = Stopwatch.StartNew();

        try
        {
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
        }
    }
}
