using System.Text.Json.Nodes;
public sealed class MetasysRequestException(string connection, HttpRequestMessage request, System.Net.HttpStatusCode status)
    : HttpRequestException($"{connection}: Metasys HTTP {(int)status} on {request.Method} {request.RequestUri!.AbsolutePath}. "
        + "The server refused this request; check its API permissions, licensing and request limits. Query values and credentials are omitted.", null, status), ISafeError
{
    public string SafeMessage => Message;
}
public class Api : IDisposable
{
    readonly HttpClient http;
    readonly Connection c;
    readonly string password;
    readonly TimeProvider clock;
    public string Origin => c.BaseUrl.TrimEnd('/');
    public string ApiVersion => c.ApiVersion;
    sealed record Access(string Token, DateTimeOffset Expires);
    sealed class Authentication
    {
        public Access? Current;
        public readonly SemaphoreSlim Gate = new(1);
        public int References = 1;
    }
    readonly Authentication authentication;
    string? token => Volatile.Read(ref authentication.Current)?.Token;
    DateTimeOffset expires => Volatile.Read(ref authentication.Current)?.Expires ?? DateTimeOffset.MinValue;
    SemaphoreSlim authGate => authentication.Gate;
    int disposed;
    public string ConnectionName { get; set; } = "Metasys connection";
    public HashSet<string> SubscriptionIds { get; } = [];
    readonly List<string> subscriptionLocations = [];
    public int CleanupFailures { get; private set; }
    public Api(Connection c, string p) : this(c, p, new HttpClient(new HttpClientHandler
        { AllowAutoRedirect=false, UseCookies=false, UseDefaultCredentials=false }) { Timeout = TimeSpan.FromSeconds(30) }) { }
    public Api(Connection c, string p, HttpClient client, TimeProvider? clock = null)
    {
        this.c = c; password = p; http = client; this.clock = clock ?? TimeProvider.System;
        authentication = new();
        http.BaseAddress = new Uri(c.BaseUrl.TrimEnd('/') + "/api/" + c.ApiVersion + "/");
    }
    Api(Api owner, string connection)
    {
        c = owner.c; password = owner.password; http = owner.http; clock = owner.clock;
        authentication = owner.authentication; ConnectionName = connection;
        Interlocked.Increment(ref authentication.References);
    }
    public Api Share(string connection)
    {
        lock (authentication)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return new Api(this, connection);
        }
    }
    void Ensure(HttpResponseMessage response, HttpRequestMessage request)
    {
        if (!response.IsSuccessStatusCode) throw new MetasysRequestException(ConnectionName, request, response.StatusCode);
    }
    public Task Login(CancellationToken ct) => Authenticate(ct);
    public async Task TestConnection(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await authGate.WaitAsync(ct);
        try { await Renew(ct); }
        finally { authGate.Release(); }
    }
    async Task Authenticate(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (token != null && clock.GetUtcNow() < expires.AddSeconds(-30)) return;
        await authGate.WaitAsync(ct);
        try
        {
            if (token != null && clock.GetUtcNow() < expires.AddSeconds(-30)) return;
            await Renew(ct);
        }
        finally { authGate.Release(); }
    }
    async Task Renew(CancellationToken ct)
    {
        if (token != null && clock.GetUtcNow() < expires)
        {
            using var request = Request(HttpMethod.Get, "refreshToken");
            using var response = await Send(request, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
            {
                Ensure(response, request);
                await AcceptToken(response, ct);
                return;
            }
        }
        Volatile.Write(ref authentication.Current, null);
        await LoginCore(ct);
    }
    async Task LoginCore(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "login") { Content = JsonContent.Create(new { username = c.Username, password }) };
        using var response = await Send(request, ct);
        Ensure(response, request);
        await AcceptToken(response, ct);
    }
    async Task AcceptToken(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await ReadJson(response, ct);
        var next = body?["accessToken"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(next) || !DateTimeOffset.TryParse(body?["expires"]?.ToString(), out var end)
            || end <= clock.GetUtcNow()) throw new IOException("Metasys returned an invalid or expired access token.");
        var lifetime = c.ApiVersion == "v4" && end > clock.GetUtcNow().AddMinutes(55) ? clock.GetUtcNow().AddMinutes(55) : end;
        Volatile.Write(ref authentication.Current, new Access(next, lifetime));
    }
    public async Task<JsonNode?> ReadBatch(IEnumerable<string> objectIds, CancellationToken ct)
    {
        var identifiers = objectIds.Distinct().ToArray();
        if (identifiers.Length is < 1 or > 50 || identifiers.Any(identifier => !Guid.TryParse(identifier, out _)))
            throw new UserInputException("Choose between 1 and 50 object GUIDs for a read-only batch.");
        var requests = new JsonArray(identifiers.Select(identifier => (JsonNode)new JsonObject
            { ["id"] = identifier, ["relativeUrl"] = identifier + "/attributes/presentValue" }).ToArray());
        await Authenticate(ct);
        using var request = Request(HttpMethod.Post, "objects/batch");
        request.Content = JsonContent.Create(new JsonObject { ["method"] = "GET", ["requests"] = requests });
        using var response = await Send(request, ct);
        Ensure(response, request);
        return await ReadJson(response, ct);
    }
    async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
    {
        try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException exception) when (exception.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw new UpstreamLimitException($"{ConnectionName}: HTTPS certificate validation failed on {request.Method} {request.RequestUri!.AbsolutePath}. Use the hostname on the certificate, verify its validity, and install the issuing CA in this machine's trusted certificate store. Certificate checks remain enabled.");
        }
    }
    static async Task<JsonNode?> ReadJson(HttpResponseMessage response, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, deadline.Token);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
    }
    HttpRequestMessage Request(HttpMethod method, string route)
    {
        var q = new HttpRequestMessage(method, route); q.Headers.Authorization = new("Bearer", token); return q;
    }
    public async Task<JsonNode?> Get(string route, CancellationToken ct)
    {
        await Authenticate(ct);
        using var q = Request(HttpMethod.Get, route);
        using var r = await Send(q, ct); Ensure(r, q);
        return await ReadJson(r, ct);
    }
    public async Task<string> Resolve(string v, CancellationToken ct)
    {
        if (Guid.TryParse(v, out var id)) return id.ToString();
        var route = c.ApiVersion == "v4" ? "objectIdentifiers" : "objects/identifiers";
        return (await Get(route + "?fqr=" + Uri.EscapeDataString(v), ct))?.GetValue<string>() ?? throw new Exception("Reference unresolved.");
    }
    public async Task Write(string id, JsonNode value, CancellationToken ct)
    {
        await Authenticate(ct);
        using var q = Request(HttpMethod.Patch, "objects/" + id);
        q.Content = JsonContent.Create(new { item = new { presentValue = value } });
        using var r = await Send(q, ct); Ensure(r, q);
    }
    public async Task Command(string id, string commandId, JsonObject body, CancellationToken ct)
    {
        await Authenticate(ct);
        using var request = Request(HttpMethod.Put, $"objects/{id}/commands/{Uri.EscapeDataString(commandId)}");
        request.Content = JsonContent.Create(body);
        using var response = await Send(request, ct);
        Ensure(response, request);
    }
    public async Task Subscribe(string id, string stream, CancellationToken ct)
    {
        await Authenticate(ct);
        using var q = Request(HttpMethod.Get, $"objects/{id}/attributes/presentValue");
        q.Headers.Add("METASYS-SUBSCRIBE", stream);
        using var r = await Send(q, ct); Ensure(r, q);
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
        using var r = await http.SendAsync(q, HttpCompletionOption.ResponseHeadersRead, streamToken); Ensure(r, q);
        using var reader = new StreamReader(await r.Content.ReadAsStreamAsync(streamToken));
        var lines = new BoundedLineReader(reader);
        var eventCharacters = 0;
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
                var line = await lines.ReadLineAsync(streamToken).WaitAsync(TimeSpan.FromSeconds(helloReceived ? 90 : 15), streamToken);
                if (line == null) throw new IOException("COV stream disconnected.");
                if (line.Length == 0)
                {
                    if (data.Count > 0)
                    {
                        if (evt == "hello") helloReceived = true;
                        await handle(evt, string.Join("\n", data));
                        if (evt == "hello" || evt.StartsWith("object.values.", StringComparison.Ordinal)) lastEvent = DateTimeOffset.UtcNow;
                    }
                    evt = ""; data.Clear(); eventCharacters = 0;
                }
                else if (line.StartsWith("event:")) evt = line[6..].Trim();
                else if (line.StartsWith("data:"))
                {
                    eventCharacters += line.Length;
                    if (eventCharacters > 262144 || data.Count >= 1024) throw new UpstreamLimitException("COV stream event exceeded its safety limit.");
                    data.Add(line[5..].TrimStart());
                }
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
    public void Dispose()
    {
        lock (authentication)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (Interlocked.Decrement(ref authentication.References) == 0)
            {
                Volatile.Write(ref authentication.Current, null);
                http.Dispose();
                authentication.Gate.Dispose();
            }
        }
    }
}
