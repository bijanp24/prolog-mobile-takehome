using System.ComponentModel.DataAnnotations;

namespace InventoryService.Api.Configuration;

/// <summary>
/// Settings for the upstream Inventory API, bound from the "InventoryApi" config section.
/// The ApiKey is deliberately blank in appsettings.json: set it with user-secrets or the
/// InventoryApi__ApiKey environment variable (see README).
/// </summary>
public class InventoryApiOptions
{
    public const string SectionName = "InventoryApi";

    [Required, Url]
    public string BaseUrl { get; set; } = string.Empty;

    [Required(ErrorMessage = "InventoryApi:ApiKey is not set. Use dotnet user-secrets or the InventoryApi__ApiKey environment variable.")]
    public string ApiKey { get; set; } = string.Empty;

    [Required]
    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";

    /// <summary>Records per page. The upstream API accepts 1 to 25.</summary>
    [Range(1, 25)]
    public int PageSize { get; set; } = 20;

    /// <summary>Retries after the first attempt, for transient failures only.</summary>
    [Range(0, 10)]
    public int MaxRetries { get; set; } = 3;

    [Range(0, 60_000)]
    public int RetryBaseDelayMilliseconds { get; set; } = 500;

    /// <summary>Overall time budget for one upstream request, retries included.</summary>
    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 30;
}
