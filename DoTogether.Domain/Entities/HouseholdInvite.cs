namespace DoTogether.Domain.Entities;

public class HouseholdInvite : BaseEntity
{
    public Guid HouseholdId { get; set; }
    public Household Household { get; set; } = null!;

    public string Token { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddDays(7);
    public bool Accepted { get; set; }
}
