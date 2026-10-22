using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using static ContractTransport;
public static class FailureScenarios
{
    public static async Task TokenExpiry()
    {
        var transport=new ContractTransport();var clock=new TestClock();using var api=new Api(new Connection{BaseUrl="https://adx.test"},"secret",new HttpClient(transport),clock);
        string Token(string value,DateTimeOffset expires)=>new JsonObject{["accessToken"]=value,["expires"]=expires.ToString("O")}.ToJsonString();
        transport.Expect(q=>Task.FromResult(Json(Token("expired",clock.Now.AddMinutes(1)))));
        await api.Login(default);
        clock.Now=clock.Now.AddMinutes(2);
        transport.Expect(q=>{Check(q.RequestUri!.AbsolutePath.EndsWith("/login"),"expired token must renew");return Task.FromResult(Json(Token("fresh",DateTimeOffset.UtcNow.AddHours(1))));});
        transport.Expect(q=>{Check(q.Headers.Authorization?.Parameter=="fresh","renewed bearer must replace expired token");return Task.FromResult(Json("{}"));});
        await api.Get("objects",default);transport.Complete();
    }
    public sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    public static async Task MissingSubscriptionHeader()
    {
        var transport=new ContractTransport();using var api=new Api(new Connection{BaseUrl="https://adx.test"},"secret",new HttpClient(transport));
        transport.Expect(q=>Task.FromResult(Json(new JsonObject{["accessToken"]="token",["expires"]=DateTimeOffset.UtcNow.AddHours(1).ToString("O")}.ToJsonString())));
        transport.Expect(q=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)));
        try{await api.Subscribe("32cfddc0-411f-5b9e-9a6d-442cb62fecbb","b6d184ee-e137-44aa-b9b1-14bc704cd25d",default);throw new Exception("Unconfirmed subscription was accepted");}
        catch(IOException){ }transport.Complete();
    }
    public static async Task StreamTokenLifetime()
    {
        var transport=new ContractTransport();using var api=new Api(new Connection{BaseUrl="https://adx.test"},"test",new HttpClient(transport));
        transport.Expect(q=>Task.FromResult(Json(new JsonObject{["accessToken"]="expiring",["expires"]=DateTimeOffset.UtcNow.AddSeconds(31).ToString("O")}.ToJsonString())));
        transport.Expect(q=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new WaitingStream())}));
        var timer=System.Diagnostics.Stopwatch.StartNew();var expired=false;
        try{await api.Stream((evt,data)=>Task.CompletedTask,default);}
        catch(IOException e) when(e.Message.Contains("expiry")){expired=true;}
        Check(expired && timer.Elapsed<TimeSpan.FromSeconds(4),"stream must reconnect before access token expiry");transport.Complete();
    }
    sealed class WaitingStream:Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> b,CancellationToken ct=default){await Task.Delay(Timeout.Infinite,ct);return 0;}
        public override Task<int> ReadAsync(byte[] b,int o,int c,CancellationToken ct)=>ReadAsync(b.AsMemory(o,c),ct).AsTask();
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();public override void Flush(){}public override long Seek(long o,SeekOrigin so)=>throw new NotSupportedException();public override void SetLength(long l)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    }
    public static async Task DurableSettingsFailure()
    {
        var dir=Path.Combine(Path.GetTempPath(),"bridge-save-failure-"+Guid.NewGuid());Directory.CreateDirectory(dir);
        var store=new Store(new EphemeralDataProtectionProvider(),new DataLocation(dir));
        await store.Set("source",new Connection{BaseUrl="https://old.test",Username="old-user",Password="old-secret"});
        var prior=store.Config.Source;
        Directory.CreateDirectory(Path.Combine(dir,"config.json.tmp"));
        try{await store.Set("source",new Connection{BaseUrl="https://new.test",Username="new-user",Password="new-secret"});throw new Exception("Persistence failure was swallowed");}
        catch(IOException){}catch(UnauthorizedAccessException){}
        Check(ReferenceEquals(store.Config.Source,prior),"Rejected settings save must not leave new in-memory connection active");
        var loaded=new Store(new EphemeralDataProtectionProvider(),new DataLocation(dir));
        Check(loaded.Config.Source.BaseUrl=="https://old.test","last durable settings preserved");
    }
    public static async Task BlankPasswordChanges()
    {
        var dir=Path.Combine(Path.GetTempPath(),"bridge-password-"+Guid.NewGuid());Directory.CreateDirectory(dir);var store=new Store(new EphemeralDataProtectionProvider(),new DataLocation(dir));
        await store.Set("source",new Connection{BaseUrl="https://old.test",Username="old-user",Password="fake-secret"});
        var rejected=false;try{await store.Set("source",new Connection{BaseUrl="https://new.test",Username="old-user",Password=""});}catch(ArgumentException){rejected=true;}Check(rejected&&store.Config.Source.BaseUrl=="https://old.test","host change must not reuse stored secret");
        rejected=false;try{using var api=store.TemporaryClient("source",new Connection{BaseUrl="https://old.test",Username="new-user",Password=""});}catch(ArgumentException){rejected=true;}Check(rejected,"testing changed account must not reuse stored secret");
    }
}
