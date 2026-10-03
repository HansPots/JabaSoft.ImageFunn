using System.Text.Json;
using System.Text.RegularExpressions;
using ImageFunn.App.ImageService;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Logging;

namespace ImageFunn.App.Ai;

/// <summary>Het voorstel dat het chatmodel doet: een stijl voor de hele film en een rij scènes.</summary>
public sealed record Tijdlijnvoorstel(string Stijl, List<FilmScene> Scenes);

/// <summary>
/// Laat het chatmodel van een verhaalidee een tijdlijn met scènes maken, en
/// leidt per scène de Engelse prompts af die de beeld- en videomodellen
/// nodig hebben. Loopt via de AI-broker, net als de andere schrijvers.
/// </summary>
public sealed partial class Verhaalschrijver(IAiBrokerClient broker)
{
    private const string TijdlijnInstructie =
        "You are a storyboard writer for an AI video generator that makes short clips from a still image. " +
        "The user gives a story idea in any language. Reply with ONLY a JSON object, no markdown fences, no comments: " +
        "{\"stijl\": \"...\", \"scenes\": [{\"titel\": \"...\", \"gebeurt\": \"...\", \"beeld\": \"...\", \"beweging\": \"...\", \"seconden\": 4}]}. " +
        "Rules: " +
        "\"stijl\" is ENGLISH, max 40 words: the visual style (colour palette, lighting) " +
        "plus a fixed physical description of every recurring character (age, hair, clothes, colours). It is placed in front of every " +
        "scene image, so it keeps the film consistent. " +
        "\"titel\" is a short DUTCH title (max 4 words). \"gebeurt\" is 1 or 2 DUTCH sentences saying what happens. " +
        "\"beeld\" is ENGLISH, max 45 words: the FIRST FRAME of the scene as a still image (subject and pose, setting, lighting, " +
        "composition). Repeat the key traits of the characters that appear. " +
        "\"beweging\" is ENGLISH, max 40 words, present tense: what moves and how, then the camera movement. " +
        "\"seconden\" is a whole number from 2 to 6. " +
        "The scenes follow each other in time: the end of a scene leads naturally into the start of the next, in the same " +
        "world and with the same characters. Use ONE simple action per scene; clips are only a few seconds long. " +
        "Do not add dialogue, text on screen or sound.";

    private const string AfleidInstructie =
        "You prepare prompts for an AI video generator. The user gives the visual style and one scene description in any language. " +
        "Reply with ONLY a JSON object, no markdown fences: {\"beeld\": \"...\", \"beweging\": \"...\"}. " +
        "\"beeld\" is ENGLISH, max 45 words: the first frame of the scene as a still image (subject and pose, setting, lighting, " +
        "composition). \"beweging\" is ENGLISH, max 40 words, present tense: what moves and how, then the camera movement. " +
        "Keep every element the user mentions and add no new subjects.";

    private const string OpmerkingenInstructie =
        "You revise a storyboard for an AI image generator after user feedback (the feedback can be in any language). " +
        "Reply with ONLY a JSON object, no markdown fences: {\"stijl\": \"...\", \"scenes\": [{\"nr\": 1, \"beeld\": \"...\"}]}. " +
        "\"stijl\" is ENGLISH, max 60 words: the visual style plus a fixed, very specific physical description of every recurring " +
        "character (shape, colours, materials, distinctive parts such as eyes, antenna, joints). Update it according to the feedback. " +
        "If the feedback says the characters look different or must be the same, make that description much more precise and concrete. " +
        "\"scenes\" lists ONLY the scenes whose image prompt must change because of the feedback (feedback on that scene, or general " +
        "feedback that affects it). Each \"beeld\" is ENGLISH, max 45 words, the first frame as a still image; keep the pose and setting " +
        "of that scene unless the feedback says otherwise, and refer to every recurring character with the SAME short noun phrase " +
        "(for example \"the blue round-headed robot\") in every scene. Do not add new characters.";

    private const string CastInstructie =
        "You prepare reference images for an AI video generator that must keep characters and locations consistent. " +
        "The user gives a story as numbered scenes (any language). Reply with ONLY a JSON object, no markdown fences: " +
        "{\"cast\": [{\"naam\": \"...\", \"soort\": \"speler\", \"beschrijving\": \"...\", \"beeld\": \"...\"}], " +
        "\"scenes\": [{\"nr\": 1, \"spelers\": [\"...\"], \"plek\": \"...\", \"voorwerpen\": [\"...\"]}]}. " +
        "Rules: \"cast\" holds the RECURRING characters (soort \"speler\", max 5), the distinct LOCATIONS (soort \"plek\", max 6) and the IMPORTANT " +
        "OBJECTS that appear in more than one scene or that the story is about (soort \"voorwerp\", max 4, for example a broomstick or a sword). " +
        "\"naam\" is a short DUTCH name, unique. \"beschrijving\" is 1 DUTCH sentence for the user. " +
        "\"beeld\" is ENGLISH, max 40 words: for a speler only the physical appearance (shape, colours, materials, clothes, distinctive parts) " +
        "WITHOUT any background or action; for a plek only the place, light and mood, WITHOUT any person or character; " +
        "for a voorwerp only the object itself (shape, material, colour, size cues), WITHOUT background or person. " +
        "\"scenes\" says for EVERY scene which spelers (by naam) appear, in which plek it plays (by naam, or null) and which voorwerpen (by naam) are important in it. " +
        "Use only names that are in \"cast\". Do not invent characters that are not in the story.";

