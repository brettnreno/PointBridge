using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed record DeliveryRecord(string Binding, string ValueHash, string Phase, string Operation, DateTimeOffset At,DateTimeOffset? SourceObservedAt=null);
public sealed class JournalException(string code) : Exception(code), ISafeError { public string Code { get; }=code; public string SafeMessage => "Recovery journal requires attention before transfers can continue."; }

// One bounded current record per mapping, not a transfer history. No values or credentials.
public sealed class RecoveryJournal : IDisposable
{
    const int Limit=1024*1024, MaxRecords=4096;
    readonly object gate=new(); readonly string file; readonly Action? beforeCommit;
    FileStream? lease; Dictionary<string,DeliveryRecord> records=[]; string? fault;
    static readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    sealed record Envelope(int Version,string Checksum,Dictionary<string,DeliveryRecord> Records);
    public RecoveryJournal(string directory,Action? beforeCommit=null)
    {
        file=Path.Combine(directory,"delivery-journal.json");this.beforeCommit=beforeCommit;
        try
        {
            lease=new FileStream(file+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            if(File.Exists(file+".tmp"))throw new JournalException("journal-incomplete");
            if(!File.Exists(file))return;
            if(new FileInfo(file).Length>Limit)throw new JournalException("journal-corrupt");
            var e=JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(file),json);
            if(e==null||e.Version!=1||e.Records==null||e.Records.Count>MaxRecords||
                e.Checksum!=Digest(JsonSerializer.SerializeToUtf8Bytes(e.Records,json))||
                e.Records.Any(p=>!Hex(p.Key)||!Hex(p.Value.Binding)||!Hex(p.Value.ValueHash)||p.Value.Phase is not("intended" or "submitted" or "confirmed")||!Guid.TryParse(p.Value.Operation,out _)))
                throw new JournalException("journal-corrupt");
            records=e.Records;
        }
        catch(Exception e){fault=e is JournalException j?j.Code:e is JsonException?"journal-corrupt":"journal-unavailable";}
    }
    static bool Hex(string? s)=>s?.Length==64&&s.All(Uri.IsHexDigit);
    static string Digest(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    public static string ValueHash(JsonNode value)
    {
        // Normalize numeric representations across source and recipient JSON serializers.
        var raw=value.ToJsonString();
        if(decimal.TryParse(raw,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var n))raw=n.ToString("G29",System.Globalization.CultureInfo.InvariantCulture);
        return Digest(Encoding.UTF8.GetBytes(raw));
    }
    static string Key(string id)=>Digest(Encoding.UTF8.GetBytes(id));
    public static string Binding(Store store,Mapping m,string source,string destination)
    {
        var dst=store.Get(Store.DestinationSide(m));var src=store.Config.Source;
        object? external=null;
        if(m.SourceKind=="json"){
            var e=store.Config.ExternalSources[m.ExternalSourceId];
            external=new{Url=store.ExternalUrl(e),e.ValuePointer,e.TimestampPointer,e.UnitPointer,e.InputUnit,e.OutputUnit,e.UnitAliasConfirmed,e.ValueType,e.MaxAgeSeconds,e.QualityPointer,e.ReliableQualityValue,e.AuthMode,e.AuthHeader};
        }
        return Digest(JsonSerializer.SerializeToUtf8Bytes(new{m.SourceKind,source,destination,m.SourceAttribute,m.DestinationAttribute,m.WriteMode,m.CommandId,m.CommandPriority,
            destinationOrigin=dst.BaseUrl.TrimEnd('/').ToLowerInvariant(),dst.ApiVersion,destinationUser=dst.Username,
            sourceOrigin=m.SourceKind=="metasys"?src.BaseUrl.TrimEnd('/').ToLowerInvariant():"",sourceUser=m.SourceKind=="metasys"?src.Username:"",external},json));
    }
    void Ready(){if(fault!=null)throw new JournalException(fault);}
    public DeliveryRecord? Read(string id,string binding)
    {
        lock(gate){Ready();var record=records.GetValueOrDefault(Key(id));if(record!=null&&record.Binding!=binding)throw new JournalException("journal-config-changed");return record;}
    }
    public DeliveryRecord Begin(string id,string binding,string hash,DateTimeOffset? sourceObservedAt=null)
    {
        lock(gate){Ready();var prior=Read(id,binding);if(prior!=null&&prior.Phase!="confirmed")throw new JournalException("journal-pending");
            var next=new DeliveryRecord(binding,hash,"intended",Guid.NewGuid().ToString(),DateTimeOffset.UtcNow,sourceObservedAt);Save(id,next);return next;}
    }
    public DeliveryRecord Transition(string id,DeliveryRecord record,string phase)
    {
        lock(gate){Ready();var current=Read(id,record.Binding);if(current!=record||!(phase=="submitted"&&record.Phase=="intended"||phase=="confirmed"&&record.Phase is "intended" or "submitted"))throw new JournalException("journal-transition");
            var next=record with{Phase=phase,At=DateTimeOffset.UtcNow};Save(id,next);return next;}
    }
    void Save(string id,DeliveryRecord record)
    {
        try{
            var next=new Dictionary<string,DeliveryRecord>(records){[Key(id)]=record};
            if(next.Count>MaxRecords)throw new JournalException("journal-capacity");
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new Envelope(1,Digest(JsonSerializer.SerializeToUtf8Bytes(next,json)),next),json);
            if(bytes.Length>Limit)throw new JournalException("journal-capacity");
            beforeCommit?.Invoke();
            using(var stream=new FileStream(file+".tmp",FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){stream.Write(bytes);stream.Flush(true);}
            if(File.Exists(file))File.Replace(file+".tmp",file,null);else File.Move(file+".tmp",file);
            records=next;
        }catch(Exception e){fault=e is JournalException j?j.Code:"journal-unavailable";throw new JournalException(fault);}
    }
    public object Summary(){lock(gate)return new{schemaVersion=1,available=fault==null,code=fault??"none",records=records.Count,pending=records.Count(x=>x.Value.Phase!="confirmed"),maximumBytes=Limit,maximumRecords=MaxRecords};}
    public void Dispose(){lease?.Dispose();lease=null;}
}
