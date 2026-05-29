using DoTogether.Application.DTOs;
using DoTogether.Application.Interfaces;
using DoTogether.Application.Services;
using DoTogether.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DoTogether.Controllers;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("auth")]
public class AuthController(
    AuthService authService,
    ICurrentUserService currentUser,
    IWebHostEnvironment env) : ControllerBase
{
    /// <summary>Register a new user with username, display name, and password.</summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto, CancellationToken ct)
    {
        var result = await authService.RegisterAsync(dto.Username, dto.DisplayName, dto.Password, ct);
        return CreatedAtAction(nameof(Register), WriteRefreshCookieAndStripBody(result));
    }

    /// <summary>Login with username and password to receive an access token (refresh sent as HttpOnly cookie).</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Login([FromBody] LoginDto dto, CancellationToken ct)
    {
        var result = await authService.LoginAsync(dto.Username, dto.Password, ct);
        return Ok(WriteRefreshCookieAndStripBody(result));
    }

    /// <summary>Rotate the refresh token. Reads from HttpOnly cookie or request body.</summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequestDto? dto, CancellationToken ct)
    {
        var refreshToken = Request.Cookies[AuthCookies.RefreshCookieName] ?? dto?.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken))
            return Unauthorized();
        var result = await authService.RefreshAsync(refreshToken, ct);
        return Ok(WriteRefreshCookieAndStripBody(result));
    }

    /// <summary>Revoke the current refresh token and clear the cookie.</summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var refreshToken = Request.Cookies[AuthCookies.RefreshCookieName];
        await authService.LogoutAsync(refreshToken, currentUser.UserId, ct);
        ClearRefreshCookie();
        return NoContent();
    }

    private AuthResponseDto WriteRefreshCookieAndStripBody(AuthResponseDto response)
    {
        if (response.RefreshToken is not null)
        {
            Response.Cookies.Append(AuthCookies.RefreshCookieName, response.RefreshToken, BuildCookieOptions(response.RefreshTokenExpiresAtUtc));
        }
        // Don't expose the refresh token in the JSON body — cookie is the canonical channel.
        return response with { RefreshToken = null };
    }

    private void ClearRefreshCookie()
    {
        Response.Cookies.Append(AuthCookies.RefreshCookieName, string.Empty, BuildCookieOptions(DateTime.UtcNow.AddDays(-1)));
    }

    private CookieOptions BuildCookieOptions(DateTime expiresAtUtc) => new()
    {
        HttpOnly = true,
        Secure = !env.IsDevelopment() || Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = "/api/Auth",
        Expires = expiresAtUtc
    };
}