    private const string CastBeeldInstructie =
        "You prepare a reference image prompt for an AI image generator. The user gives the kind (speler = character, plek = location), " +
        "an optional visual style and a short description in any language. Reply with ONLY the English prompt (max 40 words, no quotes): " +
        "for a speler only the physical appearance without background or action; for a plek only the place, light and mood without any person; for a voorwerp only the object itself without background or person.";

    private static readonly JsonSerializerOptions Opties = new() { PropertyNameCaseInsensitive = true };

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DenkBlok();

    /// <summary>Maakt uit een idee een tijdlijn van ongeveer <paramref name="totaalSeconden"/> seconden.</summary>
    public async Task<(Tijdlijnvoorstel? Voorstel, string? Fout)> MaakTijdlijnAsync(
        string idee, int totaalSeconden, string stijlPrefix, CancellationToken cancellationToken)
    {
        var aantal = Math.Max(1, (int)Math.Round(totaalSeconden / 4.0));
        var vraag =
            $"Story idea: {idee.Trim()}\n\nMake about {aantal} scenes, together about {totaalSeconden} seconds " +
            "(most scenes 4 seconds, a few 2 to 6)." +
            (string.IsNullOrWhiteSpace(stijlPrefix)
                ? string.Empty
                : $"\n\nThe \"stijl\" MUST start with exactly this text: \"{stijlPrefix}\", followed by the colour palette and the character descriptions. " +
                  "Describe the characters and places realistically in line with that style; do not call anything an illustration or drawing unless that text says so.");

        var (tekst, fout) = await VraagAsync(TijdlijnInstructie, vraag, 0.7, "Film (tijdlijn)", cancellationToken).ConfigureAwait(false);
        if (tekst is null)
        {
            return (null, fout);
        }

        try
        {
            var dto = JsonSerializer.Deserialize<TijdlijnDto>(UitJson(tekst), Opties);
            if (dto?.Scenes is not { Count: > 0 })
            {
                return (null, "Het model gaf geen scènes terug. Probeer het nog eens of maak het idee concreter.");
            }

            var scenes = dto.Scenes.Select(s => new FilmScene
            {
                Titel = s.Titel?.Trim() ?? string.Empty,
                Gebeurt = s.Gebeurt?.Trim() ?? string.Empty,
                Beeld = s.Beeld?.Trim() ?? string.Empty,
                Beweging = s.Beweging?.Trim() ?? string.Empty,
                Seconden = Math.Clamp(s.Seconden is > 0 ? s.Seconden : 4, 2, 6),
            }).ToList();

            return (new Tijdlijnvoorstel(dto.Stijl?.Trim() ?? string.Empty, scenes), null);
        }
        catch (JsonException)
        {
            return (null, "Het antwoord van het model was geen geldige tijdlijn. Probeer het nog eens.");
        }
    }

    /// <summary>Leidt voor één scène de Engelse beeld- en bewegingsprompt af uit de omschrijving.</summary>
    public async Task<(string? Beeld, string? Beweging, string? Fout)> LeidAfAsync(
        string stijl, string gebeurt, CancellationToken cancellationToken)
    {
        var vraag = $"Visual style: {(string.IsNullOrWhiteSpace(stijl) ? "(none)" : stijl.Trim())}\nScene: {gebeurt.Trim()}";
        var (tekst, fout) = await VraagAsync(AfleidInstructie, vraag, 0.4, "Film (scène)", cancellationToken).ConfigureAwait(false);
        if (tekst is null)
        {
            return (null, null, fout);
        }

        try
        {
            var dto = JsonSerializer.Deserialize<AfleidDto>(UitJson(tekst), Opties);
            return string.IsNullOrWhiteSpace(dto?.Beeld)
                ? (null, null, "Het model gaf geen beeldomschrijving terug.")
                : (dto.Beeld.Trim(), dto.Beweging?.Trim() ?? string.Empty, null);
        }
        catch (JsonException)
        {
            return (null, null, "Het antwoord van het model was geen geldige omschrijving.");
        }
    }

