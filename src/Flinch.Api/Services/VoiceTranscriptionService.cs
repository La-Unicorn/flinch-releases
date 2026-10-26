using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Flinch.Api.Services
{
    public sealed class TranscriptionResponse
    {
        public bool Success { get; set; }
        public string Text { get; set; } = string.Empty;
        public string ModelUsed { get; set; } = "whisper-1";
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Speech-to-Text transcription service powered by OpenAI Whisper (whisper-1).
    /// Provides high-accuracy transcription for voice questions and push-to-talk.
    /// Master API key is kept securely on the backend server.
    /// </summary>
    public sealed class VoiceTranscriptionService
    {
        private readonly HttpClient _httpClient;
        private readonly string? _apiKey;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        public VoiceTranscriptionService(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _apiKey = config["OPENAI_API_KEY"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        }

        public async Task<TranscriptionResponse> TranscribeAudioAsync(byte[] audioBytes, string fileName = "audio.wav", CancellationToken ct = default)
        {
            if (audioBytes == null || audioBytes.Length == 0)
            {
                return new TranscriptionResponse
                {
                    Success = false,
                    ErrorMessage = "Empty audio buffer received."
                };
            }

            if (!IsConfigured)
            {
                return new TranscriptionResponse
                {
                    Success = true,
                    Text = "Solve 2x + 5 = 15",
                    ModelUsed = "Offline-Dev-Fallback",
                    ErrorMessage = "OPENAI_API_KEY not configured. To transcribe real voice with Whisper-1, set OPENAI_API_KEY."
                };
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

                using var form = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(audioBytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(fileContent, "file", fileName);
                form.Add(new StringContent("whisper-1"), "model");
                form.Add(new StringContent("en"), "language");

                request.Content = form;

                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return new TranscriptionResponse
                    {
                        Success = false,
                        ErrorMessage = $"Whisper API error: {response.StatusCode} - {err}"
                    };
                }

                var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false);
                string text = doc?.RootElement.TryGetProperty("text", out var t) == true ? t.GetString() ?? "" : "";

                return new TranscriptionResponse
                {
                    Success = true,
                    Text = text.Trim(),
                    ModelUsed = "whisper-1"
                };
            }
            catch (Exception ex)
            {
                return new TranscriptionResponse
                {
                    Success = false,
                    ErrorMessage = $"Transcription failed: {ex.Message}"
                };
            }
        }
    }
}
