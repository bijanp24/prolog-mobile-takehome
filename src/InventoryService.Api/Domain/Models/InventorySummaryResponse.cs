namespace InventoryService.Api.Domain.Models;

/// <summary>The body returned by GET /api/inventory/categories.</summary>
public record InventorySummaryResponse(IReadOnlyList<CategorySummary> Categories);

public record CategorySummary(
    string Category,
    decimal TotalInventoryValue,
    int AvailableRecordCount,
    RecordReference MostRecentlyUpdated,
    IReadOnlyList<InventoryItem> Items);

/// <summary>Short pointer to a record: just enough to identify it.</summary>
public record RecordReference(int Id, string Name, DateTimeOffset LastUpdated);

/// <summary>A record as shown inside its category (category is implied by the parent).</summary>
public record InventoryItem(int Id, string Name, decimal Price, int Quantity, DateTimeOffset LastUpdated);
