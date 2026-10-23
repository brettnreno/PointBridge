using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

public sealed class ExternalJsonSource
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string ValuePointer { get; set; } = "";
    public string TimestampPointer { get; set; } = "";
    public string UnitPointer { get; set; } = "";
    public string InputUnit { get; set; } = "";
    public string OutputUnit { get; set; } = "";
    public bool UnitAliasConfirmed { get; set; }
    public string ValueType { get; set; } = "number";
    public int PollSeconds { get; set; } = 300;
    public int MaxAgeSeconds { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public string UserAgent { get; set; } = "";
    public string AuthMode { get; set; } = "none";
    public string AuthHeader { get; set; } = "";
    public string Secret { get; set; } = "";
    public string ProtectedQuery { get; set; } = "";
    public string QualityPointer { get; set; } = "";
    public string ReliableQualityValue { get; set; } = "";
}
public sealed record ExternalSample(JsonNode Value, string Units, string Type, DateTimeOffset ObservedAt, DateTimeOffset FetchedAt, bool QualityReported);
public sealed class ExternalSourceException(string code, int? httpStatus = null) : Exception(code), ISafeError
{
    public string SafeMessage => "External source could not be read safely. Review source health and authentication settings.";
    public string Code { get; } = code;
    public int? HttpStatus { get; } = httpStatus;
}

