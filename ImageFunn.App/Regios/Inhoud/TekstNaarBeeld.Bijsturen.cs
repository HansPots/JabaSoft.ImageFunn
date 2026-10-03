using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageFunn.App.Ai;
using ImageFunn.App.ImageService;

namespace ImageFunn.App.Regios.Inhoud;

/// <summary>
/// Het tweede deel van TekstNaarBeeld: het getoonde beeld bijsturen in plaats van opnieuw beginnen.
///
/// Twee manieren, samen in één scherm. Zonder gebied is het beeld naar beeld: het beeld wordt deels opnieuw
/// opgebouwd, zoveel als de schuif toelaat (laag = de indeling blijft bijna gelijk). Met een aangewezen gebied
/// is het inpainting: alleen dat gebied wordt opnieuw gemaakt, de rest blijft zoals het was. Het resultaat is
/// een nieuw beeld in de galerij; het origineel blijft bestaan.
/// </summary>
public partial class TekstNaarBeeld
{
    private void SterkteSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SterkteTekst is not null)
        {
            SterkteTekst.Text = $"Hoeveel mag er veranderen: {e.NewValue:0}%";
        }
    }

    private void GebiedBox_Changed(object sender, RoutedEventArgs e)
    {
        if (Tekenvlak is null || PenseelPaneel is null)
        {
            return;
        }

        var aan = GebiedBox.IsChecked == true;
        Tekenvlak.Visibility = aan ? Visibility.Visible : Visibility.Collapsed;
        PenseelPaneel.Visibility = aan ? Visibility.Visible : Visibility.Collapsed;

        if (aan)
        {
            StelPenseelIn();

            // Een object toevoegen vraagt veel vrijheid in het gebied: meteen een hogere stand.
            if (SterkteSlider.Value < 75)
            {
                SterkteSlider.Value = 85;
            }

            StatusText.Text = "Teken over het gebied dat moet veranderen, typ wat daar moet komen en druk op 'Pas toe'.";
        }
    }

    private void PenseelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => StelPenseelIn();

    private void GumBox_Changed(object sender, RoutedEventArgs e) => StelPenseelIn();

    private void WisGebied_Click(object sender, RoutedEventArgs e) => Tekenvlak.Strokes.Clear();

    private void StelPenseelIn()
    {
        // Tijdens het opbouwen van het scherm melden de schuiven al een waarde terwijl de rest nog niet bestaat.
        if (Tekenvlak is null || PenseelSlider is null || GumBox is null)
        {
            return;
        }

        var maat = PenseelSlider.Value;
        var pen = Tekenvlak.DefaultDrawingAttributes;
        pen.Color = Color.FromArgb(150, 255, 60, 60);
        pen.IsHighlighter = false;
        pen.StylusTip = StylusTip.Ellipse;
        pen.Width = maat;
        pen.Height = maat;
        pen.FitToCurve = true;

        Tekenvlak.EraserShape = new EllipseStylusShape(maat, maat);
        Tekenvlak.EditingMode = GumBox.IsChecked == true ? InkCanvasEditingMode.EraseByPoint : InkCanvasEditingMode.Ink;
    }

    /// <summary>
    /// Zet wat er getekend is om in een zwart-wit masker op de afmetingen van het beeld zelf: wit = mag veranderen.
    /// Het tekenvlak ligt over het getoonde beeld, dat netjes in het vlak past (Uniform), dus we rekenen eerst uit
    /// waar het beeld in het vlak staat en schalen de strepen daarvan terug naar de pixels van het beeld.
    /// </summary>
    private string? MaakMasker()
    {
        if (Resultaat.Source is not BitmapSource bron || Tekenvlak.ActualWidth <= 0 || Tekenvlak.ActualHeight <= 0)
        {
            return null;
        }

        var breedte = bron.PixelWidth;
        var hoogte = bron.PixelHeight;
        var schaal = Math.Min(Tekenvlak.ActualWidth / breedte, Tekenvlak.ActualHeight / hoogte);
        var verschuivingX = (Tekenvlak.ActualWidth - (breedte * schaal)) / 2;
        var verschuivingY = (Tekenvlak.ActualHeight - (hoogte * schaal)) / 2;

        var strepen = Tekenvlak.Strokes.Clone();
        foreach (var streep in strepen)
        {
            streep.DrawingAttributes.Color = Colors.White;
            streep.DrawingAttributes.IsHighlighter = false;
        }

        var visual = new DrawingVisual();
        using (var tekening = visual.RenderOpen())
        {
            tekening.DrawRectangle(Brushes.Black, null, new Rect(0, 0, breedte, hoogte));

            var omzetting = new TransformGroup();
            omzetting.Children.Add(new TranslateTransform(-verschuivingX, -verschuivingY));
            omzetting.Children.Add(new ScaleTransform(1 / schaal, 1 / schaal));
            tekening.PushTransform(omzetting);
            foreach (var streep in strepen)
            {
                streep.Draw(tekening);
            }

            tekening.Pop();
        }

        var doel = new RenderTargetBitmap(breedte, hoogte, 96, 96, PixelFormats.Pbgra32);
        doel.Render(visual);

        var pad = Path.Combine(Path.GetTempPath(), $"imagefunn-masker-{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(doel));
        using (var stroom = File.Create(pad))
        {
            encoder.Save(stroom);
        }

        return pad;
    }

    private async void BijstuurButton_Click(object sender, RoutedEventArgs e)
    {
        if (_huidigBestand is null)
        {
            return;
        }

        var wijziging = WijzigBox.Text.Trim();
        if (wijziging.Length == 0)
        {
            StatusText.Text = "Typ eerst wat er anders moet.";
            return;
        }

        var gebiedAan = GebiedBox.IsChecked == true;
        if (gebiedAan && Tekenvlak.Strokes.Count == 0)
        {
            StatusText.Text = "Teken eerst het gebied op het beeld, of zet 'Gebied aanwijzen' uit om het hele beeld bij te sturen.";
            return;
        }

        int? seed = null;
        if (!string.IsNullOrWhiteSpace(SeedBox.Text))
        {
            if (!int.TryParse(SeedBox.Text.Trim(), out var s) || s < 0)
            {
                StatusText.Text = "De seed moet een heel getal zijn, of leeg.";
                return;
            }

            seed = s;
        }

        BijstuurButton.IsEnabled = false;
        GenereerButton.IsEnabled = false;
        Voortgang.Value = 0;
        Voortgang.Visibility = Visibility.Visible;
        string? maskerPad = null;

        try
        {
            // De prompt van het beeld bijwerken met de wijziging; het beeldmodel leest alleen Engels.
            StatusText.Text = "Wijziging verwerken in de prompt…";
            var basis = _huidigePrompt ?? PromptBox.Text.Trim();
            var (nieuw, fout) = await _verbeteraar.VerbeterAsync(
                $"ORIGINAL PROMPT: {basis}\nCHANGE REQUEST: {wijziging}", CancellationToken.None, Promptsoort.Bewerk);
            var prompt = nieuw ?? $"{basis}, {wijziging}";
            if (nieuw is null)
            {
                StatusText.Text = $"Prompt bijwerken mislukt ({fout}) - de wijziging wordt achter de prompt gezet.";
            }

            if (gebiedAan)
            {
                maskerPad = MaakMasker();
                if (maskerPad is null)
                {
                    StatusText.Text = "Het gebied kon niet gemaakt worden.";
                    return;
                }
            }

            var stijl = Beeldstijl.Van((string)((ComboBoxItem)StijlKeuze.SelectedItem).Tag);
            var jobId = await _service.StartJobAsync(new BeeldOpdracht
            {
                Prompt = prompt,
                Negatief = stijl.Negatief,
                BronBeeld = _huidigBestand,
                Masker = maskerPad,
                Sterkte = SterkteSlider.Value / 100,
                Seed = seed,
            });

            while (true)
            {
                var status = await _service.GetStatusAsync(jobId);
                StatusText.Text = status.Stap;
                Voortgang.Value = status.Voortgang;

                if (status.IsKlaar && status.Bestand is { } bestand)
                {
                    Toon(bestand, prompt);
                    WijzigBox.Text = string.Empty;
                    StatusText.Text = $"Bijgestuurd - seed {status.Seed}. Het origineel staat nog in de galerij. Prompt: {prompt}";
                    await VulGalerijAsync();
                    break;
                }

                if (status.IsMislukt)
                {
                    StatusText.Text = $"Mislukt: {status.Foutmelding}";
                    break;
                }

                await Task.Delay(PeilInterval);
            }
        }
        catch (Exception fout)
        {
            StatusText.Text = $"Kon de beeldservice niet bereiken: {fout.Message}";
        }
        finally
        {
            BijstuurButton.IsEnabled = true;
            GenereerButton.IsEnabled = true;
            Voortgang.Visibility = Visibility.Collapsed;

            if (maskerPad is not null)
            {
                try
                {
                    File.Delete(maskerPad);
                }
                catch (IOException)
                {
                    // Een tijdelijk bestand dat blijft staan is geen ramp.
                }
            }
        }
    }
}
