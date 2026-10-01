using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Logging;

namespace ImageFunn.App.Ai;

/// <summary>
/// Maakt van een omschrijving in gewone (Nederlandse) taal een prompt die een
/// beeldmodel begrijpt. SDXL en FLUX lezen alleen Engels: een Nederlandse zin
/// wordt grotendeels genegeerd, waardoor je een ander beeld krijgt dan je vroeg.
/// Loopt via de AI-broker met het gewone chatmodel.
/// </summary>
public sealed class Promptverbeteraar(IAiBrokerClient broker)
{
    private const string Instructie =
        "You write prompts for a text-to-image model (SDXL/FLUX). The user describes an image in any language. " +
        "Translate it to English and turn it into ONE flowing prompt of at most 70 words. " +
        "Keep EVERY element the user mentions, exactly as asked (subjects, their count, their relative positions " +
        "such as 'in front of' or 'behind'), and never drop or replace one. Put the main subjects first. " +
        "Then add concrete visual detail that fits the request: setting, lighting, mood, colours, composition and " +
        "a fitting style (for example: detailed digital illustration). Do not add new subjects. " +
        "Output ONLY the prompt: no quotes, no explanation, no line breaks.";

    /// <summary>Geeft de verbeterde prompt, of null (met foutmelding) als dat niet lukte - de aanroeper valt dan terug op de originele tekst.</summary>
    public async Task<(string? Prompt, string? Fout)> VerbeterAsync(string omschrijving, CancellationToken cancellationToken)
    {
        var instelling = await broker.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!instelling.Success)
        {
            return (null, instelling.ErrorMessage ?? "De AI-instellingen konden niet gelezen worden.");
        }

        var model = instelling.Settings.Active.ChatModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            return (null, "Er is geen chatmodel ingesteld.");
        }

        ActivityLog.Shared.Add("app", $"Prompt: {model} schrijft de omschrijving om naar een beeldprompt…");

        using var limiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limiet.CancelAfter(TimeSpan.FromMinutes(3));

        var resultaat = await broker.ChatAsync(
            new ChatRequest(
                instelling.Settings.Provider,
                instelling.Settings.ActiveServerUrl,
                model,
                [new ChatMessage("system", Instructie), new ChatMessage("user", omschrijving)],
                "ImageFunn",
                Temperature: 0.4,
                Onderdeel: "Tekst naar beeld (prompt)"),
            limiet.Token).ConfigureAwait(false);

        if (!resultaat.Success)
        {
            return (null, resultaat.ErrorMessage ?? "Het model gaf geen antwoord.");
        }

        var tekst = resultaat.Reply.Trim().Trim('"').Replace('\n', ' ').Trim();
        return tekst.Length == 0 ? (null, "Het model gaf een leeg antwoord.") : (tekst, null);
    }
}
