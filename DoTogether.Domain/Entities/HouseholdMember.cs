using DoTogether.Domain.Enums;

namespace DoTogether.Domain.Entities;

public class HouseholdMember : BaseEntity
{
    public Guid HouseholdId { get; set; }
    public Household Household { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public MemberRole Role { get; set; } = MemberRole.Member;
    public DateTime JoinedAtUtc { get; set; } = DateTime.UtcNow;
}
