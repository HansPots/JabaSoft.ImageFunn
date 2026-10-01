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

    /// <summary>Start een nieuwe taak en geeft het job-id terug.</summary>
    public async Task<string> StartJobAsync(BeeldOpdracht opdracht, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync("/jobs", opdracht, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<StartJobResponse>(cancellationToken).ConfigureAwait(false);
        return body?.JobId ?? throw new InvalidOperationException("De beeldservice gaf geen job_id terug.");
    }

    /// <summary>Vraagt de huidige stand van een taak op - de aanroeper polt dit tot IsKlaar of IsMislukt.</summary>
    public async Task<BeeldJobStatus> GetStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var status = await _client.GetFromJsonAsync<BeeldJobStatus>($"/jobs/{jobId}", cancellationToken).ConfigureAwait(false);
        return status ?? throw new InvalidOperationException("De beeldservice gaf geen taakstatus terug.");
    }

    /// <summary>Alle eerder gemaakte beelden, nieuwste eerst. Overleeft een herstart van de service (zie service.py's meta.json).</summary>
    public async Task<List<BeeldJobStatus>> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        var lijst = await _client.GetFromJsonAsync<List<BeeldJobStatus>>("/jobs", cancellationToken).ConfigureAwait(false);
        return lijst ?? [];
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
