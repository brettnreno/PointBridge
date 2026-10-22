using System.Net;
using System.Text;

// Strict scripted transport: unexpected HTTP requests fail tests immediately.
public sealed class ContractTransport : HttpMessageHandler
{
    readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> expectations = new();
    public List<string> Seen { get; } = new();
    public void Expect(Func<HttpRequestMessage, Task<HttpResponseMessage>> request) => expectations.Enqueue(request);
    public void Complete() => Check(expectations.Count == 0, $"{expectations.Count} expected requests were never issued");
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Seen.Add($"{request.Method} {request.RequestUri}");
        Check(expectations.Count > 0, $"Unexpected {request.Method} {request.RequestUri}");
        return await expectations.Dequeue()(request);
    }
    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Events(string sse) =>
        new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
    public static void Check(bool condition, string description)
    { if (!condition) throw new InvalidOperationException(description); }
}
