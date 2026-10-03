using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Imaging;
using ImageFunn.App.Ai;
using ImageFunn.App.ImageService;
using Jabasoft.Base.AiBroker;
using Microsoft.Win32;

namespace ImageFunn.App.Regios.Inhoud;

/// <summary>
/// Interaction logic for TekstNaarBeeld.xaml - see that file for what it looks like.
///
/// Stuurt de lokale beeldservice aan: een taak starten, de voortgang
/// peilen, en bij "klaar" het beeld tonen. De service zelf (service.py, in
/// ImageFunn.Image) doet al het echte werk; dit scherm is alleen de
/// bediening ervan.
/// </summary>
public partial class TekstNaarBeeld : UserControl
{
    /// <summary>Hoe vaak de status opnieuw opgevraagd wordt terwijl een taak loopt.</summary>
    private static readonly TimeSpan PeilInterval = TimeSpan.FromSeconds(0.7);

    private readonly ImageServiceClient _service = new();

    /// <summary>Is de Clean-knop gebruikt? Dan gaat de tekst ongewijzigd naar het model, zonder vertaling en zonder stijltekst.</summary>
    private bool _clean;

    private string? _huidigBestand;

    /// <summary>De Engelse prompt waarmee het getoonde beeld gemaakt is, nodig om het bij te sturen.</summary>
    private string? _huidigePrompt;

    private bool _galerijVullen;

    /// <summary>Gaat af als de gebruiker het gemaakte beeld wil laten beschrijven; het argument is het bestandspad.</summary>
    public event EventHandler<string>? BeeldDoorgeven;

    /// <summary>Gaat af als de gebruiker het gemaakte beeld wil laten bewegen; het argument is het bestandspad.</summary>
    public event EventHandler<string>? AnimatieDoorgeven;

    private readonly Promptverbeteraar _verbeteraar;

    public TekstNaarBeeld(IAiBrokerClient broker)
    {
        InitializeComponent();
        _verbeteraar = new Promptverbeteraar(broker);
        Loaded += async (_, _) => await VulGalerijAsync();
    }

    /// <summary>Zet een prompt in het invoervak - gebruikt door "Maak er een beeld van" op het andere scherm.</summary>
    public void ZetPrompt(string prompt)
    {
        PromptBox.Text = prompt;
        StatusText.Text = "Prompt overgenomen - pas aan en druk op Genereer.";
    }

