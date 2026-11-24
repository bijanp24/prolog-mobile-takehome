using InventoryService.Api.Domain;
using InventoryService.Api.Domain.Models;

namespace InventoryService.Tests.Domain;

public class InventoryAggregatorTests
{
    private readonly InventoryAggregator _aggregator = new();

    private static readonly DateTimeOffset BaseTime = new(2026, 10, 15, 12, 30, 0, TimeSpan.Zero);

    private static InventoryRecord Record(int id, string category, decimal price, int quantity, DateTimeOffset? lastUpdated = null) =>
        new(id, $"Item {id}", category, price, quantity, lastUpdated ?? BaseTime);

    [Fact]
    public void Empty_input_returns_no_categories()
    {
        InventorySummaryResponse result = _aggregator.Aggregate(new List<InventoryRecord>());

        Assert.Empty(result.Categories);
    }

    [Fact]
    public void Zero_quantity_records_are_excluded_from_every_figure()
    {
        var records = new[]
        {
            Record(1, "Electronics", price: 10m, quantity: 2),
            Record(2, "Electronics", price: 999m, quantity: 0, lastUpdated: BaseTime.AddDays(5)),
        };

        CategorySummary electronics = Assert.Single(_aggregator.Aggregate(records).Categories);

        Assert.Equal(20m, electronics.TotalInventoryValue);
        Assert.Equal(1, electronics.AvailableRecordCount);
        Assert.Equal(1, electronics.MostRecentlyUpdated.Id); // the newer record 2 is out of stock
        Assert.Equal(new[] { 1 }, electronics.Items.Select(i => i.Id));
    }

    [Fact]
    public void Category_with_only_zero_quantity_records_is_omitted()
    {
        var records = new[]
        {
            Record(1, "Electronics", 10m, 1),
            Record(2, "Furniture", 50m, 0),
        };

        CategorySummary only = Assert.Single(_aggregator.Aggregate(records).Categories);
        Assert.Equal("Electronics", only.Category);
    }

    [Fact]
    public void Records_are_grouped_by_category_in_alphabetical_order()
    {
        var records = new[]
        {
            Record(1, "Office", 1m, 1),
            Record(2, "Electronics", 1m, 1),
            Record(3, "Office", 1m, 1),
        };

        InventorySummaryResponse result = _aggregator.Aggregate(records);

        Assert.Equal(new[] { "Electronics", "Office" }, result.Categories.Select(c => c.Category));
        Assert.Equal(2, result.Categories[1].AvailableRecordCount);
    }

    [Fact]
    public void Total_value_is_sum_of_price_times_quantity_with_exact_decimals()
    {
        var records = new[]
        {
            Record(1, "Electronics", 149.99m, 12), // 1799.88
            Record(2, "Electronics", 0.10m, 3),    //    0.30
            Record(3, "Electronics", 0.01m, 1),    //    0.01
        };

        CategorySummary electronics = Assert.Single(_aggregator.Aggregate(records).Categories);

        Assert.Equal(1800.19m, electronics.TotalInventoryValue);
    }

    [Fact]
    public void Most_recently_updated_is_the_record_with_the_latest_timestamp()
    {
        var records = new[]
        {
            Record(1, "Electronics", 1m, 1, BaseTime),
            Record(2, "Electronics", 1m, 1, BaseTime.AddHours(3)),
            Record(3, "Electronics", 1m, 1, BaseTime.AddHours(1)),
        };

        CategorySummary electronics = Assert.Single(_aggregator.Aggregate(records).Categories);

        Assert.Equal(2, electronics.MostRecentlyUpdated.Id);
        Assert.Equal("Item 2", electronics.MostRecentlyUpdated.Name);
        Assert.Equal(BaseTime.AddHours(3), electronics.MostRecentlyUpdated.LastUpdated);
    }

    [Fact]
    public void Most_recently_updated_tie_is_broken_by_lowest_id()
    {
        var records = new[]
        {
            Record(7, "Electronics", 1m, 1, BaseTime),
            Record(3, "Electronics", 1m, 1, BaseTime),
            Record(5, "Electronics", 1m, 1, BaseTime),
        };

        CategorySummary electronics = Assert.Single(_aggregator.Aggregate(records).Categories);

        Assert.Equal(3, electronics.MostRecentlyUpdated.Id);
    }

    [Fact]
    public void Items_carry_the_records_used_sorted_by_id()
    {
        var records = new[]
        {
            Record(30, "Electronics", 2.50m, 4),
            Record(10, "Electronics", 1.25m, 2),
        };

        CategorySummary electronics = Assert.Single(_aggregator.Aggregate(records).Categories);

        Assert.Equal(new[] { 10, 30 }, electronics.Items.Select(i => i.Id));
        InventoryItem first = electronics.Items[0];
        Assert.Equal("Item 10", first.Name);
        Assert.Equal(1.25m, first.Price);
        Assert.Equal(2, first.Quantity);
        Assert.Equal(BaseTime, first.LastUpdated);
    }
}
