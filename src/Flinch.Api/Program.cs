using System;
using System.IO;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Flinch.Api.Middleware;
using Flinch.Api.Models;
using Flinch.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Register HTTP Client, Search, Transcription, and AI Providers
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ExaSearchService>();
builder.Services.AddSingleton<VoiceTranscriptionService>();
builder.Services.AddSingleton<Flinch.Core.MathSolverService>();
builder.Services.AddSingleton<FakeAiProvider>();
builder.Services.AddSingleton<OpenAiResponsesProvider>();

// Register IAiProvider: Default to OpenAiResponsesProvider (which automatically falls back to FakeAiProvider if no key)
builder.Services.AddSingleton<IAiProvider>(sp =>
{
    var openAi = sp.GetRequiredService<OpenAiResponsesProvider>();
    return openAi;
});

// Configure Rate Limiting: 30 requests per minute per IP / client key
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("AnswerApiPolicy", opt =>
    {
        opt.PermitLimit = 30;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 2;
    });
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin();
    });
});

var app = builder.Build();

app.UseCors();
app.UseRateLimiter();

// Client Authentication Middleware
app.Use(async (context, next) =>
{
    // Health check is public
    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    var expectedKey = app.Configuration["FLINCH_CLIENT_KEY"] ?? "flinch-desktop-dev-token";
    var clientKey = context.Request.Headers["X-Flinch-Client-Key"].ToString();

    if (string.IsNullOrWhiteSpace(clientKey) || clientKey != expectedKey)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized: Missing or invalid X-Flinch-Client-Key header." });
        return;
    }

    await next();
});

// Health Endpoint
app.MapGet("/health", (IAiProvider provider) =>
{
    return Results.Ok(new
    {
        status = "Healthy",
        provider = provider.ProviderName,
        timestamp = DateTime.UtcNow
    });
});

// Verified Math Endpoint (/api/math/solve)
app.MapPost("/api/math/solve", async (
    MathSolveRequest request,
    Flinch.Core.MathSolverService solver,
    HttpContext httpContext) =>
{
    if (string.IsNullOrWhiteSpace(request.Expression))
    {
        return Results.BadRequest(new { error = "Mathematical expression cannot be empty." });
    }

    var result = await solver.SolveAsync(request.Expression, httpContext.RequestAborted);
    return Results.Ok(result);
}).RequireRateLimiting("AnswerApiPolicy");

// Exa Semantic Web Search Endpoint (/api/search)
app.MapPost("/api/search", async (
    ExaSearchRequest request,
    ExaSearchService searchService,
    HttpContext httpContext) =>
{
    if (string.IsNullOrWhiteSpace(request.Query))
    {
        return Results.BadRequest(new { error = "Search query cannot be empty." });
    }

    var results = await searchService.SearchAsync(request.Query, request.NumResults, httpContext.RequestAborted);
    return Results.Ok(results);
}).RequireRateLimiting("AnswerApiPolicy");

// Voice Transcription Endpoint (/api/voice/transcribe) powered by Whisper-1
app.MapPost("/api/voice/transcribe", async (
    HttpRequest httpRequest,
    VoiceTranscriptionService transcriptionService,
    HttpContext httpContext) =>
{
    using var ms = new MemoryStream();
    await httpRequest.Body.CopyToAsync(ms, httpContext.RequestAborted);
    byte[] audioBytes = ms.ToArray();

    var result = await transcriptionService.TranscribeAudioAsync(audioBytes, "audio.wav", httpContext.RequestAborted);
    return Results.Ok(result);
}).RequireRateLimiting("AnswerApiPolicy");

// Main Streaming Answer Endpoint (/api/answer)
app.MapPost("/api/answer", async (
    AnswerRequest request,
    IAiProvider provider,
    HttpContext httpContext) =>
{
    if (string.IsNullOrWhiteSpace(request.Query))
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(new { error = "Query cannot be empty." });
        return;
    }

    httpContext.Response.ContentType = "application/x-ndjson";
    httpContext.Response.Headers.Append("Cache-Control", "no-cache");
    httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

    var cancellationToken = httpContext.RequestAborted;
    int shapeCount = 0;

    try
    {
        await foreach (var rawChunk in provider.GenerateAnswerStreamAsync(request, cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var validatedChunk = ValidationHelper.ValidateAndSanitizeChunk(rawChunk, ref shapeCount);
            string jsonLine = JsonSerializer.Serialize(validatedChunk) + "\n";
            await httpContext.Response.WriteAsync(jsonLine, cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);
        }
    }
    catch (OperationCanceledException)
    {
        // Clean disconnect on client cancellation
    }
    finally
    {
        // Invariant: Promptly dispose ephemeral screen byte buffer
        if (request.Screen?.EphemeralImageData != null)
        {
            Array.Clear(request.Screen.EphemeralImageData, 0, request.Screen.EphemeralImageData.Length);
            request.Screen.EphemeralImageData = null;
        }

        await httpContext.Response.CompleteAsync();
    }
}).RequireRateLimiting("AnswerApiPolicy");

app.Run();

// Required for WebApplicationFactory in integration tests
public partial class Program { }
