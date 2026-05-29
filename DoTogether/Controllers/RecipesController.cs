using DoTogether.Application.DTOs;
using DoTogether.Application.Interfaces;
using DoTogether.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DoTogether.Controllers;

[ApiController]
[Route("api/households/{householdId:guid}/recipes")]
[Authorize]
public class RecipesController(
    RecipeService recipeService,
    RecipeImportService recipeImportService,
    ICurrentUserService currentUser) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<RecipeSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListRecipes(
        Guid householdId,
        [FromQuery] bool includeArchived,
        CancellationToken ct)
    {
        var result = await recipeService.ListAsync(householdId, currentUser.UserId, includeArchived, ct);
        return Ok(result);
    }

    [HttpGet("{recipeId:guid}")]
    [ProducesResponseType(typeof(RecipeDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecipe(Guid householdId, Guid recipeId, CancellationToken ct)
    {
        var result = await recipeService.GetAsync(householdId, recipeId, currentUser.UserId, ct);
        return Ok(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(RecipeDetailDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateRecipe(Guid householdId, [FromBody] RecipeUpsertDto dto, CancellationToken ct)
    {
        var result = await recipeService.CreateAsync(householdId, currentUser.UserId, dto, ct);
        return CreatedAtAction(nameof(GetRecipe), new { householdId, recipeId = result.Id }, result);
    }

    [HttpPut("{recipeId:guid}")]
    [ProducesResponseType(typeof(RecipeDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateRecipe(
        Guid householdId,
        Guid recipeId,
        [FromBody] RecipeUpsertDto dto,
        CancellationToken ct)
    {
        var result = await recipeService.UpdateAsync(householdId, recipeId, currentUser.UserId, dto, ct);
        return Ok(result);
    }

    [HttpDelete("{recipeId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ArchiveRecipe(Guid householdId, Guid recipeId, CancellationToken ct)
    {
        await recipeService.ArchiveAsync(householdId, recipeId, currentUser.UserId, ct);
        return NoContent();
    }

    [HttpPost("import-preview")]
    [EnableRateLimiting("recipe-import")]
    [ProducesResponseType(typeof(RecipeImportPreviewDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewImport(
        Guid householdId,
        [FromBody] RecipeImportRequestDto dto,
        CancellationToken ct)
    {
        var result = await recipeImportService.PreviewAsync(householdId, currentUser.UserId, dto, ct);
        return Ok(result);
    }
}