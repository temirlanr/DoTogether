using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using DoTogether.Application.DTOs;
using DoTogether.Domain.Enums;

namespace DoTogether.Application.Services;

public partial class RecipeImportService
{
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
}
