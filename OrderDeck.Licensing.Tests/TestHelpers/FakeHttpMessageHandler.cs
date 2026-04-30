namespace LiveDeck.Licensing.Tests.TestHelpers;

/// <summary>
/// Minimal DelegatingHandler that lets tests script HTTP responses by request.
/// Each Send call invokes the responder; tests assert on captured requests.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : this(req => Task.FromResult(responder(req))) { }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return await _responder(request);
    }

    public static HttpResponseMessage Json(int statusCode, string json) =>
        new((System.Net.HttpStatusCode)statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage Empty(int statusCode) =>
        new((System.Net.HttpStatusCode)statusCode);

    public static HttpResponseMessage Problem(int statusCode, string title, string? detail = null)
    {
        var problem = $$"""{"title":"{{title}}","detail":"{{detail ?? ""}}","status":{{statusCode}}}""";
        return new HttpResponseMessage((System.Net.HttpStatusCode)statusCode)
        {
            Content = new StringContent(problem, System.Text.Encoding.UTF8, "application/problem+json")
        };
    }
}
