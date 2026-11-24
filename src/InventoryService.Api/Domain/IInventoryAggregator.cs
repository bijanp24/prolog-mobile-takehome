using InventoryService.Api.Domain.Models;

namespace InventoryService.Api.Domain;

/// <summary>Pure business logic: turns a full list of records into per-category figures.</summary>
public interface IInventoryAggregator
{
    InventorySummaryResponse Aggregate(IEnumerable<InventoryRecord> records);
}
