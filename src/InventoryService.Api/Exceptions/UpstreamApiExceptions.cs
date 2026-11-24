namespace InventoryService.Api.Exceptions;

/// <summary>Base type for every failure that comes from the upstream Inventory API.</summary>
public abstract class UpstreamApiException : Exception
{
    protected UpstreamApiException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Upstream could not be reached or kept failing after retries
/// (network error, timeout, 5xx, 408, 429). We answer 503: "try again later".
/// </summary>
public class UpstreamUnavailableException : UpstreamApiException
{
    public UpstreamUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Upstream refused our request (400, 401, 403, 404, ...). Retrying will not help,
/// it usually means our configuration or request is wrong. We answer 502.
/// </summary>
public class UpstreamRejectedException : UpstreamApiException
{
    public int StatusCode { get; }

    public UpstreamRejectedException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Upstream said 200 but the data is unusable: bad JSON, missing fields, or the pages
/// do not add up to a complete, consistent dataset. We answer 502.
/// </summary>
public class UpstreamDataException : UpstreamApiException
{
    public UpstreamDataException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
