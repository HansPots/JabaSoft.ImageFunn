using System.Text.Json.Serialization;

namespace ImageFunn.App.ImageService;

/// <summary>Wat er gegenereerd moet worden - spiegelt service.py's Opdracht.</summary>
public sealed class BeeldOpdracht
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("negatief")]
    public string Negatief { get; set; } = string.Empty;

    [JsonPropertyName("breedte")]
    public int Breedte { get; set; } = 1024;

    [JsonPropertyName("hoogte")]
    public int Hoogte { get; set; } = 1024;

    /// <summary>Null = het standaardaantal van het model.</summary>
    [JsonPropertyName("stappen")]
    public int? Stappen { get; set; }

    /// <summary>Null = willekeurig; dezelfde seed met dezelfde prompt geeft hetzelfde beeld.</summary>
    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    /// <summary>"beeld" (tekst naar beeld) of "video" (het beeld in <see cref="BronBeeld"/> laten bewegen).</summary>
    [JsonPropertyName("soort")]
    public string Soort { get; set; } = "beeld";

    /// <summary>Pad naar het beeld dat geanimeerd moet worden - alleen bij soort "video".</summary>
    [JsonPropertyName("bron_beeld")]
    public string? BronBeeld { get; set; }

    /// <summary>Aantal beelden van de video (24 per seconde) - alleen bij soort "video".</summary>
    [JsonPropertyName("frames")]
    public int Frames { get; set; } = 97;

    /// <summary>"ltx" (snel) of "wan" (Wan 2.2: echtere beweging, trager) - alleen bij soort "video".</summary>
    [JsonPropertyName("videomodel")]
    public string Videomodel { get; set; } = "ltx";

    /// <summary>De scènes van een film - alleen bij soort "film".</summary>
    [JsonPropertyName("scenes")]
    public List<FilmScene> Scenes { get; set; } = [];

    /// <summary>"doorlopend" (elke scène begint op het laatste beeld van de vorige) of "sleutelbeelden" (elke scène een eigen beeld).</summary>
    [JsonPropertyName("modus")]
    public string Modus { get; set; } = "sleutelbeelden";

    /// <summary>"hard" of "zacht" (korte overvloeiing) tussen de scènes.</summary>
    [JsonPropertyName("overgang")]
    public string Overgang { get; set; } = "zacht";

    /// <summary>Stijl en personagebeschrijving die vóór elk sleutelbeeld komt, zodat de film samenhangt.</summary>
    [JsonPropertyName("stijl")]
    public string Stijl { get; set; } = string.Empty;

    /// <summary>Pad naar een eigen voorbeeldbeeld waar alle sleutelbeelden op moeten lijken (zelfde personages).</summary>
    [JsonPropertyName("referentie")]
    public string? Referentie { get; set; }

    /// <summary>Of anders: het sleutelbeeld van scène 1 als voorbeeld voor de rest.</summary>
    [JsonPropertyName("eerste_als_referentie")]
    public bool EersteAlsReferentie { get; set; }

    /// <summary>0..1: hoe sterk een nieuw beeld op het voorbeeld moet lijken.</summary>
    [JsonPropertyName("referentie_sterkte")]
    public double ReferentieSterkte { get; set; } = 0.45;

    /// <summary>De spelers en plekken met hun referentiebeeld - bij soort "cast" om die beelden te maken.</summary>
    [JsonPropertyName("cast")]
    public List<CastItem> Cast { get; set; } = [];

    /// <summary>Hoe sterk een speler uit de cast op zijn referentiebeeld moet lijken (0..1).</summary>
    [JsonPropertyName("speler_sterkte")]
    public double SpelerSterkte { get; set; } = 0.5;

    /// <summary>Hoe sterk een plek op zijn referentiebeeld moet lijken (0..1).</summary>
    [JsonPropertyName("plek_sterkte")]
    public double PlekSterkte { get; set; } = 0.4;

    /// <summary>Hoe sterk een belangrijk voorwerp op zijn referentiebeeld moet lijken (0..1).</summary>
    [JsonPropertyName("voorwerp_sterkte")]
    public double VoorwerpSterkte { get; set; } = 0.35;

    /// <summary>Bij bijsturen (soort "beeld" met <see cref="BronBeeld"/>): een zwart-wit masker, waarbij wit het gebied is dat mag veranderen. Leeg = het hele beeld.</summary>
    [JsonPropertyName("masker")]
    public string? Masker { get; set; }

    /// <summary>Bij bijsturen: hoeveel er mag veranderen, 0,05 (bijna niets) tot 1 (alles).</summary>
    [JsonPropertyName("sterkte")]
    public double Sterkte { get; set; } = 0.5;
}

/// <summary>Een speler of plek in de cast van een film - spiegelt service.py's CastItem.</summary>
public sealed class CastItem
{
    [JsonPropertyName("naam")]
    public string Naam { get; set; } = string.Empty;

    /// <summary>"speler", "plek" of "voorwerp".</summary>
    [JsonPropertyName("soort")]
    public string Soort { get; set; } = "speler";

    /// <summary>Omschrijving in gewone taal - voor jou. De service ziet dit veld niet.</summary>
    [JsonPropertyName("beschrijving")]
    public string Beschrijving { get; set; } = string.Empty;

    /// <summary>Engelse omschrijving om het referentiebeeld van te maken.</summary>
    [JsonPropertyName("beeld")]
    public string Beeld { get; set; } = string.Empty;

    /// <summary>Het gemaakte referentiebeeld (png).</summary>
    [JsonPropertyName("referentie")]
    public string? Referentie { get; set; }

