using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using static ContractTransport;
public static class RecoveryScenarios
{
    static string Dir(){var p=Path.Combine(Path.GetTempPath(),"bridge-recovery-"+Guid.NewGuid());Directory.CreateDirectory(p);return p;}
    static readonly string Binding=new('A',64);
    static readonly string Hash=RecoveryJournal.ValueHash(JsonValue.Create(42)!);
    static void Expect(Action action,string code){try{action();throw new Exception("Expected "+code);}catch(JournalException e){Check(e.Code==code,"controlled journal diagnostic "+code);}}
    public static Task CrashWindows()
    {
        foreach(var phase in new[]{"before-send","after-send-before-ack","after-ack-before-commit"}){
            var dir=Dir();DeliveryRecord record;
            using(var journal=new RecoveryJournal(dir)){
                record=journal.Begin("mapping",Binding,Hash);
                if(phase!="before-send")record=journal.Transition("mapping",record,"submitted");
            }
            using(var reopened=new RecoveryJournal(dir)){
                record=reopened.Read("mapping",Binding)!;
                Check(record.Phase!="confirmed"&&record.ValueHash==Hash,"crash window survives with uncertain outcome "+phase);
                Expect(()=>reopened.Begin("mapping",Binding,Hash),"journal-pending");
                // A caller can resolve only after verified readback; absence of a response is never replay permission.
                record=reopened.Transition("mapping",record,"confirmed");
            }
            using(var final=new RecoveryJournal(dir))Check(final.Read("mapping",Binding)!.Phase=="confirmed","confirmation durable "+phase);
            var text=File.ReadAllText(Path.Combine(dir,"delivery-journal.json"));Check(!text.Contains("mapping")&&!text.Contains("\"42\""),"journal excludes IDs and payload");
        }
        Check(RecoveryJournal.ValueHash(JsonNode.Parse("42.0")!)==Hash,"numeric hash stable across representations");return Task.CompletedTask;
    }
    public static Task CorruptAndIncomplete()
    {
        foreach(var corruption in new[]{"truncated","checksum","oversized","temp"}){
            var dir=Dir();var file=Path.Combine(dir,"delivery-journal.json");using(var j=new RecoveryJournal(dir))j.Begin("mapping",Binding,Hash);
            if(corruption=="truncated")File.WriteAllText(file,"{");
            if(corruption=="checksum")File.WriteAllText(file,File.ReadAllText(file).Replace("intended","submitted"));
            if(corruption=="oversized")File.WriteAllText(file,new string('x',1024*1024+1));
            if(corruption=="temp")File.WriteAllText(file+".tmp","incomplete");
            using var reopened=new RecoveryJournal(dir);Expect(()=>reopened.Read("mapping",Binding),corruption=="temp"?"journal-incomplete":"journal-corrupt");
        }
        return Task.CompletedTask;
    }
    public static Task StorageAndBinding()
    {
        var dir=Dir();using(var j=new RecoveryJournal(dir))j.Begin("mapping",Binding,Hash);
        var bytes=File.ReadAllBytes(Path.Combine(dir,"delivery-journal.json"));
        using(var fail=new RecoveryJournal(dir,()=>throw new IOException("synthetic-disk-full-secret"))){var record=fail.Read("mapping",Binding)!;Expect(()=>fail.Transition("mapping",record,"submitted"),"journal-unavailable");Expect(()=>fail.Read("mapping",Binding),"journal-unavailable");}
        Check(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(dir,"delivery-journal.json"))),"disk-full preserves previous committed file");
        var ackDir=Dir();var commits=0;using(var failing=new RecoveryJournal(ackDir,()=>{if(++commits==3)throw new IOException("synthetic-disk-full-after-ack");})){
            var record=failing.Begin("mapping",Binding,Hash);record=failing.Transition("mapping",record,"submitted");Expect(()=>failing.Transition("mapping",record,"confirmed"),"journal-unavailable");
        }
        using(var reopened=new RecoveryJournal(ackDir))Check(reopened.Read("mapping",Binding)!.Phase=="submitted","acknowledged but uncommitted result still requires readback after restart");
        using var final=new RecoveryJournal(dir);Expect(()=>final.Read("mapping",new string('B',64)),"journal-config-changed");
        using var concurrent=new RecoveryJournal(dir);Expect(()=>concurrent.Read("mapping",Binding),"journal-unavailable");return Task.CompletedTask;
    }
    public static Task ExportRedaction()
    {
        var dir=Dir();var store=new Store(new EphemeralDataProtectionProvider(),new(dir));const string canary="SENSITIVE_CANARY_credential_payload_cookie_header";
        store.Config.Source=new(){BaseUrl="https://"+canary+".invalid",Username=canary,Password=canary};
        store.Config.Mappings.Add(new(){Id=canary,Name=canary,SourceObject=canary,DestinationObject=canary,SourceValue=JsonValue.Create(canary)});
        store.Config.ExternalSources[canary]=new(){Secret=canary,Url=canary,UserAgent=canary};
        using var bridge=new Bridge(store);
        for(var i=0;i<1000;i++)bridge.Logs.Enqueue(new(DateTimeOffset.UtcNow,canary,"mapping="+canary+"; state="+canary+"; code="+canary+"; raw="+new string('x',10000)));
        var bytes=Troubleshooting.Capture(store,bridge);var text=System.Text.Encoding.UTF8.GetString(bytes);
        Check(bytes.Length<=Troubleshooting.MaximumBytes&&!text.Contains(canary)&&!text.Contains(new string('x',100)),"bounded export excludes arbitrary customer settings/messages/payload");
        using var doc=JsonDocument.Parse(bytes);Check(doc.RootElement.GetProperty("logs").GetArrayLength()==100&&doc.RootElement.GetProperty("truncated").GetProperty("logs").GetBoolean(),"bounded logs and explicit truncation");
        Check(doc.RootElement.GetProperty("config").GetProperty("mappingCount").GetInt32()==1,"useful safe config metadata retained");return Task.CompletedTask;
    }
}