public sealed class ExternalJsonSession : IDisposable
{
    readonly ExternalJsonSource config;
    readonly HttpClient http;
    readonly string secret;
    readonly TimeProvider clock;
    readonly Func<TimeSpan,CancellationToken,Task> delay;
    readonly SemaphoreSlim gate = new(1);
    ExternalSample? cached;
    ExternalSourceException? failed;
    DateTimeOffset nextAttempt;
    DateTimeOffset? newestObservation;
    public ExternalJsonSession(ExternalJsonSource config, string secret, HttpClient http, TimeProvider? clock = null,
        Func<TimeSpan,CancellationToken,Task>? delay = null)
    {
        Validate(config);this.config=config;this.secret=secret;this.http=http;this.clock=clock??TimeProvider.System;
        this.delay=delay??((duration,ct)=>Task.Delay(duration,ct));
    }
    public static void Validate(ExternalJsonSource c)
    {
        if(!Uri.TryCreate(c.Url,UriKind.Absolute,out var url)||url.Scheme!="https"||url.UserInfo!=""||url.Fragment!="")
            throw new UserInputException("External source requires an HTTPS data URL without embedded credentials or fragments.");
        if(c.Url.Length>2048||c.Name.Length>128||c.ValuePointer.Length>512||c.TimestampPointer.Length>512||
            !c.ValuePointer.StartsWith('/')||!c.TimestampPointer.StartsWith('/')||!c.UnitPointer.StartsWith('/'))
            throw new UserInputException("Configure JSON Pointer paths for value, timestamp and units.");
        if(c.InputUnit.Length==0||c.OutputUnit.Length==0||c.InputUnit.Length>128||c.OutputUnit.Length>128||
            (c.InputUnit!=c.OutputUnit&&!c.UnitAliasConfirmed))
            throw new UserInputException("Declare units; different unit labels require explicit same-scale alias confirmation. No conversion is performed.");
        if(c.PollSeconds is <15 or >86400||c.MaxAgeSeconds is <1 or >604800||c.TimeoutSeconds is <1 or >30||c.ValueType is not("number" or "integer" or "boolean" or "string"))
            throw new UserInputException("Choose a poll interval15-86400s, positive maximum observation age, timeout1-30s and scalar type.");
        if(c.AuthMode is not("none" or "header" or "bearer")||c.UserAgent.Length>256||c.UserAgent.Any(char.IsControl)||
            c.AuthHeader.Any(ch=>!char.IsAsciiLetterOrDigit(ch)&&ch!='-')||c.AuthHeader.Length>64||
            (c.AuthMode=="header"&&(c.AuthHeader.Length==0||new[]{"authorization","cookie","host","connection","content-length","proxy-authorization"}.Contains(c.AuthHeader.ToLowerInvariant()))))
            throw new UserInputException("Invalid external API authentication/header configuration.");
        if(c.QualityPointer.Length>0&&(!c.QualityPointer.StartsWith('/')||c.ReliableQualityValue.Length==0))
            throw new UserInputException("Quality checks require a JSON Pointer and explicit reliable value.");
    }
    static JsonNode? Select(JsonNode? root,string pointer)
    {
        var node=root;
        foreach(var raw in pointer.Split('/').Skip(1))
        {
            var key=raw.Replace("~1","/").Replace("~0","~");
            node=node is JsonObject obj?obj[key]:node is JsonArray a&&int.TryParse(key,out var index)&&index>=0&&index<a.Count?a[index]:null;
            if(node==null)break;
        }
        return node;
    }
    ExternalSample Check(ExternalSample sample)
    {
        var now=clock.GetUtcNow();
        if(sample.ObservedAt>now.AddSeconds(30))throw new ExternalSourceException("source-future-observation");
        if(now-sample.ObservedAt>TimeSpan.FromSeconds(config.MaxAgeSeconds))throw new ExternalSourceException("source-stale-observation");
        return sample with{Value=sample.Value.DeepClone()};
    }
    public async Task<ExternalSample> Read(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if(clock.GetUtcNow()<nextAttempt)
            {
                if(failed!=null)throw failed;
                if(cached!=null)return Check(cached);
            }
            nextAttempt=clock.GetUtcNow().AddSeconds(config.PollSeconds);
            cached=null;
            try
            {
                for(var attempt=0;attempt<3;attempt++)
                {
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
                    using var request=new HttpRequestMessage(HttpMethod.Get,config.Url);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/geo+json"));
                    if(config.UserAgent.Length>0)request.Headers.TryAddWithoutValidation("User-Agent",config.UserAgent);
                    if(config.AuthMode!="none"&&secret.Length==0)throw new ExternalSourceException("source-auth-not-configured");
                    if(config.AuthMode=="bearer")request.Headers.Authorization=new("Bearer",secret);
                    if(config.AuthMode=="header")request.Headers.Add(config.AuthHeader,secret);
                    using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                    var status=(int)response.StatusCode;
                    if(status==429||status is 502 or 503 or 504)
                    {
                        var retry=response.Headers.RetryAfter?.Delta??(response.Headers.RetryAfter?.Date-clock.GetUtcNow())??TimeSpan.FromSeconds(1<<attempt);
                        if(retry<TimeSpan.Zero)retry=TimeSpan.Zero;
                        if(attempt==2||retry>TimeSpan.FromSeconds(5))
                        {
                            if(retry>TimeSpan.Zero)nextAttempt=Max(nextAttempt,clock.GetUtcNow()+retry);
                            throw new ExternalSourceException("source-rate-or-service-unavailable",status);
                        }
                        await delay(retry+TimeSpan.FromMilliseconds(Random.Shared.Next(0,100)),ct);continue;
                    }
                    if(!response.IsSuccessStatusCode)throw new ExternalSourceException(status is >=300 and <400?"source-redirect-refused":"source-http-failed",status);
                    if(response.Content.Headers.ContentLength>1024*1024)throw new ExternalSourceException("source-response-too-large");
                    var contentType=response.Content.Headers.ContentType?.MediaType;
                    if(contentType==null||!(contentType.EndsWith("/json",StringComparison.OrdinalIgnoreCase)||contentType.EndsWith("+json",StringComparison.OrdinalIgnoreCase)))
                        throw new ExternalSourceException("source-content-type");
                    await using var input=await response.Content.ReadAsStreamAsync(timeout.Token);using var buffer=new MemoryStream();
                    var bytes=new byte[8192];int count;
                    while((count=await input.ReadAsync(bytes,timeout.Token))>0){if(buffer.Length+count>1024*1024)throw new ExternalSourceException("source-response-too-large");buffer.Write(bytes,0,count);}
                    JsonNode? root;try{root=JsonNode.Parse(buffer.ToArray(),documentOptions:new(){MaxDepth=32});}catch{throw new ExternalSourceException("source-json-invalid");}
                    var value=Select(root,config.ValuePointer);if(value==null)throw new ExternalSourceException("source-value-missing");
                    if(value is not JsonValue || value.ToJsonString().Length>4096)throw new ExternalSourceException("source-scalar-too-large");
                    var schema=new JsonObject{["type"]=config.ValueType};if(SchemaRules.Validate(value,schema)!=null)throw new ExternalSourceException("source-value-type");
                    if(config.ValueType is "number" or "integer"&&(!double.TryParse(value.ToJsonString(),System.Globalization.CultureInfo.InvariantCulture,out var numeric)||!double.IsFinite(numeric)))
                        throw new ExternalSourceException("source-value-type");
                    if(Select(root,config.UnitPointer)?.ToString()!=config.InputUnit)throw new ExternalSourceException("source-units-mismatch");
                    var timestamp=Select(root,config.TimestampPointer)?.ToString();
                    if(timestamp==null || !(timestamp.EndsWith('Z')||System.Text.RegularExpressions.Regex.IsMatch(timestamp,@"[+-]\d{2}:\d{2}$")) ||
                        !DateTimeOffset.TryParse(timestamp,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal,out var observed))
                        throw new ExternalSourceException("source-timestamp-missing");
                    if(newestObservation is {} previous&&observed<previous)throw new ExternalSourceException("source-observation-regressed");
                    if(config.QualityPointer.Length>0)
                    {
                        var quality=Select(root,config.QualityPointer)?.ToString();
                        if(quality==null)throw new ExternalSourceException("source-quality-missing");
                        if(quality!=config.ReliableQualityValue)throw new ExternalSourceException("source-quality");
                    }
                    var sample=Check(new(value.DeepClone(),config.OutputUnit,config.ValueType,observed,clock.GetUtcNow(),config.QualityPointer.Length>0));
                    cached=sample;failed=null;newestObservation=observed;return sample;
                }
                throw new ExternalSourceException("source-retry-exhausted");
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){failed=new ExternalSourceException("source-operation-cancelled");throw;}
            catch(Exception error)
            {
                failed=error as ExternalSourceException??new ExternalSourceException(error is OperationCanceledException?"source-timeout":"source-transport-failed");throw failed;
            }
        }
        finally{gate.Release();}
    }
    static DateTimeOffset Max(DateTimeOffset a,DateTimeOffset b)=>a>b?a:b;
    public void Dispose(){http.Dispose();gate.Dispose();}
}

public sealed class ExternalJsonPool(Store store) : IDisposable
{
    readonly Dictionary<string,ExternalJsonSession> sessions=[];
    public ExternalJsonSession Get(string id)
    {
        lock(sessions){if(!sessions.TryGetValue(id,out var session))sessions[id]=session=store.ExternalClient(id);return session;}
    }
    public void Dispose(){foreach(var session in sessions.Values)session.Dispose();}
}
