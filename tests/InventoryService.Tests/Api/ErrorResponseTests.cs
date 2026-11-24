using System.Net;
using System.Net.Http.Json;
using InventoryService.Api.Domain.Models;
using InventoryService.Api.Exceptions;
using InventoryService.Api.Gateway;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryService.Tests.Api;

/// <summary>
/// Runs the real app in memory with the gateway swapped for a stub, to prove that
/// upstream failures reach the caller as ProblemDetails with the right status code.
/// </summary>
public class ErrorResponseTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ErrorResponseTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClientWhoseGatewayThrows(Exception exception) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("InventoryApi:ApiKey", "test-key"); // startup validation needs a value
            builder.ConfigureServices(services =>
                services.AddScoped<IInventoryGateway>(_ => new ThrowingGateway(exception)));
        }).CreateClient();

    [Theory]
    [InlineData(typeof(UpstreamUnavailableException), HttpStatusCode.ServiceUnavailable)]
    [InlineData(typeof(UpstreamDataException), HttpStatusCode.BadGateway)]
    public async Task Upstream_failure_returns_problem_details(Type exceptionType, HttpStatusCode expectedStatus)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "upstream broke", null)!;
        HttpClient client = CreateClientWhoseGatewayThrows(exception);

        HttpResponseMessage response = await client.GetAsync("/api/inventory/categories");

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("upstream broke", problem?.Detail);
    }

    [Fact]
    public async Task Rejected_request_returns_502()
    {
        HttpClient client = CreateClientWhoseGatewayThrows(new UpstreamRejectedException(401, "bad key"));

        HttpResponseMessage response = await client.GetAsync("/api/inventory/categories");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Unexpected_exception_returns_500_without_leaking_details()
    {
        HttpClient client = CreateClientWhoseGatewayThrows(new InvalidOperationException("secret internals"));

        HttpResponseMessage response = await client.GetAsync("/api/inventory/categories");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("secret internals", body);
        Assert.DoesNotContain("InvalidOperationException", body);
    }

    private class ThrowingGateway : IInventoryGateway
    {
        private readonly Exception _exception;
        public ThrowingGateway(Exception exception) => _exception = exception;
        public Task<IReadOnlyList<InventoryRecord>> GetAllRecordsAsync(CancellationToken cancellationToken) => throw _exception;
    }
}
