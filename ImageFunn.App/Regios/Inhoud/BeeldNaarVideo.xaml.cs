using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ImageFunn.App.Ai;
using ImageFunn.App.ImageService;
using Jabasoft.Base.AiBroker;
using Microsoft.Win32;

namespace ImageFunn.App.Regios.Inhoud;

/// <summary>
/// Interaction logic for BeeldNaarVideo.xaml - see that file for what it looks like.
///
/// Stuurt de lokale beeldservice aan met een video-opdracht: bronbeeld +
/// bewegingsomschrijving erheen, voortgang peilen, en bij "klaar" de mp4
/// afspelen. De service (zie service.py en videomodellen.py) doet het echte werk.
/// </summary>
public partial class BeeldNaarVideo : UserControl
{
    private static readonly string[] Extensies = [".png", ".jpg", ".jpeg", ".bmp", ".webp"];

    /// <summary>Een video duurt minuten per stap-reeks; vaker peilen dan dit heeft geen zin.</summary>
    private static readonly TimeSpan PeilInterval = TimeSpan.FromSeconds(1.5);

    private readonly ImageServiceClient _service = new();

    /// <summary>Is de Clean-knop gebruikt? Dan gaat de tekst ongewijzigd naar het model, zonder vertaling en zonder stijltekst.</summary>
    private bool _clean;

    private readonly Promptverbeteraar _verbeteraar;

    private string? _bronBestand;

    private string? _huidigeVideo;

    private bool _galerijVullen;

    public BeeldNaarVideo(IAiBrokerClient broker)
    {
        InitializeComponent();
        _verbeteraar = new Promptverbeteraar(broker);
        Loaded += async (_, _) => await VulGalerijAsync();
    }

    /// <summary>Zet het bronbeeld - gebruikt door "Animeer dit beeld" op het scherm Tekst naar beeld.</summary>
    public void ZetBron(string pad) => LaadBron(pad);

    private void DropZone_Klik(object sender, MouseButtonEventArgs e)
    {
        var dialoog = new OpenFileDialog
        {
            Filter = "Afbeeldingen|" + string.Join(";", Extensies.Select(x => "*" + x)) + "|Alle bestanden (*.*)|*.*",
        };

        if (dialoog.ShowDialog() == true)
        {
            LaadBron(dialoog.FileName);
        }
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } bestanden)
        {
            LaadBron(bestanden[0]);
        }
    }

    private void LaadBron(string pad)
    {
        if (!File.Exists(pad) || !Extensies.Contains(Path.GetExtension(pad), StringComparer.OrdinalIgnoreCase))
        {
            StatusText.Text = "Dat is geen afbeelding die ik kan gebruiken (png, jpg, bmp, webp).";
            return;
        }

        var beeld = LaadBeeld(pad);
        if (beeld is null)
        {
            StatusText.Text = "De afbeelding kon niet geopend worden.";
            return;
        }

        Bron.Source = beeld;
        DropTekst.Visibility = Visibility.Collapsed;
        _bronBestand = pad;
        StatusText.Text = $"{Path.GetFileName(pad)} - {beeld.PixelWidth} × {beeld.PixelHeight}";
    }

    private static BitmapImage? LaadBeeld(string pad)
    {
        try
        {
            var beeld = new BitmapImage();
            beeld.BeginInit();
            beeld.CacheOption = BitmapCacheOption.OnLoad;
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

    private void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        _clean = true;
        MaakButton_Click(sender, e);
    }

    // Beelden per seconde per model (zie videomodellen.py): bepaalt hoeveel seconden een aantal beelden duurt.
    private static readonly Dictionary<string, int> BeeldenPerSeconde = new() { ["wan"] = 24, ["sana"] = 16, ["kandinsky"] = 24 };

    private static readonly (string Naam, string Tag)[] Duren = [("Kort", "49"), ("Normaal", "97"), ("Lang", "145")];

    private void ModelKeuze_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DuurKeuze is null || ModelKeuze.SelectedItem is not ComboBoxItem { Tag: string model })
        {
            return;
        }

        var fps = BeeldenPerSeconde.GetValueOrDefault(model, 24);
        var vorige = DuurKeuze.SelectedIndex < 0 ? 1 : DuurKeuze.SelectedIndex;
        DuurKeuze.Items.Clear();
        foreach (var (naam, tag) in Duren)
        {
            DuurKeuze.Items.Add(new ComboBoxItem { Content = $"{naam} (±{Math.Round(int.Parse(tag) / (double)fps)} sec)", Tag = tag });
        }

        DuurKeuze.SelectedIndex = vorige;
    }

    private async void MaakButton_Click(object sender, RoutedEventArgs e)
    {
        var clean = _clean;
        _clean = false;

        if (_bronBestand is null)
        {
            StatusText.Text = "Kies eerst een beeld om te laten bewegen.";
            return;
        }

        var frames = int.Parse((string)((ComboBoxItem)DuurKeuze.SelectedItem).Tag);
        var formaat = ((string)((ComboBoxItem)KwaliteitKeuze.SelectedItem).Tag).Split('x');

        MaakButton.IsEnabled = false;
        CleanButton.IsEnabled = false;
        Voortgang.Value = 0;
        Voortgang.Visibility = Visibility.Visible;
        StatusText.Text = "Taak starten…";

        try
        {
            var beweging = PromptBox.Text.Trim();

            // Het videomodel leest alleen Engels. Lukt het omschrijven niet, dan gaat de originele tekst mee.
            if (!clean && VerbeterBox.IsChecked == true && beweging.Length > 0)
            {
                StatusText.Text = "Beweging vertalen en verbeteren…";
                var (verbeterd, fout) = await _verbeteraar.VerbeterAsync(beweging, CancellationToken.None, Promptsoort.Beweging);
                if (verbeterd is not null)
                {
                    beweging = verbeterd;
                }
                else
                {
                    StatusText.Text = $"Verbeteren mislukt ({fout}) - de originele tekst wordt gebruikt.";
                }
            }

            var jobId = await _service.StartJobAsync(new BeeldOpdracht
            {
                Soort = "video",
                BronBeeld = _bronBestand,
                Prompt = beweging,
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
                    StatusText.Text = $"Klaar - seed {status.Seed}.";
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

    private async Task VulGalerijAsync()
    {
        _galerijVullen = true;
        try
        {
            var lijst = await _service.ListJobsAsync("video");
            Galerij.Items.Clear();
            foreach (var taak in lijst.Where(t => t.BronBeeld is not null && t.Bestand is not null && File.Exists(t.Bestand)))
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
