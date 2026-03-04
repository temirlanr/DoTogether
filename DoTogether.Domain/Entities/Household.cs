namespace DoTogether.Domain.Entities;

/// <summary>
/// A household stores its IANA timezone (e.g. "America/New_York").
/// All due-date calculations and "end of day" boundaries use this single timezone.
/// Rationale: household partners almost always share a physical home and therefore
/// a single timezone. Using one timezone eliminates ambiguity about when a chore is
/// "due" or "missed" and keeps calendar aggregation straightforward.
/// </summary>
public class Household : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>IANA timezone identifier, e.g. "America/New_York".</summary>
    public string TimeZoneId { get; set; } = "Etc/UTC";

    public ICollection<HouseholdMember> Members { get; set; } = [];
    public ICollection<HouseholdInvite> Invites { get; set; } = [];
    public ICollection<ChoreTemplate> ChoreTemplates { get; set; } = [];
}
