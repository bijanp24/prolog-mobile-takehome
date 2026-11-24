# Inventory Aggregation Service

A small ASP.NET Core (.NET 8) service that reads every page of the PrologMobile Inventory API,
checks that the data is complete and consistent, aggregates it by category, and serves the
result at `GET /api/inventory/categories`.

## Prerequisites

- .NET 8 SDK (`dotnet --list-sdks` should list an 8.0.x entry)
- The Inventory API key from the assignment email

## Configure the API key

The key never lives in this repository. `appsettings.json` ships with `"ApiKey": ""`, and the
app **refuses to start** until a key is supplied (options are validated on startup). Pick one:

```bash
# Option A: user-secrets (stored in your user profile, outside the repo)
dotnet user-secrets set "InventoryApi:ApiKey" "<your-key>" --project src/InventoryService.Api

# Option B: environment variable (double underscore = section separator)
export InventoryApi__ApiKey="<your-key>"     # macOS / Linux
$env:InventoryApi__ApiKey = "<your-key>"     # Windows PowerShell
```

Other settings live in the `InventoryApi` section of `src/InventoryService.Api/appsettings.json`
(base URL, page size 1 to 25, retry count and base delay, timeout).

## Run

```bash
dotnet run --project src/InventoryService.Api
```

Then open <http://localhost:5084/api/inventory/categories>. The JSON is indented so it reads well
in a browser. Swagger UI is at <http://localhost:5084/swagger>.

