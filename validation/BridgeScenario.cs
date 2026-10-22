using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using static ContractTransport;

public static class BridgeScenario
{
    const string SourceId="32cfddc0-411f-5b9e-9a6d-442cb62fecbb", DestinationId="52cfddc0-411f-5b9e-9a6d-442cb62fecbb";
    public static async Task ReadbackMismatch()
    {
        var env=new FakeEnvironment{ReadbackOverride=99};var dir=Path.Combine(Path.GetTempPath(),"bridge-readback-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        var store=new FakeStore(dir,env);store.Config.Mappings.Add(new Mapping{Id="mismatch",Name="Mismatch",SourceObject=SourceId,DestinationObject=DestinationId});
        using var bridge=new Bridge(store);await bridge.StartAsync(default);await bridge.SetArmed(true);
        try
        {
            await Until(()=>store.Config.Mappings.Single().Status.Contains("readback differs"),"readback mismatch visibly reported",6);
            Check(env.Writes.Count==1,"mismatched readback must not cause uncontrolled repeated writes");
            Check(store.Config.Mappings.Single().LastSuccess==null,"mismatch must not record success");
        }
        finally{await bridge.SetArmed(false);await bridge.StopAsync(default);}
    }
    public static async Task VersionFourDestination()
    {
        var env=new FakeEnvironment();var store=TestStore(env,"v5-to-v4");store.Config.Destination.ApiVersion="v4";
        using var bridge=new Bridge(store);await bridge.StartAsync(default);await bridge.SetArmed(true);
        try
        {
            await Until(()=>store.Config.Mappings.Single().LastSuccess!=null,"v5 source to v4 destination verified readback",8);
            Check(env.Writes.Count==1&&env.DestinationValues[DestinationId]==10,"mixed-version initial write confirmed once");
            env.SourceValue=11;
            await env.Event("object.values.update","{\"item\":{\"id\":\""+SourceId+"\",\"presentValue\":11}}");
            await Until(()=>env.Writes.Count==2&&store.Config.Mappings.Single().Status=="Verified","mixed-version COV readback",8);
            Check(env.DestinationValues[DestinationId]==11,"v4 destination receives current source value");
        }
        finally{await bridge.SetArmed(false);await bridge.StopAsync(default);}
    }
    public static async Task DestinationQuality()
    {
        var env=new FakeEnvironment{DestinationUnreliable=true};var dir=Path.Combine(Path.GetTempPath(),"bridge-quality-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        var store=new FakeStore(dir,env);store.Config.Mappings.Add(new Mapping{Id="quality",Name="Quality",SourceObject=SourceId,DestinationObject=DestinationId});
        using var bridge=new Bridge(store);await bridge.StartAsync(default);await bridge.SetArmed(true);
        try{await Until(()=>store.Config.Mappings.Single().Status.Contains("destination quality unreliable"),"unreliable destination reported",6);Check(store.Config.Mappings.Single().LastSuccess==null,"matching unreliable destination must not record success");}
        finally{await bridge.SetArmed(false);await bridge.StopAsync(default);}
    }
    public static async Task Run()
    {
        var env=new FakeEnvironment();
        var dir=Path.Combine(Path.GetTempPath(),"bridge-contract-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        var store=new FakeStore(dir,env);var mapping=new Mapping{Id="test",Name="Test",SourceObject=SourceId,DestinationObject=DestinationId};store.Config.Mappings.Add(mapping);
        using var bridge=new Bridge(store);
        await bridge.StartAsync(default);await bridge.SetArmed(true);mapping=store.Config.Mappings.Single();
        await Until(()=>env.Writes.Count==1 && mapping.Status=="Verified","initial reconciliation");
        Check(env.Writes.ToArray()[0]==10,"initial source read must synchronize current state");
        env.SourceValue=11;await env.Event("object.values.update","{\"item\":{\"id\":\""+SourceId+"\",\"presentValue\":11}}");
        await Until(()=>env.Writes.Count==2,"v5 COV forwarding");Check(env.Writes.ToArray()[1]==11,"v5 value");
        env.SourceValue=12;await env.Event("object.values.update","{\"value\":[{\"item\":{\"id\":\""+SourceId+"\",\"presentValue\":12}}]}");
        await Until(()=>env.Writes.Count==3,"v6 COV forwarding");Check(env.Writes.ToArray()[2]==12,"v6 value");
        await env.Event("object.values.update","{\"item\":{\"id\":\""+SourceId+"\",\"presentValue\":12}}");
        await Task.Delay(100);Check(env.Writes.Count==3,"duplicate unchanged COV must not write repeatedly");
        env.SourceUnreliable=true;await env.Event("object.values.update","{\"item\":{\"id\":\""+SourceId+"\",\"presentValue\":99},\"condition\":{\"presentValue\":{\"reliability\":\"reliabilityEnumSet.unreliableHigh\"}}}");
        await Until(()=>mapping.Status.Contains("unreliable"),"unreliable source surfaced");Check(env.Writes.Count==3,"unreliable update must not write");
        env.SourceUnreliable=false;env.SourceValue=13;await env.Event("object.values.notification","{}");
        await Until(()=>env.Writes.Count==4 && mapping.Status=="Verified","notification reread");Check(env.Writes.ToArray()[3]==13,"notification must read new current state");
        var reads=env.SourceReads;
        await env.Event("object.values.heartbeatImproved","{\"activeSubscriptions\":[],\"dateTime\":\"2026-10-21T00:00:00Z\"}");
        await Until(()=>bridge.Status.Contains("Disconnected"),"missing subscription heartbeat reconnect");
        await Until(()=>env.StreamConnections>=2 && env.SourceReads>reads,"reconnect recreates subscription and reconciles",15);
        Check(env.Writes.Count==4,"reconnect verifies unchanged values without replaying writes");
        env.SourceValue=14;env.BlockNextWrite=true;
        await env.Event("object.values.update","{\"item\":{\"id\":\""+SourceId+"\",\"presentValue\":14}}");
        await env.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var changed=false;var configure=bridge.Configure(()=>{changed=true;return Task.CompletedTask;});
        await Task.Delay(100);Check(!changed,"configuration must wait for in-flight write to unwind");
        env.ReleaseWrite.TrySetResult();await configure.WaitAsync(TimeSpan.FromSeconds(3));
        Check(changed && !bridge.Armed,"configuration disarms and commits after write unwinds");
        var count=env.Writes.Count;
        await env.Event("object.values.update","{\"item\":{\"id\":\""+SourceId+"\",\"presentValue\":15}}");
        await Task.Delay(150);Check(env.Writes.Count==count,"no writes after disarm/configuration completion");
        await bridge.StopAsync(default);
        Console.WriteLine("PASS Bridge initial sync, v5/v6 routing, duplicate suppression, quality skip, notification read, heartbeat reconnect/reconciliation, mutation serialization, disarm");
    }
    public static async Task RiskDestinationDrift()
    {
        var env=new FakeEnvironment();var store=TestStore(env,"drift");
        using var bridge=new Bridge(store){HealthCheckInterval=TimeSpan.FromMilliseconds(150)};
        await bridge.StartAsync(default);await bridge.SetArmed(true);
        try{
            var mapping=store.Config.Mappings.Single();await Until(()=>bridge.Health.Any(h=>h.State=="Healthy"&&h.CompletedChecks>0),"initial completed verified state");
            var reads=env.SourceReads;var before=bridge.Health.Single().LastDestinationConfirmed;env.ReadbackOverride=99;
            await Until(()=>bridge.Health.Single().FailureCode=="readback-mismatch","periodic drift detection without COV");
            Check(env.SourceReads>reads&&env.Writes.Count==1,"periodic check rereads source without overwriting destination drift");
            Check(bridge.Health.Single().LastDestinationConfirmed==before,"held destination trends cannot advance confirmation");
            env.ReadbackOverride=10;
            await Until(()=>bridge.Health.Single().State=="Healthy"&&bridge.Health.Single().LastDestinationConfirmed>before,"stable source recovers after readback matches");
            Check(env.Writes.Count==1,"recovery verification needs no write");
        }finally{await bridge.SetArmed(false);await bridge.StopAsync(default);}
    }
    public static async Task RiskOlderEvent()
    {
        var env=new FakeEnvironment();var store=TestStore(env,"ordering");
        using var bridge=new Bridge(store);await bridge.StartAsync(default);await bridge.SetArmed(true);
        try{
            await Until(()=>env.Writes.Count==1&&store.Config.Mappings.Single().Status=="Verified","initial source state");
            env.SourceValue=12;
            await env.Event("object.values.update","{\"item\":{\"id\":\""+SourceId+"\",\"presentValue\":12},\"timestamp\":\"2026-10-21T12:02:00Z\"}");
            await Until(()=>env.Writes.Count==2&&store.Config.Mappings.Single().Status=="Verified","current source read forwarded");
            var reads=env.SourceReads;
            await env.Event("object.values.update","{\"item\":{\"id\":\""+SourceId+"\",\"presentValue\":11},\"timestamp\":\"2026-10-21T12:01:00Z\"}");
            await Until(()=>env.SourceReads>reads,"old unknown-order event triggers safe resync");await Task.Delay(100);
            Check(env.Writes.Count==2&&env.Writes.Last()==12,"older event payload never writes stale value");
            env.SourceFailure=true;reads=env.SourceReads;
            await env.Event("object.values.update","{\"item\":{\"id\":\""+SourceId+"\",\"presentValue\":13}}");
            await Until(()=>bridge.Health.Single().FailureCode=="source-read-request-failed","source failure surfaced");
            Check(env.Writes.Count==2,"failed resync never falls back to unverified event value");
        }finally{await bridge.SetArmed(false);await bridge.StopAsync(default);}
    }
    public static async Task RiskBlockedDestination()
    {
        var env=new FakeEnvironment();var store=TestStore(env,"first");
        store.Config.Mappings.Add(new Mapping{Id="second",Name="Second",SourceObject=SourceId,DestinationObject="62cfddc0-411f-5b9e-9a6d-442cb62fecbb"});
        store.Config.Mappings.Add(new Mapping{Id="third",Name="Third",SourceObject=SourceId,DestinationObject="72cfddc0-411f-5b9e-9a6d-442cb62fecbb"});
        using var bridge=new Bridge(store){MaxConcurrentChecks=2,HealthCheckInterval=TimeSpan.FromMilliseconds(100),OperationDeadline=TimeSpan.FromSeconds(1)};await bridge.StartAsync(default);await bridge.SetArmed(true);
        try{
            await Until(()=>env.Writes.Count==3&&store.Config.Mappings.All(m=>m.Status=="Verified"),"initial three mappings verified");
            env.SourceValue=11;env.BlockNextWrite=true;
            await env.Event("object.values.update","{\"item\":{\"id\":\""+SourceId+"\",\"presentValue\":11}}");
            await env.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(()=>env.Writes.Count==5,"other mappings progress while one destination write blocked");
            for(var n=0;n<50;n++)await env.Event("object.values.notification","{}");
            await Until(()=>bridge.Health.Any(h=>h.State=="Stalled"),"uncooperative blocked worker visible as stalled");
            Check(env.MaxActiveRequests<=2,"bounded concurrency across three mapping workers");
            Check(bridge.Health.Any(h=>h.State=="Healthy"),"other mapping remains healthy during stall");
            env.ReleaseWrite.TrySetResult();await Until(()=>env.Writes.Count==6,"blocked mapping finishes after release");
        }finally{env.ReleaseWrite.TrySetResult();await bridge.SetArmed(false);await bridge.StopAsync(default);}
    }
    public static async Task SourceHealth()
    {
        var env=new FakeEnvironment();var store=TestStore(env,"health");
        using var bridge=new Bridge(store){HealthCheckInterval=TimeSpan.FromMilliseconds(100)};
        await bridge.StartAsync(default);await bridge.SetArmed(true);
        try{
            await Until(()=>bridge.Health.FirstOrDefault()?.CompletedChecks>=3,"stable source periodic checks");
            Check(bridge.Health.Single().State=="Healthy"&&env.Writes.Count==1,"unchanged source is healthy without repeated writes");
            var lastRead=bridge.Health.Single().LastSourceRead;
            env.SourceFailure=true;
            await Until(()=>bridge.Health.Single().FailureCode=="source-read-request-failed","source outage without COV");
            Check(bridge.Health.Single().LastHttpStatus==503,"controlled HTTP failure status retained");
            Check(bridge.Health.Single().LastSourceRead==lastRead,"failed read cannot claim fresh observation");
            env.SourceFailure=false;env.SourceUnreliable=true;
            await Until(()=>bridge.Health.Single().FailureCode=="source-quality","bad quality source");
            Check(env.Writes.Count==1,"bad quality must not write");
            Check(!bridge.Logs.Any(e=>e.Message.Contains("fixture-secret-not-real")),"untrusted exception/HTTP reason never logged");
            env.SourceUnreliable=false;
            await Until(()=>bridge.Health.Single().State=="Healthy"&&bridge.Health.Single().LastSourceRead>lastRead,"source recovery via current reads");
        }finally{await bridge.SetArmed(false);await bridge.StopAsync(default);}
    }
    public static async Task DeadlineAndUncertainWrite()
    {
        var env=new FakeEnvironment();var store=TestStore(env,"deadline");
        using var bridge=new Bridge(store){HealthCheckInterval=TimeSpan.FromMilliseconds(100),OperationDeadline=TimeSpan.FromMilliseconds(300)};
        await bridge.StartAsync(default);await bridge.SetArmed(true);
        try{
            await Until(()=>bridge.Health.FirstOrDefault()?.State=="Healthy","initial healthy state");
            env.SourceValue=11;env.BlockNextWrite=true;env.CancelBlockedWrite=true;
            await env.Event("object.values.notification","{}");
            await env.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(()=>bridge.Health.Single().DeadlineFailures==1,"bounded aggregate operation");
            await Until(()=>bridge.Health.Single().FailureCode=="prior-write-unconfirmed","uncertain write reconciled without replay");
            Check(env.Writes.Count==1,"unchanged uncertain write not blindly replayed");
            var checks=bridge.Health.Single().CompletedChecks;env.SourceValue=12;
            await Until(()=>bridge.Health.Single().CompletedChecks>checks,"changed source checked while prior write uncertain");
            Check(env.Writes.Count==1,"new desired value held until prior request outcome resolved");
            env.ReadbackOverride=11;
            await Until(()=>env.Writes.Count==2,"confirmed prior outcome releases latest desired value");
            Check(env.Writes.Last()==12,"old uncertain value not replayed");env.ReadbackOverride=12;
            await Until(()=>bridge.Health.Single().State=="Healthy","latest value readback confirmed");
        }finally{env.ReleaseWrite.TrySetResult();await bridge.SetArmed(false);await bridge.StopAsync(default);}
    }
    public static async Task PartialDestinationFailure()
    {
        var env=new FakeEnvironment();var store=TestStore(env,"failed");
        store.Config.Mappings.Add(new Mapping{Id="healthy",Name="Healthy",SourceObject=SourceId,DestinationObject="62cfddc0-411f-5b9e-9a6d-442cb62fecbb"});
        using var bridge=new Bridge(store);await bridge.StartAsync(default);await bridge.SetArmed(true);
        try{
            await Until(()=>env.Writes.Count==2&&bridge.Health.All(h=>h.State=="Healthy"),"initial recipients verified");
            env.DestinationFailId=DestinationId;env.SourceValue=11;
            await env.Event("object.values.notification","{}");
            await Until(()=>bridge.Health.Any(h=>h.MappingId=="failed"&&h.LastHttpStatus==503)&&bridge.Health.Any(h=>h.MappingId=="healthy"&&h.State=="Healthy"&&h.CompletedChecks>=2),"per-mapping failure isolated");
            Check(env.Writes.Count==3,"failed destination request does not stop healthy mapping");
            Check(bridge.Status.Contains("connected"),"point failure does not restart healthy COV stream");
            env.DestinationFailId=null;await env.Event("object.values.notification","{}");
            await Until(()=>bridge.Health.Any(h=>h.MappingId=="failed"&&h.FailureCode=="prior-write-unconfirmed"),"failed write held for review after transport recovery");
            Check(env.Writes.Count==3,"failed mapping not blindly replayed during recovery");
        }finally{await bridge.SetArmed(false);await bridge.StopAsync(default);}
    }
    public static async Task QuietMappingSchedule()
    {
        var env=new FakeEnvironment();var store=TestStore(env,"chatty");
        store.Config.Mappings.Add(new Mapping{Id="quiet",Name="Quiet",SourceObject="82cfddc0-411f-5b9e-9a6d-442cb62fecbb",DestinationObject="62cfddc0-411f-5b9e-9a6d-442cb62fecbb"});
        using var bridge=new Bridge(store){HealthCheckInterval=TimeSpan.FromMilliseconds(150)};
        await bridge.StartAsync(default);await bridge.SetArmed(true);
        try{
            await Until(()=>bridge.Health.Length==2&&bridge.Health.All(h=>h.State=="Healthy"),"initial quiet and chatty mappings");
            for(var n=0;n<30;n++){
                await env.Event("object.values.update","{\"item\":{\"id\":\""+SourceId+"\",\"presentValue\":10}}");await Task.Delay(20);
            }
            Check(bridge.Health.Single(h=>h.MappingId=="quiet").CompletedChecks>=3,"unrelated COV cannot postpone quiet mapping reconciliation");
            Check(env.Writes.Count==2,"periodic/chatty stable checks do not produce repeated writes");
        }finally{await bridge.SetArmed(false);await bridge.StopAsync(default);}
    }
    public static async Task DurableRestart()
    {
        foreach(var scenario in new[]{"intended-matches","submitted-matches-new-source","submitted-mismatch","confirmed-drift","binding-changed","disk-unavailable"})
        {
            var env=new FakeEnvironment();var store=TestStore(env,"restart");var mapping=store.Config.Mappings.Single();
            if(scenario!="disk-unavailable"){
                using var journal=new RecoveryJournal(store.DirectoryPath);
                var record=journal.Begin(mapping.Id,RecoveryJournal.Binding(store,mapping,SourceId,DestinationId),RecoveryJournal.ValueHash(JsonValue.Create(10)!));
                if(!scenario.StartsWith("intended"))record=journal.Transition(mapping.Id,record,"submitted");
                if(scenario=="confirmed-drift")journal.Transition(mapping.Id,record,"confirmed");
            }else Directory.CreateDirectory(Path.Combine(store.DirectoryPath,"delivery-journal.json.tmp"));
            if(scenario is "intended-matches" or "submitted-matches-new-source")env.DestinationValues[DestinationId]=10;
            if(scenario=="submitted-matches-new-source")env.SourceValue=11;
            if(scenario=="binding-changed")store.Config.Source.Username="changed-account";
            using var bridge=new Bridge(store){HealthCheckInterval=TimeSpan.FromMilliseconds(100)};
            await bridge.StartAsync(default);await bridge.SetArmed(true);
            try{
                var expected=scenario is "intended-matches" or "submitted-matches-new-source"?"none":scenario=="submitted-mismatch"?"prior-write-unconfirmed":scenario=="confirmed-drift"?"readback-mismatch":scenario=="binding-changed"?"journal-config-changed":"journal-unavailable";
                await Until(()=>bridge.Health.Any(h=>h.CompletedChecks>0&&h.FailureCode==expected),"durable restart "+scenario);
                Check(env.Writes.Count==(scenario=="submitted-matches-new-source"?1:0),"restart never blindly replays uncertain write "+scenario);
                Check(!bridge.Logs.Any(e=>e.Message.Contains("test")),"recovery diagnostics contain no API credential");
            }finally{await bridge.SetArmed(false);await bridge.StopAsync(default);}
        }
    }
    static FakeStore TestStore(FakeEnvironment env,string id)
    {
        var dir=Path.Combine(Path.GetTempPath(),"bridge-safety-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        var store=new FakeStore(dir,env);store.Config.Mappings.Add(new Mapping{Id=id,Name=id,SourceObject=SourceId,DestinationObject=DestinationId});return store;
    }    static async Task Until(Func<bool> predicate,string name,int seconds=4){var end=DateTime.UtcNow.AddSeconds(seconds);while(!predicate()){if(DateTime.UtcNow>end)throw new Exception("Timed out: "+name);await Task.Delay(20);}}
    sealed class FakeStore:Store
    {
        readonly FakeEnvironment env;
        public FakeStore(string dir,FakeEnvironment e):base(new EphemeralDataProtectionProvider(),new DataLocation(dir)){env=e;Config.Source.BaseUrl="https://source.test";Config.Destination.BaseUrl="https://destination.test";}
        public override Api Client(string side)=>new(Get(side),"test",new HttpClient(new Handler(env,side)));
    }
    sealed class FakeEnvironment
    {
        public readonly PushStream Stream=new();public readonly System.Collections.Concurrent.ConcurrentQueue<double> Writes=new();public double SourceValue=10;public bool BlockNextWrite;public int SourceReads,StreamConnections,ActiveRequests,MaxActiveRequests;public double? ReadbackOverride;public bool DestinationUnreliable,SourceUnreliable,SourceFailure,CancelBlockedWrite;public string? DestinationFailId;public readonly System.Collections.Concurrent.ConcurrentDictionary<string,double> DestinationValues=new();
        public TaskCompletionSource WriteStarted=new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseWrite=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask Event(string kind,string data)=>Stream.Push($"event: {kind}\ndata: {data}\n\n");
    }
    sealed class Handler(FakeEnvironment env,string side):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage q,CancellationToken ct)
        {
            var active=Interlocked.Increment(ref env.ActiveRequests);int seen;
            do{seen=env.MaxActiveRequests;if(seen>=active)break;}while(Interlocked.CompareExchange(ref env.MaxActiveRequests,active,seen)!=seen);
            try{return await Reply(q,ct);}finally{Interlocked.Decrement(ref env.ActiveRequests);}
        }
        async Task<HttpResponseMessage> Reply(HttpRequestMessage q,CancellationToken ct)
        {
            var route=q.RequestUri!.AbsolutePath;
            if(route.EndsWith("/login"))return Json(new JsonObject{["accessToken"]="test",["expires"]=DateTimeOffset.UtcNow.AddHours(1).ToString("O")}.ToJsonString());
            if(route.EndsWith("/stream")){env.StreamConnections++;await env.Event("hello","b6d184ee-e137-44aa-b9b1-14bc704cd25d");return new(HttpStatusCode.OK){Content=new StreamContent(env.Stream)};}
            if(q.Headers.Contains("METASYS-SUBSCRIBE")){var r=new HttpResponseMessage(HttpStatusCode.Accepted);r.Headers.Add("METASYS-SUBSCRIPTION-LOCATION","https://source.test/api/v5/objects/streams/b6d184ee-e137-44aa-b9b1-14bc704cd25d/subscriptions/c9fcb046-4199-4f1c-a3e8-7841be1a3c30");return r;}
            if(q.Method==HttpMethod.Patch){if(env.DestinationFailId!=null&&route.EndsWith("/"+env.DestinationFailId))return new(HttpStatusCode.ServiceUnavailable);var b=JsonNode.Parse(await q.Content!.ReadAsStringAsync(ct));if(env.BlockNextWrite){env.BlockNextWrite=false;env.WriteStarted.TrySetResult();if(env.CancelBlockedWrite)await env.ReleaseWrite.Task.WaitAsync(ct);else await env.ReleaseWrite.Task;}var written=b!["item"]!["presentValue"]!.GetValue<double>();env.DestinationValues[route.Split('/' )[4]]=written;env.Writes.Enqueue(written);return Json("{}");}
            if(route.EndsWith("/attributes/presentValue")){if(side=="source"){env.SourceReads++;if(env.SourceFailure)return new(HttpStatusCode.ServiceUnavailable){ReasonPhrase="fixture-secret-not-real"};}var value=side=="source"?env.SourceValue:env.ReadbackOverride??env.DestinationValues.GetValueOrDefault(route.Split('/' )[4]);var data=JsonNode.Parse("{\"item\":{\"presentValue\":"+value.ToString(System.Globalization.CultureInfo.InvariantCulture)+"},\"schema\":{\"properties\":{\"presentValue\":{\"type\":\"number\",\"minimum\":0,\"maximum\":100}}}}")!;if((side=="destination"&&env.DestinationUnreliable)||(side=="source"&&env.SourceUnreliable))data["condition"]=JsonNode.Parse("{\"presentValue\":{\"reliability\":\"reliabilityEnumSet.unreliableHigh\"}}");return Json(data.ToJsonString());}
            if(q.Method==HttpMethod.Get)return new PreflightScenarios.Fixture().Reply(q);
            throw new Exception("Unexpected mock request "+q.Method+" "+q.RequestUri);
        }
    }
    sealed class PushStream:Stream
    {
        readonly Channel<byte[]> channel=Channel.CreateUnbounded<byte[]>();byte[]? current;int offset;
        public ValueTask Push(string s)=>channel.Writer.WriteAsync(Encoding.UTF8.GetBytes(s));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default){if(current==null||offset==current.Length){current=await channel.Reader.ReadAsync(ct);offset=0;}var n=Math.Min(buffer.Length,current.Length-offset);current.AsMemory(offset,n).CopyTo(buffer);offset+=n;return n;}
        public override Task<int> ReadAsync(byte[] b,int o,int c,CancellationToken ct)=>ReadAsync(b.AsMemory(o,c),ct).AsTask();
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();public override void Flush(){}public override long Seek(long o,SeekOrigin so)=>throw new NotSupportedException();public override void SetLength(long l)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    }
}
