using System.IO;
using System.Windows;
using System.Windows.Controls;
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

    private string? _huidigBestand;

    private bool _galerijVullen;

    /// <summary>Gaat af als de gebruiker het gemaakte beeld wil laten beschrijven; het argument is het bestandspad.</summary>
    public event EventHandler<string>? BeeldDoorgeven;

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

    private async void GenereerButton_Click(object sender, RoutedEventArgs e)
    {
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
        Voortgang.Value = 0;
        Voortgang.Visibility = Visibility.Visible;
        StatusText.Text = "Taak starten…";

        try
        {
            // Het beeldmodel leest alleen Engels: eerst laten omschrijven door het chatmodel.
            // Lukt dat niet, dan gaat de originele tekst gewoon mee.
            var beeldPrompt = prompt;
            if (VerbeterBox.IsChecked == true)
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

            var jobId = await _service.StartJobAsync(new BeeldOpdracht
            {
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
                    Toon(bestand);
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
            Voortgang.Visibility = Visibility.Collapsed;
        }
    }

    private void Toon(string bestand)
    {
        var beeld = Laad(bestand, null);
        if (beeld is null)
        {
            return;
        }

        Resultaat.Source = beeld;
        LegeTekst.Visibility = Visibility.Collapsed;
        _huidigBestand = bestand;
        OpslaanButton.IsEnabled = true;
        NaarTekstButton.IsEnabled = true;
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

                Galerij.Items.Add(new Image
                {
                    Source = miniatuur,
                    Height = 100,
                    Margin = new Thickness(0, 0, 8, 0),
                    Tag = taak.Bestand,
                    ToolTip = taak.Prompt,
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

    private void Galerij_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_galerijVullen || Galerij.SelectedItem is not Image { Tag: string bestand })
        {
            return;
        }

        Toon(bestand);

        if (Galerij.SelectedItem is Image { ToolTip: string prompt })
        {
            PromptBox.Text = prompt;
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

    private void NaarTekstButton_Click(object sender, RoutedEventArgs e)
    {
        if (_huidigBestand is not null)
        {
            BeeldDoorgeven?.Invoke(this, _huidigBestand);
        }
    }
}
