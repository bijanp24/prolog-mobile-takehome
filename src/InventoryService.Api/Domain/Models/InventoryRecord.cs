namespace InventoryService.Api.Domain.Models;

/// <summary>
/// One validated inventory record. The gateway only creates these after checking the
/// upstream data, so the aggregator can trust every field.
/// </summary>
public record InventoryRecord(
    int Id,
    string Name,
    string Category,
    decimal Price,
    int Quantity,
    DateTimeOffset LastUpdated);
