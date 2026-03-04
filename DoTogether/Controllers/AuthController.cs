using DoTogether.Application.DTOs;
using DoTogether.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace DoTogether.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(AuthService authService) : ControllerBase
{
    /// <summary>Register a new user with email, display name, and password.</summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto, CancellationToken ct)
    {
        var result = await authService.RegisterAsync(dto.Email, dto.DisplayName, dto.Password, ct);
        return CreatedAtAction(nameof(Register), result);
    }

    /// <summary>Login with email and password to receive JWT tokens.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Login([FromBody] LoginDto dto, CancellationToken ct)
    {
        var result = await authService.LoginAsync(dto.Email, dto.Password, ct);
        return Ok(result);
    }

    /// <summary>Refresh an access token using a refresh token.</summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(AuthResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequestDto dto, CancellationToken ct)
    {
        var result = await authService.RefreshAsync(dto.RefreshToken, ct);
        return Ok(result);
    }
}