    /// <summary>
    /// Verwerkt de opmerkingen van de gebruiker over de sleutelbeelden: geeft een bijgewerkte stijl/personagebeschrijving
    /// en nieuwe beeldomschrijvingen voor de scènes die daardoor veranderen (sleutel = scènenummer vanaf 1).
    /// </summary>
    public async Task<(string? Stijl, Dictionary<int, string>? Beelden, string? Fout)> PasOpmerkingenToeAsync(
        string stijl,
        IReadOnlyList<(int Nr, string Titel, string Beeld)> scenes,
        string algemeen,
        IReadOnlyDictionary<int, string> perScene,
        CancellationToken cancellationToken)
    {
        var tekst = new System.Text.StringBuilder();
        tekst.AppendLine($"Current style and character description: {(string.IsNullOrWhiteSpace(stijl) ? "(none)" : stijl.Trim())}");
        tekst.AppendLine("Scenes (number, title, current image prompt):");
        foreach (var (nr, titel, beeld) in scenes)
        {
            tekst.AppendLine($"{nr}. [{titel}] {beeld}");
        }

        tekst.AppendLine();
        tekst.AppendLine($"General feedback: {(string.IsNullOrWhiteSpace(algemeen) ? "(none)" : algemeen.Trim())}");
        tekst.AppendLine("Feedback per scene:");
        foreach (var (nr, opmerking) in perScene.OrderBy(p => p.Key))
        {
            tekst.AppendLine($"{nr}: {opmerking.Trim()}");
        }

        var (antwoord, fout) = await VraagAsync(OpmerkingenInstructie, tekst.ToString(), 0.4, "Film (opmerkingen)", cancellationToken).ConfigureAwait(false);
        if (antwoord is null)
        {
            return (null, null, fout);
        }

        try
        {
            var dto = JsonSerializer.Deserialize<OpmerkingenDto>(UitJson(antwoord), Opties);
            if (dto is null)
            {
                return (null, null, "Het model gaf geen bruikbaar antwoord.");
            }

            var beelden = (dto.Scenes ?? [])
                .Where(s => s.Nr >= 1 && !string.IsNullOrWhiteSpace(s.Beeld))
                .GroupBy(s => s.Nr)
                .ToDictionary(g => g.Key, g => g.Last().Beeld!.Trim());
            return (dto.Stijl?.Trim(), beelden, null);
        }
        catch (JsonException)
        {
            return (null, null, "Het antwoord van het model was geen geldige aanpassing.");
        }
    }

    /// <summary>Het resultaat van <see cref="MaakCastAsync"/>: de cast en per scène (nummer vanaf 1) wie er meespeelt en waar.</summary>
    public sealed record CastVoorstel(List<CastItem> Cast, Dictionary<int, (List<string> Spelers, string? Plek, List<string> Voorwerpen)> Scenes);

