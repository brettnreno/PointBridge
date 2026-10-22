using System.Text.Json.Nodes;
public class Api : IDisposable
{
    readonly HttpClient http;
    readonly Connection c;
    readonly string password;
    public string Origin => c.BaseUrl.TrimEnd('/');
    string? token;
    DateTimeOffset expires = DateTimeOffset.MinValue;
    readonly SemaphoreSlim authGate = new(1);
    public HashSet<string> SubscriptionIds { get; } = [];
    readonly List<string> subscriptionLocations = [];
    public int CleanupFailures { get; private set; }
    public Api(Connection c, string p) : this(c, p, new HttpClient(new HttpClientHandler
        { AllowAutoRedirect=false, UseCookies=false, UseDefaultCredentials=false }) { Timeout = TimeSpan.FromSeconds(30) }) { }
    public Api(Connection c, string p, HttpClient client)
    {
        this.c = c; password = p; http = client;
        http.BaseAddress = new Uri(c.BaseUrl.TrimEnd('/') + "/api/" + c.ApiVersion + "/");
    }
    static void Ensure(HttpResponseMessage r)
    {
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"Metasys HTTP {(int)r.StatusCode}; check API licensing and permissions.", null, r.StatusCode);
    }
    public async Task Login(CancellationToken ct)
    {
        await authGate.WaitAsync(ct);
        try
        {
            using var r = await http.PostAsJsonAsync("login", new { username = c.Username, password }, ct);
            Ensure(r);
            var j = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
            token = j?["accessToken"]?.GetValue<string>() ?? throw new Exception("Login returned no token.");
            expires = DateTimeOffset.TryParse(j?["expires"]?.ToString(), out var end) ? end : DateTimeOffset.UtcNow.AddMinutes(5);
        }
        finally { authGate.Release(); }
    }
    async Task Authenticate(CancellationToken ct)
    {
        if (token == null || DateTimeOffset.UtcNow >= expires.AddSeconds(-30)) await Login(ct);
    }
    HttpRequestMessage Request(HttpMethod method, string route)
    {
        var q = new HttpRequestMessage(method, route); q.Headers.Authorization = new("Bearer", token); return q;
    }
    public async Task<JsonNode?> Get(string route, CancellationToken ct)
    {
        await Authenticate(ct);
        using var q = Request(HttpMethod.Get, route);
        using var r = await http.SendAsync(q, ct); Ensure(r);
        return JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
    }
    public async Task<string> Resolve(string v, CancellationToken ct)
    {
        if (Guid.TryParse(v, out var id)) return id.ToString();
        return (await Get("objects/identifiers?fqr=" + Uri.EscapeDataString(v), ct))?.GetValue<string>() ?? throw new Exception("Reference unresolved.");
    }
    public async Task Write(string id, JsonNode value, CancellationToken ct)
    {
        await Authenticate(ct);
        using var q = Request(HttpMethod.Patch, "objects/" + id);
        q.Content = JsonContent.Create(new { item = new { presentValue = value } });
        using var r = await http.SendAsync(q, ct); Ensure(r);
    }
    public async Task Command(string id, string commandId, JsonObject body, CancellationToken ct)
    {
        await Authenticate(ct);
        using var request = Request(HttpMethod.Put, $"objects/{id}/commands/{Uri.EscapeDataString(commandId)}");
        request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        Ensure(response);
    }
    public async Task Subscribe(string id, string stream, CancellationToken ct)
    {
        await Authenticate(ct);
        using var q = Request(HttpMethod.Get, $"objects/{id}/attributes/presentValue");
        q.Headers.Add("METASYS-SUBSCRIBE", stream);
        using var r = await http.SendAsync(q, ct); Ensure(r);
        if (r.Headers.TryGetValues("METASYS-SUBSCRIPTION-LOCATION", out var locations))
        {
            var location = locations.First();
            if (!Uri.TryCreate(http.BaseAddress, location, out var uri)
                || uri.Scheme != http.BaseAddress!.Scheme || uri.Authority != http.BaseAddress.Authority
                || !uri.AbsolutePath.StartsWith(http.BaseAddress.AbsolutePath, StringComparison.Ordinal))
                throw new IOException("Server returned an invalid subscription location.");
            subscriptionLocations.Add(uri.AbsoluteUri);
            SubscriptionIds.Add(location.TrimEnd('/').Split('/').Last());
        }
        else throw new IOException("Server did not confirm COV subscription creation.");
    }
    public async Task Stream(Func<string, string, Task> handle, CancellationToken ct)
    {
        await Authenticate(ct);
        // Recreate the stream and subscriptions before its token expires. Do not assume an old stream refreshes itself.
        var remaining = expires - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30);
        if (remaining <= TimeSpan.Zero) throw new Exception("Metasys returned an already expired access token.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(remaining < TimeSpan.FromDays(1) ? remaining : TimeSpan.FromDays(1));
        var streamToken = lifetime.Token;
        SubscriptionIds.Clear();
        subscriptionLocations.Clear();
        using var q = Request(HttpMethod.Get, "stream"); q.Headers.Accept.Add(new("text/event-stream"));
        using var r = await http.SendAsync(q, HttpCompletionOption.ResponseHeadersRead, streamToken); Ensure(r);
        using var reader = new StreamReader(await r.Content.ReadAsStreamAsync(streamToken));
        string evt = ""; List<string> data = [];
        var opened = DateTimeOffset.UtcNow; bool helloReceived = false;
        var lastEvent = opened;
        try
        {
            while (!streamToken.IsCancellationRequested)
            {
                if (!helloReceived && DateTimeOffset.UtcNow - opened > TimeSpan.FromSeconds(15)) throw new IOException("COV stream did not provide a hello event.");
                if (helloReceived && DateTimeOffset.UtcNow - lastEvent > TimeSpan.FromSeconds(90))
                    throw new IOException("COV stream heartbeat or update events stopped.");
                var line = await reader.ReadLineAsync(streamToken).AsTask().WaitAsync(TimeSpan.FromSeconds(helloReceived ? 90 : 15), streamToken);
                if (line == null) throw new IOException("COV stream disconnected.");
                if (line.Length == 0)
                {
                    if (data.Count > 0)
                    {
                        if (evt == "hello") helloReceived = true;
                        await handle(evt, string.Join("\n", data));
                        if (evt == "hello" || evt.StartsWith("object.values.", StringComparison.Ordinal)) lastEvent = DateTimeOffset.UtcNow;
                    }
                    evt = ""; data.Clear();
                }
                else if (line.StartsWith("event:")) evt = line[6..].Trim();
                else if (line.StartsWith("data:")) data.Add(line[5..].TrimStart());
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Reconnecting COV stream before access-token expiry.");
        }
        finally { await CleanupSubscriptions(); }
    }
    public async Task CleanupSubscriptions()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var location in subscriptionLocations.ToArray())
        {
            try
            {
                await Authenticate(cleanup.Token);
                using var request = Request(HttpMethod.Delete, location);
                using var response = await http.SendAsync(request, cleanup.Token);
                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound) CleanupFailures++;
            }
            catch (Exception) { CleanupFailures++; }
        }
        subscriptionLocations.Clear();
        SubscriptionIds.Clear();
    }
    public void Dispose() => http.Dispose();
}
