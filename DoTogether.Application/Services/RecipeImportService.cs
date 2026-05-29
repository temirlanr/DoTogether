using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using AngleSharp.Html.Parser;
using DoTogether.Application.DTOs;
using DoTogether.Application.Exceptions;
using DoTogether.Application.Interfaces;
using DoTogether.Domain.Enums;

namespace DoTogether.Application.Services;

public partial class RecipeImportService(HttpClient httpClient, HouseholdAccessService householdAccess, IRecipeLlmExtractor llmExtractor)
{
    private const int MaxRedirects = 5;
    private const long MaxResponseBytes = 4 * 1024 * 1024; // 4 MB hard cap on body
    private const int MaxUnitTokenLength = 3;
    private static readonly string[] BrowserUserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_4) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15",
        "Mozilla/5.0 (X11; Linux x86_64; rv:125.0) Gecko/20100101 Firefox/125.0"
    ];
    private static readonly HashSet<string> NavigationSectionLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "About Us",
        "Cuisines",
        "Dinners",
        "Features",
        "Kitchen Tips",
        "Meals",
        "News",
        "Occasions",
        "Recipes"
    };
    private static readonly HtmlParser HtmlParser = new();
    private static readonly IReadOnlyDictionary<string, string> UnitAliases = BuildUnitAliases();

    static RecipeImportService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<RecipeImportPreviewDto> PreviewAsync(
        Guid householdId,
        Guid userId,
        RecipeImportRequestDto request,
        CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, userId, ct);

        var sourceUri = await ValidateImportUriAsync(request.Url, ct);
        var failures = new List<ImportFailure>();

        var directContent = await TryFetchDirectContentAsync(sourceUri, failures, ct);
        if (directContent is not null)
        {
            var directPreview = await TryBuildPreviewAsync(directContent, false, ct);
            if (directPreview is not null)
                return directPreview;
        }

        var jinaContent = await TryFetchJinaReaderContentAsync(sourceUri, failures, ct);
        if (jinaContent is not null)
        {
            var jinaPreview = await TryBuildPreviewAsync(jinaContent, true, ct);
            if (jinaPreview is not null)
                return jinaPreview;
        }

        if (directContent is not null)
        {
            var directPreview = await TryBuildPreviewAsync(directContent, true, ct);
            if (directPreview is not null)
                return directPreview;
        }

        if (failures.Any(f => f.Code == "recipe_import_fetch_blocked"))
        {
            throw ApiProblemException.BadGateway(
                "recipe_import_fetch_blocked",
                "The source site blocked or rate-limited server-side recipe import, and proxy fallback did not return a usable recipe.");
        }

        if (failures.Any(f => f.Code == "recipe_import_fetch_failed"))
        {
            throw ApiProblemException.BadGateway(
                "recipe_import_fetch_failed",
                "The recipe page could not be fetched from the server or proxy fallback.");
        }

        throw ApiProblemException.Unprocessable(
            "recipe_import_parse_failed",
            "No supported recipe metadata or readable recipe content was found on the source page.");
    }

    private async Task<RecipePageContent?> TryFetchDirectContentAsync(Uri sourceUri, List<ImportFailure> failures, CancellationToken ct)
    {
        try
        {
            var fetchResult = await SendImportRequestAsync(sourceUri, ct);
            using var response = fetchResult.Response;
            var effectiveUri = fetchResult.EffectiveUri;

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
            {
                failures.Add(new ImportFailure("recipe_import_fetch_blocked"));
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                failures.Add(new ImportFailure("recipe_import_fetch_failed"));
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(new ImportFailure("recipe_import_parse_failed"));
                return null;
            }

            if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > MaxResponseBytes)
            {
                failures.Add(new ImportFailure("recipe_import_too_large"));
                return null;
            }

            var html = await ReadBoundedStringAsync(response, ct);
            return new RecipePageContent(effectiveUri, html, true, "direct");
        }
        catch (ApiProblemException ex) when (ex.StatusCode is 502 or 422)
        {
            failures.Add(new ImportFailure(ex.ErrorCode));
            return null;
        }
    }

    private async Task<RecipePageContent?> TryFetchJinaReaderContentAsync(Uri sourceUri, List<ImportFailure> failures, CancellationToken ct)
    {
        var readerUri = new Uri($"https://r.jina.ai/http://{sourceUri}");
        using var message = new HttpRequestMessage(HttpMethod.Get, readerUri);
        AddBrowserHeaders(message, sourceUri.Host);
        message.Headers.Accept.Clear();
        message.Headers.Accept.ParseAdd("text/markdown,text/plain,*/*;q=0.5");

        try
        {
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                failures.Add(new ImportFailure(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests
                    ? "recipe_import_fetch_blocked"
                    : "recipe_import_fetch_failed"));
                return null;
            }

            if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > MaxResponseBytes)
            {
                failures.Add(new ImportFailure("recipe_import_too_large"));
                return null;
            }

            var markdown = await ReadBoundedStringAsync(response, ct);
            return new RecipePageContent(sourceUri, markdown, false, "jina_reader");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            failures.Add(new ImportFailure("recipe_import_fetch_failed"));
            return null;
        }
        catch (HttpRequestException)
        {
            failures.Add(new ImportFailure("recipe_import_fetch_failed"));
            return null;
        }
    }

    private async Task<RecipeImportPreviewDto?> TryBuildPreviewAsync(RecipePageContent content, bool allowTextFallbacks, CancellationToken ct)
    {
        string pageText;
        string? title = null;

        if (content.IsHtml)
        {
            var document = await HtmlParser.ParseDocumentAsync(content.Text);
            title = document.Title;
            var scripts = document
                .QuerySelectorAll("script[type='application/ld+json']")
                .Select(node => node.TextContent)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();

            var candidate = ParseBestRecipe(scripts, content.EffectiveUri, title);
            if (candidate is not null)
                return BuildPreview(content.EffectiveUri, candidate);

            pageText = ExtractVisibleText(document.Body?.TextContent ?? content.Text);
        }
        else
        {
            title = ExtractTitleFromReaderText(content.Text);
            pageText = ExtractReaderMarkdownBody(content.Text);
        }

        if (!allowTextFallbacks)
            return null;

        var heuristicCandidate = BuildHeuristicCandidate(pageText, content.EffectiveUri, title, content.Strategy);
        if (heuristicCandidate is not null)
            return BuildPreview(content.EffectiveUri, heuristicCandidate);

        var llmResult = await llmExtractor.ExtractAsync(pageText, content.EffectiveUri, ct);
        var llmCandidate = llmResult is null ? null : BuildLlmCandidate(llmResult, content.EffectiveUri, content.Strategy);
        return llmCandidate is null ? null : BuildPreview(content.EffectiveUri, llmCandidate);
    }

    private async Task<ImportFetchResult> SendImportRequestAsync(Uri sourceUri, CancellationToken ct)
    {
        var currentUri = sourceUri;

        for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, currentUri);
            AddBrowserHeaders(message, currentUri.Host);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw ApiProblemException.BadGateway(
                    "recipe_import_fetch_failed",
                    "The recipe page timed out during server-side import.");
            }
            catch (HttpRequestException)
            {
                throw ApiProblemException.BadGateway(
                    "recipe_import_fetch_failed",
                    "The recipe page could not be fetched from the server.");
            }

            if (!IsRedirectStatusCode(response.StatusCode))
                return new ImportFetchResult(currentUri, response);

            response.Dispose();

            if (redirectCount == MaxRedirects)
            {
                throw ApiProblemException.BadGateway(
                    "recipe_import_fetch_failed",
                    "The recipe source redirected too many times during import.");
            }

            if (response.Headers.Location is null)
            {
                throw ApiProblemException.BadGateway(
                    "recipe_import_fetch_failed",
                    "The recipe source redirected without a valid target URL.");
            }

            var nextUri = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(currentUri, response.Headers.Location);

            currentUri = await ValidateImportUriAsync(nextUri.ToString(), ct);
        }

        throw ApiProblemException.BadGateway(
            "recipe_import_fetch_failed",
            "The recipe source redirected too many times during import.");
    }

    private static void AddBrowserHeaders(HttpRequestMessage message, string host)
    {
        message.Headers.UserAgent.ParseAdd(PickBrowserUserAgent(host));
        message.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        message.Headers.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
        message.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
    }

    private static string PickBrowserUserAgent(string host)
    {
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(host);
        var index = Math.Abs(hash % BrowserUserAgents.Length);
        return BrowserUserAgents[index];
    }

    private static RecipeImportPreviewDto BuildPreview(Uri effectiveUri, ParsedRecipeCandidate candidate)
        => new(
            effectiveUri.ToString(),
            effectiveUri.Host.ToLowerInvariant(),
            candidate.Confidence,
            candidate.RequiresManualReview,
            candidate.Warnings,
            candidate.Recipe);

    private static ParsedRecipeCandidate? BuildHeuristicCandidate(string pageText, Uri sourceUri, string? title, string strategy)
    {
        var lines = NormalizeTextLines(pageText);
        if (lines.Count == 0)
            return null;

        title = string.IsNullOrWhiteSpace(title) ? ExtractTitleFromLines(lines) : title.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var ingredients = ExtractSectionLines(lines, IsIngredientHeading, IsInstructionHeading, 120)
            .Where(IsLikelyIngredientLine)
            .Select(ParseIngredientLine)
            .ToList();
        var instructions = ExtractSectionLines(lines, IsInstructionHeading, IsIngredientHeading, 120)
            .Where(IsLikelyInstructionLine)
            .Select(line => new RecipeInstructionInputDto(line, null))
            .ToList();

        if (ingredients.Count == 0 || instructions.Count == 0)
            return null;

        var warnings = new List<RecipeImportWarningDto>
        {
            new("heuristic_recipe_import", "Recipe content was extracted from page text and needs manual review."),
            new("import_strategy", strategy == "jina_reader"
                ? "The source page was imported through the Jina Reader fallback."
                : "The source page did not expose Recipe JSON-LD and was parsed heuristically.")
        };

        if (ingredients.Any(i => i.Quantity is null || string.IsNullOrWhiteSpace(i.Item)))
        {
            warnings.Add(new RecipeImportWarningDto(
                "ingredient_normalization_partial",
                "Some ingredient quantities or item names could not be normalized and were preserved as text."));
        }

        var source = new RecipeSourceInputDto(
            RecipeOriginType.ImportedStructuredData,
            sourceUri.ToString(),
            sourceUri.Host.ToLowerInvariant(),
            null,
            true,
            warnings);

        var recipe = new RecipeUpsertDto(
            title,
            null,
            source,
            ingredients,
            instructions,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            false);

        return new ParsedRecipeCandidate(recipe, warnings, ImportConfidenceLevel.Low, true, 4);
    }

    private static ParsedRecipeCandidate? BuildLlmCandidate(RecipeLlmExtractionResult result, Uri sourceUri, string strategy)
    {
        if (string.IsNullOrWhiteSpace(result.Name) || result.Ingredients.Count == 0 || result.Instructions.Count == 0)
            return null;

        var warnings = new List<RecipeImportWarningDto>
        {
            new("llm_recipe_import", "Recipe content was extracted with an LLM fallback and needs manual review."),
            new("import_strategy", strategy == "jina_reader"
                ? "The source page was imported through Jina Reader before LLM extraction."
                : "The source page did not expose Recipe JSON-LD and was extracted with an LLM fallback.")
        };

        var ingredients = result.Ingredients.Select(ParseIngredientLine).ToList();
        if (ingredients.Any(i => i.Quantity is null || string.IsNullOrWhiteSpace(i.Item)))
        {
            warnings.Add(new RecipeImportWarningDto(
                "ingredient_normalization_partial",
                "Some ingredient quantities or item names could not be normalized and were preserved as text."));
        }

        var source = new RecipeSourceInputDto(
            RecipeOriginType.ImportedStructuredData,
            sourceUri.ToString(),
            sourceUri.Host.ToLowerInvariant(),
            string.IsNullOrWhiteSpace(result.Attribution) ? null : result.Attribution.Trim(),
            true,
            warnings);

        var recipe = new RecipeUpsertDto(
            result.Name.Trim(),
            string.IsNullOrWhiteSpace(result.Description) ? null : result.Description.Trim(),
            source,
            ingredients,
            result.Instructions.Select(step => new RecipeInstructionInputDto(step.Trim(), null)).ToList(),
            result.Servings,
            string.IsNullOrWhiteSpace(result.YieldText) ? null : result.YieldText.Trim(),
            result.PrepMinutes,
            result.CookMinutes,
            result.TotalMinutes,
            result.Nutrition,
            string.IsNullOrWhiteSpace(result.ImageUrl) ? null : result.ImageUrl.Trim(),
            result.Tags ?? [],
            false);

        return new ParsedRecipeCandidate(recipe, warnings, ImportConfidenceLevel.Medium, true, 6);
    }

    private static string ExtractVisibleText(string text)
        => string.Join('\n', NormalizeTextLines(text));

    private static string? ExtractTitleFromReaderText(string text)
    {
        return NormalizeTextLines(text)
            .Select(line => line.StartsWith("Title:", StringComparison.OrdinalIgnoreCase) ? line[6..].Trim() : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string ExtractReaderMarkdownBody(string text)
    {
        var marker = "Markdown Content:";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? text[(index + marker.Length)..] : text;
    }

    private static List<string> NormalizeTextLines(string text)
    {
        return text.Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanRecipeLine)
            .Where(line => line.Length is > 0 and <= 2_000)
            .ToList();
    }

    private static string CleanRecipeLine(string line)
    {
        var cleaned = line.Trim().Trim('#').Trim();
        cleaned = BulletPrefixRegex().Replace(cleaned, string.Empty);
        cleaned = NumberedPrefixRegex().Replace(cleaned, string.Empty);
        return WhitespaceRegex().Replace(cleaned.Trim(), " ");
    }

    private static string? ExtractTitleFromLines(List<string> lines)
    {
        return lines.FirstOrDefault(line => line.Length is > 3 and <= 120
            && !line.Contains(':', StringComparison.Ordinal)
            && !IsIngredientHeading(line)
            && !IsInstructionHeading(line));
    }

    private static List<string> ExtractSectionLines(
        List<string> lines,
        Func<string, bool> startPredicate,
        Func<string, bool> stopPredicate,
        int maxLines)
    {
        var startIndex = lines.FindIndex(line => startPredicate(line));
        if (startIndex < 0)
            return [];

        var result = new List<string>();
        for (var index = startIndex + 1; index < lines.Count && result.Count < maxLines; index++)
        {
            var line = lines[index];
            if (stopPredicate(line))
                break;

            if (result.Count > 0 && IsTerminalSectionHeading(line))
                break;

            if (IsSectionNoise(line))
                continue;

            result.Add(line);
        }

        return result;
    }

    private static bool IsIngredientHeading(string line)
    {
        var value = NormalizeRussianText(line);
        return value is "ingredients" or "ingredient" or "ингредиенты" or "состав" or "что понадобится"
            || value.StartsWith("ингредиенты ", StringComparison.Ordinal)
            || value.StartsWith("состав ", StringComparison.Ordinal);
    }

    private static bool IsInstructionHeading(string line)
    {
        var value = NormalizeRussianText(line);
        return value is "instructions" or "directions" or "method" or "preparation" or "приготовление" or "способ приготовления" or "как приготовить"
            || value.StartsWith("приготовление ", StringComparison.Ordinal)
            || value.StartsWith("способ приготовления ", StringComparison.Ordinal)
            || value.StartsWith("как приготовить ", StringComparison.Ordinal);
    }

    private static bool IsSectionNoise(string line)
    {
        var value = NormalizeRussianText(line);
        return value.StartsWith("url source:", StringComparison.Ordinal)
            || value.StartsWith("published time:", StringComparison.Ordinal)
            || value.StartsWith("warning:", StringComparison.Ordinal)
            || value.StartsWith("markdown content:", StringComparison.Ordinal)
            || line.StartsWith("![", StringComparison.Ordinal)
            || MarkdownOnlyLinkRegex().IsMatch(line)
            || IsNavigationSectionLabel(line)
            || value.Length < 2;
    }

    private static bool IsTerminalSectionHeading(string line)
    {
        var value = NormalizeRussianText(line).Replace('’', '\'');
        return value is "cook's note" or "local offers" or "nutrition facts" or "reviews" or "rate print"
            || value.StartsWith("nutrition facts ", StringComparison.Ordinal)
            || value.StartsWith("reviews ", StringComparison.Ordinal);
    }

    private static bool IsLikelyIngredientLine(string line)
    {
        var normalized = NormalizeIngredientWhitespace(line);
        if (normalized.Length is < 2 or > 180)
            return false;

        if (IsSectionNoise(normalized) || IsNonIngredientReaderLine(normalized))
            return false;

        var quantity = ParseLeadingQuantity(normalized, out var consumedTokens);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (quantity is not null)
            return tokens.Length > consumedTokens && tokens.Skip(consumedTokens).Any(token => token.Any(char.IsLetter));

        var lower = normalized.ToLowerInvariant();
        if (lower.Contains(" to taste", StringComparison.Ordinal))
            return true;

        if (normalized.Contains(',', StringComparison.Ordinal) && normalized.Any(char.IsLetter))
            return true;

        return normalized.Length <= 50
            && normalized.Any(char.IsLetter)
            && normalized.All(character => char.IsLetter(character) || char.IsWhiteSpace(character) || character is '-' or '\'')
            && char.IsLower(normalized.First(character => char.IsLetter(character)));
    }

    private static bool IsLikelyInstructionLine(string line)
    {
        var normalized = NormalizeIngredientWhitespace(line);
        return normalized.Length is >= 5 and <= 600
            && normalized.Any(char.IsLetter)
            && !IsSectionNoise(normalized)
            && !IsNonInstructionReaderLine(normalized);
    }

    private static bool IsNonIngredientReaderLine(string line)
    {
        var lower = line.ToLowerInvariant();
        return ParseHumanDurationMinutes(line) is not null
            || lower.Contains("review", StringComparison.Ordinal)
            || lower.Contains("photo", StringComparison.Ordinal)
            || lower.Contains("recipe", StringComparison.Ordinal)
            || lower.Contains("submitted by", StringComparison.Ordinal)
            || lower.Contains("updated on", StringComparison.Ordinal)
            || lower.Contains("tested by", StringComparison.Ordinal)
            || lower.Contains("test kitchen", StringComparison.Ordinal)
            || lower.Contains("for the loaf", StringComparison.Ordinal)
            || lower.Contains("for the sauce", StringComparison.Ordinal)
            || lower.Contains("instant read thermometer", StringComparison.Ordinal)
            || lower.Contains("community member", StringComparison.Ordinal)
            || lower.Contains("keyboard shortcut", StringComparison.Ordinal)
            || lower.Contains("screen awake", StringComparison.Ordinal)
            || lower.Contains("volume", StringComparison.Ordinal)
            || lower.Contains("original recipe", StringComparison.Ordinal)
            || lower.Contains("local offers", StringComparison.Ordinal)
            || lower.Contains("servings", StringComparison.Ordinal)
            || lower.Contains("oops", StringComparison.Ordinal)
            || lower.Contains("our team", StringComparison.Ordinal)
            || lower.Contains("recipe was", StringComparison.Ordinal)
                || lower.Contains("inch) meatloaf", StringComparison.Ordinal)
                || line.StartsWith('"')
                || line.StartsWith("· For ", StringComparison.Ordinal)
            || ScaleControlRegex().IsMatch(line);
    }

    private static bool IsNonInstructionReaderLine(string line)
    {
        var lower = line.ToLowerInvariant();
        return lower.Contains("food styling", StringComparison.Ordinal)
            || lower.Contains("prop styling", StringComparison.Ordinal)
            || lower.Contains("photo", StringComparison.Ordinal)
            || lower.Contains("keyboard shortcut", StringComparison.Ordinal)
            || lower.Contains("home cooks made it", StringComparison.Ordinal)
            || lower is "save" or "rate" or "print";
    }

    private static bool IsNavigationSectionLabel(string line)
    {
        var trimmed = line.Trim();
        if (NavigationSectionLabels.Contains(trimmed))
            return true;

        if (trimmed.Length > 40 || trimmed.Any(char.IsDigit))
            return false;

        if (trimmed.Any(character => character is ',' or '.' or ':' or ';' or '/' or '(' or ')' or '[' or ']'))
            return false;

        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length is > 0 and <= 4 && words.All(IsTitleCaseWord);
    }

    private static bool IsTitleCaseWord(string word)
        => word.Length > 1 && char.IsUpper(word[0]) && word.Skip(1).Any(char.IsLower);

    private static string NormalizeRussianText(string value)
        => value.Trim().Replace('ё', 'е').Replace('Ё', 'е').ToLowerInvariant().Trim(':', '.', '-');

    private static ParsedRecipeCandidate? ParseBestRecipe(IEnumerable<string> jsonLdBlocks, Uri sourceUri, string? documentTitle)
    {
        var candidates = new List<ParsedRecipeCandidate>();

        foreach (var block in jsonLdBlocks)
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(block);
            }
            catch
            {
                continue;
            }

            if (root is null)
                continue;

            foreach (var obj in EnumerateObjects(root))
            {
                if (!IsRecipeNode(obj))
                    continue;

                var candidate = BuildCandidate(obj, sourceUri, documentTitle);
                if (candidate is not null)
                    candidates.Add(candidate);
            }
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Warnings.Count)
            .FirstOrDefault();
    }

    private static ParsedRecipeCandidate? BuildCandidate(JsonObject recipeNode, Uri sourceUri, string? documentTitle)
    {
        var warnings = new List<RecipeImportWarningDto>();

        var name = ReadText(recipeNode["name"]);
        if (string.IsNullOrWhiteSpace(name))
            name = string.IsNullOrWhiteSpace(documentTitle) ? null : documentTitle.Trim();

        if (string.IsNullOrWhiteSpace(name))
            return null;

        var ingredientLines = ReadIngredientLines(recipeNode["recipeIngredient"]);
        var ingredients = ingredientLines.Select(ParseIngredientLine).ToList();
        if (ingredients.Count == 0)
            warnings.Add(new RecipeImportWarningDto("missing_ingredients", "No ingredient lines were found in the source metadata."));

        if (ingredients.Any(i => i.Quantity is null || string.IsNullOrWhiteSpace(i.Item)))
        {
            warnings.Add(new RecipeImportWarningDto(
                "ingredient_normalization_partial",
                "Some ingredient quantities or item names could not be normalized and were preserved as text."));
        }

        var instructions = ParseInstructions(recipeNode["recipeInstructions"]);
        if (instructions.Count == 0)
            warnings.Add(new RecipeImportWarningDto("missing_instructions", "No instruction steps were found in the source metadata."));

        if (ingredients.Count == 0 && instructions.Count == 0)
            return null;

        var description = ReadText(recipeNode["description"]);
        if (string.IsNullOrWhiteSpace(description))
        {
            warnings.Add(new RecipeImportWarningDto(
                "missing_description",
                "The source metadata did not include a recipe description."));
        }

        var yieldText = ReadSingleOrFirst(recipeNode["recipeYield"]);
        var servings = ParseServings(yieldText);
        if (servings is null)
        {
            warnings.Add(new RecipeImportWarningDto(
                "missing_servings",
                "The source metadata did not expose a reliable servings value."));
        }

        var prepMinutes = ParseDurationMinutes(recipeNode["prepTime"]);
        var cookMinutes = ParseDurationMinutes(recipeNode["cookTime"]);
        var totalMinutes = ParseDurationMinutes(recipeNode["totalTime"]);
        if (prepMinutes is null && cookMinutes is null && totalMinutes is null)
        {
            warnings.Add(new RecipeImportWarningDto(
                "missing_times",
                "The source metadata did not include prep, cook, or total time."));
        }

        var nutrition = ParseNutrition(recipeNode["nutrition"]);
        var imageUrl = ParseImage(recipeNode["image"], sourceUri);
        var tags = ParseTags(recipeNode["keywords"]);
        var attribution = ParseAttribution(recipeNode);

        var score = 0;
        if (!string.IsNullOrWhiteSpace(name)) score += 2;
        if (ingredients.Count > 0) score += 2;
        if (instructions.Count > 0) score += 2;
        if (servings is not null) score += 1;
        if (prepMinutes is not null || cookMinutes is not null || totalMinutes is not null) score += 1;
        if (nutrition is not null) score += 1;
        if (!string.IsNullOrWhiteSpace(imageUrl)) score += 1;
        if (tags.Count > 0) score += 1;

        var confidence = score >= 8
            ? ImportConfidenceLevel.High
            : score >= 5
                ? ImportConfidenceLevel.Medium
                : ImportConfidenceLevel.Low;

        var requiresManualReview = warnings.Count > 0 || confidence != ImportConfidenceLevel.High;
        var source = new RecipeSourceInputDto(
            RecipeOriginType.ImportedStructuredData,
            sourceUri.ToString(),
            sourceUri.Host.ToLowerInvariant(),
            attribution,
            requiresManualReview,
            warnings);

        var recipe = new RecipeUpsertDto(
            name.Trim(),
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            source,
            ingredients,
            instructions,
            servings,
            string.IsNullOrWhiteSpace(yieldText) ? null : yieldText.Trim(),
            prepMinutes,
            cookMinutes,
            totalMinutes,
            nutrition,
            imageUrl,
            tags,
            false);

        return new ParsedRecipeCandidate(recipe, warnings, confidence, requiresManualReview, score);
    }

    private static IEnumerable<JsonObject> EnumerateObjects(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;

            foreach (var property in obj)
            {
                var value = property.Value;
                if (value is null)
                    continue;

                foreach (var child in EnumerateObjects(value))
                    yield return child;
            }

            yield break;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is null)
                    continue;

                foreach (var child in EnumerateObjects(item))
                    yield return child;
            }
        }
    }

    private static bool IsRecipeNode(JsonObject obj)
    {
        var typeNode = obj["@type"];
        return typeNode switch
        {
            JsonValue value => value.ToString().Contains("Recipe", StringComparison.OrdinalIgnoreCase),
            JsonArray array => array.Any(item => item?.ToString().Contains("Recipe", StringComparison.OrdinalIgnoreCase) == true),
            _ => false
        };
    }

    private static List<RecipeInstructionInputDto> ParseInstructions(JsonNode? node)
    {
        var result = new List<RecipeInstructionInputDto>();
        AddInstructions(node, null, result);
        return result;
    }

    private static void AddInstructions(JsonNode? node, string? section, List<RecipeInstructionInputDto> result)
    {
        switch (node)
        {
            case null:
                return;
            case JsonValue value:
                {
                    var text = value.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                        result.Add(new RecipeInstructionInputDto(text, section));
                    return;
                }
            case JsonArray array:
                foreach (var item in array)
                    AddInstructions(item, section, result);
                return;
            case JsonObject obj:
                {
                    if (IsType(obj, "HowToSection"))
                    {
                        var nextSection = ReadText(obj["name"]) ?? section;
                        AddInstructions(obj["itemListElement"], nextSection, result);
                        return;
                    }

                    var text = ReadText(obj["text"]) ?? ReadText(obj["name"]);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        result.Add(new RecipeInstructionInputDto(text.Trim(), section));
                        return;
                    }

                    AddInstructions(obj["itemListElement"], section, result);
                    return;
                }
        }
    }

    private static bool IsType(JsonObject obj, string type)
    {
        var typeNode = obj["@type"];
        return typeNode switch
        {
            JsonValue value => string.Equals(value.ToString(), type, StringComparison.OrdinalIgnoreCase),
            JsonArray array => array.Any(item => string.Equals(item?.ToString(), type, StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private static List<string> ReadStringList(JsonNode? node)
    {
        return node switch
        {
            null => [],
            JsonValue value => SplitList(value.ToString()),
            JsonArray array => array.SelectMany(item => ReadStringList(item)).ToList(),
            _ => []
        };
    }

    private static List<string> ReadIngredientLines(JsonNode? node)
    {
        return node switch
        {
            null => [],
            JsonValue value => SplitIngredientLines(value.ToString()),
            JsonArray array => array
                .Select(item => ReadText(item))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!.Trim())
                .ToList(),
            _ => []
        };
    }

    private static List<string> SplitList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
    }

    private static List<string> SplitIngredientLines(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split(['\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
    }

    private static RecipeIngredientInputDto ParseIngredientLine(string line)
    {
        var normalized = NormalizeIngredientWhitespace(line);
        var quantity = ParseLeadingQuantity(normalized, out var consumedTokens);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? unit = null;
        var unitMatch = TryReadUnit(tokens, consumedTokens);
        if (unitMatch is not null)
        {
            unit = unitMatch.Value.Unit;
            consumedTokens += unitMatch.Value.TokenCount;
        }

        var remaining = tokens.Length > consumedTokens
            ? string.Join(' ', tokens[consumedTokens..])
            : normalized;

        string? item = remaining;
        string? note = null;
        var commaIndex = remaining.IndexOf(',');
        if (commaIndex >= 0)
        {
            item = remaining[..commaIndex].Trim();
            note = remaining[(commaIndex + 1)..].Trim();
        }

        if (string.IsNullOrWhiteSpace(item))
            item = null;

        return new RecipeIngredientInputDto(normalized, quantity, unit, item, string.IsNullOrWhiteSpace(note) ? null : note);
    }

    private static decimal? ParseLeadingQuantity(string input, out int consumedTokens)
    {
        consumedTokens = 0;
        var tokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return null;

        if (TryParseQuantityToken(tokens[0], out var first))
        {
            consumedTokens = 1;
            if (tokens.Length > 1 && TryParseQuantityToken(tokens[1], out var second) && tokens[1].Contains('/'))
            {
                consumedTokens = 2;
                return first + second;
            }

            return first;
        }

        return null;
    }

    private static bool TryParseQuantityToken(string token, out decimal value)
    {
        token = token.Replace("½", "1/2", StringComparison.Ordinal)
            .Replace("¼", "1/4", StringComparison.Ordinal)
            .Replace("¾", "3/4", StringComparison.Ordinal)
            .Replace("⅓", "1/3", StringComparison.Ordinal)
            .Replace("⅔", "2/3", StringComparison.Ordinal)
            .Replace("⅛", "1/8", StringComparison.Ordinal)
            .Replace("⅜", "3/8", StringComparison.Ordinal)
            .Replace("⅝", "5/8", StringComparison.Ordinal)
            .Replace("⅞", "7/8", StringComparison.Ordinal)
            .Replace('–', '-')
            .Replace('—', '-')
            .TrimEnd('.', ',', ';', ':')
            .Trim();

        var rangeParts = token.Split('-', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rangeParts.Length == 2
            && TryParseQuantityToken(rangeParts[0], out var rangeStart)
            && TryParseQuantityToken(rangeParts[1], out var rangeEnd))
        {
            value = (rangeStart + rangeEnd) / 2;
            return true;
        }

        if (token.Contains('/'))
        {
            var parts = token.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var numerator)
                && decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var denominator)
                && denominator != 0)
            {
                value = numerator / denominator;
                return true;
            }
        }

        return decimal.TryParse(
            token.Replace(',', '.'),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static string NormalizeIngredientWhitespace(string line)
    {
        var normalized = ReplaceUnicodeFractions(line)
            .Replace('\u00A0', ' ')
            .Replace('–', '-')
            .Replace('—', '-');

        return WhitespaceRegex().Replace(normalized.Trim(), " ");
    }

    private static string ReplaceUnicodeFractions(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            var replacement = character switch
            {
                '½' => "1/2",
                '¼' => "1/4",
                '¾' => "3/4",
                '⅓' => "1/3",
                '⅔' => "2/3",
                '⅛' => "1/8",
                '⅜' => "3/8",
                '⅝' => "5/8",
                '⅞' => "7/8",
                _ => null
            };

            if (replacement is null)
            {
                builder.Append(character);
                continue;
            }

            if (builder.Length > 0 && char.IsDigit(builder[^1]))
                builder.Append(' ');

            builder.Append(replacement);
        }

        return builder.ToString();
    }

    private static (string Unit, int TokenCount)? TryReadUnit(string[] tokens, int startIndex)
    {
        var maxLength = Math.Min(MaxUnitTokenLength, tokens.Length - startIndex);
        for (var tokenCount = maxLength; tokenCount >= 1; tokenCount--)
        {
            var candidate = string.Join(' ', tokens.Skip(startIndex).Take(tokenCount));
            if (UnitAliases.TryGetValue(NormalizeUnitKey(candidate), out var unit))
                return (unit, tokenCount);
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> BuildUnitAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string canonical, params string[] values)
        {
            aliases[NormalizeUnitKey(canonical)] = canonical;
            foreach (var value in values)
                aliases[NormalizeUnitKey(value)] = canonical;
        }

        Add("tsp", "teaspoon", "teaspoons", "tsp.");
        Add("tbsp", "tablespoon", "tablespoons", "tbsp.");
        Add("cup", "cups");
        Add("oz", "ounce", "ounces");
        Add("lb", "lbs", "pound", "pounds");
        Add("g", "gram", "grams");
        Add("kg");
        Add("ml");
        Add("l", "liter", "liters", "litre", "litres");
        Add("pinch", "pinches");
        Add("can", "cans");
        Add("clove", "cloves");
        Add("slice", "slices");
        Add("package", "packages", "pkg", "pkg.");

        Add("ч. л.", "ч л", "ч.л.", "чайная ложка", "чайной ложки", "чайные ложки", "чайных ложек");
        Add("ст. л.", "ст л", "ст.л.", "столовая ложка", "столовой ложки", "столовые ложки", "столовых ложек");
        Add("г", "гр", "гр.", "грамм", "грамма", "граммов");
        Add("кг", "килограмм", "килограмма", "килограммов");
        Add("мл", "миллилитр", "миллилитра", "миллилитров");
        Add("л", "литр", "литра", "литров");
        Add("стакан", "стакана", "стаканов", "стак.");
        Add("щепотка", "щепотки", "щепоток");
        Add("банка", "банки", "банок");
        Add("зубчик", "зубчика", "зубчиков");
        Add("ломтик", "ломтика", "ломтиков");
        Add("упаковка", "упаковки", "упаковок", "пачка", "пачки", "пачек");
        Add("шт.", "шт", "штука", "штуки", "штук");
        Add("пучок", "пучка", "пучков");

        return aliases;
    }

    private static string NormalizeUnitKey(string value)
    {
        var normalized = value.Trim()
            .Trim(',', ';', ':')
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace('ё', 'е')
            .Replace('Ё', 'е')
            .ToLowerInvariant();

        return WhitespaceRegex().Replace(normalized, " ");
    }

    private static string? ReadText(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonValue value => value.ToString(),
            JsonArray array => array.Select(ReadText).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
            JsonObject obj => ReadText(obj["@value"]) ?? ReadText(obj["name"]) ?? ReadText(obj["text"]),
            _ => null
        };
    }

    private static string? ReadSingleOrFirst(JsonNode? node)
        => ReadStringList(node).FirstOrDefault() ?? ReadText(node);

    private static int? ParseServings(string? yieldText)
    {
        if (string.IsNullOrWhiteSpace(yieldText))
            return null;

        var match = DigitRegex().Match(yieldText);
        return match.Success && int.TryParse(match.Value, out var servings) && servings > 0
            ? servings
            : null;
    }

    private static int? ParseDurationMinutes(JsonNode? node)
    {
        var value = ReadText(node);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return (int)Math.Round(XmlConvert.ToTimeSpan(value).TotalMinutes);
        }
        catch
        {
        }

        return ParseHumanDurationMinutes(value);
    }

    private static int? ParseHumanDurationMinutes(string value)
    {
        var normalized = NormalizeIngredientWhitespace(value)
            .Replace('ё', 'е')
            .Replace('Ё', 'е')
            .ToLowerInvariant();

        decimal totalMinutes = 0;
        var matched = false;

        foreach (Match match in DurationHourRegex().Matches(normalized))
        {
            if (TryParseFlexibleDecimal(match.Groups["value"].Value, out var hours))
            {
                totalMinutes += hours * 60;
                matched = true;
            }
        }

        foreach (Match match in DurationMinuteRegex().Matches(normalized))
        {
            if (TryParseFlexibleDecimal(match.Groups["value"].Value, out var minutes))
            {
                totalMinutes += minutes;
                matched = true;
            }
        }

        return matched && totalMinutes > 0
            ? (int)Math.Round(totalMinutes)
            : null;
    }

    private static bool TryParseFlexibleDecimal(string value, out decimal parsed)
    {
        return decimal.TryParse(
            value.Replace(',', '.'),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out parsed);
    }

    private static RecipeNutritionDto? ParseNutrition(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return null;

        var calories = ParseIntValue(obj["calories"]);
        var protein = ParseDecimalValue(obj["proteinContent"]);
        var carbs = ParseDecimalValue(obj["carbohydrateContent"]);
        var fat = ParseDecimalValue(obj["fatContent"]);

        return calories is null && protein is null && carbs is null && fat is null
            ? null
            : new RecipeNutritionDto(calories, protein, carbs, fat);
    }

    private static int? ParseIntValue(JsonNode? node)
    {
        var value = ReadText(node);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = DecimalRegex().Match(value.Replace(',', '.'));
        if (!match.Success)
            return null;

        return decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? (int)Math.Round(parsed)
            : null;
    }

    private static decimal? ParseDecimalValue(JsonNode? node)
    {
        var value = ReadText(node);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = DecimalRegex().Match(value.Replace(',', '.'));
        if (!match.Success)
            return null;

        return decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? ParseImage(JsonNode? node, Uri sourceUri)
    {
        var raw = node switch
        {
            JsonValue value => value.ToString(),
            JsonArray array => array.Select(item => ParseImage(item, sourceUri)).FirstOrDefault(image => !string.IsNullOrWhiteSpace(image)),
            JsonObject obj => ReadText(obj["url"]),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return Uri.TryCreate(raw, UriKind.Absolute, out var absolute)
            ? absolute.ToString()
            : new Uri(sourceUri, raw).ToString();
    }

    private static List<string> ParseTags(JsonNode? node)
    {
        var tags = ReadStringList(node);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in tags)
        {
            var trimmed = tag.Trim();
            if (trimmed.Length == 0)
                continue;

            if (seen.Add(trimmed))
                result.Add(trimmed);
        }

        return result;
    }

    private static string? ParseAttribution(JsonObject recipeNode)
    {
        static string? ReadName(JsonNode? node)
        {
            return node switch
            {
                JsonObject obj => ReadText(obj["name"]),
                JsonArray array => array.Select(ReadName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                _ => ReadText(node)
            };
        }

        return ReadName(recipeNode["author"]) ?? ReadName(recipeNode["publisher"]);
    }

    private static async Task<Uri> ValidateImportUriAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw ApiProblemException.BadRequest(
                "unsupported_domain",
                "Only public http and https recipe URLs are supported.");
        }

        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw ApiProblemException.BadRequest(
                "unsupported_domain",
                "Local or loopback addresses are not supported for recipe import.");
        }

        if (IPAddress.TryParse(uri.Host, out var ipAddress))
        {
            if (IsPrivateOrReserved(ipAddress))
            {
                throw ApiProblemException.BadRequest(
                    "unsupported_domain",
                    "Private network addresses are not supported for recipe import.");
            }

            return uri;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
            if (addresses.Any(IsPrivateOrReserved))
            {
                throw ApiProblemException.BadRequest(
                    "unsupported_domain",
                    "Private or non-public source domains are not supported for recipe import.");
            }
        }
        catch (SocketException)
        {
            throw ApiProblemException.BadGateway(
                "recipe_import_fetch_failed",
                "The recipe source domain could not be resolved.");
        }

        return uri;
    }

    private static bool IsPrivateOrReserved(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254)
                   || bytes[0] == 127;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal
                   || address.IsIPv6SiteLocal
                   || address.Equals(IPAddress.IPv6Loopback)
                   || (address.GetAddressBytes()[0] & 0xfe) == 0xfc;
        }

        return false;
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
        => (int)statusCode is >= 300 and < 400;

    private static async Task<string> ReadBoundedStringAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        await using var ms = new MemoryStream();
        var total = 0L;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
            {
                throw ApiProblemException.Unprocessable(
                    "recipe_import_too_large",
                    "The recipe page is too large to import.");
            }
            ms.Write(buffer, 0, read);
        }

        var bytes = ms.ToArray();
        var encoding = response.Content.Headers.ContentType?.CharSet is { Length: > 0 } cs
            ? TryGetEncoding(cs)
            : null;
        encoding ??= TrySniffMetaCharset(bytes);
        encoding ??= System.Text.Encoding.UTF8;
        return encoding.GetString(bytes);
    }

    private static System.Text.Encoding? TrySniffMetaCharset(byte[] bytes)
    {
        var headLength = Math.Min(bytes.Length, 4096);
        if (headLength == 0)
            return null;

        var head = System.Text.Encoding.ASCII.GetString(bytes, 0, headLength);
        var match = MetaCharsetRegex().Match(head);
        return match.Success ? TryGetEncoding(match.Groups["charset"].Value) : null;
    }

    private static System.Text.Encoding? TryGetEncoding(string charset)
    {
        try { return System.Text.Encoding.GetEncoding(charset.Trim('"', '\'')); }
        catch { return null; }
    }

    private sealed record ParsedRecipeCandidate(
        RecipeUpsertDto Recipe,
        List<RecipeImportWarningDto> Warnings,
        ImportConfidenceLevel Confidence,
        bool RequiresManualReview,
        int Score);

    private sealed record ImportFetchResult(Uri EffectiveUri, HttpResponseMessage Response);
    private sealed record RecipePageContent(Uri EffectiveUri, string Text, bool IsHtml, string Strategy);
    private sealed record ImportFailure(string Code);

    [GeneratedRegex("\\d+(?:[\\.,]\\d+)?", RegexOptions.Compiled)]
    private static partial Regex DecimalRegex();

    [GeneratedRegex("(?<value>\\d+(?:[\\.,]\\d+)?)\\s*(?:h|hr|hrs|hour|hours|ч|час|часа|часов)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DurationHourRegex();

    [GeneratedRegex("(?<value>\\d+(?:[\\.,]\\d+)?)\\s*(?:m|min|mins|minute|minutes|мин|минута|минуты|минут)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DurationMinuteRegex();

    [GeneratedRegex("\\d+", RegexOptions.Compiled)]
    private static partial Regex DigitRegex();

    [GeneratedRegex("\\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("charset\\s*=\\s*[\\\"']?(?<charset>[a-zA-Z0-9._-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MetaCharsetRegex();

    [GeneratedRegex("^[-*•]+\\s*", RegexOptions.Compiled)]
    private static partial Regex BulletPrefixRegex();

    [GeneratedRegex("^\\d+[.)]\\s*", RegexOptions.Compiled)]
    private static partial Regex NumberedPrefixRegex();

    [GeneratedRegex("^!?\\[[^\\]]{0,160}\\]\\([^)]+\\)$", RegexOptions.Compiled)]
    private static partial Regex MarkdownOnlyLinkRegex();

    [GeneratedRegex("^\\d+(?:/\\d+)?x$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ScaleControlRegex();
}