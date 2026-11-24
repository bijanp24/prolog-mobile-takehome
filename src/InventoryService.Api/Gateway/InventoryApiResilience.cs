using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace InventoryService.Api.Gateway;

/// <summary>
/// The one resilience rule we use: retry transient failures with exponential backoff.
/// Program.cs and the unit tests both call AddRetry, so the tests exercise the real policy.
///
/// What it does for us:
///   - Retries up to maxRetries times after the first attempt.
///   - Waits baseDelay, then roughly 2x, 4x ... (exponential), with random jitter so many
///     clients do not retry in lockstep.
///   - Honours a Retry-After header if upstream sends one (built into HttpRetryStrategyOptions).
///   - Only retries what can succeed next time (see IsTransient). A 400 or 401 will fail
///     the same way again, so it is returned immediately.
/// </summary>
public static class InventoryApiResilience
{
    public static void AddRetry(
        ResiliencePipelineBuilder<HttpResponseMessage> pipeline,
        int maxRetries,
        TimeSpan baseDelay,
        ILogger logger)
    {
        if (maxRetries <= 0)
        {
            return; // Polly requires at least one retry; zero means "no retry strategy".
        }

        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = maxRetries,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = baseDelay,
            ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),
            OnRetry = args =>
            {
                string reason = args.Outcome.Exception?.Message
                    ?? $"HTTP {(int?)args.Outcome.Result?.StatusCode}";
                logger.LogWarning(
                    "Inventory API call failed ({Reason}). Retry {Attempt} of {MaxRetries} in {Delay} ms.",
                    reason, args.AttemptNumber + 1, maxRetries, (int)args.RetryDelay.TotalMilliseconds);
                return ValueTask.CompletedTask;
            }
        });
    }

    /// <summary>True for failures worth retrying: network errors, timeouts, 5xx, 408 and 429.</summary>
    public static bool IsTransient(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is HttpRequestException)
        {
            return true;
        }

        if (outcome.Result is null)
        {
            return false;
        }

        HttpStatusCode status = outcome.Result.StatusCode;
        return (int)status >= 500
            || status == HttpStatusCode.RequestTimeout
            || status == HttpStatusCode.TooManyRequests;
    }
}
