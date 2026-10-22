using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using static ContractTransport;

public static class ApiSessionScenarios
{
    const string Id="32cfddc0-411f-5b9e-9a6d-442cb62fecbb";
    const string Hidden="fixture-query-password-token-never-display";
    sealed class SessionTransport : HttpMessageHandler
    {
        public int Logins,Reads,Refreshes;
        public bool Disposed;
        public readonly List<string> Paths=[];
        readonly PreflightScenarios.Fixture fixture=new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Check(!Disposed,"shared HTTP client remains available to active leases");
            Paths.Add(request.RequestUri!.AbsolutePath);
            if(request.RequestUri.AbsolutePath.EndsWith("/login"))Interlocked.Increment(ref Logins);
            else if(request.RequestUri.AbsolutePath.EndsWith("/refreshToken"))
            {
                Interlocked.Increment(ref Refreshes);
                return Task.FromResult(Json("{\"accessToken\":\"fixture-refreshed\",\"expires\":\"2099-01-01T00:00:00Z\"}"));
            }
            else Interlocked.Increment(ref Reads);
            return Task.FromResult(fixture.Reply(request));
        }
        protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}
    }
    sealed class SessionStore(string directory) : Store(new EphemeralDataProtectionProvider(),new DataLocation(directory))
    {
        public readonly List<SessionTransport> Transports=[];
        protected override Api CreateClient(Connection connection,string password)
        {
            var transport=new SessionTransport();Transports.Add(transport);
            return new(connection,password,new HttpClient(transport));
        }
    }
    public static async Task ReuseAndRetirement()
    {
        var directory=Path.Combine(Path.GetTempPath(),"pointbridge-session-"+Guid.NewGuid());
        using var store=new SessionStore(directory);
        await store.Set("source",new Connection{BaseUrl="https://source.test",ApiVersion="v5",Username="fixture-api",Password="fixture-password"});
        await store.Set("destination",new Connection{BaseUrl="https://destination.test",ApiVersion="v4",Username="fixture-api",Password="fixture-password"});
        foreach(var destination in new[]{Id,"52cfddc0-411f-5b9e-9a6d-442cb62fecbb"})
        {
            var validation=await store.ValidateMapping(new Mapping{Name="Fixture",SourceObject=Id,DestinationObject=destination},default);
            Check(validation.Valid,"two mapping preflights succeed: "+string.Join(";",validation.Errors));
        }
        Check(store.Transports.Count==2&&store.Transports.Sum(transport=>transport.Logins)==2,"two mappings create only one login per connection");
        store.Config.Mappings.AddRange(new[]{new Mapping{Id="fixture-first",Name="Fixture first",SourceObject=Id,DestinationObject=Id},
            new Mapping{Id="fixture-second",Name="Fixture second",SourceObject=Id,DestinationObject="52cfddc0-411f-5b9e-9a6d-442cb62fecbb"}});
        using(var bridge=new Bridge(store))
        {
            await bridge.SetArmed(true);
            Check(bridge.Armed&&store.Transports.Sum(transport=>transport.Logins)==2,"Start validates both mappings using existing connection sessions");
            await bridge.SetArmed(false);
        }
        var sourceTransport=store.Transports.Single(transport=>transport.Paths[0]=="/api/v5/login");
        var first=store.Client("source");var second=store.Client("source");first.Dispose();first.Dispose();
        await second.Get("objects",default);
        Check(!sourceTransport.Disposed&&sourceTransport.Logins==1,"closing a lease neither invalidates shared token nor closes other requests");
        using(var savedTest=store.TemporaryClient("source",new Connection{BaseUrl="https://source.test",ApiVersion="v5",Username="fixture-api"}))
            await savedTest.TestConnection(default);
        Check(sourceTransport.Logins==1&&sourceTransport.Refreshes==1,"saved-credential test contacts server through token refresh rather than another password login");
        var priorSourceReads=sourceTransport.Reads;
        await store.Set("source",new Connection{BaseUrl="https://source.test",ApiVersion="v6",Username="fixture-api"});
        Check(!sourceTransport.Disposed,"configuration retirement waits for active old leases");
        await second.Get("objects",default);
        Check(sourceTransport.Reads==priorSourceReads+1&&sourceTransport.Paths.Last()=="/api/v5/objects","old lease stays bound to original version and origin");
        using(var changed=store.Client("source"))await changed.Get("objects",default);
        Check(store.Transports.Count==3&&store.Transports.Last().Logins==1,"version change creates a separate authenticated client");
        second.Dispose();Check(sourceTransport.Disposed,"retired HTTP client closes after final old lease ends");
        var changedTransport=store.Transports.Last();await store.ClearCredentials("source");
        Check(changedTransport.Disposed,"clearing credentials immediately retires idle token/client");
        var rejected=false;try{using var unavailable=store.Client("source");}catch(InvalidOperationException){rejected=true;}
        Check(rejected,"cleared credentials cannot acquire cached authentication");
        store.Dispose();Check(store.Transports.All(transport=>transport.Disposed),"store shutdown releases all idle clients");
    }
    public static async Task RequestErrors()
    {
        foreach(var side in new[]{"source","destination"})
        {
            var transport=new ContractTransport();
            using var api=new Api(new Connection{BaseUrl="https://error.test",ApiVersion="v5",Username=Hidden},Hidden,new HttpClient(transport)){ConnectionName=side};
            transport.Expect(request=>Task.FromResult(Json("{\"accessToken\":\""+Hidden+"\",\"expires\":\"2099-01-01T00:00:00Z\"}")));
            await api.Login(default);
            foreach(var operation in new (string Method,string Path,Func<Task> Call)[]{
                ("GET",$"/api/v5/objects/{Id}/attributes/presentValue",async()=>{await api.Get($"objects/{Id}/attributes/presentValue?fqr={Hidden}",default);}),
                ("PATCH",$"/api/v5/objects/{Id}",()=>api.Write(Id,JsonValue.Create(20)!,default)),
                ("PUT",$"/api/v5/objects/{Id}/commands/adjustCommand",()=>api.Command(Id,"adjustCommand",new JsonObject{["parameters"]=new JsonArray(20)},default)),
                ("GET","/api/v5/stream",()=>api.Stream((kind,data)=>Task.CompletedTask,default))})
            {
                transport.Expect(request=>Task.FromResult(Json("{\"private\":\""+Hidden+"\"}",HttpStatusCode.Forbidden)));
                var rejected=false;
                try{await operation.Call();}
                catch(MetasysRequestException exception)
                {
                    rejected=exception.StatusCode==HttpStatusCode.Forbidden&&exception.Message.Contains(side+":")
                        &&exception.Message.Contains(operation.Method+" "+operation.Path)&&!exception.Message.Contains(Hidden)&&!exception.Message.Contains("?");
                }
                Check(rejected,"403 identifies exact connection, method and path without query, credentials or response body");
            }
            transport.Complete();
        }
        var failed=new ContractTransport();using var login=new Api(new Connection{BaseUrl="https://error.test"},Hidden,new HttpClient(failed)){ConnectionName="source"};
        failed.Expect(request=>Task.FromResult(Json("{}",HttpStatusCode.Forbidden)));
        var identified=false;try{await login.Login(default);}catch(MetasysRequestException exception){identified=exception.Message.Contains("source:")&&exception.Message.Contains("POST /api/v5/login");}
        Check(identified,"login refusal distinguished from point permission refusal");failed.Complete();
        var clock=new FailureScenarios.TestClock();var refresh=new ContractTransport();
        using var capped=new Api(new Connection{BaseUrl="https://error.test",ApiVersion="v4"},Hidden,new HttpClient(refresh),clock);
        refresh.Expect(request=>Task.FromResult(Json(new JsonObject{["accessToken"]="fixture",["expires"]=clock.Now.AddHours(5).ToString("O")}.ToJsonString())));
        await capped.Login(default);clock.Now=clock.Now.AddMinutes(55).AddSeconds(-10);
        refresh.Expect(request=>{Check(request.RequestUri!.AbsolutePath=="/api/v4/refreshToken","v4 long-reported expiry refreshes before one hour");return Task.FromResult(Json(new JsonObject{["accessToken"]="renewed",["expires"]=clock.Now.AddHours(5).ToString("O")}.ToJsonString()));});
        refresh.Expect(request=>Task.FromResult(Json("{}")));await capped.Get("objects",default);refresh.Complete();
    }
}
