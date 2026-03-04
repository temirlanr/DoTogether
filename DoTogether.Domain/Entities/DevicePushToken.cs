using DoTogether.Domain.Enums;

namespace DoTogether.Domain.Entities;

public class DevicePushToken : BaseEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public PushPlatform Platform { get; set; }
    public string Token { get; set; } = string.Empty;
}
