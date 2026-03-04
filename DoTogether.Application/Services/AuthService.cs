using System.Security.Cryptography;
using System.Text;
using DoTogether.Application.DTOs;
using DoTogether.Application.Interfaces;
using DoTogether.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Application.Services;

public class AuthService(IAppDbContext db, ITokenService tokenService, IDateTimeProvider clock)
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public async Task<AuthResponseDto> RegisterAsync(string email, string displayName, string password, CancellationToken ct)
    {
        email = email.ToLowerInvariant();

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw new InvalidOperationException("Email is already registered.");

        var user = new User
        {
            Email = email,
            DisplayName = displayName,
            PasswordHash = HashPassword(password)
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        return await GenerateTokensAsync(user, ct);
    }

    public async Task<AuthResponseDto> LoginAsync(string email, string password, CancellationToken ct)
    {
        email = email.ToLowerInvariant();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct)
            ?? throw new UnauthorizedAccessException("Invalid email or password.");

        if (!VerifyPassword(password, user.PasswordHash))
            throw new UnauthorizedAccessException("Invalid email or password.");

        return await GenerateTokensAsync(user, ct);
    }

    public async Task<AuthResponseDto> RefreshAsync(string refreshTokenValue, CancellationToken ct)
    {
        var token = await db.RefreshTokens
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.Token == refreshTokenValue && !r.IsRevoked, ct)
            ?? throw new UnauthorizedAccessException("Invalid refresh token.");

        if (token.ExpiresAtUtc < clock.UtcNow)
            throw new UnauthorizedAccessException("Refresh token expired.");

        // Rotate: revoke old, issue new.
        token.IsRevoked = true;
        var response = await GenerateTokensAsync(token.User, ct);
        token.ReplacedByToken = response.RefreshToken;
        await db.SaveChangesAsync(ct);

        return response;
    }

    private async Task<AuthResponseDto> GenerateTokensAsync(User user, CancellationToken ct)
    {
        var accessToken = tokenService.GenerateAccessToken(user.Id, user.Email);
        var (refreshTokenValue, refreshExpires) = tokenService.GenerateRefreshToken();

        var refreshToken = new RefreshToken
        {
            UserId = user.Id,
            Token = refreshTokenValue,
            ExpiresAtUtc = refreshExpires
        };

        db.RefreshTokens.Add(refreshToken);
        await db.SaveChangesAsync(ct);

        return new AuthResponseDto(
            accessToken, refreshTokenValue, refreshExpires,
            new UserDto(user.Id, user.Email, user.DisplayName));
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, Algorithm, HashSize);

        // Store as "iterations.salt.hash" (all base64-encoded)
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split('.');
        if (parts.Length != 3) return false;

        if (!int.TryParse(parts[0], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expectedHash = Convert.FromBase64String(parts[2]);

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, Algorithm, expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}

