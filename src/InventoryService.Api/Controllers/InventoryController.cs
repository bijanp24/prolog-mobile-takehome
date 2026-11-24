using InventoryService.Api.Domain.Models;
using InventoryService.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace InventoryService.Api.Controllers;

[ApiController]
[Route("api/inventory")]
public class InventoryController : ControllerBase
{
    private readonly IInventoryOrchestrator _orchestrator;

    public InventoryController(IInventoryOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    /// <summary>Inventory aggregated by category, built from every page of the upstream API.</summary>
    /// <remarks>No try/catch here: upstream failures are turned into ProblemDetails by GlobalExceptionHandler.</remarks>
    [HttpGet("categories")]
    [ProducesResponseType(typeof(InventorySummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<InventorySummaryResponse>> GetCategories(CancellationToken cancellationToken)
    {
        InventorySummaryResponse summary = await _orchestrator.GetCategorySummariesAsync(cancellationToken);
        return Ok(summary);
    }
}
