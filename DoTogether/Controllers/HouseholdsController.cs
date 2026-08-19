using DoTogether.Application.DTOs;
using DoTogether.Application.Interfaces;
using DoTogether.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoTogether.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class HouseholdsController(
    HouseholdService householdService,
    HouseholdAccessService householdAccess,
    ICurrentUserService currentUser) : ControllerBase
{
    /// <summary>Create a new household. The caller becomes Admin.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(HouseholdDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateHouseholdDto dto, CancellationToken ct)
    {
        var result = await householdService.CreateAsync(currentUser.UserId, dto, ct);
        return CreatedAtAction(nameof(GetById), new { householdId = result.Id }, result);
    }

    /// <summary>List all households for the current user.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<HouseholdDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var result = await householdService.GetForUserAsync(currentUser.UserId, ct);
        return Ok(result);
    }

    /// <summary>Get a household by ID. Caller must be a member.</summary>
    [HttpGet("{householdId:guid}")]
    [ProducesResponseType(typeof(HouseholdDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetById(Guid householdId, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, currentUser.UserId, ct);
        var result = await householdService.GetByIdAsync(householdId, ct);
        return Ok(result);
    }

    /// <summary>Update household details. Only admins can change household details.</summary>
    [HttpPatch("{householdId:guid}")]
    [ProducesResponseType(typeof(HouseholdDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid householdId, [FromBody] UpdateHouseholdDto dto, CancellationToken ct)
    {
        var result = await householdService.UpdateAsync(householdId, currentUser.UserId, dto, ct);
        return Ok(result);
    }

    /// <summary>Get the current invite token. Any household member can view invite tokens.</summary>
    [HttpGet("{householdId:guid}/invites/current")]
    [ProducesResponseType(typeof(InviteResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInviteToken(Guid householdId, CancellationToken ct)
    {
        var result = await householdService.GetInviteTokenAsync(householdId, currentUser.UserId, ct);
        return Ok(result);
    }

    /// <summary>Regenerate the invite token. Any household member can manage invite tokens.</summary>
    [HttpPost("{householdId:guid}/invites")]
    [ProducesResponseType(typeof(InviteResponseDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Invite(Guid householdId, CancellationToken ct)
    {
        var result = await householdService.RegenerateInviteTokenAsync(householdId, currentUser.UserId, ct);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>Update a household member role. Only admins can manage member roles.</summary>
    [HttpPatch("{householdId:guid}/members/{memberUserId:guid}/role")]
    [ProducesResponseType(typeof(HouseholdDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateMemberRole(
        Guid householdId,
        Guid memberUserId,
        [FromBody] UpdateHouseholdMemberRoleDto dto,
        CancellationToken ct)
    {
        var result = await householdService.UpdateMemberRoleAsync(householdId, currentUser.UserId, memberUserId, dto, ct);
        return Ok(result);
    }

    /// <summary>Remove a household member. Only admins can kick members.</summary>
    [HttpDelete("{householdId:guid}/members/{memberUserId:guid}")]
    [ProducesResponseType(typeof(HouseholdDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveMember(Guid householdId, Guid memberUserId, CancellationToken ct)
    {
        var result = await householdService.RemoveMemberAsync(householdId, currentUser.UserId, memberUserId, ct);
        return Ok(result);
    }

    /// <summary>Leave the current household. Any household member can leave.</summary>
    [HttpPost("{householdId:guid}/leave")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Leave(Guid householdId, CancellationToken ct)
    {
        await householdService.LeaveAsync(householdId, currentUser.UserId, ct);
        return NoContent();
    }

    /// <summary>Join a household using an invite token.</summary>
    [HttpPost("join")]
    [ProducesResponseType(typeof(HouseholdDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Join([FromBody] JoinHouseholdDto dto, CancellationToken ct)
    {
        var result = await householdService.JoinAsync(currentUser.UserId, dto, ct);
        return Ok(result);
    }
}
