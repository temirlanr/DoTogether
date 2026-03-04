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

    /// <summary>Get a household by ID.</summary>
    [HttpGet("{householdId:guid}")]
    [ProducesResponseType(typeof(HouseholdDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetById(Guid householdId, CancellationToken ct)
    {
        var result = await householdService.GetByIdAsync(householdId, ct);
        return Ok(result);
    }

    /// <summary>Invite a member by email. Only admins can invite.</summary>
    [HttpPost("{householdId:guid}/invites")]
    [ProducesResponseType(typeof(InviteResponseDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Invite(Guid householdId, [FromBody] InviteMemberDto dto, CancellationToken ct)
    {
        var result = await householdService.InviteMemberAsync(householdId, currentUser.UserId, dto, ct);
        return StatusCode(StatusCodes.Status201Created, result);
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
