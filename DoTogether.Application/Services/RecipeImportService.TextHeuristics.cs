namespace DoTogether.Application.Services;

public partial class RecipeImportService
{
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
}
