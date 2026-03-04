using DoTogether.Domain.Enums;

namespace DoTogether.Domain.Entities;

/// <summary>
/// Immutable audit event. Every state change on an occurrence produces one event.
/// </summary>
public class ChoreEvent : BaseEntity
{
    public Guid ChoreOccurrenceId { get; set; }
    public ChoreOccurrence ChoreOccurrence { get; set; } = null!;

    public ChoreEventType EventType { get; set; }

    public Guid PerformedByUserId { get; set; }
    public User PerformedByUser { get; set; } = null!;

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Client-supplied idempotency key (UUID). Unique per household.</summary>
    public Guid ClientOperationId { get; set; }

    /// <summary>Optional JSON payload, e.g. {"previousAssigneeId":"..."}</summary>
    public string? Metadata { get; set; }
}
