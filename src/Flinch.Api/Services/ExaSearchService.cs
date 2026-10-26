using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Flinch.Api.Services
{
    public sealed class ExaSearchResult
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Snippet { get; set; } = string.Empty;
        public string? PublishedDate { get; set; }
    }

    /// <summary>
    /// Service integration for Exa.ai real-time semantic web search.
    /// Provides up-to-date web information to prevent outdated responses.
    /// Master key EXA_API_KEY is configured on the backend.
    /// </summary>
    public sealed class ExaSearchService
    {
        private readonly HttpClient _httpClient;
        private readonly string? _apiKey;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        public ExaSearchService(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _apiKey = config["EXA_API_KEY"] ?? Environment.GetEnvironmentVariable("EXA_API_KEY");
        }

        public async Task<List<ExaSearchResult>> SearchAsync(string query, int numResults = 3, CancellationToken ct = default)
        {
            var results = new List<ExaSearchResult>();
            if (string.IsNullOrWhiteSpace(query))
                return results;

            if (!IsConfigured)
            {
                // Fallback notice when running without external EXA key
                results.Add(new ExaSearchResult
                {
                    Title = "Exa Search (Offline Notice)",
                    Url = "https://exa.ai",
                    Snippet = "EXA_API_KEY not configured on server. Configure EXA_API_KEY in environment variables or appsettings.json for live web results."
                });
                return results;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.exa.ai/search");
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var payload = new
                {
                    query = query,
                    numResults = Math.Clamp(numResults, 1, 10),
                    useAutoprompt = true,
                    contents = new
                    {
                        text = new { maxCharacters = 400 }
                    }
                };

                request.Content = JsonContent.Create(payload);

                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return results;

                var jsonDoc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false);
                if (jsonDoc != null && jsonDoc.RootElement.TryGetProperty("results", out var resultsArr))
                {
                    foreach (var item in resultsArr.EnumerateArray())
                    {
                        string title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                        string url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        string text = item.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                        string? date = item.TryGetProperty("publishedDate", out var d) ? d.GetString() : null;

                        results.Add(new ExaSearchResult
                        {
                            Title = title,
                            Url = url,
                            Snippet = text,
                            PublishedDate = date
                        });
                    }
                }
            }
            catch
            {
                // Graceful fallback on network or API failure
            }

            return results;
        }
    }
}
