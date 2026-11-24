namespace InventoryService.Api.Gateway.Dtos;

/// <summary>
/// One page exactly as the upstream API sends it. Everything is nullable on purpose:
/// a missing field should be caught by our validation, not silently become 0 or "".
/// </summary>
public class RecordsPageDto
{
    public List<RecordDto?>? Records { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
    public int? TotalPages { get; set; }
    public int? TotalRecords { get; set; }
}

public class RecordDto
{
    public int? Id { get; set; }
    public string? Name { get; set; }
    public string? Category { get; set; }
    public decimal? Price { get; set; }
    public int? Quantity { get; set; }

    /// <summary>Kept as a string so we can report a bad date clearly instead of failing the whole page.</summary>
    public string? LastUpdated { get; set; }
}
