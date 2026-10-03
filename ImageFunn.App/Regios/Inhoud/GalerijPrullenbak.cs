using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Pad = System.IO.Path;
using Pictogram = System.Windows.Shapes.Path;

namespace ImageFunn.App.Regios.Inhoud;

/// <summary>
/// Gedeelde onderdelen voor de prullenbak in de galerijen (video's en films): een regel met tekst en een prullenbak
/// die verschijnt als je er met de muis boven hangt, de vraag om bevestiging, en een controle of een bestand in
/// een bepaalde map staat. Bij Tekst naar beeld zit dezelfde prullenbak, daar op een miniatuur.
/// </summary>
internal static class GalerijPrullenbak
{
    /// <summary>
    /// De inhoud van een regel in de galerij: de tekst met rechts een prullenbak. De prullenbak is onzichtbaar tot
    /// je erboven hangt, maar houdt zijn plek vast zodat de regels niet verspringen.
    /// </summary>
    public static Grid MaakRegel(string tekst, Func<Task> verwijder)
    {
        var knop = new Button
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Hidden,
            ToolTip = "Verwijderen",
            Content = new Pictogram
            {
                // Het prullenbakpictogram (Material Design), zelfde als bij Tekst naar beeld.
                Data = Geometry.Parse("M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z"),
                Stretch = Stretch.Uniform,
                Width = 12,
                Height = 12,
            },
        };
        ((Pictogram)knop.Content).SetResourceReference(Shape.FillProperty, "Text01Brush");
        knop.SetResourceReference(Control.BackgroundProperty, "BackgroundBrush");
        knop.Click += async (_, e) =>
        {
            e.Handled = true; // niet ook de regel selecteren
            await verwijder();
        };

        var raster = new Grid { Background = Brushes.Transparent };
        raster.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        raster.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        raster.Children.Add(new TextBlock { Text = tekst, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(knop, 1);
        raster.Children.Add(knop);
        raster.MouseEnter += (_, _) => knop.Visibility = Visibility.Visible;
        raster.MouseLeave += (_, _) => knop.Visibility = Visibility.Hidden;
        return raster;
    }

    /// <summary>Vraagt of het echt weg mag. Standaard staat het antwoord op Nee.</summary>
    public static bool Bevestig(string wat, string omschrijving, string extra = "")
    {
        var kort = omschrijving.Length > 80 ? omschrijving[..80] + "…" : omschrijving;
        var tekst = $"Deze {wat} definitief verwijderen?\n\n{kort}\n\n{(extra.Length > 0 ? extra + "\n\n" : string.Empty)}Dit kan niet ongedaan gemaakt worden.";
        return MessageBox.Show(tekst, "ImageFunn", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    /// <summary>Staat dit bestand in (of onder) deze map? Wordt gebruikt om te zien of wat er nu speelt bij het te verwijderen item hoort.</summary>
    public static bool ZitIn(string? bestand, string? map)
    {
        if (string.IsNullOrEmpty(bestand) || string.IsNullOrEmpty(map))
        {
            return false;
        }

        var basis = Pad.GetFullPath(map).TrimEnd(Pad.DirectorySeparatorChar) + Pad.DirectorySeparatorChar;
        return Pad.GetFullPath(bestand).StartsWith(basis, StringComparison.OrdinalIgnoreCase);
    }
}
