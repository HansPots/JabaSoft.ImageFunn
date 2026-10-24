using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Jabasoft.Base;
using Jabasoft.Base.Logging;

namespace ImageFunn.App.ImageService;

/// <summary>
/// Start de lokale Python beeldservice (service.py) als die nog niet
/// draait - zelfde rol als Jabasoft.Base.AiBroker.AiBrokerProcessLauncher,
/// maar dan voor de beeldgeneratie. Die service leeft in het eigen
/// venv onder ImageFunn.Image, niet in dit .NET-project.
///
/// Anders dan de AI-broker (die door de hele familie gedeeld wordt) is deze
/// service alleen van ImageFunn, dus we hoeven niet te checken of een
/// andere applicatie hem nog nodig heeft voordat we hem afsluiten - alleen
/// of WIJ hem gestart hebben.
/// </summary>
public static class ImageServiceLauncher
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// De map met .venv en service.py - naast het ImageFunn.App-project, niet
    /// erin. Vanuit de projectmap die bij het bouwen is vastgelegd (zie
    /// ImageFunn.App.csproj) en niet vanuit de map van de exe: ImageFunn
    /// draait in Jabasoft, en die exe staat in een heel andere repo.
    /// </summary>
    private static readonly string ProjectPath = Path.GetFullPath(
        Path.Combine(ProjectMap(), "..", "ImageFunn.Image"));

    private static string ProjectMap() =>
        typeof(ImageServiceLauncher).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ProjectMap")?.Value
        ?? AppContext.BaseDirectory;

    private static Process? _gestartProces;

    public static async Task EnsureRunningAsync(
        string baseUrl = ImageServiceClient.DefaultBaseUrl,
        CancellationToken cancellationToken = default)
    {
        using var probeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var (gezond, versie) = await LeesGezondheidAsync(probeClient, baseUrl).ConfigureAwait(false);
        if (gezond)
        {
            // Een draaiende service is niet automatisch de goede: hij kan van een eerdere sessie zijn, met oude code.
            // Meldt hij niet dezelfde versie als het bestand op schijf (of helemaal geen), dan stoppen we hem en starten we hem opnieuw.
            if (versie == HuidigeVersie())
            {
                return;
            }

            ActivityLog.Shared.Add("app", "De beeldservice draait met oude code - opnieuw starten.");
            FamilieServices.StopPoort(new Uri(baseUrl).Port);
        }

        var pythonExe = Path.Combine(ProjectPath, ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExe))
        {
            // De omgeving is nog niet opgezet (venv/pip install) - niets aan
            // te doen vanuit de app, dan blijft het scherm gewoon melden dat
            // de service niet bereikbaar is.
            return;
        }

        var poortTekst = new Uri(baseUrl).Port.ToString();
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExe,
            WorkingDirectory = ProjectPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("uvicorn");
        startInfo.ArgumentList.Add("service:app");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(poortTekst);

        try
        {
            _gestartProces = Process.Start(startInfo);
        }
        catch
        {
            return;
        }

        using var pollClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.Add(StartupTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsHealthyAsync(pollClient, baseUrl).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stopt de service, maar alleen als ImageFunn hem zelf gestart heeft in deze sessie.</summary>
    public static void StopIfStarted()
    {
        if (_gestartProces is null)
        {
            return;
        }

        try
        {
            if (!_gestartProces.HasExited)
            {
                _gestartProces.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Al gestopt, of we mogen er niet bij - bij het afsluiten van de
            // app is daar niets meer aan te doen.
        }
        finally
        {
            _gestartProces.Dispose();
            _gestartProces = null;
        }
    }

    /// <summary>De versie van service.py op schijf: tijdstip van de laatste wijziging, in seconden (zoals service.py hem zelf meldt).</summary>
    private static long HuidigeVersie()
    {
        var pad = Path.Combine(ProjectPath, "service.py");
        return File.Exists(pad) ? new DateTimeOffset(File.GetLastWriteTimeUtc(pad)).ToUnixTimeSeconds() : -1;
    }

    /// <summary>Is de service bereikbaar, en welke versie van de code draait hij (null als hij dat niet meldt)?</summary>
    private static async Task<(bool Gezond, long? Versie)> LeesGezondheidAsync(HttpClient client, string baseUrl)
    {
        try
        {
            using var response = await client.GetAsync($"{baseUrl.TrimEnd('/')}/health").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, null);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return (true, document.RootElement.TryGetProperty("versie", out var versie) && versie.TryGetInt64(out var waarde) ? waarde : null);
        }
        catch
        {
            return (false, null);
        }
    }

    private static async Task<bool> IsHealthyAsync(HttpClient client, string baseUrl)
    {
        try
        {
            using var response = await client.GetAsync($"{baseUrl.TrimEnd('/')}/health").ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
