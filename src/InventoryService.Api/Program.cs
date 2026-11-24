using InventoryService.Api.Configuration;
using InventoryService.Api.Domain;
using InventoryService.Api.Exceptions;
using InventoryService.Api.Gateway;
using InventoryService.Api.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration -------------------------------------------------------
// Fails at startup (not at first request) if BaseUrl/ApiKey/PageSize are missing or invalid.
builder.Services
    .AddOptions<InventoryApiOptions>()
    .Bind(builder.Configuration.GetSection(InventoryApiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// ---- Our own services ----------------------------------------------------
builder.Services.AddSingleton<IInventoryAggregator, InventoryAggregator>(); // stateless, pure logic
builder.Services.AddScoped<IInventoryOrchestrator, InventoryOrchestrator>();

// ---- Upstream API client -------------------------------------------------
// Typed client: IHttpClientFactory creates the HttpClient and manages handler lifetimes.
builder.Services
    .AddHttpClient<IInventoryGateway, PrologInventoryClient>((serviceProvider, client) =>
    {
        InventoryApiOptions options = serviceProvider.GetRequiredService<IOptions<InventoryApiOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
        client.DefaultRequestHeaders.Add(options.ApiKeyHeaderName, options.ApiKey);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds); // covers all retries of one call
    })
    .AddResilienceHandler("inventory-api-retry", (pipeline, context) =>
    {
        InventoryApiOptions options = context.ServiceProvider.GetRequiredService<IOptions<InventoryApiOptions>>().Value;
        ILogger logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("InventoryApiRetry");
        InventoryApiResilience.AddRetry(
            pipeline,
            options.MaxRetries,
            TimeSpan.FromMilliseconds(options.RetryBaseDelayMilliseconds),
            logger);
    });

// ---- Web plumbing --------------------------------------------------------
builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.WriteIndented = true); // readable in a browser

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();

app.Run();

// Lets the test project reference the app's entry point (WebApplicationFactory<Program>).
public partial class Program { }
