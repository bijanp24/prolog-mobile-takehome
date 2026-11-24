using InventoryService.Api.Domain.Models;

namespace InventoryService.Api.Gateway;

/// <summary>
/// Everything the rest of the app knows about the upstream Inventory API:
/// "give me every record". Paging, HTTP, retries and validation live behind this.
/// </summary>
public interface IInventoryGateway
{
    Task<IReadOnlyList<InventoryRecord>> GetAllRecordsAsync(CancellationToken cancellationToken);
}
