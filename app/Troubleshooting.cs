using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public static class Troubleshooting
{
    public const int MaximumBytes=512*1024;
    static readonly string buildHash=BuildHash();
    static string BuildHash(){try{var path=typeof(Bridge).Assembly.Location;if(new FileInfo(path).Length>64*1024*1024)return "unavailable";using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}catch{return "unavailable";}}
    static readonly HashSet<string> states=["AwaitingCheck","Stopped","Stalled","Healthy","SourceUnhealthy","DestinationUnconfirmed","DeliveryUncertain","Unhealthy"];
    static readonly HashSet<string> codes=["none","progress-deadline","source-stale-observation","source-future-observation","source-observation-regressed","source-value-missing","source-value-type","source-scalar-too-large","source-units-mismatch","source-timestamp-missing","source-quality","source-quality-missing","source-timeout","source-operation-cancelled","source-transport-failed","source-auth-not-configured","source-rate-or-service-unavailable","source-content-type","source-response-too-large","source-json-invalid","source-redirect-refused","source-retry-exhausted","destination-quality","readback-mismatch","prior-write-unconfirmed","journal-incomplete","journal-corrupt","journal-unavailable","journal-config-changed","journal-pending","journal-transition","journal-capacity","connection-failed","stream-disconnected"];
    static string Code(string? value)
    {
        if(value!=null&&codes.Contains(value))return value;
        foreach(var stage in new[]{"worker-capacity","source-read","source-validation","destination-write","destination-readback","recovery-journal"})
            foreach(var kind in new[]{"deadline","invalid-data","request-failed"})if(value==stage+"-"+kind)return value;
        return "withheld";
    }
    static string State(string? s)=>s!=null&&states.Contains(s)?s:"withheld";
    static string Alias(string id)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16];
    static string? Part(string message,string name)=>Regex.Match(message,@"(?:^|; )"+name+@"=([^;]*)(?:;|$)").Groups[1] is {Success:true} group?group.Value:null;
    static long Count(long count)=>Math.Max(0,count);
    static int? Http(int? status)=>status is >=100 and <=599?status:null;
    static object Log(AuditEntry e)=>new{
        e.Timestamp,level=e.Level is "info" or "warning" or "error"?e.Level:"withheld",
        mapping=Part(e.Message,"mapping") is {} id?Alias(id):null,
        state=State(Part(e.Message,"state")),code=Code(Part(e.Message,"code")),
        http=int.TryParse(Part(e.Message,"http"),out var h)?Http(h):null,
        failures=long.TryParse(Part(e.Message,"failures"),out var f)?Count(f):0,
        checks=long.TryParse(Part(e.Message,"checks"),out var c)?Count(c):0};
    public static byte[] Capture(Store store,Bridge bridge)
    {
        var snapshot=store.Snapshot();var health=bridge.Health;var logs=bridge.Logs.ToArray();
        var body=new{
            exportSchemaVersion=1,generatedAt=DateTimeOffset.UtcNow,
            buildVersion=typeof(Bridge).Assembly.GetName().Version?.ToString(),buildSha256=buildHash,runtime=Environment.Version.ToString(),
            config=new{schemaVersion=snapshot.SchemaVersion,extensionsEnabled=snapshot.ExtendedSourcesEnabled,
                mappingCount=snapshot.Mappings.Count,enabledMappingCount=snapshot.Mappings.Count(m=>m.Enabled),
                jsonMappingCount=snapshot.Mappings.Count(m=>m.SourceKind=="json"),recipientCount=1+snapshot.Destinations.Count,externalSourceCount=snapshot.ExternalSources.Count},
            runtimeState=new{armed=bridge.Armed,reconciliationSeconds=bridge.HealthCheckInterval.TotalSeconds,deadlineSeconds=bridge.OperationDeadline.TotalSeconds,maxConcurrentChecks=bridge.MaxConcurrentChecks},
            recovery=bridge.RecoveryStatus,
            mappings=health.Take(256).Select(h=>new{mapping=Alias(h.MappingId),state=State(h.State),code=Code(h.FailureCode),lastFailureCode=Code(h.LastFailureCode),h.LastSourceContact,h.LastSourceRead,h.SourceObservationAt,h.LastDestinationConfirmed,h.LastCheckStarted,h.LastCheckCompleted,h.LastFailureAt,
                completedChecks=Count(h.CompletedChecks),consecutiveFailures=Count(h.ConsecutiveFailures),deadlineFailures=Count(h.DeadlineFailures),http=Http(h.LastHttpStatus),lastFailureHttp=Http(h.LastFailureHttpStatus),
                sourceQuality=h.SourceQuality is "reported-reliable" or "reported-unreliable"?h.SourceQuality:"unreported",destinationQuality=h.DestinationQuality is "reported-reliable" or "reported-unreliable"?h.DestinationQuality:"unreported"}),
            logs=logs.TakeLast(100).Select(Log),
            truncated=new{mappings=health.Length>256,logs=logs.Length>100},
            scope="allowlisted-diagnostics-only; excludes connection addresses, usernames, point references/names/values, credentials, headers, cookies, raw messages/responses and raw files; no upload"};
        var bytes=JsonSerializer.SerializeToUtf8Bytes(body,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if(bytes.Length>MaximumBytes)throw new InvalidOperationException("Diagnostic export exceeded its size limit; no file was generated.");
        return bytes;
    }
}
