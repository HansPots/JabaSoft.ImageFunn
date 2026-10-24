using System.IO;
using System.Windows;
using System.Windows.Controls;
using ImageFunn.App.Ai;
using ImageFunn.App.ImageService;
using Jabasoft.Base.AiBroker;
using Microsoft.Win32;

namespace ImageFunn.App.Regios.Inhoud;

/// <summary>
/// Interaction logic for TekstNaarVideo.xaml - see that file for what it looks like.
///
/// Stuurt de lokale beeldservice aan met een video-opdracht ZONDER
/// bronbeeld: de service maakt de video dan uit alleen de tekst (het gekozen videomodel,
/// zie service.py). Voortgang peilen en bij "klaar" de mp4 afspelen.
/// </summary>
public partial class TekstNaarVideo : UserControl
{
    private static readonly TimeSpan PeilInterval = TimeSpan.FromSeconds(1.5);

    private readonly ImageServiceClient _service = new();

    /// <summary>Is de Clean-knop gebruikt? Dan gaat de tekst ongewijzigd naar het model, zonder vertaling en zonder stijltekst.</summary>
    private bool _clean;

    private readonly Promptverbeteraar _verbeteraar;

    private string? _huidigeVideo;

    private bool _galerijVullen;

    public TekstNaarVideo(IAiBrokerClient broker)
    {
        InitializeComponent();

        // "Automatisch vertalen en verbeteren": standaard uit, en de keuze wordt
        // onthouden voor de volgende keer (zie Layout.Preferences.Verbeteren).
        VerbeterBox.IsChecked = Layout.Preferences.Verbeteren("tekstnaarvideo");
        VerbeterBox.Checked += (_, _) => Layout.Preferences.BewaarVerbeteren("tekstnaarvideo", true);
        VerbeterBox.Unchecked += (_, _) => Layout.Preferences.BewaarVerbeteren("tekstnaarvideo", false);
        _verbeteraar = new Promptverbeteraar(broker);
        Loaded += async (_, _) => await VulGalerijAsync();
    }

    // Elk videomodel heeft zijn eigen goede formaten (zie videomodellen.py voor de afrondingsmaat).
    private static readonly Dictionary<string, (string Naam, string Tag)[]> Formaten = new()
    {
        ["wan"] = [("Liggend 704 × 480", "704x480"), ("Staand 480 × 704", "480x704"), ("Vierkant 512 × 512", "512x512"), ("Klein vierkant 384 × 384 (sneller)", "384x384")],
        ["sana"] = [("Liggend 832 × 480", "832x480"), ("Staand 480 × 832", "480x832"), ("Vierkant 512 × 512", "512x512"), ("Klein vierkant 384 × 384 (sneller)", "384x384")],
        ["kandinsky"] = [("Liggend 768 × 512", "768x512"), ("Staand 512 × 768", "512x768"), ("Vierkant 512 × 512", "512x512"), ("Klein vierkant 384 × 384 (sneller)", "384x384")],
    };

    // Beelden per seconde per model (zie videomodellen.py): bepaalt hoeveel seconden een aantal beelden duurt.
    private static readonly Dictionary<string, int> BeeldenPerSeconde = new() { ["wan"] = 24, ["sana"] = 16, ["kandinsky"] = 24 };

    private static readonly (string Naam, string Tag)[] Duren = [("Kort", "49"), ("Normaal", "97"), ("Lang", "145")];

    private void VulDuurKeuze(string model)
    {
        var fps = BeeldenPerSeconde.GetValueOrDefault(model, 24);
        var vorige = DuurKeuze.SelectedIndex < 0 ? 1 : DuurKeuze.SelectedIndex;
        DuurKeuze.Items.Clear();
        foreach (var (naam, tag) in Duren)
        {
            DuurKeuze.Items.Add(new ComboBoxItem { Content = $"{naam} (±{Math.Round(int.Parse(tag) / (double)fps)} sec)", Tag = tag });
        }

        DuurKeuze.SelectedIndex = vorige;
    }

