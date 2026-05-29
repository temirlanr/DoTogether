using DoTogether.Application.DTOs;
using DoTogether.Application.Exceptions;
using DoTogether.Application.Interfaces;
using DoTogether.Application.Services;
using DoTogether.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Tests;

public class AuthServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly MutableDateTimeProvider _clock;
    private readonly FakeTokenService _tokens = new();
    private readonly AuthService _service;

    public AuthServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _clock = new MutableDateTimeProvider(new DateTime(2026, 5, 14, 8, 0, 0, DateTimeKind.Utc));
        _service = new AuthService(_db, _tokens, _clock);
    }

    [Fact]
    public async Task Register_CreatesUserAndReturnsTokens()
    {
        var response = await _service.RegisterAsync("TestUser", "Test User", StrongPassword, CancellationToken.None);

        Assert.Equal("testuser", response.User.Username);
        Assert.Equal("Test User", response.User.DisplayName);
        Assert.NotNull(response.AccessToken);

        var user = await _db.Users.SingleAsync();
        Assert.Equal("testuser", user.Username);
    }

    [Fact]
    public async Task Login_LocksAccountAfterRepeatedFailures()
    {
        await _service.RegisterAsync("LockUser", "Lock Me", StrongPassword, CancellationToken.None);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var invalid = await Assert.ThrowsAsync<ApiProblemException>(() =>
                _service.LoginAsync("lockuser", "Wrong!Pass123", CancellationToken.None));
            Assert.Equal("invalid_credentials", invalid.ErrorCode);
        }

        var locked = await Assert.ThrowsAsync<ApiProblemException>(() =>
            _service.LoginAsync("lockuser", StrongPassword, CancellationToken.None));

        Assert.Equal("account_locked", locked.ErrorCode);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(16);
        var success = await _service.LoginAsync("lockuser", StrongPassword, CancellationToken.None);

        Assert.NotNull(success.AccessToken);
        var user = await _db.Users.SingleAsync(u => u.Username == "lockuser");
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockoutEndsAtUtc);
    }

    [Fact]
    public async Task Register_RejectsWeakPasswords()
    {
        var ex = await Assert.ThrowsAsync<ApiProblemException>(() =>
            _service.RegisterAsync("WeakUser", "Weak", "Password123", CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("weak_password", ex.ErrorCode);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private const string StrongPassword = "Str0ng!Pass";

    private sealed class FakeTokenService : ITokenService
    {
        public (string Token, DateTime ExpiresAtUtc) GenerateAccessToken(Guid userId, string username)
            => ($"access-{Guid.NewGuid():N}", DateTime.UtcNow.AddHours(1));

        public (string Token, DateTime ExpiresAtUtc) GenerateRefreshToken()
            => ($"refresh-{Guid.NewGuid():N}", DateTime.UtcNow.AddDays(30));

        public Guid? ValidateAccessToken(string token) => null;
    }

    private sealed class MutableDateTimeProvider(DateTime utcNow) : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;

        public DateOnly TodayIn(string ianaTimeZoneId)
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId);
            var local = TimeZoneInfo.ConvertTimeFromUtc(UtcNow, tz);
            return DateOnly.FromDateTime(local);
        }
    }
}
