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
    // Ruim: na een herstart van de pc staat er nog niets in de schijfcache en duurt alleen het laden van PyTorch al lang.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);

    private const long MaxLogBytes = 5_000_000;

    // Eén start tegelijk: de start bij het openen van de app en een eerste opdracht kunnen samenvallen.
    private static readonly SemaphoreSlim _startSlot = new(1, 1);

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

    /// <summary>Zorgt dat de service draait. Geeft null als dat gelukt is, en anders wat er misging.</summary>
    public static async Task<string?> EnsureRunningAsync(
        string baseUrl = ImageServiceClient.DefaultBaseUrl,
        IProgress<string>? melding = null,
        CancellationToken cancellationToken = default)
    {
        if (_startSlot.CurrentCount == 0)
        {
            melding?.Report(OpstartTekst);
        }

        await _startSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartAlsNodigAsync(baseUrl, melding, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startSlot.Release();
        }
    }

    private const string OpstartTekst = "De beeldservice start op, dit kan even duren…";

    private static async Task<string?> StartAlsNodigAsync(string baseUrl, IProgress<string>? melding, CancellationToken cancellationToken)
    {
        // Ruimer dan een gewoon antwoord: tijdens het laden van een model kan de service even op zich laten wachten.
        using var probeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var (gezond, versie) = await LeesGezondheidAsync(probeClient, baseUrl).ConfigureAwait(false);
        if (gezond)
        {
            // Een draaiende service is niet automatisch de goede: hij kan van een eerdere sessie zijn, met oude code.
            // Meldt hij niet dezelfde versie als het bestand op schijf (of helemaal geen), dan stoppen we hem en starten we hem opnieuw.
            if (versie == HuidigeVersie())
            {
                return null;
            }

            ActivityLog.Shared.Add("app", "De beeldservice draait met oude code - opnieuw starten.");
            FamilieServices.StopPoort(new Uri(baseUrl).Port);
            VergeetGestartProces();
        }

        var pythonExe = Path.Combine(ProjectPath, ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExe))
        {
            // De omgeving is nog niet opgezet (venv/pip install) - niets aan
            // te doen vanuit de app.
            return $"De Python-omgeving van de beeldservice ontbreekt ({pythonExe}).";
        }

        melding?.Report(OpstartTekst);

        // Is de vorige start nog bezig (langer dan de wachttijd), dan niet nog een: die zou de poort niet krijgen.
        if (_gestartProces is { HasExited: false })
        {
            return await WachtOpStartAsync(baseUrl, cancellationToken).ConfigureAwait(false);
        }

        VergeetGestartProces();
        BereidLogVoor();

        // Via cmd, zodat de uitvoer rechtstreeks naar het logbestand gaat: dat blijft werken als de app wegvalt.
        var poortTekst = new Uri(baseUrl).Port.ToString();
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /s /c \"\"{pythonExe}\" -m uvicorn service:app --port {poortTekst} >> \"{LogPad}\" 2>&1\"",
            WorkingDirectory = ProjectPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        // De keuzes van het instellingenscherm (ImageFunn-kaart in Jabasoft).
        // Alleen als er echt iets gekozen is: anders blijft een
        // omgevingsvariabele die al op de pc staat gewoon gelden.
        if (!string.IsNullOrWhiteSpace(Layout.Preferences.Beeldmodel))
        {
            startInfo.Environment["IMAGEFUNN_MODEL"] = Layout.Preferences.Beeldmodel;
        }

        if (Layout.Preferences.OntladenNaSeconden >= 0)
        {
            startInfo.Environment["IMAGEFUNN_ONTLAAD_NA"] = ((int)Layout.Preferences.OntladenNaSeconden).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        try
        {
            _gestartProces = Process.Start(startInfo);
        }
        catch (Exception fout)
        {
            return Meld($"De beeldservice kon niet gestart worden: {fout.Message}");
        }

        return await WachtOpStartAsync(baseUrl, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Wacht tot de service antwoordt. Null als dat gelukt is; stopt hij of duurt het te lang, dan wat er misging.</summary>
    private static async Task<string?> WachtOpStartAsync(string baseUrl, CancellationToken cancellationToken)
    {
        using var pollClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.Add(StartupTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsHealthyAsync(pollClient, baseUrl).ConfigureAwait(false))
            {
                return null;
            }

            if (_gestartProces is null or { HasExited: true })
            {
                return Meld($"De beeldservice stopte tijdens het opstarten. {LaatsteLogregels()}");
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return Meld($"De beeldservice is na {StartupTimeout.TotalMinutes:0} minuten nog niet opgestart. {LaatsteLogregels()}");
    }

    private static string Meld(string fout)
    {
        ActivityLog.Shared.Add("app", fout);
        return fout;
    }

    private static string LogPad => Path.Combine(ProjectPath, "service.log");

    /// <summary>Begint elke start met een kopregel, en begint opnieuw als het bestand te groot wordt.</summary>
    private static void BereidLogVoor()
    {
        try
        {
            if (File.Exists(LogPad) && new FileInfo(LogPad).Length > MaxLogBytes)
            {
                File.Delete(LogPad);
            }

            File.AppendAllText(LogPad, $"{Environment.NewLine}=== Gestart door ImageFunn: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch (IOException)
        {
            // Het logbestand mag het starten niet in de weg zitten.
        }
    }

    /// <summary>Het einde van de laatste start uit het logbestand, om in de foutmelding te zetten.</summary>
    private static string LaatsteLogregels()
    {
        try
        {
            using var stroom = new FileStream(LogPad, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var lezer = new StreamReader(stroom);
            var tekst = lezer.ReadToEnd();
            var start = tekst.LastIndexOf("=== Gestart door ImageFunn", StringComparison.Ordinal);
            var regels = tekst[Math.Max(start, 0)..].Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Skip(1).TakeLast(3);
            return $"Zie {LogPad}. Laatste uitvoer: {string.Join(" ", regels)}";
        }
        catch (IOException)
        {
            return $"Zie {LogPad}.";
        }
    }

    private static void VergeetGestartProces()
    {
        _gestartProces?.Dispose();
        _gestartProces = null;
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

    /// <summary>De versie van de code op schijf: de laatste wijziging van service.py of videomodellen.py, in seconden (zoals service.py hem zelf meldt).</summary>
    private static long HuidigeVersie()
    {
        var pad = Path.Combine(ProjectPath, "service.py");
        if (!File.Exists(pad))
        {
            return -1;
        }

        var laatste = new[] { pad, Path.Combine(ProjectPath, "videomodellen.py") }.Max(File.GetLastWriteTimeUtc);
        return new DateTimeOffset(laatste).ToUnixTimeSeconds();
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
