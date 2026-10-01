using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ImageFunn.App.Ai;
using Jabasoft.Base.AiBroker;
using Microsoft.Win32;

namespace ImageFunn.App.Regios.Inhoud;

/// <summary>
/// Interaction logic for BeeldNaarTekst.xaml - see that file for what it looks like.
///
/// Neemt een afbeelding aan (kiezen, slepen of plakken), bewaart hem zo
/// nodig als tijdelijk bestand en laat de <see cref="Beeldlezer"/> hem
/// lezen. Dit scherm is alleen de bediening; het model draait via de broker.
/// </summary>
public partial class BeeldNaarTekst : UserControl
{
    private static readonly string[] Extensies = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"];

    private readonly Beeldlezer _lezer;

    private string? _bestand;

    /// <summary>Gaat af als de gebruiker het resultaat als prompt voor een nieuw beeld wil gebruiken.</summary>
    public event EventHandler<string>? PromptDoorgeven;

    public BeeldNaarTekst(IAiBrokerClient broker)
    {
        InitializeComponent();
        _lezer = new Beeldlezer(broker);

        // Ctrl+V plakt een afbeelding van het klembord (bijvoorbeeld een screenshot).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage())
            {
                PlakVanKlembord();
                e.Handled = true;
            }
        };
        Loaded += (_, _) => Focusable = true;
    }

    private void DropZone_Klik(object sender, MouseButtonEventArgs e)
    {
        var dialoog = new OpenFileDialog
        {
            Filter = "Afbeeldingen|" + string.Join(";", Extensies.Select(x => "*" + x)) + "|Alle bestanden (*.*)|*.*",
        };

        if (dialoog.ShowDialog() == true)
        {
            Laad(dialoog.FileName);
        }
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } bestanden)
        {
            Laad(bestanden[0]);
        }
    }

    private void PlakVanKlembord()
    {
        var bron = Clipboard.GetImage();
        if (bron is null)
        {
            return;
        }

        var pad = Path.Combine(Path.GetTempPath(), $"imagefunn-{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bron));
        using (var stroom = File.Create(pad))
        {
            encoder.Save(stroom);
        }

        Laad(pad);
    }

    /// <summary>Zet een afbeelding in het scherm - ook gebruikt door "Laat dit beeld beschrijven" op het andere scherm.</summary>
    public void Laad(string pad)
    {
        if (!File.Exists(pad) || !Extensies.Contains(Path.GetExtension(pad), StringComparer.OrdinalIgnoreCase))
        {
            StatusText.Text = "Dat is geen afbeelding die ik kan lezen (png, jpg, bmp, gif, webp, tif).";
            return;
        }

        try
        {
            // OnLoad: het bestand wordt meteen ingelezen en niet vastgehouden.
            var beeld = new BitmapImage();
            beeld.BeginInit();
            beeld.CacheOption = BitmapCacheOption.OnLoad;
            beeld.UriSource = new Uri(pad);
            beeld.EndInit();
            beeld.Freeze();

            Voorbeeld.Source = beeld;
            DropTekst.Visibility = Visibility.Collapsed;
            _bestand = pad;
            StatusText.Text = $"{Path.GetFileName(pad)} - {beeld.PixelWidth} × {beeld.PixelHeight}";
        }
        catch (Exception fout)
        {
            StatusText.Text = $"De afbeelding kon niet geopend worden: {fout.Message}";
        }
    }

    private async void LeesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bestand is null)
        {
            StatusText.Text = "Kies eerst een afbeelding.";
            return;
        }

        var doel = DoelKeuze.SelectedIndex switch
        {
            1 => Leesdoel.TekstUitlezen,
            2 => Leesdoel.Prompt,
            _ => Leesdoel.Beschrijving,
        };

        LeesButton.IsEnabled = false;
        KopieerButton.IsEnabled = false;
        NaarBeeldButton.IsEnabled = false;
        ResultaatBox.Text = string.Empty;
        StatusText.Text = "Het model bekijkt de afbeelding…";

        try
        {
            var (tekst, fout, model) = await _lezer.LeesAsync(_bestand, doel, WensBox.Text, CancellationToken.None);

            if (tekst is null)
            {
                StatusText.Text = $"Mislukt: {fout}";
                return;
            }

            ResultaatBox.Text = tekst;
            StatusText.Text = $"Klaar - gelezen door {model}.";
            KopieerButton.IsEnabled = true;
            NaarBeeldButton.IsEnabled = true;
        }
        catch (Exception fout)
        {
            StatusText.Text = $"Kon het model niet bereiken: {fout.Message}";
        }
        finally
        {
            LeesButton.IsEnabled = true;
        }
    }

    private void KopieerButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(ResultaatBox.Text);
        StatusText.Text = "Gekopieerd naar het klembord.";
    }

    private void NaarBeeldButton_Click(object sender, RoutedEventArgs e) =>
        PromptDoorgeven?.Invoke(this, ResultaatBox.Text);
}
