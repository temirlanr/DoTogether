using System.Globalization;
using System.Text;
using DoTogether.Application.DTOs;

namespace DoTogether.Application.Services;

public partial class RecipeImportService
{
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
}
