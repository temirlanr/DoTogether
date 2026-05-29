using System.Security.Cryptography;
using System.Text;
using DoTogether.Application.DTOs;
using DoTogether.Application.Exceptions;
using DoTogether.Application.Interfaces;
using DoTogether.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Application.Services;

public class AuthService(IAppDbContext db, ITokenService tokenService, IDateTimeProvider clock)
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;
    private const int MaxFailedLoginAttempts = 5;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;
    private static readonly TimeSpan InitialLockoutDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxLockoutDuration = TimeSpan.FromHours(24);
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "qwerty", "qwerty123", "admin123",
        "letmein", "welcome", "welcome1", "iloveyou", "12345678", "123456789",
        "1234567890", "111111", "000000", "abc123", "doTogether123!"
    };

    public async Task<AuthResponseDto> RegisterAsync(string username, string displayName, string password, CancellationToken ct)
    {
        username = NormalizeUsername(username);
        displayName = displayName.Trim();
        ValidatePasswordStrength(password);

        if (await db.Users.AnyAsync(u => u.Username == username, ct))
            throw ApiProblemException.Conflict("username_already_taken", "Username is already taken.");

        var user = new User
        {
            Username = username,
            DisplayName = displayName,
            PasswordHash = HashPassword(password),
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        return await GenerateTokensAsync(user, ct);
    }

    public async Task<AuthResponseDto> LoginAsync(string username, string password, CancellationToken ct)
    {
        username = NormalizeUsername(username);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        // Verify a dummy hash for non-existent users to keep timing constant.
        if (user is null)
        {
            VerifyPassword(password, DummyHash);
            throw ApiProblemException.Unauthorized("invalid_credentials", "Invalid username or password.");
        }

        if (user.LockoutEndsAtUtc is { } lockoutEnds && lockoutEnds > clock.UtcNow)
        {
            throw ApiProblemException.Unauthorized(
                "account_locked",
                "Account is temporarily locked. Please try again later.");
        }

        if (!VerifyPassword(password, user.PasswordHash))
        {
            await RecordFailedLoginAsync(user, ct);
            throw ApiProblemException.Unauthorized("invalid_credentials", "Invalid username or password.");
        }

        if (user.FailedLoginAttempts > 0 || user.LockoutEndsAtUtc is not null || user.LockoutCount > 0)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutEndsAtUtc = null;
            user.LockoutCount = 0;
            await db.SaveChangesAsync(ct);
        }

        return await GenerateTokensAsync(user, ct);
    }

    public async Task<AuthResponseDto> RefreshAsync(string refreshTokenValue, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshTokenValue))
            throw ApiProblemException.Unauthorized("invalid_refresh_token", "Refresh token missing.");

        var token = await db.RefreshTokens
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.Token == refreshTokenValue, ct);

        if (token is null)
            throw ApiProblemException.Unauthorized("invalid_refresh_token", "Invalid refresh token.");

        // Reuse detection: presenting an already-revoked token signals theft —
        // revoke every active token for this user (kill all sessions).
        if (token.IsRevoked)
        {
            var allActive = await db.RefreshTokens
                .Where(r => r.UserId == token.UserId && !r.IsRevoked)
                .ToListAsync(ct);
            foreach (var t in allActive)
                t.IsRevoked = true;
            await db.SaveChangesAsync(ct);
            throw ApiProblemException.Unauthorized(
                "refresh_token_reused",
                "Refresh token reuse detected; all sessions revoked.");
        }

        if (token.ExpiresAtUtc < clock.UtcNow)
            throw ApiProblemException.Unauthorized("refresh_token_expired", "Refresh token expired.");

        // Rotate: revoke old, issue new.
        token.IsRevoked = true;
        var response = await GenerateTokensAsync(token.User, ct);
        token.ReplacedByToken = response.RefreshToken;
        await db.SaveChangesAsync(ct);

        return response;
    }

    public async Task LogoutAsync(string? refreshTokenValue, Guid userId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(refreshTokenValue))
        {
            var token = await db.RefreshTokens
                .FirstOrDefaultAsync(r => r.Token == refreshTokenValue && !r.IsRevoked, ct);
            if (token is not null)
            {
                token.IsRevoked = true;
                await db.SaveChangesAsync(ct);
                return;
            }
        }

        // Fallback: revoke everything for this user.
        if (userId != Guid.Empty)
        {
            var all = await db.RefreshTokens
                .Where(r => r.UserId == userId && !r.IsRevoked)
                .ToListAsync(ct);
            foreach (var t in all)
                t.IsRevoked = true;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<AuthResponseDto> GenerateTokensAsync(User user, CancellationToken ct)
    {
        var (accessToken, accessExpires) = tokenService.GenerateAccessToken(user.Id, user.Username);
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
            accessToken, accessExpires, refreshTokenValue, refreshExpires,
            new UserDto(user.Id, user.Username, user.DisplayName));
    }

    private async Task RecordFailedLoginAsync(User user, CancellationToken ct)
    {
        user.FailedLoginAttempts++;
        if (user.FailedLoginAttempts >= MaxFailedLoginAttempts)
        {
            user.LockoutCount++;
            user.FailedLoginAttempts = 0;
            var multiplier = Math.Pow(2, Math.Max(0, user.LockoutCount - 1));
            var durationTicks = Math.Min(
                MaxLockoutDuration.Ticks,
                (long)(InitialLockoutDuration.Ticks * multiplier));
            user.LockoutEndsAtUtc = clock.UtcNow.AddTicks(durationTicks);
        }

        await db.SaveChangesAsync(ct);
    }

    private static string NormalizeUsername(string username)
        => username.Trim().ToLowerInvariant();

    private static void ValidatePasswordStrength(string password)
    {
        if (password.Length < 10
            || !password.Any(char.IsLower)
            || !password.Any(char.IsUpper)
            || !password.Any(char.IsDigit)
            || !password.Any(ch => !char.IsLetterOrDigit(ch)))
        {
            throw ApiProblemException.BadRequest(
                "weak_password",
                "Password must be at least 10 characters and include lowercase, uppercase, number, and symbol characters.");
        }

        var normalized = password.Trim();
        if (CommonPasswords.Contains(normalized))
        {
            throw ApiProblemException.BadRequest(
                "weak_password",
                "Password is too common. Please choose a stronger password.");
        }
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

    // Pre-computed constant-time dummy hash so login attempts on non-existent users
    // take roughly the same time as real lookups (mitigates user-enumeration timing).
    private static readonly string DummyHash = HashPassword("___never_a_real_password___");
}

