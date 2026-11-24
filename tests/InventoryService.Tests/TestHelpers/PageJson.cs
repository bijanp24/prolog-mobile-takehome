using System.Text.Json;

namespace InventoryService.Tests.TestHelpers;

/// <summary>Builds upstream API JSON bodies so tests read like the real API responses.</summary>
public static class PageJson
{
    public static object Record(int id, string category = "Electronics", decimal price = 10m, int quantity = 1,
        string lastUpdated = "2026-10-15T12:30:00Z", string? name = null) => new
    {
        id,
        name = name ?? $"Item {id}",
        category,
        price,
        quantity,
        lastUpdated
    };

    public static string Page(int page, int totalPages, int totalRecords, int pageSize, params object[] records) =>
        JsonSerializer.Serialize(new
        {
            records,
            page,
            pageSize,
            totalPages,
            totalRecords
        });
}