    private void ModelKeuze_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DuurKeuze is not null && ModelKeuze.SelectedItem is ComboBoxItem { Tag: string gekozen })
        {
            VulDuurKeuze(gekozen);
        }

        if (FormaatKeuze is null || ModelKeuze.SelectedItem is not ComboBoxItem { Tag: string model } || !Formaten.TryGetValue(model, out var lijst))
        {
            return;
        }

        var vorige = (FormaatKeuze.SelectedItem as ComboBoxItem)?.Tag as string;
        FormaatKeuze.Items.Clear();
        foreach (var (naam, tag) in lijst)
        {
            FormaatKeuze.Items.Add(new ComboBoxItem { Content = naam, Tag = tag });
        }

        // Dezelfde vorm (liggend/staand/vierkant) als eerder gekozen, anders de eerste.
        FormaatKeuze.SelectedIndex = vorige is null ? 0 : VormIndex(vorige);
    }

    private static int VormIndex(string tag)
    {
        var d = tag.Split('x');
        var (b, h) = (int.Parse(d[0]), int.Parse(d[1]));
        return b > h ? 0 : b < h ? 1 : b >= 512 ? 2 : 3;
    }

    private void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        _clean = true;
        MaakButton_Click(sender, e);
    }

    private async void MaakButton_Click(object sender, RoutedEventArgs e)
    {
        var clean = _clean;
        _clean = false;

        var scene = PromptBox.Text.Trim();
        if (scene.Length == 0)
        {
            StatusText.Text = "Beschrijf eerst de scène.";
            return;
        }

        var frames = int.Parse((string)((ComboBoxItem)DuurKeuze.SelectedItem).Tag);
        var formaat = ((string)((ComboBoxItem)FormaatKeuze.SelectedItem).Tag).Split('x');

        MaakButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        Voortgang.Value = 0;
        Voortgang.Visibility = Visibility.Visible;
        StatusText.Text = "Taak starten…";

        try
        {
            var prompt = scene;

            // Het videomodel leest alleen Engels. Lukt het omschrijven niet, dan gaat de originele tekst mee.
            if (!clean && VerbeterBox.IsChecked == true)
            {
                StatusText.Text = "Scène vertalen en verbeteren…";
                var (verbeterd, fout) = await _verbeteraar.VerbeterAsync(scene, CancellationToken.None, Promptsoort.Scene);
                if (verbeterd is not null)
                {
                    prompt = verbeterd;
                }
                else
                {
                    StatusText.Text = $"Verbeteren mislukt ({fout}) - de originele tekst wordt gebruikt.";
                }
            }

            var jobId = await _service.StartJobAsync(new BeeldOpdracht
            {
                Soort = "video",
                BronBeeld = null,
                Prompt = prompt,
                Frames = frames,
                Videomodel = (string)((ComboBoxItem)ModelKeuze.SelectedItem).Tag,
                Breedte = int.Parse(formaat[0]),
                Hoogte = int.Parse(formaat[1]),
            });

            var voorproefNr = 0;
            while (true)
            {
                var status = await _service.GetStatusAsync(jobId);
                StatusText.Text = status.Stap;
                Voortgang.Value = status.Voortgang;
                if (status.VoorproefNr != voorproefNr)
                {
                    voorproefNr = status.VoorproefNr;
                    LegeTekst.Visibility = Visibility.Collapsed;
                    VoorproefWeergave.Toon(VoorproefBeeld, status.Voorproef);
                }

                if (status.IsKlaar && status.Bestand is { } bestand)
                {
                    Speel(bestand);
                    StatusText.Text = $"Klaar - seed {status.Seed}. Prompt: {prompt}";
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
            MaakButton.IsEnabled = true;
            CleanButton.IsEnabled = true;
            Voortgang.Visibility = Visibility.Collapsed;
            VoorproefWeergave.Verberg(VoorproefBeeld);
        }
    }

    private void Speel(string bestand)
    {
        _huidigeVideo = bestand;
        LegeTekst.Visibility = Visibility.Collapsed;
        OpslaanButton.IsEnabled = true;
        Speler.Source = new Uri(bestand);
        Speler.Play();
    }

    /// <summary>De video herhaalt zichzelf, zoals je dat van een korte animatie verwacht.</summary>
    private void Speler_MediaEnded(object sender, RoutedEventArgs e)
    {
        Speler.Position = TimeSpan.Zero;
        Speler.Play();
    }

    private void Speler_MediaFailed(object sender, ExceptionRoutedEventArgs e) =>
        StatusText.Text = $"De video kon niet afgespeeld worden: {e.ErrorException.Message}";

    /// <summary>Alleen video's zonder bronbeeld: de geanimeerde beelden staan op het scherm Beeld naar video.</summary>
    private async Task VulGalerijAsync()
    {
        _galerijVullen = true;
        try
        {
            var lijst = await _service.ListJobsAsync("video");
            Galerij.Items.Clear();
            foreach (var taak in lijst.Where(t => t.BronBeeld is null && t.Bestand is not null && File.Exists(t.Bestand)))
            {
                var tekst = taak.Prompt.Length > 40 ? taak.Prompt[..40] + "…" : taak.Prompt;
                Galerij.Items.Add(new ListBoxItem
                {
                    Content = GalerijPrullenbak.MaakRegel($"{taak.GemaaktOp.LocalDateTime:dd-MM HH:mm}  {tekst}", () => VerwijderVideoAsync(taak)),
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

    private void Galerij_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_galerijVullen && Galerij.SelectedItem is ListBoxItem { Tag: BeeldJobStatus { Bestand: { } bestand } })
        {
            Speel(bestand);
        }
    }

    /// <summary>Verwijdert een eerder gemaakte video met zijn map. De speler houdt het bestand open, dus die laten we eerst los.</summary>
    private async Task VerwijderVideoAsync(BeeldJobStatus taak)
    {
        if (!GalerijPrullenbak.Bevestig("video", taak.Prompt))
        {
            return;
        }

        if (GalerijPrullenbak.ZitIn(_huidigeVideo, Path.GetDirectoryName(taak.Bestand)))
        {
            Speler.Stop();
            Speler.Close();
            Speler.Source = null;
            _huidigeVideo = null;
            LegeTekst.Visibility = Visibility.Visible;
            OpslaanButton.IsEnabled = false;
            await Task.Delay(300);
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

        await VulGalerijAsync();
        StatusText.Text = "Video verwijderd.";
    }

    private void OpslaanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_huidigeVideo is null)
        {
            return;
        }

        var dialoog = new SaveFileDialog { Filter = "MP4-video (*.mp4)|*.mp4", FileName = "imagefunn.mp4" };
        if (dialoog.ShowDialog() == true)
        {
            File.Copy(_huidigeVideo, dialoog.FileName, overwrite: true);
            StatusText.Text = $"Opgeslagen: {dialoog.FileName}";
        }
    }
}
