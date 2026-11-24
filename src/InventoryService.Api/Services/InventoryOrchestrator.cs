using InventoryService.Api.Domain;
using InventoryService.Api.Domain.Models;
using InventoryService.Api.Gateway;

namespace InventoryService.Api.Services;

/// <summary>
/// Glue between the gateway (fetching) and the aggregator (business rules).
/// Kept deliberately thin: fetch everything, then aggregate.
/// </summary>
public class InventoryOrchestrator : IInventoryOrchestrator
{
    private readonly IInventoryGateway _gateway;
    private readonly IInventoryAggregator _aggregator;

    public InventoryOrchestrator(IInventoryGateway gateway, IInventoryAggregator aggregator)
    {
        _gateway = gateway;
        _aggregator = aggregator;
    }

    public async Task<InventorySummaryResponse> GetCategorySummariesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<InventoryRecord> records = await _gateway.GetAllRecordsAsync(cancellationToken);
        return _aggregator.Aggregate(records);
    }
}
