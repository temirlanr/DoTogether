namespace DoTogether.Application.Interfaces;

public interface ITokenService
{
    string GenerateAccessToken(Guid userId, string email);
    (string Token, DateTime ExpiresAtUtc) GenerateRefreshToken();
    Guid? ValidateAccessToken(string token);
}
