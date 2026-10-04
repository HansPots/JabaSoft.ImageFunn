using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace ImageFunn.App.Regios.Inhoud;

/// <summary>
/// Het voorproefje van een video die nog gemaakt wordt: de service zet om de paar stappen het middelste beeld klaar
/// zoals het er op dat moment uitziet. Dit laadt het bestand zonder het vast te houden (de service ruimt het zo weer op).
/// </summary>
internal static class VoorproefWeergave
{
    public static void Toon(Image beeld, string? pad)
    {
        if (string.IsNullOrEmpty(pad) || !File.Exists(pad))
        {
            return;
        }

        try
        {
            var bron = new BitmapImage();
            bron.BeginInit();
            bron.CacheOption = BitmapCacheOption.OnLoad;
            bron.UriSource = new Uri(pad);
            bron.EndInit();
            bron.Freeze();
            beeld.Source = bron;
            beeld.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            // Net weggehaald of nog niet helemaal geschreven: de volgende keer lukt het wel.
        }
    }

    public static void Verberg(Image beeld)
    {
        beeld.Visibility = Visibility.Collapsed;
        beeld.Source = null;
    }
}
