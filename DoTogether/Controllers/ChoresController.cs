using DoTogether.Application.DTOs;
using DoTogether.Application.Interfaces;
using DoTogether.Application.Services;
using DoTogether.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoTogether.Controllers;

[ApiController]
[Route("api/households/{householdId:guid}/chores")]
[Authorize]
public class ChoresController(
    ChoreService choreService,
    HouseholdAccessService householdAccess,
    ICurrentUserService currentUser) : ControllerBase
{
    // ── Template CRUD ──

    /// <summary>Create a new chore template and generate initial occurrences. Any household member can do this.</summary>
    [HttpPost("templates")]
    [ProducesResponseType(typeof(ChoreTemplateDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateTemplate(
        Guid householdId, [FromBody] CreateChoreTemplateDto dto, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, currentUser.UserId, ct);
        var result = await choreService.CreateTemplateAsync(householdId, currentUser.UserId, dto, ct);
        return CreatedAtAction(nameof(GetTemplate), new { householdId, templateId = result.Id }, result);
    }

    /// <summary>List all chore templates for a household.</summary>
    [HttpGet("templates")]
    [ProducesResponseType(typeof(List<ChoreTemplateDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListTemplates(Guid householdId, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, currentUser.UserId, ct);
        var result = await choreService.ListTemplatesAsync(householdId, ct);
        return Ok(result);
    }

    /// <summary>Get a single chore template.</summary>
    [HttpGet("templates/{templateId:guid}")]
    [ProducesResponseType(typeof(ChoreTemplateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTemplate(Guid householdId, Guid templateId, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, currentUser.UserId, ct);
        var result = await choreService.GetTemplateAsync(templateId, householdId, ct);
        return Ok(result);
    }

    /// <summary>Update a chore template (partial update). Any household member can do this.</summary>
    [HttpPatch("templates/{templateId:guid}")]
    [ProducesResponseType(typeof(ChoreTemplateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateTemplate(
        Guid householdId, Guid templateId, [FromBody] UpdateChoreTemplateDto dto, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, currentUser.UserId, ct);
        var result = await choreService.UpdateTemplateAsync(templateId, householdId, dto, ct);
        return Ok(result);
    }

    /// <summary>Soft-delete a chore template. Any household member can do this.</summary>
    [HttpDelete("templates/{templateId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteTemplate(Guid householdId, Guid templateId, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, currentUser.UserId, ct);
        await choreService.SoftDeleteTemplateAsync(templateId, householdId, ct);
        return NoContent();
    }

    // ── Occurrence generation ──

    /// <summary>Trigger rolling generation of occurrences for all active templates.</summary>
    [HttpPost("generate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> GenerateOccurrences(Guid householdId, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, currentUser.UserId, ct);
        await choreService.GenerateOccurrencesForHouseholdAsync(householdId, ct);
        return NoContent();
    }

    // ── Idempotent occurrence mutations ──

    /// <summary>Mark an occurrence as completed. Idempotent via clientOperationId.</summary>
    [HttpPost("occurrences/{occurrenceId:guid}/complete")]
    [ProducesResponseType(typeof(ChoreOccurrenceDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Complete(
        Guid householdId, Guid occurrenceId, [FromBody] MutationRequestDto dto, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, currentUser.UserId, ct);
        var result = await choreService.MutateOccurrenceAsync(
            occurrenceId, householdId, currentUser.UserId,
            ChoreEventType.Completed, dto.ClientOperationId, null, ct);
        return Ok(result);
    }

    /// <summary>Undo completion (set back to pending). Idempotent via clientOperationId.</summary>
    [HttpPost("occurrences/{occurrenceId:guid}/undo")]
    [ProducesResponseType(typeof(ChoreOccurrenceDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Undo(
        Guid householdId, Guid occurrenceId, [FromBody] MutationRequestDto dto, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, currentUser.UserId, ct);
        var result = await choreService.MutateOccurrenceAsync(
            occurrenceId, householdId, currentUser.UserId,
            ChoreEventType.Undone, dto.ClientOperationId, null, ct);
        return Ok(result);
    }

    /// <summary>Skip an occurrence. Idempotent via clientOperationId.</summary>
    [HttpPost("occurrences/{occurrenceId:guid}/skip")]
    [ProducesResponseType(typeof(ChoreOccurrenceDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Skip(
        Guid householdId, Guid occurrenceId, [FromBody] MutationRequestDto dto, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, currentUser.UserId, ct);
        var result = await choreService.MutateOccurrenceAsync(
            occurrenceId, householdId, currentUser.UserId,
            ChoreEventType.Skipped, dto.ClientOperationId, null, ct);
        return Ok(result);
    }

    /// <summary>Reassign an occurrence to a different household member. Idempotent.</summary>
    [HttpPost("occurrences/{occurrenceId:guid}/reassign")]
    [ProducesResponseType(typeof(ChoreOccurrenceDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reassign(
        Guid householdId, Guid occurrenceId, [FromBody] ReassignRequestDto dto, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, currentUser.UserId, ct);
        var result = await choreService.ReassignOccurrenceAsync(
            occurrenceId, householdId, currentUser.UserId,
            dto.NewAssigneeId, dto.ClientOperationId, ct);
        return Ok(result);
    }
}
