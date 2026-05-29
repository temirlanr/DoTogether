using DoTogether.Application.DTOs;
using DoTogether.Application.Interfaces;
using DoTogether.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoTogether.Controllers;

[ApiController]
[Route("api/households/{householdId:guid}/meal-plans")]
[Authorize]
public class MealPlansController(
    MealPlanService mealPlanService,
    ICurrentUserService currentUser) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<MealPlanEntryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid householdId, [FromQuery] DateRangeQueryDto query, CancellationToken ct)
    {
        var result = await mealPlanService.ListAsync(householdId, currentUser.UserId, query.From, query.To, ct);
        return Ok(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(MealPlanEntryDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(Guid householdId, [FromBody] MealPlanEntryUpsertDto dto, CancellationToken ct)
    {
        var result = await mealPlanService.CreateAsync(householdId, currentUser.UserId, dto, ct);
        return CreatedAtAction(nameof(List), new { householdId, from = result.Date, to = result.Date }, result);
    }

    [HttpPut("{entryId:guid}")]
    [ProducesResponseType(typeof(MealPlanEntryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        Guid householdId,
        Guid entryId,
        [FromBody] MealPlanEntryUpsertDto dto,
        CancellationToken ct)
    {
        var result = await mealPlanService.UpdateAsync(householdId, entryId, currentUser.UserId, dto, ct);
        return Ok(result);
    }

    [HttpDelete("{entryId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid householdId, Guid entryId, CancellationToken ct)
    {
        await mealPlanService.DeleteAsync(householdId, entryId, currentUser.UserId, ct);
        return NoContent();
    }

    [HttpGet("grocery-list")]
    [ProducesResponseType(typeof(GroceryListDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GroceryList(Guid householdId, [FromQuery] DateRangeQueryDto query, CancellationToken ct)
    {
        var result = await mealPlanService.GenerateGroceryListAsync(householdId, currentUser.UserId, query.From, query.To, ct);
        return Ok(result);
    }
}