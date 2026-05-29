using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DoTogether.Application.DTOs;
using DoTogether.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DoTogether.Infrastructure.Services;

public sealed class GeminiRecipeLlmExtractor(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<GeminiRecipeLlmExtractor> logger) : IRecipeLlmExtractor
{
    private const int MaxPromptChars = 30_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RecipeLlmExtractionResult?> ExtractAsync(string pageText, Uri sourceUri, CancellationToken ct)
    {
        var apiKey = configuration["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var model = configuration["Gemini:Model"] ?? "gemini-1.5-flash";
        var uri = new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(apiKey)}");
        var prompt = BuildPrompt(sourceUri, pageText);

        using var response = await httpClient.PostAsJsonAsync(uri, new
        {
            generationConfig = new
            {
                temperature = 0.1,
                responseMimeType = "application/json"
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = prompt } }
                }
            }
        }, JsonOptions, ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Gemini recipe extraction failed for {SourceUri} with status {StatusCode}", sourceUri, (int)response.StatusCode);
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<GeminiResponse>(JsonOptions, ct);
        var json = payload?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(json))
            return null;

        json = StripCodeFence(json);

        try
        {
            var extracted = JsonSerializer.Deserialize<GeminiRecipeResult>(json, JsonOptions);
            if (extracted is null
                || string.IsNullOrWhiteSpace(extracted.Name)
                || extracted.Ingredients.Count == 0
                || extracted.Instructions.Count == 0)
                return null;

            return new RecipeLlmExtractionResult(
                extracted.Name.Trim(),
                string.IsNullOrWhiteSpace(extracted.Description) ? null : extracted.Description.Trim(),
                CleanList(extracted.Ingredients),
                CleanList(extracted.Instructions),
                extracted.Servings,
                extracted.YieldText,
                extracted.PrepMinutes,
                extracted.CookMinutes,
                extracted.TotalMinutes,
                extracted.Nutrition,
                extracted.ImageUrl,
                CleanList(extracted.Tags),
                extracted.Attribution);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Gemini returned invalid recipe JSON for {SourceUri}", sourceUri);
            return null;
        }
    }

    private static string BuildPrompt(Uri sourceUri, string pageText)
    {
        var text = pageText.Length > MaxPromptChars ? pageText[..MaxPromptChars] : pageText;
        return $$"""
            Extract one cooking recipe from the page text below.
            Return ONLY valid JSON with this shape:
            {
              "name": "string",
              "description": "string or null",
              "ingredients": ["raw ingredient line"],
              "instructions": ["instruction step"],
              "servings": 4,
              "yieldText": "string or null",
              "prepMinutes": 10,
              "cookMinutes": 20,
              "totalMinutes": 30,
              "nutrition": { "calories": 100, "proteinGrams": 1, "carbsGrams": 2, "fatGrams": 3 },
              "imageUrl": "absolute URL or null",
              "tags": ["tag"],
              "attribution": "author or publisher or null"
            }
            Use Russian text as-is when the recipe is Russian. Do not invent missing fields; use null.

            Source URL: {{sourceUri}}

            Page text:
            {{text}}
            """;
    }

    private static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstNewLine = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewLine >= 0 && lastFence > firstNewLine
            ? trimmed[(firstNewLine + 1)..lastFence].Trim()
            : trimmed;
    }

    private static List<string> CleanList(IEnumerable<string>? values)
        => values?.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];

    private sealed record GeminiResponse(List<GeminiCandidate>? Candidates);
    private sealed record GeminiCandidate(GeminiContent? Content);
    private sealed record GeminiContent(List<GeminiPart>? Parts);
    private sealed record GeminiPart(string? Text);

    private sealed record GeminiRecipeResult
    {
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public List<string> Ingredients { get; init; } = [];
        public List<string> Instructions { get; init; } = [];
        public int? Servings { get; init; }
        public string? YieldText { get; init; }
        public int? PrepMinutes { get; init; }
        public int? CookMinutes { get; init; }
        public int? TotalMinutes { get; init; }
        public RecipeNutritionDto? Nutrition { get; init; }
        public string? ImageUrl { get; init; }
        public List<string>? Tags { get; init; }
        public string? Attribution { get; init; }
    }
}