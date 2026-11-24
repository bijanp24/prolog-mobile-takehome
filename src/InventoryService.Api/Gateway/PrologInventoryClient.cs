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
        // Page 1 tells us how many pages and records to expect. Everything after is checked against it.
        RecordsPageDto firstPage = await GetPageAsync(1, cancellationToken);
        int totalPages = firstPage.TotalPages ?? throw new UpstreamDataException("Page 1 did not include totalPages.");
        int totalRecords = firstPage.TotalRecords ?? throw new UpstreamDataException("Page 1 did not include totalRecords.");

        int expectedPages = (totalRecords + _pageSize - 1) / _pageSize; // ceiling division
        if (totalPages != expectedPages)
        {
            throw new UpstreamDataException(
                $"Page 1 says totalPages={totalPages}, but {totalRecords} records at {_pageSize} per page needs {expectedPages}.");
        }

        // Keyed by id: the live API repeats a record at some page boundaries (docs/api-findings.md #3).
        var recordsById = new Dictionary<int, InventoryRecord>();
        AddPageRecords(firstPage, 1, recordsById);

        // Pages are fetched one at a time: simple to follow in logs and in the debugger,
        // and kinder to the API's rate limit (5 requests per 10 seconds).
        for (int pageNumber = 2; pageNumber <= totalPages; pageNumber++)
        {
            RecordsPageDto page = await GetPageAsync(pageNumber, cancellationToken);

            if (page.TotalPages != totalPages || page.TotalRecords != totalRecords)
            {
                throw new UpstreamDataException(
                    $"Page {pageNumber} reports totalPages={page.TotalPages}, totalRecords={page.TotalRecords}, " +
                    $"but page 1 reported {totalPages} and {totalRecords}. The dataset changed or the API is inconsistent.");
            }

            AddPageRecords(page, pageNumber, recordsById);
        }

        // The final completeness check: a 200 on every page does not mean we got everything.
        if (recordsById.Count != totalRecords)
        {
            throw new UpstreamDataException(
                $"Expected {totalRecords} unique records but received {recordsById.Count} across {totalPages} pages.");
        }

        _logger.LogInformation("Fetched {RecordCount} unique records across {PageCount} pages.", recordsById.Count, totalPages);
        return recordsById.Values.ToList();
    }

    /// <summary>
    /// Validates each record on a page and adds it to the collection. A repeated id is fine if the
    /// data is identical (we keep one copy); a repeated id with different data means we cannot tell
    /// which is correct, so we fail rather than guess.
    /// </summary>
    private void AddPageRecords(RecordsPageDto page, int pageNumber, Dictionary<int, InventoryRecord> recordsById)
    {
        int duplicates = 0;

        foreach (RecordDto? rawRecord in page.Records!)
        {
            InventoryRecord record = ToDomainRecord(rawRecord, pageNumber);

            if (recordsById.TryGetValue(record.Id, out InventoryRecord? existing))
            {
                if (existing != record) // records compare by value, field by field
                {
                    throw new UpstreamDataException(
                        $"Record {record.Id} appears more than once with different data (seen again on page {pageNumber}).");
                }

                duplicates++;
                continue;
            }

            recordsById.Add(record.Id, record);
        }

        if (duplicates > 0)
        {
            _logger.LogInformation("Page {Page}: skipped {Count} duplicate record(s) already seen on an earlier page.", pageNumber, duplicates);
        }
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

            if (page.Page != pageNumber)
            {
                throw new UpstreamDataException($"Asked for page {pageNumber} but the API returned page {page.Page?.ToString() ?? "(missing)"}.");
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

    /// <summary>
    /// Converts a raw upstream record into a domain record, rejecting missing or invalid fields.
    /// The category is passed through as-is; cleaning it up is a business rule (see InventoryAggregator).
    /// </summary>
    private static InventoryRecord ToDomainRecord(RecordDto? raw, int pageNumber)
    {
        if (raw is null)
        {
            throw new UpstreamDataException($"Page {pageNumber} contained a null record.");
        }

        if (raw.Id is null)
        {
            throw new UpstreamDataException($"A record on page {pageNumber} is missing its id.");
        }

        int id = raw.Id.Value;

        if (string.IsNullOrWhiteSpace(raw.Name)) throw new UpstreamDataException($"Record {id} has no name.");
        if (string.IsNullOrWhiteSpace(raw.Category)) throw new UpstreamDataException($"Record {id} has no category.");
        if (raw.Price is null) throw new UpstreamDataException($"Record {id} has no price.");
        if (raw.Quantity is null) throw new UpstreamDataException($"Record {id} has no quantity.");
        if (raw.Price < 0) throw new UpstreamDataException($"Record {id} has a negative price ({raw.Price}).");
        if (raw.Quantity < 0) throw new UpstreamDataException($"Record {id} has a negative quantity ({raw.Quantity}).");

        if (!DateTimeOffset.TryParse(raw.LastUpdated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset lastUpdated))
        {
            throw new UpstreamDataException($"Record {id} has an invalid lastUpdated value '{raw.LastUpdated}'.");
        }

        return new InventoryRecord(id, raw.Name, raw.Category, raw.Price.Value, raw.Quantity.Value, lastUpdated);
    }
}
