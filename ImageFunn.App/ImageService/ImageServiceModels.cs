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

    [JsonPropertyName("model_geladen")]
    public bool ModelGeladen { get; set; }

    [JsonPropertyName("cuda")]
    public bool Cuda { get; set; }

    [JsonPropertyName("gpu")]
    public string? Gpu { get; set; }
}
