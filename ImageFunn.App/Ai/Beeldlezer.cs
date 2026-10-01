using System.IO;
using System.Text;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Logging;

namespace ImageFunn.App.Ai;

/// <summary>Wat het beeldmodel met een afbeelding moet doen.</summary>
public enum Leesdoel
{
    /// <summary>Een volledige, feitelijke beschrijving van wat er te zien is.</summary>
    Beschrijving,

    /// <summary>Alle zichtbare tekst letterlijk overnemen (OCR).</summary>
    TekstUitlezen,

    /// <summary>Een prompt schrijven waarmee een beeldgenerator dit beeld kan nabouwen.</summary>
    Prompt,
}

/// <summary>
/// Laat het beeldmodel (apart ingesteld in Jabasoft, AiServerSettings.BeeldModel)
/// een afbeelding lezen. Zelfde route als AI Studio: via de broker, zodat
/// het verbruik in het tokenoverzicht van Jabasoft meetelt.
/// </summary>
public sealed class Beeldlezer(IAiBrokerClient broker)
{
    private const string Applicatie = "ImageFunn";

    private static readonly TimeSpan Limiet = TimeSpan.FromMinutes(10);

    private static string Instructie(Leesdoel doel) => doel switch
    {
        Leesdoel.TekstUitlezen =>
            "Je leest tekst uit een afbeelding (OCR). Neem ALLE zichtbare tekst letterlijk over, teken voor teken, " +
            "in leesvolgorde (boven naar beneden, links naar rechts) en behoud regelafbreking, opsommingen en tabellen " +
            "(tabellen als markdown-tabel). Vertaal niets, corrigeer niets en vat niets samen. Is een stuk tekst " +
            "onleesbaar of afgekapt, schrijf dan [onleesbaar] op die plek in plaats van te gokken. Staat er geen " +
            "tekst in de afbeelding, zeg dat dan in één zin.",

        Leesdoel.Prompt =>
            "Je schrijft een prompt voor een tekst-naar-beeld-model (zoals FLUX of Stable Diffusion) waarmee dit " +
            "beeld zo getrouw mogelijk nagemaakt kan worden. Schrijf ÉÉN doorlopende prompt in het ENGELS, geen " +
            "opsomming en geen uitleg eromheen. Volgorde: onderwerp en handeling, details van personen/objecten " +
            "(kleding, kleuren, materialen), omgeving en achtergrond, compositie en camerastandpunt, lenskeuze en " +
            "scherptediepte, lichtopbouw en sfeer, kleurenpalet, en tot slot de stijl of het medium (foto, olieverf, " +
            "3D-render, illustratie…). Wees concreet en beschrijf alleen wat je ziet. Geef niets weer als tekst in " +
            "het beeld tenzij die echt onderdeel van het beeld is, dan tussen aanhalingstekens.",

        _ =>
            "Je beschrijft een afbeelding volledig en feitelijk. Behandel, in deze volgorde: (1) in één zin wat het " +
            "is (foto, screenshot, tekening, schilderij, diagram…); (2) het onderwerp en wat er gebeurt; (3) alle " +
            "personen, dieren en objecten met kleuren, kleding, houding en onderlinge posities; (4) de omgeving en " +
            "achtergrond; (5) compositie, licht, sfeer en stijl; (6) alle zichtbare tekst letterlijk, tussen " +
            "aanhalingstekens. Schrijf alleen wat je ziet, geef aan wat onzeker of onleesbaar is in plaats van te " +
            "gokken, en doe geen aannames over wat niet zichtbaar is.",
    };

    /// <summary>De naam van het model dat gebruikt gaat worden, of null als er niets ingesteld is.</summary>
    public async Task<(string? Model, string? Fout)> KiesModelAsync(CancellationToken cancellationToken)
    {
        var instelling = await broker.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!instelling.Success)
        {
            return (null, instelling.ErrorMessage ?? "De AI-instellingen konden niet gelezen worden - draait de broker?");
        }

        var actief = instelling.Settings.Active;

        // Het beeldmodel als dat ingesteld is; anders het gewone chatmodel (kan het geen
        // beelden lezen, dan meldt de server dat zelf).
        var model = !string.IsNullOrWhiteSpace(actief.BeeldModel) ? actief.BeeldModel : actief.ChatModel;
        return string.IsNullOrWhiteSpace(model)
            ? (null, "Er is geen beeldmodel ingesteld. Kies er een in Jabasoft > Instellingen.")
            : (model, null);
    }

    /// <summary>Leest de afbeelding. Geeft de tekst terug, of een foutmelding.</summary>
    public async Task<(string? Tekst, string? Fout, string Model)> LeesAsync(
        string bestandspad,
        Leesdoel doel,
        string extraVraag,
        CancellationToken cancellationToken)
    {
        var (model, modelFout) = await KiesModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return (null, modelFout, string.Empty);
        }

        var instelling = (await broker.GetSettingsAsync(cancellationToken).ConfigureAwait(false)).Settings;
        var base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(bestandspad, cancellationToken).ConfigureAwait(false));

        var vraag = string.IsNullOrWhiteSpace(extraVraag)
            ? "Voer de opdracht uit op deze afbeelding."
            : $"Voer de opdracht uit op deze afbeelding. Extra wens van de gebruiker: {extraVraag.Trim()}";

        var berichten = new List<ChatMessage>
        {
            new("system", Instructie(doel) + (doel == Leesdoel.Prompt ? string.Empty : " Antwoord in het Nederlands.")),
            new("user", vraag, [base64]),
        };

        ActivityLog.Shared.Add("app", $"Afbeelding: {model} bekijkt de afbeelding ({doel})…");

        using var limiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limiet.CancelAfter(Limiet);

        // Streaming en niet ChatAsync: alleen de streaming-route van de broker stuurt
        // afbeeldingen aan het model mee (zie Jabasoft.Broker/ProviderStream).
        var antwoord = new StringBuilder();
        string? fout = null;

        await foreach (var stukje in broker.ChatStreamAsync(
            new ChatRequest(
                instelling.Provider,
                instelling.ActiveServerUrl,
                model,
                berichten,
                Applicatie,
                Temperature: doel == Leesdoel.TekstUitlezen ? 0.0 : 0.3,
                Onderdeel: $"Beeld naar tekst ({doel})"),
            limiet.Token).ConfigureAwait(false))
        {
            if (stukje.ErrorMessage is not null)
            {
                fout = stukje.ErrorMessage;
                break;
            }

            if (!string.IsNullOrEmpty(stukje.Text))
            {
                antwoord.Append(stukje.Text);
            }
        }

        if (fout is not null)
        {
            return (null, fout, model);
        }

        var tekst = antwoord.ToString().Trim();
        return tekst.Length == 0
            ? (null, "Het model gaf geen antwoord.", model)
            : (tekst, null, model);
    }
}
