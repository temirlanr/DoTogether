using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using DoTogether.Application.DTOs;
using DoTogether.Application.Exceptions;
using DoTogether.Application.Interfaces;
using DoTogether.Domain.Enums;

namespace DoTogether.Application.Services;

public sealed class RecipeImportOptions
{
    public bool EnableReaderFallback { get; set; }
}

public partial class RecipeImportService(
    HttpClient httpClient,
    HouseholdAccessService householdAccess,
    IRecipeLlmExtractor llmExtractor,
    RecipeImportOptions? options = null)
{
    private readonly RecipeImportOptions _options = options ?? new RecipeImportOptions();

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

        if (_options.EnableReaderFallback)
        {
            var jinaContent = await TryFetchJinaReaderContentAsync(sourceUri, failures, ct);
            if (jinaContent is not null)
            {
                var jinaPreview = await TryBuildPreviewAsync(jinaContent, true, ct);
                if (jinaPreview is not null)
                    return jinaPreview;
            }
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
        var readerUri = new Uri($"https://r.jina.ai/{sourceUri.AbsoluteUri}");
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

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

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