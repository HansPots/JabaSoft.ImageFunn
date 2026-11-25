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
    // Niet "localhost": uvicorn luistert alleen op 127.0.0.1, en dan probeert Windows eerst ::1. Dat kost 2 seconden per
    // nieuwe verbinding (gemeten: 2,05 s tegen 0,03 s), langer dan de statuscontrole van de starter wacht.
    public const string DefaultBaseUrl = "http://127.0.0.1:5330";

    private readonly HttpClient _client;

    public ImageServiceClient(string baseUrl = DefaultBaseUrl)
    {
        _client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Krijgt te horen dat de service voor een nieuwe opdracht eerst moet opstarten, om op het scherm te zetten.</summary>
    public IProgress<string>? Opstartmelding { get; set; }

    /// <summary>De tekst voor op het scherm bij een mislukte aanroep: niet bereikbaar is iets anders dan een geweigerde opdracht.</summary>
    public static string Foutmelding(Exception fout) => fout switch
    {
        BeeldServiceFout => fout.Message,
        HttpRequestException { StatusCode: not null } => $"De beeldservice weigerde dit: {fout.Message}",
        HttpRequestException or TaskCanceledException => $"Kon de beeldservice niet bereiken: {fout.Message}",
        _ => $"Er ging iets mis: {fout.Message}",
    };

    /// <summary>Als EnsureSuccessStatusCode, maar met wat de service zelf als reden geeft.</summary>
    private static async Task ControleerAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var reden = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
            throw new HttpRequestException(reden.Length > 300 ? reden[..300] + "…" : reden, null, response.StatusCode);
        }
    }

    /// <summary>
    /// Voert een aanroep uit en start de service als die niet bereikbaar blijkt. De service stopt zichzelf als hij een
    /// tijd niets te doen had (zie ONTLAAD_NA in service.py), en wordt dan hier weer opgestart. Meer dan één poging:
    /// de service kan net aan het stoppen zijn, en dan geeft hij nog antwoord terwijl de volgende aanroep al mislukt.
    /// Een foutantwoord van de service zelf (met statuscode) wordt niet herhaald.
    /// </summary>
    private static async Task<T> MetHerstelAsync<T>(Func<Task<T>> actie, CancellationToken cancellationToken, IProgress<string>? melding = null)
    {
        const int Pogingen = 3;
        for (var poging = 1; ; poging++)
        {
            try
            {
                return await actie().ConfigureAwait(false);
            }
            catch (HttpRequestException fout) when (fout.StatusCode is null)
            {
                if (poging == Pogingen)
                {
                    throw;
                }

                if (poging > 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }

                var startFout = await ImageServiceLauncher.EnsureRunningAsync(melding: melding, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (startFout is not null)
                {
                    throw new BeeldServiceFout(startFout, fout);
                }
            }
        }
    }

    /// <summary>Start een nieuwe taak en geeft het job-id terug.</summary>
    public async Task<string> StartJobAsync(BeeldOpdracht opdracht, CancellationToken cancellationToken = default)
    {
        // Eerst het geheugen van de GPU vrijmaken: een nog geladen chatmodel laat te weinig ruimte over voor het beeld- en videomodel.
        await Ai.ModelOntlader.OntlaadAsync().ConfigureAwait(false);
        await HerstartBijAnderModelAsync(opdracht, cancellationToken).ConfigureAwait(false);

        return await MetHerstelAsync(
            async () =>
            {
                using var response = await _client.PostAsJsonAsync("/jobs", opdracht, cancellationToken).ConfigureAwait(false);
                await ControleerAsync(response, cancellationToken).ConfigureAwait(false);

                var body = await response.Content.ReadFromJsonAsync<StartJobResponse>(cancellationToken).ConfigureAwait(false);
                return body?.JobId ?? throw new InvalidOperationException("De beeldservice gaf geen job_id terug.");
            },
            cancellationToken,
            Opstartmelding).ConfigureAwait(false);
    }

    /// <summary>
    /// Laat de service opnieuw starten als er een ander model geladen is dan deze opdracht wil. PyTorch geeft het
    /// werkgeheugen pas terug als het proces stopt (zie ONTLAAD_NA in service.py); bij wisselen binnen één proces stapelt
    /// het op tot het virtuele geheugen van Windows vol is en de service crasht. Loopt er een opdracht, dan gebeurt er niets.
    /// </summary>
    private async Task HerstartBijAnderModelAsync(BeeldOpdracht opdracht, CancellationToken cancellationToken)
    {
        var gezond = await GetHealthAsync(cancellationToken).ConfigureAwait(false);
        if (gezond is null || !HeeftAnderModel(gezond, opdracht))
        {
            return;
        }

        Opstartmelding?.Report("Ander model: de beeldservice start opnieuw om het geheugen vrij te maken…");
        if (!await MaakVideogeheugenVrijAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // De service stopt kort na zijn antwoord; een opdracht die daar nog tussenkomt zou mee verdwijnen.
        for (var i = 0; i < 20 && await GetHealthAsync(cancellationToken).ConfigureAwait(false) is not null; i++)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool HeeftAnderModel(BeeldServiceGezondheid gezond, BeeldOpdracht opdracht)
    {
        var wilBeeld = opdracht.Soort != "video";
        var wilVideo = opdracht.Soort is "video" or "film";
        var beeldmodel = string.IsNullOrEmpty(opdracht.Beeldmodel) ? gezond.Model : opdracht.Beeldmodel;

        // Bijsturen kan niet met FLUX: de service gebruikt dan RealVisXL (zie _draai_bewerk in service.py).
        if (opdracht.Soort == "beeld" && opdracht.BronBeeld is not null && beeldmodel == "flux")
        {
            beeldmodel = "realvis";
        }

        return gezond.ModelGeladen && (!wilBeeld || gezond.BeeldModel != beeldmodel)
               || gezond.VideoModelGeladen && (!wilVideo || gezond.VideoModel != opdracht.Videomodel);
    }

    /// <summary>
    /// Vraagt de huidige stand van een taak op - de aanroeper polt dit tot IsKlaar of IsMislukt. Blijft de service
    /// onbereikbaar, dan is hij tijdens de opdracht gestopt en is die verloren: dat zegt de foutmelding dan ook.
    /// </summary>
    public async Task<BeeldJobStatus> GetStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        const int Pogingen = 3;
        for (var poging = 1; ; poging++)
        {
            try
            {
                var status = await _client.GetFromJsonAsync<BeeldJobStatus>($"/jobs/{jobId}", cancellationToken).ConfigureAwait(false);
                return status ?? throw new InvalidOperationException("De beeldservice gaf geen taakstatus terug.");
            }
            catch (HttpRequestException fout) when (fout.StatusCode is null)
            {
                if (poging == Pogingen)
                {
                    throw new BeeldServiceFout(
                        "De beeldservice is tijdens deze opdracht gestopt, waarschijnlijk doordat het geheugen vol raakte. Probeer het opnieuw; de service start dan vanzelf weer.",
                        fout);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
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
                await ControleerAsync(response, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Vraagt een lopende taak te stoppen (bij een film na de huidige stap; de al gemaakte scènes blijven bewaard).</summary>
    public async Task StopJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsync($"/jobs/{jobId}/stop", null, cancellationToken).ConfigureAwait(false);
        await ControleerAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Vraagt de service zijn modellen los te laten, zodat er videogeheugen vrij
    /// is voor iets anders - het beeldmodel van LM Studio bij Beeld naar tekst.
    /// De service stopt daarbij zichzelf en start bij de volgende opdracht weer.
    /// Geeft false als dat nu niet kan omdat er een opdracht loopt; draait de
    /// service niet, dan is er niets vrij te maken en is het antwoord true.
    /// </summary>
    public async Task<bool> MaakVideogeheugenVrijAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _client.PostAsync("/ontladen", null, cancellationToken).ConfigureAwait(false);
            return response.StatusCode != System.Net.HttpStatusCode.Conflict;
        }
        catch (Exception fout) when (fout is HttpRequestException or TaskCanceledException)
        {
            return true;
        }
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

/// <summary>Een fout van de beeldservice met een tekst die zo op het scherm kan.</summary>
public sealed class BeeldServiceFout(string melding, Exception? oorzaak = null) : Exception(melding, oorzaak);
