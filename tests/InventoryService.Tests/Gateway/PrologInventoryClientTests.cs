using System.Net;
using InventoryService.Api.Configuration;
using InventoryService.Api.Domain.Models;
using InventoryService.Api.Exceptions;
using InventoryService.Api.Gateway;
using InventoryService.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static InventoryService.Tests.TestHelpers.PageJson;

namespace InventoryService.Tests.Gateway;

public class PrologInventoryClientTests
{
    private const int MaxRetries = 3;
    private readonly FakeHttpMessageHandler _fakeApi = new();

    /// <summary>
    /// Builds the client the same way Program.cs does: IHttpClientFactory, the real retry
    /// policy (with no delay so tests are fast), and our fake handler in place of the network.
    /// </summary>
    private PrologInventoryClient CreateClient(int pageSize = 2)
    {
        var services = new ServiceCollection();
        services
            .AddHttpClient("inventory-test", client => client.BaseAddress = new Uri("https://inventory.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => _fakeApi)
            .AddResilienceHandler("retry", pipeline =>
                InventoryApiResilience.AddRetry(pipeline, MaxRetries, baseDelay: TimeSpan.Zero, NullLogger.Instance));

        HttpClient httpClient = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient("inventory-test");

        var options = Options.Create(new InventoryApiOptions { PageSize = pageSize });
        return new PrologInventoryClient(httpClient, options, NullLogger<PrologInventoryClient>.Instance);
    }

    // ---- Happy path ------------------------------------------------------

    [Fact]
    public async Task Fetches_every_page_and_maps_records()
    {
        _fakeApi
            .RespondWithJson(Page(1, totalPages: 2, totalRecords: 3, pageSize: 2, Record(1), Record(2)))
            .RespondWithJson(Page(2, totalPages: 2, totalRecords: 3, pageSize: 2,
                Record(3, category: "Office", price: 4.5m, quantity: 7, lastUpdated: "2026-10-16T08:00:00Z", name: "Stapler")));

        IReadOnlyList<InventoryRecord> records = await CreateClient().GetAllRecordsAsync(CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3 }, records.Select(r => r.Id));
        Assert.Equal(new InventoryRecord(3, "Stapler", "Office", 4.5m, 7, new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.Zero)), records[2]);
        Assert.Equal(
            new[] { "/api/records?page=1&pageSize=2", "/api/records?page=2&pageSize=2" },
            _fakeApi.Requests.Select(r => r.RequestUri!.PathAndQuery));
    }

    // ---- Retry behaviour -------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Transient_status_is_retried_and_then_succeeds(HttpStatusCode transientStatus)
    {
        _fakeApi
            .RespondWith(transientStatus)
            .RespondWithJson(Page(1, totalPages: 1, totalRecords: 1, pageSize: 2, Record(1)));

        IReadOnlyList<InventoryRecord> records = await CreateClient().GetAllRecordsAsync(CancellationToken.None);

        Assert.Single(records);
        Assert.Equal(2, _fakeApi.Requests.Count);
    }

    [Fact]
    public async Task Network_error_is_retried_and_then_succeeds()
    {
        _fakeApi
            .Throw(new HttpRequestException("Connection reset"))
            .RespondWithJson(Page(1, totalPages: 1, totalRecords: 1, pageSize: 2, Record(1)));

        IReadOnlyList<InventoryRecord> records = await CreateClient().GetAllRecordsAsync(CancellationToken.None);

        Assert.Single(records);
        Assert.Equal(2, _fakeApi.Requests.Count);
    }

    [Fact]
    public async Task A_failing_later_page_is_retried_without_refetching_earlier_pages()
    {
        _fakeApi
            .RespondWithJson(Page(1, totalPages: 2, totalRecords: 3, pageSize: 2, Record(1), Record(2)))
            .RespondWith(HttpStatusCode.InternalServerError)
            .RespondWithJson(Page(2, totalPages: 2, totalRecords: 3, pageSize: 2, Record(3)));

        IReadOnlyList<InventoryRecord> records = await CreateClient().GetAllRecordsAsync(CancellationToken.None);

        Assert.Equal(3, records.Count);
        Assert.Equal(
            new[] { "page=1", "page=2", "page=2" },
            _fakeApi.Requests.Select(r => r.RequestUri!.Query.Split('&')[0].TrimStart('?')));
    }

    [Fact]
    public async Task Persistent_500_gives_up_after_max_retries_as_unavailable()
    {
        for (int i = 0; i < MaxRetries + 1; i++)
        {
            _fakeApi.RespondWith(HttpStatusCode.InternalServerError);
        }

        await Assert.ThrowsAsync<UpstreamUnavailableException>(
            () => CreateClient().GetAllRecordsAsync(CancellationToken.None));

        Assert.Equal(MaxRetries + 1, _fakeApi.Requests.Count); // first attempt + retries
    }

    [Fact]
    public async Task Persistent_network_error_gives_up_as_unavailable()
    {
        for (int i = 0; i < MaxRetries + 1; i++)
        {
            _fakeApi.Throw(new HttpRequestException("No such host"));
        }

        await Assert.ThrowsAsync<UpstreamUnavailableException>(
            () => CreateClient().GetAllRecordsAsync(CancellationToken.None));

        Assert.Equal(MaxRetries + 1, _fakeApi.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Client_error_is_not_retried_and_is_reported_as_rejected(HttpStatusCode status)
    {
        _fakeApi.RespondWith(status);

        var ex = await Assert.ThrowsAsync<UpstreamRejectedException>(
            () => CreateClient().GetAllRecordsAsync(CancellationToken.None));

        Assert.Equal((int)status, ex.StatusCode);
        Assert.Single(_fakeApi.Requests);
    }

    // ---- 200 responses we cannot use -------------------------------------

    [Fact]
    public async Task Body_that_is_not_json_is_a_data_error()
    {
        _fakeApi.RespondWithJson("<html>maintenance</html>");

        await Assert.ThrowsAsync<UpstreamDataException>(
            () => CreateClient().GetAllRecordsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Missing_records_array_is_a_data_error()
    {
        _fakeApi.RespondWithJson("""{ "page": 1, "pageSize": 2, "totalPages": 1, "totalRecords": 0 }""");

        await Assert.ThrowsAsync<UpstreamDataException>(
            () => CreateClient().GetAllRecordsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Record_with_unparseable_date_is_a_data_error()
    {
        _fakeApi.RespondWithJson(Page(1, totalPages: 1, totalRecords: 1, pageSize: 2, Record(1, lastUpdated: "yesterday")));

        var ex = await Assert.ThrowsAsync<UpstreamDataException>(
            () => CreateClient().GetAllRecordsAsync(CancellationToken.None));

        Assert.Contains("Record 1", ex.Message);
    }
}
