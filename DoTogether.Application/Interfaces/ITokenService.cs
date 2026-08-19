namespace DoTogether.Application.Interfaces;

public interface ITokenService
{
    (string Token, DateTime ExpiresAtUtc) GenerateAccessToken(Guid userId, string username);
    (string Token, DateTime ExpiresAtUtc) GenerateRefreshToken();
}
