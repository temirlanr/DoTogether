using DoTogether.Domain.ValueObjects;

namespace DoTogether.Domain.Entities;

public class ChoreTemplate : BaseEntity
{
    public Guid HouseholdId { get; set; }
    public Household Household { get; set; } = null!;

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    public RecurrenceRule RecurrenceRule { get; set; } = new();

    /// <summary>Default assignee. Null means unassigned / alternating.</summary>
    public Guid? AssigneeId { get; set; }
    public User? Assignee { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;

    /// <summary>Start date for recurrence generation (household-local).</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Optional end date for the recurrence.</summary>
    public DateOnly? EndDate { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Furthest date for which occurrences have been generated.</summary>
    public DateOnly? GeneratedThroughDate { get; set; }

    public ICollection<ChoreOccurrence> Occurrences { get; set; } = [];
}
