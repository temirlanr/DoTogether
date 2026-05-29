using DoTogether.Application.Interfaces;

namespace DoTogether.Application.Services;

public sealed class NullRecipeLlmExtractor : IRecipeLlmExtractor
{
    public Task<RecipeLlmExtractionResult?> ExtractAsync(string pageText, Uri sourceUri, CancellationToken ct)
        => Task.FromResult<RecipeLlmExtractionResult?>(null);
}