In VS Code (C# Dev Kit), F5 launches the browser straight to the endpoint.
`src/InventoryService.Api/InventoryService.Api.http` has a one-click request for the REST Client extension.

**Expect a call to take about 10 to 12 seconds.** The upstream API allows 5 requests per 10 seconds and
a full fetch needs 8 pages, so the client always waits once for the rate limit (see below).

## Test

```bash
dotnet test
```

38 tests, no network needed:

- `Domain/InventoryAggregatorTests`: the business rules.
- `Gateway/PrologInventoryClientTests`: paging, retries and data validation, using a fake
  `HttpMessageHandler` behind the **same retry policy** the app uses (with zero delay).
- `Api/ErrorResponseTests`: runs the app in memory with a failing gateway and checks the caller
  gets ProblemDetails with the right status code and no internals.

## Endpoint

`GET /api/inventory/categories`

| Status | When | Body |
|--------|------|------|
| 200 | Every page fetched and validated | `{ "categories": [ ... ] }`, shape as in the assignment |
| 502 | Upstream rejected us (400, 401, 403, 404...) or sent data that is incomplete or inconsistent | ProblemDetails |
| 503 | Upstream unreachable, timed out, or still failing (5xx, 408, 429) after retries | ProblemDetails |
| 500 | A bug on our side | ProblemDetails with a generic message |

The response shape follows the assignment example. Categories are sorted alphabetically,
items by id.

## Architecture

```
HTTP request
  -> InventoryController         (Controllers/)  thin: calls the orchestrator, returns 200
  -> InventoryOrchestrator       (Services/)     fetch everything, then aggregate
       -> IInventoryGateway      (Gateway/)      PrologInventoryClient: HTTP, paging, validation
            -> retry handler     (Gateway/InventoryApiResilience)  below the HttpClient
       -> IInventoryAggregator   (Domain/)       pure business rules, no HTTP, no async
  exceptions -> GlobalExceptionHandler (Exceptions/)  ProblemDetails 502/503/500
```

**Why split here.** The assignment's hard part is getting a trustworthy dataset out of an
unreliable API, and the easy part is the arithmetic. So those two live on opposite sides of
one interface:

- **The gateway** owns everything about the upstream API: URLs, the `X-Api-Key` header, paging,
  status codes, JSON, and checking the data. Its output is a plain `List<InventoryRecord>` that
  has already been validated. If the API changes, this is the only place to change.
- **The aggregator** takes that list and applies the rules. It does not know HTTP exists, so its
  tests are plain object-in, object-out.
- **The orchestrator** is the seam between them. It is two lines today, and it is where a new
  business requirement (a filter, a cache, a second source) goes without touching either side.
- **Retries** sit in the `HttpClient` pipeline rather than in the client code, so the client
  reads as "one request per page" and each page is retried on its own (a failure on page 6 does
  not refetch pages 1 to 5).
- **Errors** are exceptions typed by what the caller should do about them (try later = 503,
  upstream is broken = 502). One handler maps them, so the controller has no try/catch.

Two projects only (API + tests). The folders give the separation; more projects would add
ceremony without adding clarity at this size.

### What the retry library does for us

`Microsoft.Extensions.Http.Resilience` (Polly v8) with **one** retry strategy, configured in
`Gateway/InventoryApiResilience.cs`. Deliberately not `AddStandardResilienceHandler`, which also
adds a circuit breaker, rate limiter and two timeouts we would not be using knowingly.

- Up to 3 retries after the first attempt.
- Exponential backoff from 500 ms (about 0.5 s, 1 s, 2 s) with jitter.
- If the response has a `Retry-After` header, that delay is used instead. This matters here:
  the API's 429 says `Retry-After: 8`.
- Retries only network errors, 5xx, 408 and 429. A 400/401/403/404 fails the same way every time,
  so it is not retried.
- `HttpClient.Timeout` (30 s) caps one page's attempts including waits.

## What the live API actually does

Full notes and evidence in [docs/api-findings.md](docs/api-findings.md). In short:

1. Auth is the `X-Api-Key` header; a missing or wrong key gives 401.
2. Rate limit of 5 requests per 10 seconds, 429 with `Retry-After: 8`. One full fetch trips it.
3. **Pages overlap**: every second page repeats the last record of the page before.
   At page size 20 there are 161 rows for 157 records.
4. **Category names are dirty**: 36 spellings of 7 categories (case and spaces).
5. The "Discontinued" category has only zero-quantity records.
6. Pages past the end return 200 with an empty list.

## Data checks (a 200 is not a guarantee)

The gateway fails the request with a 502 rather than return a partial or doubtful answer if:

- a body is not JSON, or has no `records` array;
- the `page` in the response is not the page we asked for;
- `totalPages` disagrees with `totalRecords / pageSize`, or the totals change between pages;
- the same id appears twice **with different data**;
- after de-duplication, the number of unique records is not `totalRecords`;
- a record is missing a field, has a negative price or quantity, or an unparseable `lastUpdated`.

## Assumptions and judgement calls

- **Duplicates.** An id repeated with identical data is the page-overlap quirk, so we keep one copy
  and log it. An id repeated with different data means we cannot know which is right, so we fail
  instead of guessing.
- **Categories.** Grouped after trimming and ignoring case, displayed in title case
  (`"  home & kitchen "` becomes `"Home & Kitchen"`). The assignment says "exactly one category per
  record", and treating 36 spellings as 36 categories would clearly be wrong. Title case was chosen
  over "most common spelling" because it is predictable.
- **Zero-quantity categories** are omitted, not shown with zeros. "Discontinued" therefore does not
  appear. This follows rule 1 literally: such records contribute to no figure.
- **Negative quantity or price** is treated as bad data and fails the request. None exist today.
- **Most recently updated tie** is broken by the lowest id, so the answer is stable.
- **Money** is `decimal` end to end and is not rounded. Upstream prices have two decimals, so totals do too.
- **All or nothing.** If any page is still failing after retries, the caller gets 503, not a
  partial result. A partial inventory total looks correct and is not.
- Requests are sequential. With this rate limit, parallel requests would only hit 429 sooner.

## Known limitations and what I would do next

- **Every call refetches everything and takes about 11 seconds** because of the rate limit. The
  dataset is static, so the obvious next step is caching the validated records with `IMemoryCache`
  (a few minutes) in the orchestrator. Concurrent callers also share the upstream rate limit today.
- **A client-side rate limiter** (pace requests at 5 per 10 s) would avoid the 429 round trip.
  Relying on `Retry-After` works and is simpler, so I stopped there.
- **No circuit breaker.** Under a sustained outage every request still makes its retries.
- `Retry-After` is trusted as sent; a very large value would be capped only by the 30 s timeout.
- No authentication on our own endpoint, no health check endpoint, no metrics beyond logs.
- Tests do not cover the real `Retry-After` wait (it would make the suite slow); that path was
  checked by hand against the live API.
- No 500 was ever seen from the live API in about 120 requests, so that path is tested only with fakes.
