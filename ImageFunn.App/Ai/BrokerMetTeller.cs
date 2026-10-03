using System.Net.Http;
using System.Text;
using Jabasoft.Base.AiBroker;

namespace ImageFunn.App.Ai;

/// <summary>
/// Een chatvraag aan de broker waarbij de teller meeloopt, zoals in AI Studio: het TOKENS-blok telt op
/// terwijl het model antwoord geeft, en de pil in de footer toont hoever het inlezen van de vraag is.
/// Zonder dit gebeurt er zichtbaar niets terwijl het chatmodel bezig is, en lijkt de app te hangen.
///
/// Loopt altijd via de streaming-route van de broker. Dat is ook de enige route die afbeeldingen aan het
/// model meegeeft, dus één weg voor alles.
/// </summary>
public static class BrokerMetTeller
{
    /// <summary>
    /// Stelt de vraag en geeft het antwoord (zonder denkblokken) terug in hetzelfde formaat als
    /// <see cref="IAiBrokerClient.ChatAsync"/>, zodat het een directe vervanger is.
    /// </summary>
    public static async Task<ChatResult> ChatMetTellerAsync(this IAiBrokerClient broker, ChatRequest vraag, CancellationToken cancellationToken)
    {
        TokenTicker.Start(vraag.Model);
        PromptProgressTicker.Start();

        var tekst = new StringBuilder();
        string? fout = null;
        long vraagTokens = 0;
        long antwoordTokens = 0;

        try
        {
            await foreach (var stukje in broker.ChatStreamAsync(vraag, cancellationToken).ConfigureAwait(false))
            {
                if (stukje.ErrorMessage is not null)
                {
                    fout = stukje.ErrorMessage;
                    break;
                }

                // Een model dat eerst hardop denkt levert stukjes zonder tekst: ook die tellen, anders staat de teller stil.
                if (stukje.Thinking)
                {
                    TokenTicker.Add();
                }

                if (stukje.PromptProgress is { } voortgang)
                {
                    PromptProgressTicker.Update(voortgang);
                }

                if (!string.IsNullOrEmpty(stukje.Text))
                {
                    tekst.Append(stukje.Text);
                    TokenTicker.Add();
                }

                if (stukje.Done)
                {
                    vraagTokens = stukje.PromptTokens;
                    antwoordTokens = stukje.CompletionTokens;
                }
            }
        }
        catch (Exception uitzondering) when (uitzondering is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            fout = uitzondering.Message;
        }
        finally
        {
            TokenTicker.Stop(vraagTokens + antwoordTokens);
            PromptProgressTicker.Stop();
        }

        if (fout is not null)
        {
            return new ChatResult(false, string.Empty, fout, vraagTokens, antwoordTokens);
        }

        var antwoord = AiAntwoord.ZonderDenkblokken(tekst.ToString()).Trim();
        return antwoord.Length == 0
            ? new ChatResult(false, string.Empty, "Het model gaf geen antwoord.", vraagTokens, antwoordTokens)
            : new ChatResult(true, antwoord, null, vraagTokens, antwoordTokens);
    }
}
