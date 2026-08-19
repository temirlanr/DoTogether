using DoTogether.Application.DTOs;
using DoTogether.Application.Interfaces;
using DoTogether.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DevicesController(
    IAppDbContext db,
    ICurrentUserService currentUser) : ControllerBase
{
    /// <summary>Register or update a device push token (FCM / APNs).</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceDto dto, CancellationToken ct)
    {
        var userId = currentUser.UserId;

        var existing = await db.DevicePushTokens
            .FirstOrDefaultAsync(d => d.UserId == userId
                                      && d.Platform == dto.Platform
                                      && d.Token == dto.Token, ct);

        if (existing is null)
        {
            db.DevicePushTokens.Add(new DevicePushToken
            {
                UserId = userId,
                Platform = dto.Platform,
                Token = dto.Token
            });
            await db.SaveChangesAsync(ct);
        }
        else if (existing.IsDeleted)
        {
            // Unregister soft-deletes the row; re-registering must resurrect it.
            existing.IsDeleted = false;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    /// <summary>Remove a device push token.</summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Unregister([FromBody] RegisterDeviceDto dto, CancellationToken ct)
    {
        var userId = currentUser.UserId;

        var existing = await db.DevicePushTokens
            .FirstOrDefaultAsync(d => d.UserId == userId
                                      && d.Platform == dto.Platform
                                      && d.Token == dto.Token, ct);

        if (existing is not null)
        {
            existing.IsDeleted = true;
            await db.SaveChangesAsync(ct);
        }

        return NoContent();
    }
}
