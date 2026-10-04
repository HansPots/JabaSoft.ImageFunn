using System.IO;
using System.Text.Json;

namespace ImageFunn.App.Regios.Inhoud;

/// <summary>Eén aanwijzing bij een beeld, bijvoorbeeld "de persoon iets groter". Aangevinkt telt hij mee bij de volgende ronde.</summary>
public sealed class Aanwijzing
{
    public string Tekst { get; set; } = string.Empty;

    public bool Aan { get; set; } = true;

    /// <summary>Was dit een aanwijzing voor een aangetekend gebied? Die wordt alleen in de prompt onthouden, het gebied zelf niet.</summary>
    public bool Gebied { get; set; }
}

/// <summary>Eén stand van het beeld: het bestand, de prompt die het maakte en de aanwijzingen zoals ze er op dat moment bij hoorden.</summary>
public sealed class BeeldVersie
{
    public string Bestand { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    public List<Aanwijzing> Aanwijzingen { get; set; } = [];
}

/// <summary>Een beeld met alle versies die ervan gemaakt zijn door bij te sturen, en de oorspronkelijke prompt waar alle aanwijzingen op voortbouwen.</summary>
public sealed class BeeldSessie
{
    public string BasisPrompt { get; set; } = string.Empty;

    public List<BeeldVersie> Versies { get; set; } = [];
}

/// <summary>
/// De bewaarde sessies, in één bestand in de gebruikersmap, zodat aanwijzingen en versies na sluiten van de app
/// nog klaarstaan. Versies waarvan het bestand weg is (bijvoorbeeld met de prullenbak verwijderd) vallen vanzelf af.
/// </summary>
public static class BeeldSessies
{
    private static readonly JsonSerializerOptions Opties = new() { WriteIndented = true };

    private static List<BeeldSessie>? _alle;

    private static string Pad => Path.Combine(Map, "beeldsessies.json");

    public static string Map { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JabaSoft", "ImageFunn");

    private static List<BeeldSessie> Alle()
    {
        if (_alle is not null)
        {
            return _alle;
        }

        try
        {
            _alle = File.Exists(Pad) ? JsonSerializer.Deserialize<List<BeeldSessie>>(File.ReadAllText(Pad)) ?? [] : [];
        }
        catch (Exception)
        {
            _alle = [];
        }

        foreach (var sessie in _alle)
        {
            sessie.Versies.RemoveAll(v => !File.Exists(v.Bestand));
        }

        _alle.RemoveAll(s => s.Versies.Count == 0);
        return _alle;
    }

    /// <summary>De sessie waar dit beeldbestand bij hoort, of null.</summary>
    public static BeeldSessie? Zoek(string bestand) =>
        Alle().FirstOrDefault(s => s.Versies.Any(v => string.Equals(v.Bestand, bestand, StringComparison.OrdinalIgnoreCase)));

    public static BeeldSessie Nieuw(string bestand, string prompt)
    {
        var sessie = new BeeldSessie
        {
            BasisPrompt = prompt,
            Versies = [new BeeldVersie { Bestand = bestand, Prompt = prompt }],
        };
        Alle().Add(sessie);
        Bewaar();
        return sessie;
    }

    public static void Bewaar()
    {
        try
        {
            Directory.CreateDirectory(Map);
            File.WriteAllText(Pad, JsonSerializer.Serialize(Alle(), Opties));
        }
        catch (Exception)
        {
            // Niet kunnen bewaren is jammer, maar mag het werken niet in de weg zitten.
        }
    }
}
