using System.Net;
using System.Text;

namespace InventoryService.Tests.TestHelpers;

/// <summary>
/// Stands in for the network. Each test queues up the responses it wants, in order;
/// every request the client sends takes the next one off the queue.
/// Records every request so tests can check what was asked for (and how many times).
/// </summary>
public class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeHttpMessageHandler RespondWith(HttpStatusCode statusCode, string body = "")
    {
        _responses.Enqueue(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        return this;
    }

    public FakeHttpMessageHandler RespondWithJson(string json) => RespondWith(HttpStatusCode.OK, json);

    public FakeHttpMessageHandler Throw(Exception exception)
    {
        _responses.Enqueue(_ => throw exception);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException($"Test did not queue a response for request #{Requests.Count}: {request.RequestUri}");
        }

        return Task.FromResult(_responses.Dequeue()(request));
    }
}