    /// <summary>Laat het chatmodel uit de scènes een lijst met spelers en plekken halen en bij elke scène zetten wie waar speelt.</summary>
    public async Task<(CastVoorstel? Voorstel, string? Fout)> MaakCastAsync(
        string stijl, IReadOnlyList<FilmScene> scenes, CancellationToken cancellationToken)
    {
        var tekst = new System.Text.StringBuilder();
        tekst.AppendLine($"Visual style: {(string.IsNullOrWhiteSpace(stijl) ? "(none)" : stijl.Trim())}");
        for (var i = 0; i < scenes.Count; i++)
        {
            var s = scenes[i];
            tekst.AppendLine($"{i + 1}. {s.Titel}: {(string.IsNullOrWhiteSpace(s.Gebeurt) ? s.Beeld : s.Gebeurt)}");
        }

        var (antwoord, fout) = await VraagAsync(CastInstructie, tekst.ToString(), 0.4, "Film (cast)", cancellationToken).ConfigureAwait(false);
        if (antwoord is null)
        {
            return (null, fout);
        }

        try
        {
            var dto = JsonSerializer.Deserialize<CastDto>(UitJson(antwoord), Opties);
            if (dto?.Cast is not { Count: > 0 })
            {
                return (null, "Het model vond geen spelers of plekken in het verhaal.");
            }

            var cast = dto.Cast
                .Where(c => !string.IsNullOrWhiteSpace(c.Naam) && !string.IsNullOrWhiteSpace(c.Beeld))
                .GroupBy(c => c.Naam!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(c => new CastItem
                {
                    Naam = c.Naam!.Trim(),
                    Soort = c.Soort?.Trim().ToLowerInvariant() switch { "plek" => "plek", "voorwerp" => "voorwerp", _ => "speler" },
                    Beschrijving = c.Beschrijving?.Trim() ?? string.Empty,
                    Beeld = c.Beeld!.Trim(),
                }).ToList();

            var namen = cast.ToDictionary(c => c.Naam, c => c, StringComparer.OrdinalIgnoreCase);
            var perScene = new Dictionary<int, (List<string>, string?, List<string>)>();
            foreach (var s in dto.Scenes ?? [])
            {
                var spelers = (s.Spelers ?? [])
                    .Select(n => n.Trim())
                    .Where(n => namen.TryGetValue(n, out var c) && c.Soort == "speler")
                    .Select(n => namen[n].Naam)
                    .Distinct()
                    .ToList();
                string? plek = s.Plek is { } p && namen.TryGetValue(p.Trim(), out var pc) && pc.Soort == "plek" ? pc.Naam : null;
                var voorwerpen = (s.Voorwerpen ?? [])
                    .Select(n => n.Trim())
                    .Where(n => namen.TryGetValue(n, out var c) && c.Soort == "voorwerp")
                    .Select(n => namen[n].Naam)
                    .Distinct()
                    .ToList();
                perScene[s.Nr] = (spelers, plek, voorwerpen);
            }

            return (new CastVoorstel(cast, perScene), null);
        }
        catch (JsonException)
        {
            return (null, "Het antwoord van het model was geen geldige lijst.");
        }
    }

    /// <summary>Maakt van de omschrijving van een speler of plek de Engelse prompt voor zijn referentiebeeld.</summary>
    public async Task<(string? Beeld, string? Fout)> LeidCastBeeldAsync(
        string soort, string stijl, string beschrijving, CancellationToken cancellationToken)
    {
        var vraag = $"Kind: {soort}\nVisual style: {(string.IsNullOrWhiteSpace(stijl) ? "(none)" : stijl.Trim())}\nDescription: {beschrijving.Trim()}";
        var (antwoord, fout) = await VraagAsync(CastBeeldInstructie, vraag, 0.4, "Film (cast)", cancellationToken).ConfigureAwait(false);
        if (antwoord is null)
        {
            return (null, fout);
        }

        var schoon = DenkBlok().Replace(antwoord, string.Empty).Trim().Trim('"').Replace('\n', ' ').Trim();
        return schoon.Length == 0 ? (null, "Het model gaf een leeg antwoord.") : (schoon, null);
    }

    private async Task<(string? Tekst, string? Fout)> VraagAsync(
        string systeem, string vraag, double temperatuur, string onderdeel, CancellationToken cancellationToken)
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

        ActivityLog.Shared.Add("app", $"Film: {model} schrijft ({onderdeel})…");

        using var limiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limiet.CancelAfter(TimeSpan.FromMinutes(10));

        var resultaat = await broker.ChatMetTellerAsync(
            new ChatRequest(
                instelling.Settings.Provider,
                instelling.Settings.ActiveServerUrl,
                model,
                [new ChatMessage("system", systeem), new ChatMessage("user", vraag)],
                "ImageFunn",
                Temperature: temperatuur,
                Onderdeel: onderdeel),
            limiet.Token).ConfigureAwait(false);

        return resultaat.Success
            ? (resultaat.Reply, null)
            : (null, resultaat.ErrorMessage ?? "Het model gaf geen antwoord.");
    }

    /// <summary>Haalt het JSON-object uit een antwoord: zonder denkblokken en codehekjes.</summary>
    private static string UitJson(string antwoord)
    {
        var schoon = DenkBlok().Replace(antwoord, string.Empty);
        var begin = schoon.IndexOf('{');
        var einde = schoon.LastIndexOf('}');
        return begin >= 0 && einde > begin ? schoon[begin..(einde + 1)] : schoon;
    }

    private sealed class TijdlijnDto
    {
        public string? Stijl { get; set; }

        public List<SceneDto>? Scenes { get; set; }
    }

    private sealed class SceneDto
    {
        public string? Titel { get; set; }

        public string? Gebeurt { get; set; }

        public string? Beeld { get; set; }

        public string? Beweging { get; set; }

        public int Seconden { get; set; }
    }

    private sealed class CastDto
    {
        public List<CastDtoItem>? Cast { get; set; }

        public List<CastSceneDto>? Scenes { get; set; }
    }

    private sealed class CastDtoItem
    {
        public string? Naam { get; set; }

        public string? Soort { get; set; }

        public string? Beschrijving { get; set; }

        public string? Beeld { get; set; }
    }

    private sealed class CastSceneDto
    {
        public int Nr { get; set; }

        public List<string>? Spelers { get; set; }

        public string? Plek { get; set; }

        public List<string>? Voorwerpen { get; set; }
    }

    private sealed class OpmerkingenDto
    {
        public string? Stijl { get; set; }

        public List<OpmerkingSceneDto>? Scenes { get; set; }
    }

    private sealed class OpmerkingSceneDto
    {
        public int Nr { get; set; }

        public string? Beeld { get; set; }
    }

    private sealed class AfleidDto
    {
        public string? Beeld { get; set; }

        public string? Beweging { get; set; }
    }
}
