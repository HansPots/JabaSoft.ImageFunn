using System.Windows;
using System.Windows.Controls;
using Jabasoft.Base.Layout;

namespace ImageFunn.App.Layout;

/// <summary>
/// Onthoudt en herstelt de kolombreedtes die achter een splitter zitten.
/// De opslag zelf zit in Jabasoft.Base (LayoutStore); hier staat alleen
/// het vertaalslagje tussen een WPF-Grid en een rij getallen.
///
/// Alleen STER-kolommen doen mee. De vaste kolommen - de elleboogjes van
/// 48 in de header - horen niet bij wat de gebruiker versleept en zouden
/// bij terugzetten alleen maar in de weg zitten.
/// </summary>
public static class SplitterLayout
{
    /// <summary>Eén opslag voor de hele applicatie; het bestand wordt bij de eerste aanroep gelezen.</summary>
    public static LayoutStore Store { get; } = new("ImageFunn.App");

    /// <summary>Zet de bewaarde breedtes terug. Is er niets bewaard, of klopt het aantal niet meer (een blok erbij of eraf), dan blijft de indeling zoals hij in de XAML staat.</summary>
    public static void Restore(Grid grid, string key)
    {
        ArgumentNullException.ThrowIfNull(grid);

        var saved = Store.Load(key);
        if (saved is null)
        {
            return;
        }

        var starColumns = StarColumns(grid);
        if (starColumns.Count != saved.Length)
        {
            return;
        }

        for (var i = 0; i < starColumns.Count; i++)
        {
            if (saved[i] > 0)
            {
                starColumns[i].Width = new GridLength(saved[i], GridUnitType.Star);
            }
        }
    }

    /// <summary>Bewaart de breedtes zoals ze nu staan.</summary>
    public static void Remember(Grid grid, string key)
    {
        ArgumentNullException.ThrowIfNull(grid);

        Store.Save(key, [.. StarColumns(grid).Select(column => column.Width.Value)]);
    }

    /// <summary>
    /// Zet alles terug naar de standaard: alle sterkolommen even breed, en
    /// het bewaarde gesleep vergeten. Dit is wat de verversknop in de
    /// actierail doet.
    /// </summary>
    public static void Reset(Grid grid, string key)
    {
        ArgumentNullException.ThrowIfNull(grid);

        Store.Forget(key);

        foreach (var column in StarColumns(grid))
        {
            column.Width = new GridLength(1, GridUnitType.Star);
        }
    }

    private static List<ColumnDefinition> StarColumns(Grid grid) =>
        [.. grid.ColumnDefinitions.Where(column => column.Width.IsStar)];
}
