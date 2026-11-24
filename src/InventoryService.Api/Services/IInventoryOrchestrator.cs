using InventoryService.Api.Domain.Models;

namespace InventoryService.Api.Services;

public interface IInventoryOrchestrator
{
    Task<InventorySummaryResponse> GetCategorySummariesAsync(CancellationToken cancellationToken);
}
