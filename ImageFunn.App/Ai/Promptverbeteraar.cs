using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Logging;

namespace ImageFunn.App.Ai;

/// <summary>
/// Maakt van een omschrijving in gewone (Nederlandse) taal een prompt die een
/// beeldmodel begrijpt. SDXL en FLUX lezen alleen Engels: een Nederlandse zin
/// wordt grotendeels genegeerd, waardoor je een ander beeld krijgt dan je vroeg.
/// Loopt via de AI-broker met het gewone chatmodel.
/// </summary>
/// <summary>Voor welk soort model de prompt bedoeld is.</summary>
public enum Promptsoort
{
    /// <summary>Tekst naar beeld (SDXL/FLUX).</summary>
    Beeld,

    /// <summary>De beweging bij een bestaand beeld (beeld naar video).</summary>
    Beweging,

    /// <summary>Een bestaande prompt aanpassen met een wijzigingsverzoek (bijsturen van een beeld).</summary>
    Bewerk,

    /// <summary>Een hele scène met beweging (tekst naar video).</summary>
    Scene,
}

public sealed class Promptverbeteraar(IAiBrokerClient broker)
{
    private const string Instructie =
        "You write prompts for a text-to-image model (SDXL/FLUX). The user describes an image in any language. " +
        "Translate it to English and turn it into ONE flowing prompt of at most 70 words. " +
        "Keep EVERY element the user mentions, exactly as asked (subjects, their count, their relative positions " +
        "such as 'in front of' or 'behind'), and never drop or replace one. Put the main subjects first. " +
        "Then add concrete visual detail that fits the request: setting, lighting, mood, colours, composition and " +
        "Keep any style the user mentions (for example watercolour or photo), but do NOT invent a style of your own: the style is added separately. Do not add new subjects. " +
        "Output ONLY the prompt: no quotes, no explanation, no line breaks.";

    private const string BewerkInstructie =
        "You edit a prompt for a text-to-image model. The user gives the ORIGINAL English prompt of an existing image and a " +
        "CHANGE REQUEST in any language (for example: make the person a bit bigger, or add a red bicycle in the background). " +
        "Return the complete UPDATED English prompt, ONE flowing paragraph of at most 90 words, with the change applied and " +
        "everything else kept as it was (subjects, style words, lighting, setting). Describe the change concretely: for a " +
        "larger person write that the person fills more of the frame, closer to the camera; for an object name it and say where it is. " +
        "Output ONLY the prompt: no quotes, no explanation, no line breaks.";

    private const string SceneInstructie =
        "You write prompts for a text-to-video model (LTX-Video). The user describes, in any language, a short " +
        "video scene. Translate it to English and write ONE flowing paragraph of at most 90 words in present " +
        "tense: start with the main subject and its action, then the setting and background, then lighting, " +
        "colours and mood, then the camera movement (for example: slow push-in, gentle pan, tracking shot, static) " +
        "and the pace. Keep every element the user mentions, keep the motion physically plausible and do not add " +
        "new subjects. Output ONLY the prompt: no quotes, no explanation, no line breaks.";

    private const string BewegingInstructie =
        "You write motion prompts for an image-to-video model (LTX-Video). The user describes, in any language, how " +
        "a still image should come to life. Translate it to English and write ONE flowing paragraph of at most 60 " +
        "words in present tense: first what moves and how (subject action, hair, cloth, water, clouds, light), then " +
        "the camera movement (for example: slow push-in, gentle pan, static), then the pace and mood. Keep it " +
        "physically plausible and do not add new subjects or change what is in the picture. " +
        "Output ONLY the prompt: no quotes, no explanation, no line breaks.";

    /// <summary>Geeft de verbeterde prompt, of null (met foutmelding) als dat niet lukte - de aanroeper valt dan terug op de originele tekst.</summary>
    public async Task<(string? Prompt, string? Fout)> VerbeterAsync(string omschrijving, CancellationToken cancellationToken, Promptsoort soort = Promptsoort.Beeld)
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

        var resultaat = await broker.ChatMetTellerAsync(
            new ChatRequest(
                instelling.Settings.Provider,
                instelling.Settings.ActiveServerUrl,
                model,
                [new ChatMessage("system", soort switch { Promptsoort.Beweging => BewegingInstructie, Promptsoort.Bewerk => BewerkInstructie, Promptsoort.Scene => SceneInstructie, _ => Instructie }), new ChatMessage("user", omschrijving)],
                "ImageFunn",
                Temperature: 0.4,
                Onderdeel: soort switch { Promptsoort.Beweging => "Beeld naar video (prompt)", Promptsoort.Bewerk => "Beeld bijsturen (prompt)", Promptsoort.Scene => "Tekst naar video (prompt)", _ => "Tekst naar beeld (prompt)" }),
            limiet.Token).ConfigureAwait(false);

        if (!resultaat.Success)
        {
            return (null, resultaat.ErrorMessage ?? "Het model gaf geen antwoord.");
        }

        var tekst = resultaat.Reply.Trim().Trim('"').Replace('\n', ' ').Trim();
        return tekst.Length == 0 ? (null, "Het model gaf een leeg antwoord.") : (tekst, null);
    }
}
