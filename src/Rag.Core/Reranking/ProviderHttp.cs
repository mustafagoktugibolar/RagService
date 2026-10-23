using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rag.Core.Reranking;

/// <summary>HTTP details the adapters share. Response bodies are never read into messages: a validation error may echo the input.</summary>
internal static class ProviderHttp
{
    public static async Task<JsonObject> PostJsonAsync(
        HttpClient client, string url, string bearerToken, JsonNode body, string provider,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        foreach (var (name, value) in extraHeaders ?? [])
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            // 408, 429 and 5xx are worth retrying; everything else (400, 401, 403, 404, 422, ...) is the caller's problem.
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || status >= 500)
            {
                throw new HttpRequestException($"{provider} returned HTTP {status}.", null, response.StatusCode);
            }

            throw new RerankProviderException(provider, status);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<JsonObject>(ct)
                ?? throw new InvalidProviderResponseException($"{provider} returned an empty body.");
        }
        catch (JsonException)
        {
            throw new InvalidProviderResponseException($"{provider} returned a body that is not valid JSON.");
        }
    }

    public static string BuildUrl(string baseUrl, string path) => $"{baseUrl.TrimEnd('/')}{path}";

    public static string QueryInput(string query) => $"Search query: {query}";
}
