using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImageFunn.App.ImageService;

namespace ImageFunn.App.Regios.Inhoud;

/// <summary>
/// Het derde deel van TekstNaarBeeld: een lopend gesprek met het beeld. Elke aanwijzing die je geeft blijft in een lijst
/// staan (aan of uit te zetten, te verwijderen) en elke ronde bouwt voort op de oorspronkelijke prompt plus alle aanwijzingen
/// die aanstaan. Alle versies blijven bewaard, ook na sluiten van de app, zodat je terug kunt naar een eerdere stand.
/// Ook: een beeld meegeven aan Film maken, als speler, plek of voorwerp.
/// </summary>
public partial class TekstNaarBeeld
{
    private BeeldSessie? _sessie;

    private BeeldVersie? _versie;

    /// <summary>Wordt afgevuurd als je een beeld in de film wilt gebruiken; het item heeft het (bewaarde) beeld als referentie.</summary>
    public event EventHandler<CastItem>? NaarFilm;

    /// <summary>Zoekt de bewaarde sessie bij dit beeld, of begint een nieuwe, en toont de aanwijzingen en versies.</summary>
    private void ZetSessie(string bestand, string? prompt)
    {
        _sessie = BeeldSessies.Zoek(bestand) ?? BeeldSessies.Nieuw(bestand, prompt ?? PromptBox.Text.Trim());
        _versie = _sessie.Versies.First(v => string.Equals(v.Bestand, bestand, StringComparison.OrdinalIgnoreCase));
        BouwAanwijzingen();
    }

    private void WisSessie()
    {
        _sessie = null;
        _versie = null;
        BouwAanwijzingen();
    }

    private void BouwAanwijzingen()
    {
        AanwijzingenLijst.Children.Clear();
        VersiesLijst.Children.Clear();

        var aanwijzingen = _versie?.Aanwijzingen ?? [];
        AanwijzingenPaneel.Visibility = aanwijzingen.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var a in aanwijzingen)
        {
            var vak = new CheckBox
            {
                Content = a.Gebied ? $"{a.Tekst}  (gebied)" : a.Tekst,
                IsChecked = a.Aan,
                Margin = new Thickness(0, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            };
            vak.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            vak.Checked += (_, _) => ZetAan(a, true);
            vak.Unchecked += (_, _) => ZetAan(a, false);

            var weg = new Button
            {
                Content = "✕",
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "Deze aanwijzing verwijderen",
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            weg.SetResourceReference(BackgroundProperty, "BackgroundBrush");
            weg.SetResourceReference(ForegroundProperty, "TextMutedBrush");
            weg.Click += (_, _) =>
            {
                _versie!.Aanwijzingen.Remove(a);
                BeeldSessies.Bewaar();
                BouwAanwijzingen();
            };

            var rij = new StackPanel { Orientation = Orientation.Horizontal };
            rij.Children.Add(vak);
            rij.Children.Add(weg);
            AanwijzingenLijst.Children.Add(rij);
        }

        var versies = _sessie?.Versies ?? [];
        VersiesPaneel.Visibility = versies.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < versies.Count; i++)
        {
            var versie = versies[i];
            var huidig = ReferenceEquals(versie, _versie);
            var knop = new Button
            {
                Content = i == 0 ? "Origineel" : $"Versie {i + 1}",
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(10, 3, 10, 3),
                IsEnabled = !huidig,
                ToolTip = huidig ? "Dit beeld zie je nu" : "Terug naar deze versie en van hieruit verder sturen",
            };
            if (huidig)
            {
                knop.SetResourceReference(StyleProperty, "AccentButtonStyle");
            }

            knop.Click += (_, _) =>
            {
                if (File.Exists(versie.Bestand))
                {
                    Toon(versie.Bestand, versie.Prompt);
                    StatusText.Text = "Je zit nu op een eerdere versie. De aanwijzingen die erbij horen staan aan; wat je nu bijstuurt, bouwt hierop verder.";
                }
            };
            VersiesLijst.Children.Add(knop);
        }
    }

    private static void ZetAan(Aanwijzing aanwijzing, bool aan)
    {
        aanwijzing.Aan = aan;
        BeeldSessies.Bewaar();
    }

