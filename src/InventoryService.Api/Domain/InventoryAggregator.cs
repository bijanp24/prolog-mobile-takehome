using System.Globalization;
using InventoryService.Api.Domain.Models;

namespace InventoryService.Api.Domain;

/// <summary>
/// Applies the business rules from the assignment. No HTTP, no async, no I/O:
/// a list of records goes in, the category summaries come out.
/// </summary>
public class InventoryAggregator : IInventoryAggregator
{
    public InventorySummaryResponse Aggregate(IEnumerable<InventoryRecord> records)
    {
        // Rule 1: a quantity of zero is not available stock, so it counts for nothing.
        List<InventoryRecord> availableRecords = records
            .Where(record => record.Quantity != 0)
            .ToList();

        // Rule 2: one group per category, alphabetical so the output is always in the same order.
        // Upstream spells categories inconsistently ("  electronics ", "ELECTRONICS"), so we group
        // on a cleaned-up name. A category whose records are all zero-quantity has no group, so it is left out.
        var groups = availableRecords
            .GroupBy(record => NormalizeCategory(record.Category))
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        var categories = new List<CategorySummary>();
        foreach (var group in groups)
        {
            categories.Add(BuildCategorySummary(group.Key, group.ToList()));
        }

        return new InventorySummaryResponse(categories);
    }

    /// <summary>
    /// "  home & kitchen " and "HOME & KITCHEN" both become "Home & Kitchen":
    /// trim the spaces, then title-case so every spelling maps to one name.
    /// </summary>
    public static string NormalizeCategory(string category)
    {
        string trimmed = category.Trim().ToLowerInvariant();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed);
    }

    private static CategorySummary BuildCategorySummary(string category, List<InventoryRecord> records)
    {
        // Rule 3: total value = sum of price x quantity. decimal keeps cents exact.
        decimal totalInventoryValue = 0m;
        foreach (InventoryRecord record in records)
        {
            totalInventoryValue += record.Price * record.Quantity;
        }

        // Rule 5: newest lastUpdated wins; on a tie the lowest id wins, so the answer is stable.
        InventoryRecord mostRecent = records
            .OrderByDescending(record => record.LastUpdated)
            .ThenBy(record => record.Id)
            .First();

        // Rule 6: the records the figures were computed from, sorted by id.
        List<InventoryItem> items = records
            .OrderBy(record => record.Id)
            .Select(record => new InventoryItem(record.Id, record.Name, record.Price, record.Quantity, record.LastUpdated))
            .ToList();

        return new CategorySummary(
            Category: category,
            TotalInventoryValue: totalInventoryValue,
            AvailableRecordCount: records.Count, // Rule 4
            MostRecentlyUpdated: new RecordReference(mostRecent.Id, mostRecent.Name, mostRecent.LastUpdated),
            Items: items);
    }
}
