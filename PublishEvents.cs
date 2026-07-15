using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MatchZy;

public partial class MatchZy
{
    private static readonly HttpClient EventHttpClient = CreateEventHttpClient();

    private static HttpClient CreateEventHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task SendEventAsync(MatchZyEvent @event)
    {
        if (string.IsNullOrWhiteSpace(matchConfig.RemoteLogURL)) return;

        string payload = JsonSerializer.Serialize(@event, @event.GetType());
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, matchConfig.RemoteLogURL)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrWhiteSpace(matchConfig.RemoteLogHeaderKey) &&
                    !string.IsNullOrWhiteSpace(matchConfig.RemoteLogHeaderValue))
                {
                    request.Headers.TryAddWithoutValidation(
                        matchConfig.RemoteLogHeaderKey,
                        matchConfig.RemoteLogHeaderValue);
                }

                using HttpResponseMessage response = await EventHttpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    Log($"[SendEventAsync] { @event.EventName } event { @event.EventId } delivered on attempt {attempt}.");
                    return;
                }

                bool retryable = response.StatusCode == HttpStatusCode.RequestTimeout ||
                    (int)response.StatusCode >= 500;

                Log($"[SendEventAsync] { @event.EventName } event { @event.EventId } failed with {(int)response.StatusCode} on attempt {attempt}/{maxAttempts}.");
                if (!retryable || attempt == maxAttempts) return;
            }
            catch (OperationCanceledException) when (attempt < maxAttempts)
            {
                Log($"[SendEventAsync] { @event.EventName } event { @event.EventId } timed out on attempt {attempt}/{maxAttempts}.");
            }
            catch (HttpRequestException exception) when (attempt < maxAttempts)
            {
                Log($"[SendEventAsync] { @event.EventName } event { @event.EventId } network error on attempt {attempt}/{maxAttempts}: {exception.Message}");
            }
            catch (Exception exception)
            {
                Log($"[SendEventAsync] { @event.EventName } event { @event.EventId } failed: {exception.Message}");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(attempt));
        }
    }
}