    /// <summary>De wijzigingen voor de prompt: alle aanwijzingen die aanstaan, plus wat je nu typt.</summary>
    private string WijzigingenTekst(string nieuw)
    {
        var regels = (_versie?.Aanwijzingen ?? []).Where(a => a.Aan).Select(a => a.Tekst).ToList();
        regels.Add(nieuw);
        return string.Join("\n", regels.Select(r => "- " + r));
    }

    /// <summary>Een nieuw bijgestuurd beeld wordt een nieuwe versie met de aanwijzingen zoals ze nu staan, plus de nieuwe.</summary>
    private void VoegVersieToe(string bestand, string prompt, string nieuweAanwijzing, bool gebied)
    {
        if (_sessie is null || _versie is null)
        {
            return;
        }

        var aanwijzingen = _versie.Aanwijzingen
            .Select(a => new Aanwijzing { Tekst = a.Tekst, Aan = a.Aan, Gebied = a.Gebied })
            .ToList();
        aanwijzingen.Add(new Aanwijzing { Tekst = nieuweAanwijzing, Aan = true, Gebied = gebied });
        _sessie.Versies.Add(new BeeldVersie { Bestand = bestand, Prompt = prompt, Aanwijzingen = aanwijzingen });
        BeeldSessies.Bewaar();
    }

    // ---------------------------------------------------------------- gebruik in film

    private void NaarFilmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_huidigBestand is null || !File.Exists(_huidigBestand))
        {
            return;
        }

        var dialoog = new Window
        {
            Title = "Gebruik in film",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
        };
        dialoog.SetResourceReference(BackgroundProperty, "SurfaceBrush");

        var naam = new TextBox { Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(6, 4, 6, 4) };
        var soort = new ComboBox { Margin = new Thickness(0, 4, 0, 16) };
        soort.Items.Add("Voorwerp");
        soort.Items.Add("Speler");
        soort.Items.Add("Plek");
        soort.SelectedIndex = 0;

        var ok = new Button { Content = "Toevoegen", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(StyleProperty, "AccentButtonStyle");
        ok.Click += (_, _) => dialoog.DialogResult = true;
        var annuleer = new Button { Content = "Annuleren", IsCancel = true, Padding = new Thickness(14, 4, 14, 4) };

        var knoppen = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        knoppen.Children.Add(ok);
        knoppen.Children.Add(annuleer);

        var inhoud = new StackPanel { Margin = new Thickness(18) };
        inhoud.Children.Add(Etiket("Naam in de cast"));
        inhoud.Children.Add(naam);
        inhoud.Children.Add(Etiket("Wat is het?"));
        inhoud.Children.Add(soort);
        inhoud.Children.Add(knoppen);
        dialoog.Content = inhoud;

        naam.Loaded += (_, _) => naam.Focus();
        if (dialoog.ShowDialog() != true)
        {
            return;
        }

        var gekozen = naam.Text.Trim();
        if (gekozen.Length == 0)
        {
            StatusText.Text = "Geef het beeld eerst een naam.";
            return;
        }

        try
        {
            // Een eigen kopie: verwijder je het beeld hier later met de prullenbak, dan blijft de film het gewoon houden.
            var map = System.IO.Path.Combine(BeeldSessies.Map, "cast");
            Directory.CreateDirectory(map);
            var kopie = System.IO.Path.Combine(map, $"{Guid.NewGuid():N}.png");
            File.Copy(_huidigBestand, kopie);

            var soortNaam = soort.SelectedIndex switch { 1 => "speler", 2 => "plek", _ => "voorwerp" };
            var prompt = _versie?.Prompt ?? _huidigePrompt ?? string.Empty;
            NaarFilm?.Invoke(this, new CastItem
            {
                Naam = gekozen,
                Soort = soortNaam,
                Beschrijving = _sessie?.BasisPrompt ?? prompt,
                Beeld = prompt,
                Referentie = kopie,
            });
            StatusText.Text = $"'{gekozen}' staat nu in de cast van Film maken (tab 3) als {soortNaam}. Kies het daar per scène aan.";
        }
        catch (Exception fout)
        {
            StatusText.Text = $"Kon het beeld niet doorgeven: {fout.Message}";
        }
    }

    private static TextBlock Etiket(string tekst)
    {
        var blok = new TextBlock { Text = tekst, FontSize = 12 };
        blok.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        return blok;
    }
}
