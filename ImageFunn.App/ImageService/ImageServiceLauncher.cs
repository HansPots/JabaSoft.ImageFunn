using System.Diagnostics;
using System.IO;
using System.Net.Http;

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

    /// <summary>De map met .venv en service.py - naast het ImageFunn.App-project, niet erin.</summary>
    private static readonly string ProjectPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ImageFunn.Image"));

    private static Process? _gestartProces;

    public static async Task EnsureRunningAsync(
        string baseUrl = ImageServiceClient.DefaultBaseUrl,
        CancellationToken cancellationToken = default)
    {
        using var probeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        if (await IsHealthyAsync(probeClient, baseUrl).ConfigureAwait(false))
        {
            return;
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
