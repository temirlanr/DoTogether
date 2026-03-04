namespace DoTogether.Domain.Entities;

/// <summary>
/// Server-side idempotency ledger. Stores the result of a mutation keyed by
/// ClientOperationId so that retries return the original response.
/// </summary>
public class IdempotentOperation
{
    public Guid ClientOperationId { get; set; }
    public Guid HouseholdId { get; set; }
    public string OperationType { get; set; } = string.Empty;
    public string ResponseJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
