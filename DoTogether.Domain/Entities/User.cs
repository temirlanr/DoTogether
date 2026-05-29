namespace DoTogether.Domain.Entities;

public class User : BaseEntity
{
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public int FailedLoginAttempts { get; set; }
    public int LockoutCount { get; set; }
    public DateTime? LockoutEndsAtUtc { get; set; }

    public ICollection<HouseholdMember> Memberships { get; set; } = [];
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<DevicePushToken> DevicePushTokens { get; set; } = [];
}
