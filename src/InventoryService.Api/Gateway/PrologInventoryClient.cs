using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryService.Api.Configuration;
using InventoryService.Api.Domain.Models;
using InventoryService.Api.Exceptions;
using InventoryService.Api.Gateway.Dtos;
using Microsoft.Extensions.Options;

namespace InventoryService.Api.Gateway;

/// <summary>
/// Talks to the upstream Inventory API: walks every page, turns HTTP problems into our own
/// exceptions, and converts the raw DTOs into validated domain records.
/// Retries happen below this class, in the resilience handler (see InventoryApiResilience).
/// </summary>
public class PrologInventoryClient : IInventoryGateway
{
    private const string RecordsPath = "api/records";

    private readonly HttpClient _httpClient;
    private readonly ILogger<PrologInventoryClient> _logger;
    private readonly int _pageSize;

    public PrologInventoryClient(HttpClient httpClient, IOptions<InventoryApiOptions> options, ILogger<PrologInventoryClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _pageSize = options.Value.PageSize;
    }

    public async Task<IReadOnlyList<InventoryRecord>> GetAllRecordsAsync(CancellationToken cancellationToken)
    {
        // Page 1 tells us how many pages there are.
        RecordsPageDto firstPage = await GetPageAsync(1, cancellationToken);
        int totalPages = firstPage.TotalPages ?? throw new UpstreamDataException("Page 1 did not include totalPages.");

        var rawRecords = new List<RecordDto?>(firstPage.Records!);

        // Pages are fetched one at a time: simple to follow in logs and in the debugger.
        for (int pageNumber = 2; pageNumber <= totalPages; pageNumber++)
        {
            RecordsPageDto page = await GetPageAsync(pageNumber, cancellationToken);
            rawRecords.AddRange(page.Records!);
        }

        var records = new List<InventoryRecord>();
        foreach (RecordDto? rawRecord in rawRecords)
        {
            records.Add(ToDomainRecord(rawRecord));
        }

        _logger.LogInformation("Fetched {RecordCount} records across {PageCount} pages.", records.Count, totalPages);
        return records;
    }

    /// <summary>
    /// Fetches one page. Returns only a 200 with a readable body; every other outcome
    /// becomes one of our UpstreamApiException types.
    /// </summary>
    private async Task<RecordsPageDto> GetPageAsync(int pageNumber, CancellationToken cancellationToken)
    {
        string url = $"{RecordsPath}?page={pageNumber}&pageSize={_pageSize}";
        _logger.LogInformation("Requesting page {Page} ({Url})", pageNumber, url);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(url, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // Network-level failure (DNS, refused connection, TLS...) that survived all retries.
            throw new UpstreamUnavailableException($"Could not reach the Inventory API for page {pageNumber}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout fired. (If the *caller* cancelled, we let that exception through.)
            throw new UpstreamUnavailableException($"The Inventory API timed out on page {pageNumber}.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw CreateExceptionForStatus(response.StatusCode, pageNumber);
            }

            RecordsPageDto? page;
            try
            {
                page = await response.Content.ReadFromJsonAsync<RecordsPageDto>(cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new UpstreamDataException($"Page {pageNumber} was not valid JSON: {ex.Message}", ex);
            }

            if (page?.Records is null)
            {
                throw new UpstreamDataException($"Page {pageNumber} had no records array.");
            }

            _logger.LogInformation("Page {Page} returned {Count} records.", pageNumber, page.Records.Count);
            return page;
        }
    }

    private UpstreamApiException CreateExceptionForStatus(HttpStatusCode statusCode, int pageNumber)
    {
        int code = (int)statusCode;
        _logger.LogWarning("Inventory API returned HTTP {StatusCode} for page {Page}.", code, pageNumber);

        bool isTransient = code >= 500
            || statusCode == HttpStatusCode.RequestTimeout
            || statusCode == HttpStatusCode.TooManyRequests;

        if (isTransient)
        {
            // Retries have already been used up by the resilience handler.
            return new UpstreamUnavailableException($"The Inventory API kept failing with HTTP {code} on page {pageNumber}.");
        }

        string hint = statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? " Check the configured API key."
            : string.Empty;
        return new UpstreamRejectedException(code, $"The Inventory API rejected page {pageNumber} with HTTP {code}.{hint}");
    }

    /// <summary>Converts a raw upstream record into a domain record, rejecting missing or invalid fields.</summary>
    private static InventoryRecord ToDomainRecord(RecordDto? raw)
    {
        if (raw is null)
        {
            throw new UpstreamDataException("A page contained a null record.");
        }

        if (raw.Id is null)
        {
            throw new UpstreamDataException("A record is missing its id.");
        }

        int id = raw.Id.Value;

        if (string.IsNullOrWhiteSpace(raw.Name)) throw new UpstreamDataException($"Record {id} has no name.");
        if (string.IsNullOrWhiteSpace(raw.Category)) throw new UpstreamDataException($"Record {id} has no category.");
        if (raw.Price is null) throw new UpstreamDataException($"Record {id} has no price.");
        if (raw.Quantity is null) throw new UpstreamDataException($"Record {id} has no quantity.");

        if (!DateTimeOffset.TryParse(raw.LastUpdated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset lastUpdated))
        {
            throw new UpstreamDataException($"Record {id} has an invalid lastUpdated value '{raw.LastUpdated}'.");
        }

        return new InventoryRecord(id, raw.Name, raw.Category, raw.Price.Value, raw.Quantity.Value, lastUpdated);
    }
}