    [JsonPropertyName("opnieuw")]
    public bool Opnieuw { get; set; }
}

/// <summary>Eén scène van een film - spiegelt service.py's Scene.</summary>
public sealed class FilmScene
{
    [JsonPropertyName("titel")]
    public string Titel { get; set; } = string.Empty;

    /// <summary>Wat er gebeurt, in gewone taal (Nederlands) - voor jou.</summary>
    [JsonPropertyName("gebeurt")]
    public string Gebeurt { get; set; } = string.Empty;

    /// <summary>Engelse omschrijving van het eerste beeld - voor het beeldmodel.</summary>
    [JsonPropertyName("beeld")]
    public string Beeld { get; set; } = string.Empty;

    /// <summary>Engelse omschrijving van de beweging en de camera - voor het videomodel.</summary>
    [JsonPropertyName("beweging")]
    public string Beweging { get; set; } = string.Empty;

    [JsonPropertyName("seconden")]
    public int Seconden { get; set; } = 4;

    /// <summary>De al gemaakte scène (mp4); is die er, dan wordt hij hergebruikt in plaats van opnieuw gemaakt.</summary>
    [JsonPropertyName("bestand")]
    public string? Bestand { get; set; }

    /// <summary>"wachtend" | "bezig" | "klaar" | "mislukt".</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "wachtend";

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    /// <summary>0..1: hoever het animeren van deze scène is.</summary>
    [JsonPropertyName("voortgang")]
    public double Voortgang { get; set; }

    /// <summary>Het beeld waarmee deze scène begint (sleutelbeeld of laatste beeld van de vorige scène).</summary>
    [JsonPropertyName("sleutel")]
    public string? Sleutel { get; set; }

    /// <summary>Alleen bij het maken van sleutelbeelden: dit beeld opnieuw maken.</summary>
    [JsonPropertyName("opnieuw")]
    public bool Opnieuw { get; set; }

    /// <summary>Namen van de spelers uit de cast die in deze scène voorkomen.</summary>
    [JsonPropertyName("spelers")]
    public List<string> Spelers { get; set; } = [];

    /// <summary>Naam van de plek uit de cast waar deze scène speelt.</summary>
    [JsonPropertyName("plek")]
    public string? Plek { get; set; }

    /// <summary>Namen van de belangrijke voorwerpen uit de cast in deze scène. Alleen het eerste krijgt een eigen voorbeeldbeeld.</summary>
    [JsonPropertyName("voorwerpen")]
    public List<string> Voorwerpen { get; set; } = [];

    /// <summary>De referentiebeelden van de spelers - de app zet deze in vlak voor het versturen.</summary>
    [JsonPropertyName("speler_refs")]
    public List<string> SpelerRefs { get; set; } = [];

    /// <summary>Het referentiebeeld van de plek - de app zet dit in vlak voor het versturen.</summary>
    [JsonPropertyName("plek_ref")]
    public string? PlekRef { get; set; }

    /// <summary>Het referentiebeeld van het voorwerp - de app zet dit in vlak voor het versturen.</summary>
    [JsonPropertyName("voorwerp_ref")]
    public string? VoorwerpRef { get; set; }
}

/// <summary>De stand van één taak bij de beeldservice - spiegelt service.py's Taak.als_dict().</summary>
public sealed class BeeldJobStatus
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("gemaakt_op")]
    public DateTimeOffset GemaaktOp { get; set; }

    /// <summary>"wachtend" | "bezig" | "klaar" | "mislukt" - zie service.py's Status.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("stap")]
    public string Stap { get; set; } = string.Empty;

    /// <summary>0..1, alleen tijdens het genereren zelf.</summary>
    [JsonPropertyName("voortgang")]
    public double Voortgang { get; set; }

    /// <summary>Pad naar de PNG zodra de taak klaar is.</summary>
    [JsonPropertyName("bestand")]
    public string? Bestand { get; set; }

    [JsonPropertyName("breedte")]
    public int Breedte { get; set; }

    [JsonPropertyName("hoogte")]
    public int Hoogte { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("soort")]
    public string Soort { get; set; } = "beeld";

    [JsonPropertyName("bron_beeld")]
    public string? BronBeeld { get; set; }

    [JsonPropertyName("scenes")]
    public List<FilmScene> Scenes { get; set; } = [];

    [JsonPropertyName("modus")]
    public string Modus { get; set; } = string.Empty;

    [JsonPropertyName("overgang")]
    public string Overgang { get; set; } = string.Empty;

    [JsonPropertyName("stijl")]
    public string Stijl { get; set; } = string.Empty;

    [JsonPropertyName("cast")]
    public List<CastItem> Cast { get; set; } = [];

    /// <summary>Bij een film: "sleutelbeelden" | "model" | "scenes" | "samenvoegen" | "klaar".</summary>
    [JsonPropertyName("fase")]
    public string Fase { get; set; } = string.Empty;

    [JsonPropertyName("foutmelding")]
    public string? Foutmelding { get; set; }

    public bool IsKlaar => Status == "klaar";

    public bool IsMislukt => Status == "mislukt";
}

/// <summary>Antwoord van /health.</summary>
public sealed class BeeldServiceGezondheid
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("video_model_geladen")]
    public bool VideoModelGeladen { get; set; }

    [JsonPropertyName("model_geladen")]
    public bool ModelGeladen { get; set; }

    [JsonPropertyName("cuda")]
    public bool Cuda { get; set; }

    [JsonPropertyName("gpu")]
    public string? Gpu { get; set; }
}
