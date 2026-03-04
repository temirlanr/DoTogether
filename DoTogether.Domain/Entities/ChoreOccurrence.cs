using DoTogether.Domain.Enums;

namespace DoTogether.Domain.Entities;

public class ChoreOccurrence : BaseEntity
{
    public Guid ChoreTemplateId { get; set; }
    public ChoreTemplate ChoreTemplate { get; set; } = null!;

    public Guid HouseholdId { get; set; }
    public Household Household { get; set; } = null!;

    public Guid? AssigneeId { get; set; }
    public User? Assignee { get; set; }

    /// <summary>Due date in the household's local timezone.</summary>
    public DateOnly DueDate { get; set; }

    public OccurrenceStatus Status { get; set; } = OccurrenceStatus.Pending;

    /// <summary>Optimistic concurrency token for conflict-safe mutations.</summary>
    public int Version { get; set; }

    public ICollection<ChoreEvent> Events { get; set; } = [];
}
