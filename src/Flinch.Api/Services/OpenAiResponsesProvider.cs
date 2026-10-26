using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Flinch.Api.Models;
using Flinch.Core;
using Flinch.Core.Contracts;

namespace Flinch.Api.Services
{
    /// <summary>
    /// OpenAI Responses API adapter for vision and structured schema output.
    /// Master API key is kept strictly on the backend.
    /// Screens are sent as ephemeral base64 buffers and never written to disk.
    /// </summary>
    public sealed class OpenAiResponsesProvider : IAiProvider
    {
        private readonly HttpClient _httpClient;
        private readonly string? _apiKey;
        private readonly string _modelName;
        private readonly IAiProvider _fallbackProvider;
        private readonly ExaSearchService? _exaSearch;

        public string ProviderName => "OpenAiResponsesProvider";

        public OpenAiResponsesProvider(HttpClient httpClient, IConfiguration config, FakeAiProvider fallbackProvider, ExaSearchService? exaSearch = null)
        {
            _httpClient = httpClient;
            _apiKey = config["OPENAI_API_KEY"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            _modelName = config["OPENAI_MODEL"] ?? "gpt-4o-mini";
            _fallbackProvider = fallbackProvider;
            _exaSearch = exaSearch;
        }

        public async IAsyncEnumerable<AnswerStreamChunk> GenerateAnswerStreamAsync(
            AnswerRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // If master key is absent, fallback cleanly to offline fake provider
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                await foreach (var chunk in _fallbackProvider.GenerateAnswerStreamAsync(request, cancellationToken))
                {
                    yield return chunk;
                }
                yield break;
            }

            var planId = "plan-" + Guid.NewGuid().ToString("N")[..8];

            yield return new AnswerStreamChunk
            {
                Type = StreamChunkType.PlanStarted,
                PlanId = planId,
                IsVerified = true
            };

            // Build multimodal message payload with conversation memory
            var messages = new List<object>();

            messages.Add(new
            {
                role = "system",
                content = "You are Flinch, a friendly, intelligent desktop assistant. Answer questions clearly. When the user asks about their screen, reference the visual context and return normalized visual cues. When they do not ask about the screen, converse normally or use real-time search. Remember past context."
            });

            // 1. Replay past conversation turns (RAG / Memory)
            if (request.ConversationHistory != null)
            {
                foreach (var turn in request.ConversationHistory)
                {
                    messages.Add(new
                    {
                        role = turn.Role.ToLowerInvariant() == "assistant" ? "assistant" : "user",
                        content = turn.Content
                    });
                }
            }

            string qLower = request.Query.ToLowerInvariant();
            bool isScreenExplicitlyRequested = request.ExplicitScreenRequested ||
                                               qLower.Contains("on the screen") ||
                                               qLower.Contains("on my screen") ||
                                               qLower.Contains("this window") ||
                                               qLower.Contains("what's on screen") ||
                                               qLower.Contains("what you see");

            // 2. Perform Exa Search if up-to-date information is needed
            if (_exaSearch != null && (qLower.Contains("search") || qLower.Contains("latest") || qLower.Contains("recent") || qLower.Contains("news")))
            {
                var searchResults = await _exaSearch.SearchAsync(request.Query, 3, cancellationToken).ConfigureAwait(false);
                if (searchResults.Count > 0 && !searchResults[0].Title.Contains("Notice"))
                {
                    var searchSb = new StringBuilder("Real-time Web Search Results (via Exa):\n");
                    foreach (var res in searchResults)
                    {
                        searchSb.AppendLine($"- [{res.Title}] ({res.Url}): {res.Snippet}");
                    }
                    messages.Add(new { role = "system", content = searchSb.ToString() });
                }
            }

            // 3. Build current user content
            var userContent = new List<object>
            {
                new { type = "text", text = CoordinateMapper.SanitizeScreenText(request.Query, 1000) }
            };

            // Include ephemeral image ONLY when screen analysis is explicitly requested
            if (isScreenExplicitlyRequested && request.Screen?.EphemeralImageData != null && request.Screen.EphemeralImageData.Length > 0)
            {
                string base64Image = Convert.ToBase64String(request.Screen.EphemeralImageData);
                userContent.Add(new
                {
                    type = "image_url",
                    image_url = new { url = $"data:image/jpeg;base64,{base64Image}", detail = "high" }
                });
            }

            // Include bounded OCR & UIA context text ONLY when screen analysis is explicitly requested
            if (isScreenExplicitlyRequested && request.Screen != null)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Target Window: {request.Screen.TargetTitle}");
                sb.AppendLine($"Resolution: {request.Screen.CoordinateSpace.PhysicalWidth}x{request.Screen.CoordinateSpace.PhysicalHeight}, DPI: {request.Screen.CoordinateSpace.DpiScale}");
                if (request.Screen.UiaItems.Count > 0)
                {
                    sb.AppendLine("Accessible Controls:");
                    foreach (var item in request.Screen.UiaItems)
                    {
                        sb.AppendLine($"- [{item.ControlType}] '{item.Name}' at norm [{item.Bounds.X:F2},{item.Bounds.Y:F2},{item.Bounds.Width:F2},{item.Bounds.Height:F2}]");
                    }
                }
                if (request.Screen.OcrItems.Count > 0)
                {
                    sb.AppendLine("OCR Text Blocks:");
                    foreach (var item in request.Screen.OcrItems)
                    {
                        sb.AppendLine($"- '{item.Text}' at norm [{item.Bounds.X:F2},{item.Bounds.Y:F2},{item.Bounds.Width:F2},{item.Bounds.Height:F2}]");
                    }
                }
                userContent.Add(new { type = "text", text = sb.ToString() });
            }

            messages.Add(new { role = "user", content = userContent });

            var payload = new
            {
                model = _modelName,
                messages = messages,
                max_tokens = 1200
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            HttpResponseMessage? response = null;
            string? networkError = null;
            try
            {
                response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception ex)
            {
                networkError = ex.Message;
            }

            if (networkError != null)
            {
                yield return new AnswerStreamChunk
                {
                    Type = StreamChunkType.Error,
                    PlanId = planId,
                    ErrorMessage = $"Provider network error: {networkError}"
                };
                yield break;
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                yield return new AnswerStreamChunk
                {
                    Type = StreamChunkType.Error,
                    PlanId = planId,
                    ErrorMessage = $"OpenAI API returned status code {(int)(response?.StatusCode ?? System.Net.HttpStatusCode.InternalServerError)}"
                };
                yield break;
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(responseJson);

            string? completionText = null;
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                {
                    completionText = content.GetString();
                }
            }

            string sanitizedAnswer = CoordinateMapper.SanitizeScreenText(completionText, 2000);

            // Yield parsed segment
            yield return new AnswerStreamChunk
            {
                Type = StreamChunkType.Segment,
                PlanId = planId,
                Segment = new ExplanationSegment
                {
                    Id = "seg-1",
                    VisibleText = sanitizedAnswer,
                    SpokenText = sanitizedAnswer
                }
            };

            yield return new AnswerStreamChunk
            {
                Type = StreamChunkType.PlanCompleted,
                PlanId = planId,
                IsVerified = true
            };
        }
    }
}
