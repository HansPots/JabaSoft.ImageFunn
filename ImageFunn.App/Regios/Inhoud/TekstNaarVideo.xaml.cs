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
/// bronbeeld: de service maakt de video dan uit alleen de tekst (LTX-Video,
/// zie service.py). Voortgang peilen en bij "klaar" de mp4 afspelen.
/// </summary>
public partial class TekstNaarVideo : UserControl
{
    private static readonly TimeSpan PeilInterval = TimeSpan.FromSeconds(1.5);

    private readonly ImageServiceClient _service = new();

    private readonly Promptverbeteraar _verbeteraar;

    private string? _huidigeVideo;

    private bool _galerijVullen;

    public TekstNaarVideo(IAiBrokerClient broker)
    {
        InitializeComponent();
        _verbeteraar = new Promptverbeteraar(broker);
        Loaded += async (_, _) => await VulGalerijAsync();
    }

    private async void MaakButton_Click(object sender, RoutedEventArgs e)
    {
        var scene = PromptBox.Text.Trim();
        if (scene.Length == 0)
        {
            StatusText.Text = "Beschrijf eerst de scène.";
            return;
        }

        var frames = int.Parse((string)((ComboBoxItem)DuurKeuze.SelectedItem).Tag);
        var formaat = ((string)((ComboBoxItem)FormaatKeuze.SelectedItem).Tag).Split('x');

        MaakButton.IsEnabled = false;
        Voortgang.Value = 0;
        Voortgang.Visibility = Visibility.Visible;
        StatusText.Text = "Taak starten…";

        try
        {
            var prompt = scene;

            // Het videomodel leest alleen Engels. Lukt het omschrijven niet, dan gaat de originele tekst mee.
            if (VerbeterBox.IsChecked == true)
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
                Breedte = int.Parse(formaat[0]),
                Hoogte = int.Parse(formaat[1]),
            });

            while (true)
            {
                var status = await _service.GetStatusAsync(jobId);
                StatusText.Text = status.Stap;
                Voortgang.Value = status.Voortgang;

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
            Voortgang.Visibility = Visibility.Collapsed;
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
