# Inventory API: what the live API actually does

Probed on 2026-11-24 against `https://stg.prologmobile.com/api/records` (~120 requests,
page sizes 7, 20 and 25, three full passes at page size 20). Each finding below has a unit test.

| # | Finding | Evidence | How the service handles it |
|---|---------|----------|----------------------------|
| 1 | **Auth header is `X-Api-Key`.** Missing or wrong key gives 401 ProblemDetails: "A valid API key is required in the X-Api-Key header." | 401 without key and with a bad key | `ApiKeyHeaderName` = `X-Api-Key`. 401/403 are not retried, surface as 502 with "check the configured API key". |
| 2 | **Rate limit: 5 requests per 10 seconds.** Excess requests get 429 with `Retry-After: 8`. A full fetch is 8 pages at size 20, so **one call to our endpoint always trips it**. | Bursts of 7+ requests, 429 bodies and headers | 429 is retried; the retry waits for `Retry-After` instead of our own backoff (built into `HttpRetryStrategyOptions`). |
| 3 | **Pages overlap.** Every second page starts with the last record of the previous page, so pages alternate between `pageSize` and `pageSize + 1` records. Size 20: ids 1020, 1060, 1100, 1140 appear twice (161 rows, 157 unique). Same pattern at sizes 7 and 25. | Page boundaries at three page sizes | Duplicates are dropped by id **only if identical**; a duplicate id with different data fails the request (we cannot know which copy is right). Page record counts are not checked against `pageSize` because of this; the **unique count must equal `totalRecords`** instead. |
| 4 | **Category names are dirty**: 36 spellings of 7 categories, varying in case and surrounding spaces (`"  home & kitchen "`, `"OFFICE SUPPLIES"`, `" Toys"`). | All 157 records | Categories are trimmed and grouped ignoring case, then shown in title case (`"Home & Kitchen"`). |
| 5 | **"Discontinued" category: all 7 records have quantity 0.** 22 records in total have quantity 0. | Records 1151 to 1157 | Excluded by rule 1, so the category is omitted from the response entirely. |
| 6 | **Pages past the end return 200 with an empty list** (page 9 and 10 at size 20). | Pages 9, 10 | We only request `1..totalPages` from page 1, then check the unique count, so a short or empty page inside the range is caught. |
| 7 | **400 for bad paging** with clear messages: `pageSize` outside 1..25, `page` < 1 or non-numeric. | pageSize 0/26, page 0/-1/abc | `PageSize` is validated 1..25 at startup, so we never send these. A 400 is not retried; surfaces as 502. |
| 8 | **Data is static** between runs, as documented. Prices have at most 2 decimals, all dates are ISO 8601 UTC, no nulls, no negative values seen. | Three byte-identical passes | Still validated (missing fields, negative price/quantity, unparseable dates fail the request) because "a 200 is not a guarantee". |
| 9 | **No 500s observed** in ~120 requests. | n/a | Still handled: 5xx is retried, then surfaces as 503. |
