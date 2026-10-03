using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ImageFunn.App.ImageService;

/// <summary>
/// Praat met de lokale Python beeldservice (service.py) - dezelfde rol als
/// AiBrokerClient voor de AI-broker, maar dan voor het genereren van beelden.
/// Elke aanroep is gewoon HTTP naar localhost; wat er in Python gebeurt is
/// deze klasse niet aan.
/// </summary>
public sealed class ImageServiceClient
{
    public const string DefaultBaseUrl = "http://localhost:5330";

    private readonly HttpClient _client;

    public ImageServiceClient(string baseUrl = DefaultBaseUrl)
    {
        _client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Voert een aanroep uit en start de service als die niet bereikbaar blijkt. De service stopt zichzelf als hij een
    /// tijd niets te doen had (zie ONTLAAD_NA in service.py), en wordt dan hier weer opgestart.
    /// </summary>
    private static async Task<T> MetHerstelAsync<T>(Func<Task<T>> actie, CancellationToken cancellationToken)
    {
        try
        {
            return await actie().ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await ImageServiceLauncher.EnsureRunningAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return await actie().ConfigureAwait(false);
        }
    }

    /// <summary>Start een nieuwe taak en geeft het job-id terug.</summary>
    public async Task<string> StartJobAsync(BeeldOpdracht opdracht, CancellationToken cancellationToken = default)
    {
        // Eerst het geheugen van de GPU vrijmaken: een nog geladen chatmodel laat te weinig ruimte over voor het beeld- en videomodel.
        await Ai.ModelOntlader.OntlaadAsync().ConfigureAwait(false);

        return await MetHerstelAsync(
            async () =>
            {
                using var response = await _client.PostAsJsonAsync("/jobs", opdracht, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var body = await response.Content.ReadFromJsonAsync<StartJobResponse>(cancellationToken).ConfigureAwait(false);
                return body?.JobId ?? throw new InvalidOperationException("De beeldservice gaf geen job_id terug.");
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Vraagt de huidige stand van een taak op - de aanroeper polt dit tot IsKlaar of IsMislukt.</summary>
    public async Task<BeeldJobStatus> GetStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var status = await _client.GetFromJsonAsync<BeeldJobStatus>($"/jobs/{jobId}", cancellationToken).ConfigureAwait(false);
        return status ?? throw new InvalidOperationException("De beeldservice gaf geen taakstatus terug.");
    }

    /// <summary>Alle eerder gemaakte beelden (soort "beeld") of video's (soort "video"), nieuwste eerst. Overleeft een herstart van de service (zie meta.json in service.py).</summary>
    public async Task<List<BeeldJobStatus>> ListJobsAsync(string soort = "beeld", CancellationToken cancellationToken = default)
    {
        var lijst = await MetHerstelAsync(
            () => _client.GetFromJsonAsync<List<BeeldJobStatus>>($"/jobs?soort={soort}", cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return lijst ?? [];
    }

    /// <summary>Verwijdert een afgeronde opdracht met alle bestanden. Dit kan niet ongedaan gemaakt worden.</summary>
    public async Task DeleteJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await MetHerstelAsync(
            async () =>
            {
                using var response = await _client.DeleteAsync($"/jobs/{jobId}", cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Vraagt een lopende taak te stoppen (bij een film na de huidige stap; de al gemaakte scènes blijven bewaard).</summary>
    public async Task StopJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsync($"/jobs/{jobId}/stop", null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Is de service bereikbaar, en heeft hij een GPU?</summary>
    public async Task<BeeldServiceGezondheid?> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _client.GetFromJsonAsync<BeeldServiceGezondheid>("/health", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class StartJobResponse
    {
        [JsonPropertyName("job_id")]
        public string? JobId { get; set; }
    }
}
