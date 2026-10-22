using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using static ContractTransport;
public static class ExtensionScenarios
{
    const string ObjectId="52cfddc0-411f-5b9e-9a6d-442cb62fecbb";
    const string SourceId="a7a80f73-343d-48dd-a8b9-9207c42d6745",RecipientId="3ddc1120-a657-4c49-aea7-b40ed67d3cce";
    sealed class Clock:TimeProvider
    {
        DateTimeOffset now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>now;
        public void Advance(int seconds)=>now=now.AddSeconds(seconds);
    }
    static ExternalJsonSource Config()=>new(){Enabled=true,Name="Fixture API",Url="https://external-fixture.invalid/data",ValuePointer="/value",TimestampPointer="/observed",UnitPointer="/unit",InputUnit="unitEnumSet.degC",OutputUnit="unitEnumSet.degC",MaxAgeSeconds=60,PollSeconds=15,TimeoutSeconds=1,QualityPointer="/quality",ReliableQualityValue="good"};
    sealed class Source(Clock clock):HttpMessageHandler
    {
        public int Calls;public JsonNode? Value=JsonValue.Create(20);public string Unit="unitEnumSet.degC",Quality="good";public DateTimeOffset? Timestamp;public Func<int,HttpResponseMessage>? Response;
        public bool Hang;public string? ExpectedHeader;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var call=Interlocked.Increment(ref Calls);Check(request.Method==HttpMethod.Get,"external adapter is read-only GET");
            if(ExpectedHeader!=null)Check(request.Headers.GetValues("X-Fixture-Key").Single()==ExpectedHeader,"explicit fixture header applied");
            if(Hang)await Task.Delay(Timeout.Infinite,ct);
            return Response?.Invoke(call)??Json(new JsonObject{["value"]=Value?.DeepClone(),["unit"]=Unit,["observed"]=(Timestamp??clock.GetUtcNow()).ToString("O"),["quality"]=Quality}.ToJsonString());
        }
    }
    static ExternalJsonSession Session(ExternalJsonSource cfg,Source source,Clock clock,string secret="")=>new(cfg,secret,new HttpClient(source,disposeHandler:false),clock,(duration,ct)=>{clock.Advance((int)Math.Ceiling(duration.TotalSeconds));return Task.CompletedTask;});
    static async Task ExpectCode(ExternalJsonSession session,string code)
    {
        try{await session.Read(default);throw new Exception("Expected "+code);}catch(ExternalSourceException e){Check(e.Code==code,"controlled source failure "+code);Check(!e.Message.Contains("fixture-secret"),"response/secret never exposed by source failure");}
    }
    public static async Task ReadAndCache()
    {
        var clock=new Clock();var source=new Source(clock);var cfg=Config();cfg.AuthMode="header";cfg.AuthHeader="X-Fixture-Key";source.ExpectedHeader="fixture-secret-not-real";
        using var session=Session(cfg,source,clock,"fixture-secret-not-real");
        var reads=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>session.Read(default)));
        Check(source.Calls==1&&reads.All(s=>s.Value.GetValue<int>()==20),"one fetched sample fans out to concurrent readers");
        Check((await session.Read(default)).Value.GetValue<int>()==20,"cached sample is not mutable by a recipient");
        clock.Advance(16);await session.Read(default);Check(source.Calls==2,"poll interval controls request budget");
    }
    public static async Task RateAndServiceErrors()
    {
        var clock=new Clock();var source=new Source(clock){Response=call=>call==1?new(HttpStatusCode.TooManyRequests){Headers={RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2))}}:Json("{\"value\":20,\"unit\":\"unitEnumSet.degC\",\"observed\":\""+clock.GetUtcNow().ToString("O")+"\",\"quality\":\"good\"}")};
        using(var session=Session(Config(),source,clock)){await session.Read(default);Check(source.Calls==2,"429 bounded retry honors server delay");}
        source=new(clock){Response=_=>new(HttpStatusCode.ServiceUnavailable)};
        using(var session=Session(Config(),source,clock)){await ExpectCode(session,"source-rate-or-service-unavailable");await ExpectCode(session,"source-rate-or-service-unavailable");Check(source.Calls==3,"service errors stop at three attempts and share backoff");}
        source=new(clock){Response=_=>new(HttpStatusCode.TooManyRequests){Headers={RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60))}}};
        using(var session=Session(Config(),source,clock)){await ExpectCode(session,"source-rate-or-service-unavailable");clock.Advance(16);await ExpectCode(session,"source-rate-or-service-unavailable");Check(source.Calls==1,"long Retry-After defers rather than sleeping or polling early");}
    }
    public static async Task InvalidAndStale()
    {
        var clock=new Clock();
        foreach(var code in new[]{"source-value-missing","source-units-mismatch","source-stale-observation","source-future-observation","source-value-type","source-quality"}){
            var source=new Source(clock);
            switch(code){case "source-value-missing":source.Value=null;break;case "source-units-mismatch":source.Unit="wrong";break;case "source-stale-observation":source.Timestamp=clock.GetUtcNow().AddSeconds(-61);break;case "source-future-observation":source.Timestamp=clock.GetUtcNow().AddSeconds(31);break;case "source-value-type":source.Value=JsonValue.Create(true);break;case "source-quality":source.Quality="bad";break;}
            using var session=Session(Config(),source,clock);await ExpectCode(session,code);
        }
        var ordered=new Source(clock);using(var session=Session(Config(),ordered,clock)){await session.Read(default);var previous=clock.GetUtcNow();clock.Advance(16);ordered.Timestamp=previous.AddSeconds(-1);await ExpectCode(session,"source-observation-regressed");}
        var cached=new Source(clock);using(var session=Session(Config(),cached,clock)){await session.Read(default);clock.Advance(61);cached.Timestamp=clock.GetUtcNow().AddSeconds(-61);await ExpectCode(session,"source-stale-observation");Check(cached.Calls==2,"HTTP200 with old observation does not revive held data");}
    }
    public static async Task LimitsAndTimeout()
    {
        var clock=new Clock();var source=new Source(clock){Hang=true};using(var session=Session(Config(),source,clock)){await ExpectCode(session,"source-timeout");Check(source.Calls==1,"timeout has no uncontrolled retries");}
        source=new(clock);using(var session=Session(Config(),source,clock)){
            await session.Read(default);clock.Advance(16);source.Hang=true;
            using var cancelled=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try{await session.Read(cancelled.Token);throw new Exception("Expected operation cancellation");}catch(OperationCanceledException){}
            await ExpectCode(session,"source-operation-cancelled");
            Check(source.Calls==2,"cancelled refresh cannot reuse last-good cache or retry before budget");
        }
        source=new(clock){Response=_=>new(HttpStatusCode.OK){Content=new StringContent("<html>fixture-secret-not-real</html>",Encoding.UTF8,"text/html")}};
        using(var session=Session(Config(),source,clock))await ExpectCode(session,"source-content-type");
        source=new(clock){Response=_=>new(HttpStatusCode.OK){Content=new StringContent(new string('x',1024*1024+1),Encoding.UTF8,"application/json")}};
        using(var session=Session(Config(),source,clock))await ExpectCode(session,"source-response-too-large");
        source=new(clock){Response=_=>new(HttpStatusCode.Redirect){Headers={Location=new Uri("https://another-fixture.invalid/")}}};
        using(var session=Session(Config(),source,clock)){await ExpectCode(session,"source-redirect-refused");Check(source.Calls==1,"redirect never dispatches credentials to a second origin");}
    }
    public static async Task MigrationAndSecrets()
    {
        var dir=Path.Combine(Path.GetTempPath(),"bridge-migration-"+Guid.NewGuid());Directory.CreateDirectory(dir);var file=Path.Combine(dir,"config.json");
        const string legacy="{\"transfersEnabled\":false,\"source\":{\"baseUrl\":\"https://source.invalid\",\"apiVersion\":\"v5\",\"username\":\"fixture\"},\"destination\":{\"baseUrl\":\"https://destination.invalid\",\"apiVersion\":\"v6\",\"username\":\"fixture\"},\"mappings\":[{\"id\":\"legacy\",\"name\":\"Legacy\",\"sourceObject\":\"source-point\",\"destinationObject\":\"target-point\",\"writeMode\":\"command\",\"commandId\":\"adjust\",\"commandPriority\":\"16\",\"enabled\":true}]}";
        File.WriteAllText(file,legacy);var store=new Store(new EphemeralDataProtectionProvider(),new(dir));
        Check(File.ReadAllText(file)==legacy&&!store.Config.ExtendedSourcesEnabled,"loading v1 does not rewrite or enable anything");
        var m=store.Config.Mappings.Single();Check(m.SourceKind=="metasys"&&m.DestinationConnectionId=="destination"&&m.CommandPriority=="16","legacy mapping keeps routing and priority");
        await store.Update(next=>{next.SchemaVersion=2;next.ExtendedSourcesEnabled=true;});
        Check(File.ReadAllText(file+".v1-before-extension")==legacy,"first explicit migration preserves exact v1 backup");
        var source=Config();source.AuthMode="header";source.AuthHeader="X-Fixture-Key";source.Secret="fixture-secret-not-real";await store.SetExternal(SourceId,source);
        Check(!File.ReadAllText(file).Contains(source.Secret),"external secret encrypted at rest");
        Check(!System.Text.Json.JsonSerializer.Serialize(store.PublicExternal(SourceId)).Contains(source.Secret),"public external configuration excludes secret");
        source.Url="https://different-fixture.invalid/data";source.Secret="";
        try{await store.SetExternal(SourceId,source);throw new Exception("cross-origin secret reuse allowed");}catch(ArgumentException){}
        var reloaded=new Store(new EphemeralDataProtectionProvider(),new(dir));Check(reloaded.Config.Mappings.Single().CommandId=="adjust","v2 reload preserves original command mapping");
    }
    sealed class Recipient
    {
        public int Attempts,Writes,Reads;public double Value;public bool Fail;
    }
    sealed class DestinationHandler(Recipient recipient):HttpMessageHandler
    {
        readonly PreflightScenarios.Fixture metadata=new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage q,CancellationToken ct)
        {
            Check(!q.Headers.Contains("X-Fixture-Key"),"source API key never enters recipient transport");
            Check(q.Headers.Authorization?.Parameter!="fixture-secret-not-real","source bearer never enters recipient transport");
            var path=q.RequestUri!.AbsolutePath;
            if(path.EndsWith("/login"))return Json("{\"accessToken\":\"fixture-token-not-real\",\"expires\":\""+DateTimeOffset.UtcNow.AddHours(1).ToString("O")+"\"}");
            if(q.Method==HttpMethod.Patch){Interlocked.Increment(ref recipient.Attempts);if(recipient.Fail)return new(HttpStatusCode.ServiceUnavailable);var j=JsonNode.Parse(await q.Content!.ReadAsStringAsync(ct));recipient.Value=j!["item"]!["presentValue"]!.GetValue<double>();Interlocked.Increment(ref recipient.Writes);return Json("{}");}
            if(path.EndsWith("/attributes/presentValue")&&q.RequestUri.Query.Length==0){Interlocked.Increment(ref recipient.Reads);return Json(new JsonObject{["item"]=new JsonObject{["presentValue"]=recipient.Value},["condition"]=new JsonObject{["presentValue"]=new JsonObject{["reliability"]="reliabilityEnumSet.reliable"}}}.ToJsonString());}
            return metadata.Reply(q);
        }
    }
    sealed class FanoutStore:Store
    {
        public readonly Clock Clock=new();public readonly Source Source;
        public readonly Recipient First=new(),Second=new();
        public FanoutStore(string dir):base(new EphemeralDataProtectionProvider(),new(dir))
        {
            Source=new(Clock);Config.ExtendedSourcesEnabled=true;Config.SchemaVersion=2;Config.ExternalSources[SourceId]=ExtensionScenarios.Config();
            Config.ExternalSources[SourceId].AuthMode="header";Config.ExternalSources[SourceId].AuthHeader="X-Fixture-Key";Source.ExpectedHeader="fixture-secret-not-real";
            Config.Destination=new(){BaseUrl="https://first-fixture.invalid",ApiVersion="v5"};Config.Destinations[RecipientId]=new(){BaseUrl="https://second-fixture.invalid",ApiVersion="v6"};
            Config.Mappings.Add(new(){Id="first",Name="First",SourceKind="json",ExternalSourceId=SourceId,DestinationObject=ObjectId});
            Config.Mappings.Add(new(){Id="second",Name="Second",SourceKind="json",ExternalSourceId=SourceId,DestinationConnectionId=RecipientId,DestinationObject=ObjectId});
        }
        public override Api Client(string side){Check(side!="source","JSON-only run never uses Metasys source account");return new(Get(side),"",new HttpClient(new DestinationHandler(side=="destination"?First:Second)));}
        public override ExternalJsonSession ExternalClient(string id)=>Session(Config.ExternalSources[id],Source,Clock,"fixture-secret-not-real");
    }
    static FanoutStore Fanout(){var dir=Path.Combine(Path.GetTempPath(),"bridge-fanout-"+Guid.NewGuid());Directory.CreateDirectory(dir);return new(dir);}
    static async Task Until(Func<bool> test,string label){var end=DateTime.UtcNow.AddSeconds(5);while(!test()){if(DateTime.UtcNow>end)throw new Exception("Timed out: "+label);await Task.Delay(20);}}
    public static async Task FanoutAndRecovery()
    {
        var store=Fanout();store.Second.Fail=true;using var bridge=new Bridge(store){HealthCheckInterval=TimeSpan.FromMilliseconds(100),MaxConcurrentChecks=2};
        await bridge.StartAsync(default);await bridge.SetArmed(true);
        try{
            await Until(()=>bridge.Health.Any(h=>h.MappingId=="first"&&h.State=="Healthy")&&bridge.Health.Any(h=>h.MappingId=="second"&&bridge.Logs.Any(e=>e.Message.Contains("mapping=second")&&e.Message.Contains("http=503"))),"mixed recipient success");
            Check(store.First.Writes==1&&store.Second.Writes==0&&store.Source.Calls==1,"shared preflight/fetch and runtime sample fan out with isolated writes");
            store.Source.Value=null;store.Clock.Advance(16);
            await Until(()=>bridge.Health.All(h=>h.FailureCode=="source-value-missing"),"invalid shared source stops both recipients");
            Check(store.First.Writes==1&&store.Second.Writes==0,"invalid data cannot write zero or cached values");
            store.Source.Value=JsonValue.Create(21);store.Clock.Advance(16);
            await Until(()=>store.First.Writes==2,"healthy recipient resumes with fresh source");
            Check(store.Second.Attempts==1,"uncertain recipient write is not blindly repeated");
            store.Second.Value=20;store.Second.Fail=false;
            await Until(()=>store.Second.Writes==1&&bridge.Health.All(h=>h.State=="Healthy"),"late result reconciled before latest value delivered");
            Check(store.Second.Value==21,"recovered recipient receives latest value only");
        }finally{await bridge.SetArmed(false);await bridge.StopAsync(default);}
        var writes=store.First.Writes+store.Second.Writes;store.Clock.Advance(16);await Task.Delay(150);Check(store.First.Writes+store.Second.Writes==writes,"paused fanout performs no writes");
    }
    public static async Task FeatureGateAndPaused()
    {
        var store=Fanout();store.Config.ExtendedSourcesEnabled=false;using var bridge=new Bridge(store);await bridge.StartAsync(default);
        try{try{await bridge.SetArmed(true);throw new Exception("feature gate bypassed");}catch(InvalidOperationException){}Check(store.Source.Calls==0&&store.First.Attempts==0&&store.Second.Attempts==0,"disabled feature cannot fetch or write");}
        finally{await bridge.StopAsync(default);}
        store=Fanout();using var paused=new Bridge(store);await paused.StartAsync(default);await Task.Delay(100);await paused.StopAsync(default);
        Check(store.Source.Calls==0&&store.First.Attempts==0&&store.Second.Attempts==0,"configured but paused instance performs no source requests or writes");
    }
    public static async Task DurableSourceOrdering()
    {
        var store=Fanout();var deliveredAt=store.Clock.GetUtcNow();store.Source.Timestamp=deliveredAt.AddSeconds(-1);store.Source.Value=JsonValue.Create(21);
        store.First.Value=20;store.Second.Value=20;
        using(var journal=new RecoveryJournal(store.DirectoryPath))foreach(var m in store.Config.Mappings){
            var record=journal.Begin(m.Id,RecoveryJournal.Binding(store,m,"json:"+SourceId,ObjectId),RecoveryJournal.ValueHash(JsonValue.Create(20)!),deliveredAt);
            journal.Transition(m.Id,record,"confirmed");
        }
        using var bridge=new Bridge(store){HealthCheckInterval=TimeSpan.FromMilliseconds(100)};await bridge.StartAsync(default);await bridge.SetArmed(true);
        try{
            await Until(()=>bridge.Health.Length==2&&bridge.Health.All(h=>h.FailureCode=="source-observation-regressed"),"restart rejects older external observation");
            Check(store.First.Writes==0&&store.Second.Writes==0,"durable observation floor prevents stale overwrite after restart");
            store.Clock.Advance(16);store.Source.Timestamp=store.Clock.GetUtcNow();
            await Until(()=>bridge.Health.All(h=>h.State=="Healthy"),"fresh observation recovers after ordering fault");
            Check(store.First.Writes==1&&store.Second.Writes==1,"fresh observation uses validated recipient paths");
        }finally{await bridge.SetArmed(false);await bridge.StopAsync(default);}
    }
}
