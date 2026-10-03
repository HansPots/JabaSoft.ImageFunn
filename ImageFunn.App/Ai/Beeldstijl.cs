namespace ImageFunn.App.Ai;

/// <summary>
/// Een keuze voor hoe de beelden eruitzien: een stuk Engelse stijltekst dat vóór de prompt komt, en
/// wat het model juist NIET moet maken. Het beeldmodel kiest zonder aanwijzing graag voor een getekende
/// look; "realistisch" vraagt daarom ook expliciet om geen tekening, schilderij of 3D-render.
/// </summary>
public sealed record Beeldstijl(string Tag, string Naam, string Prefix, string Negatief)
{
    private const string NietGetekend = "cartoon, illustration, drawing, painting, anime, cgi, 3d render, plastic, oversaturated, deformed, blurry, low quality, text, watermark";

    public static readonly IReadOnlyList<Beeldstijl> Alle =
    [
        new("realistisch", "Realistisch (foto)",
            "photorealistic, professional photograph, shot on a 35mm lens, natural lighting, realistic textures and proportions, sharp focus, high detail",
            NietGetekend),
        new("filmisch", "Filmisch",
            "cinematic film still, shot on 35mm anamorphic lens, dramatic realistic lighting, shallow depth of field, subtle film grain, high detail",
            NietGetekend),
        new("illustratie", "Illustratie",
            "detailed digital illustration, painterly, soft colours",
            "photo, blurry, low quality, text, watermark"),
        new("tekenfilm", "Tekenfilm / animatie",
            "stylized 3D animated movie look, soft studio lighting, appealing character design, high detail",
            "photo, realistic, blurry, low quality, text, watermark"),
        new("geen", "Geen (alleen mijn tekst)", string.Empty, "blurry, low quality, text, watermark"),
    ];

    public static Beeldstijl Van(string tag) => Alle.FirstOrDefault(s => s.Tag == tag) ?? Alle[0];

    /// <summary>Zet de stijltekst vóór de prompt, tenzij die er al in staat.</summary>
    public string MetStijl(string tekst)
    {
        if (Prefix.Length == 0 || tekst.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return tekst;
        }

        return string.IsNullOrWhiteSpace(tekst) ? Prefix : $"{Prefix}, {tekst}";
    }

    /// <summary>Vervangt een bekende stijltekst aan het begin van <paramref name="tekst"/> door die van deze stijl.</summary>
    public string Wissel(string tekst)
    {
        var oud = Alle.Where(s => s.Prefix.Length > 0 && tekst.StartsWith(s.Prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Prefix.Length)
            .FirstOrDefault();
        if (oud is null)
        {
            return tekst;
        }

        var rest = tekst[oud.Prefix.Length..].TrimStart(',', ' ');
        return Prefix.Length == 0 ? rest : (rest.Length == 0 ? Prefix : $"{Prefix}, {rest}");
    }
}