    private void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        _clean = true;
        GenereerButton_Click(sender, e);
    }

    private async void GenereerButton_Click(object sender, RoutedEventArgs e)
    {
        var clean = _clean;
        _clean = false;

        var prompt = PromptBox.Text.Trim();
        if (prompt.Length == 0)
        {
            StatusText.Text = "Typ eerst een omschrijving van het beeld.";
            return;
        }

        var formaat = ((string)((ComboBoxItem)FormaatKeuze.SelectedItem).Tag).Split('x');
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

        GenereerButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        Voortgang.Value = 0;
        Voortgang.Visibility = Visibility.Visible;
        StatusText.Text = "Taak starten…";

        try
        {
            // Het beeldmodel leest alleen Engels: eerst laten omschrijven door het chatmodel.
            // Lukt dat niet, dan gaat de originele tekst gewoon mee.
            var beeldPrompt = prompt;
            if (!clean && VerbeterBox.IsChecked == true)
            {
                StatusText.Text = "Prompt vertalen en verbeteren…";
                var (verbeterd, fout) = await _verbeteraar.VerbeterAsync(prompt, CancellationToken.None);
                if (verbeterd is not null)
                {
                    beeldPrompt = verbeterd;
                    PromptBox.ToolTip = $"Verstuurd naar het beeldmodel: {verbeterd}";
                }
                else
                {
                    StatusText.Text = $"Prompt verbeteren mislukt ({fout}) - de originele tekst wordt gebruikt.";
                }
            }

            // De gekozen stijl komt vóór de prompt, en hoort bij een eigen negatieve prompt (bijvoorbeeld "geen tekening" bij realistisch).
            // Clean: de stijl "geen" voegt niets toe, dus de tekst gaat precies zoals getypt naar het beeldmodel.
            var stijl = Beeldstijl.Van(clean ? "geen" : (string)((ComboBoxItem)StijlKeuze.SelectedItem).Tag);
            beeldPrompt = stijl.MetStijl(beeldPrompt);

            var jobId = await _service.StartJobAsync(new BeeldOpdracht
            {
                Negatief = stijl.Negatief,
                Prompt = beeldPrompt,
                Breedte = int.Parse(formaat[0]),
                Hoogte = int.Parse(formaat[1]),
                Seed = seed,
            });

            while (true)
            {
                var status = await _service.GetStatusAsync(jobId);
                StatusText.Text = status.Stap;
                Voortgang.Value = status.Voortgang;

                if (status.IsKlaar && status.Bestand is { } bestand)
                {
                    Toon(bestand, beeldPrompt);
                    StatusText.Text = $"Klaar - seed {status.Seed}. Prompt: {beeldPrompt}";
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
            // Meestal: de beeldservice is niet bereikbaar (nog aan het opstarten,
            // of de Python-omgeving staat niet klaar - zie ImageServiceLauncher).
            StatusText.Text = $"Kon de beeldservice niet bereiken: {fout.Message}";
        }
        finally
        {
            GenereerButton.IsEnabled = true;
            CleanButton.IsEnabled = true;
            Voortgang.Visibility = Visibility.Collapsed;
        }
    }

    private void Toon(string bestand, string? prompt = null)
    {
        var beeld = Laad(bestand, null);
        if (beeld is null)
        {
            return;
        }

        Resultaat.Source = beeld;
        _huidigePrompt = prompt;
        BijstuurKaart.Visibility = Visibility.Visible;
        Tekenvlak.Strokes.Clear();
        LegeTekst.Visibility = Visibility.Collapsed;
        _huidigBestand = bestand;
        OpslaanButton.IsEnabled = true;
        NaarTekstButton.IsEnabled = true;
        AnimeerButton.IsEnabled = true;
    }

    /// <summary>Laadt een bestand zonder het vast te houden; decodeBreedte houdt miniaturen klein in het geheugen.</summary>
    private static BitmapImage? Laad(string bestand, int? decodeBreedte)
    {
        try
        {
            var beeld = new BitmapImage();
            beeld.BeginInit();
            beeld.CacheOption = BitmapCacheOption.OnLoad;
            beeld.UriSource = new Uri(bestand);
            if (decodeBreedte is { } breedte)
            {
                beeld.DecodePixelWidth = breedte;
            }

            beeld.EndInit();
            beeld.Freeze();
            return beeld;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task VulGalerijAsync()
    {
        _galerijVullen = true;
        try
        {
            var lijst = await _service.ListJobsAsync();
            Galerij.Items.Clear();
            foreach (var taak in lijst.Where(t => t.Bestand is not null && File.Exists(t.Bestand)))
            {
                var miniatuur = Laad(taak.Bestand!, 200);
                if (miniatuur is null)
                {
                    continue;
                }

                Galerij.Items.Add(MaakGalerijItem(new GalerijBeeld(taak.JobId, taak.Bestand!, taak.Prompt), miniatuur));
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

    /// <summary>Wat er bij een beeld in de galerij hoort: de opdracht (om te verwijderen), het bestand en de omschrijving.</summary>
    private sealed record GalerijBeeld(string JobId, string Bestand, string Prompt);

    /// <summary>
    /// Een beeld in de galerij: de miniatuur met rechtsboven een prullenbak. De prullenbak verschijnt als je met de muis
    /// boven het beeld hangt, zodat de galerij rustig blijft.
    /// </summary>
    private Grid MaakGalerijItem(GalerijBeeld beeld, ImageSource miniatuur)
    {
        var prullenbak = new Button
        {
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            ToolTip = "Dit beeld verwijderen",
            Padding = new Thickness(0),
            Content = new System.Windows.Shapes.Path
            {
                // Het prullenbakpictogram (Material Design).
                Data = Geometry.Parse("M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z"),
                Stretch = Stretch.Uniform,
                Width = 14,
                Height = 14,
            },
        };
        ((System.Windows.Shapes.Path)prullenbak.Content).SetResourceReference(Shape.FillProperty, "Text01Brush");
        prullenbak.SetResourceReference(BackgroundProperty, "BackgroundBrush");
        prullenbak.Click += async (_, e) =>
        {
            e.Handled = true; // niet ook het beeld selecteren
            await VerwijderBeeldAsync(beeld);
        };

        var raster = new Grid { Margin = new Thickness(0, 0, 8, 0), Tag = beeld, ToolTip = beeld.Prompt };
        raster.Children.Add(new Image { Source = miniatuur, Height = 100 });
        raster.Children.Add(prullenbak);
        raster.MouseEnter += (_, _) => prullenbak.Visibility = Visibility.Visible;
        raster.MouseLeave += (_, _) => prullenbak.Visibility = Visibility.Collapsed;
        return raster;
    }

    private async Task VerwijderBeeldAsync(GalerijBeeld beeld)
    {
        var korteTekst = beeld.Prompt.Length > 80 ? beeld.Prompt[..80] + "…" : beeld.Prompt;
        if (MessageBox.Show($"Dit beeld definitief verwijderen?\n\n{korteTekst}\n\nDit kan niet ongedaan gemaakt worden.", "ImageFunn",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _service.DeleteJobAsync(beeld.JobId);
        }
        catch (Exception fout)
        {
            StatusText.Text = $"Verwijderen mislukt: {fout.Message}";
            return;
        }

        // Stond dit beeld net groot in beeld, dan ook daar weghalen.
        if (_huidigBestand == beeld.Bestand)
        {
            _huidigBestand = null;
            _huidigePrompt = null;
            BijstuurKaart.Visibility = Visibility.Collapsed;
            Resultaat.Source = null;
            LegeTekst.Visibility = Visibility.Visible;
            OpslaanButton.IsEnabled = false;
            NaarTekstButton.IsEnabled = false;
            AnimeerButton.IsEnabled = false;
        }

        await VulGalerijAsync();
        StatusText.Text = "Beeld verwijderd.";
    }

    private void Galerij_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_galerijVullen || Galerij.SelectedItem is not Grid { Tag: GalerijBeeld beeld })
        {
            return;
        }

        Toon(beeld.Bestand, beeld.Prompt);
        PromptBox.Text = beeld.Prompt;
    }

    /// <summary>De Delete-toets verwijdert het geselecteerde beeld uit de galerij.</summary>
    private async void Galerij_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Galerij.SelectedItem is Grid { Tag: GalerijBeeld beeld })
        {
            e.Handled = true;
            await VerwijderBeeldAsync(beeld);
        }
    }

    private void OpslaanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_huidigBestand is null)
        {
            return;
        }

        var dialoog = new SaveFileDialog { Filter = "PNG-afbeelding (*.png)|*.png", FileName = "imagefunn.png" };
        if (dialoog.ShowDialog() == true)
        {
            File.Copy(_huidigBestand, dialoog.FileName, overwrite: true);
            StatusText.Text = $"Opgeslagen: {dialoog.FileName}";
        }
    }

    private void AnimeerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_huidigBestand is not null)
        {
            AnimatieDoorgeven?.Invoke(this, _huidigBestand);
        }
    }

    private void NaarTekstButton_Click(object sender, RoutedEventArgs e)
    {
        if (_huidigBestand is not null)
        {
            BeeldDoorgeven?.Invoke(this, _huidigBestand);
        }
    }
}
