# Inventory Aggregation Service

A small ASP.NET Core (.NET 8) service that reads every page of the PrologMobile Inventory API,
aggregates the records by category, and serves the result at `GET /api/inventory/categories`.

> Work in progress. Architecture notes, assumptions and known limitations will be filled in
> once the upstream API's real behaviour has been explored.

## Prerequisites

- .NET 8 SDK (`dotnet --list-sdks` should show an 8.0.x entry)

## Configure the API key

The key is never stored in this repository. `appsettings.json` ships with `"ApiKey": ""`, and
the app refuses to start until a key is supplied. Pick one:

```bash
# Option A: user-secrets (stored in your user profile, outside the repo)
dotnet user-secrets set "InventoryApi:ApiKey" "<your-key>" --project src/InventoryService.Api

# Option B: environment variable (double underscore = section separator)
export InventoryApi__ApiKey="<your-key>"          # macOS / Linux
$env:InventoryApi__ApiKey = "<your-key>"          # Windows PowerShell
```

## Run

```bash
dotnet run --project src/InventoryService.Api
```

Then open <http://localhost:5084/api/inventory/categories> (indented JSON in the browser)
or <http://localhost:5084/swagger>. In VS Code, F5 launches the browser straight to the endpoint,
and `src/InventoryService.Api/InventoryService.Api.http` has a one-click request.

## Test

```bash
dotnet test
```
