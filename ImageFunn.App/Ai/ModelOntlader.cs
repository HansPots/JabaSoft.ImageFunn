using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Jabasoft.Base.AiBroker;
using Jabasoft.Base.Logging;

namespace ImageFunn.App.Ai;

/// <summary>
/// Haalt de taalmodellen uit het GPU-geheugen vlak voordat de beeldservice begint.
/// Ollama en LM Studio houden een model na een aanroep nog minutenlang geladen, en een
/// chatmodel van ±9 GB laat op een kaart van 12 GB weinig ruimte over voor het beeld- en
/// videomodel, dat daardoor traag wordt. Het chatmodel wordt bij de volgende aanroep vanzelf
/// weer geladen. Alles hierin is "best effort": mislukt het, dan gaat het genereren gewoon door.
/// </summary>
public static class ModelOntlader
{
    private static readonly TimeSpan Limiet = TimeSpan.FromSeconds(8);

    public static async Task OntlaadAsync()
    {
        try
        {
            using var limiet = new CancellationTokenSource(Limiet);
            var instelling = await AiBrokerClient.CreateDefault().GetSettingsAsync(limiet.Token).ConfigureAwait(false);
            if (!instelling.Success)
            {
                return;
            }

            if (instelling.Settings.Provider == AiProvider.Ollama)
            {
                await OntlaadOllamaAsync(instelling.Settings.ActiveServerUrl, limiet.Token).ConfigureAwait(false);
            }
            else
            {
                OntlaadLmStudio();
            }
        }
        catch (Exception)
        {
            // Niet bereikbaar of geen rechten: het genereren zelf hoeft daar niet door te stoppen.
        }
    }

    /// <summary>Vraagt Ollama welke modellen geladen zijn en zet ze op keep_alive 0, wat ze meteen ontlaadt.</summary>
    private static async Task OntlaadOllamaAsync(string url, CancellationToken token)
    {
        using var client = new HttpClient { BaseAddress = new Uri(url), Timeout = Limiet };
        using var antwoord = await client.GetAsync("/api/ps", token).ConfigureAwait(false);
        if (!antwoord.IsSuccessStatusCode)
        {
            return;
        }

        using var document = JsonDocument.Parse(await antwoord.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("models", out var modellen))
        {
            return;
        }

        foreach (var model in modellen.EnumerateArray())
        {
            if (model.TryGetProperty("name", out var naam) && naam.GetString() is { Length: > 0 } modelNaam)
            {
                using var _ = await client.PostAsJsonAsync("/api/generate", new { model = modelNaam, keep_alive = 0 }, token).ConfigureAwait(false);
                ActivityLog.Shared.Add("app", $"Geheugen vrijmaken: {modelNaam} ontladen voor het genereren.");
            }
        }
    }

    /// <summary>LM Studio heeft een opdrachtregelprogramma (lms) dat alle geladen modellen ontlaadt.</summary>
    private static void OntlaadLmStudio()
    {
        var lms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "bin", "lms.exe");
        if (!File.Exists(lms))
        {
            return;
        }

        using var proces = Process.Start(new ProcessStartInfo
        {
            FileName = lms,
            Arguments = "unload --all",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (proces is not null && proces.WaitForExit((int)Limiet.TotalMilliseconds))
        {
            ActivityLog.Shared.Add("app", "Geheugen vrijmaken: LM Studio-modellen ontladen voor het genereren.");
        }
        else
        {
            try
            {
                proces?.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Al gestopt.
            }
        }
    }
}
