using System.Net;
using System.Text.Json.Nodes;
using static ContractTransport;

public static class MetasysVersionScenarios
{
    const string Id="32cfddc0-411f-5b9e-9a6d-442cb62fecbb";
    static HttpResponseMessage Token(string value,DateTimeOffset expires)=>Json(new JsonObject
        {["accessToken"]=value,["expires"]=expires.ToString("O")}.ToJsonString());
    public static async Task TokensAndBatch()
    {
        foreach(var version in new[]{"v4","v5","v6"})
        {
            var clock=new FailureScenarios.TestClock();var transport=new ContractTransport();
            using var api=new Api(new Connection{BaseUrl="https://version.test",ApiVersion=version,Username="fixture-api"},"fixture-password",new HttpClient(transport),clock);
            transport.Expect(request=>Task.FromResult(Token("initial",clock.Now.AddMinutes(1))));
            await api.Login(default);clock.Now=clock.Now.AddSeconds(40);
            transport.Expect(request=>{
                Check(request.Method==HttpMethod.Get&&request.RequestUri!.AbsolutePath==$"/api/{version}/refreshToken","refresh uses versioned GET");
                Check(request.Headers.Authorization?.Parameter=="initial"&&request.Content==null,"refresh sends bearer, not password");
                return Task.FromResult(Token("renewed",clock.Now.AddMinutes(1)));
            });
            transport.Expect(async request=>{
                Check(request.Method==HttpMethod.Post&&request.RequestUri!.AbsolutePath==$"/api/{version}/objects/batch","batch endpoint");
                Check(request.Headers.Authorization?.Parameter=="renewed","batch uses refreshed bearer");
                var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync());
                Check(body?["method"]?.ToString()=="GET"&&body?["requests"] is JsonArray requests&&requests.Count==1
                    &&requests[0]?["relativeUrl"]?.ToString()==Id+"/attributes/presentValue","batch deduplicates and only reads presentValue");
                return Json("{\"responses\":[{\"id\":\""+Id+"\",\"status\":403,\"body\":{}}]}");
            });
            var batch=await api.ReadBatch(new[]{Id,Id},default);
            Check(batch?["responses"]?[0]?["status"]?.GetValue<int>()==403,"batch retains individual failure status");
            var rejected=false;try{await api.ReadBatch(new[]{"../login"},default);}catch(ArgumentException){rejected=true;}
            Check(rejected,"batch rejects route injection before sending HTTP");
            rejected=false;try{await api.ReadBatch(Enumerable.Range(0,51).Select(index=>Guid.NewGuid().ToString()),default);}catch(ArgumentException){rejected=true;}
            Check(rejected,"batch request count bounded");
            clock.Now=clock.Now.AddSeconds(40);
            transport.Expect(request=>Task.FromResult(Json("{}",HttpStatusCode.ServiceUnavailable)));
            rejected=false;try{await api.Get("objects",default);}catch(HttpRequestException exception)when(exception.StatusCode==HttpStatusCode.ServiceUnavailable){rejected=true;}
            Check(rejected,"refresh outage is surfaced without credential retry or point request");
            transport.Expect(request=>Task.FromResult(Json("{}",HttpStatusCode.Unauthorized)));
            transport.Expect(request=>{
                Check(request.Method==HttpMethod.Post&&request.RequestUri!.AbsolutePath.EndsWith("/login"),"only rejected refresh permits one password login");
                return Task.FromResult(Token("replacement",clock.Now.AddMinutes(1)));
            });
            transport.Expect(request=>Task.FromResult(Json("{}")));
            await api.Get("objects",default);transport.Complete();
        }
    }
    sealed class VersionFourTransport:HttpMessageHandler
    {
        public bool ReadOnly,Prioritized,DenyModify,DenyCommand,MissingPermissions;
        public int Writes;
        readonly PreflightScenarios.Fixture fixture=new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Check(request.RequestUri!.AbsolutePath.StartsWith("/api/v4/"),"v4 destination stays on selected API");
            if(request.Method==HttpMethod.Put)
            {
                Writes++;var body=JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
                Check(body?["parameters"]?[0]?.GetValue<int>()==25,"v4 command native numeric tuple");
                return Json("{}");
            }
            using var response=fixture.Reply(request);
            var bodyNode=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            if(request.RequestUri.AbsolutePath.EndsWith("/attributes"))
            {
                Check(request.RequestUri.Query=="","v4 does not send unsupported writableBasic filter");
                bodyNode["schema"]!["properties"]!["presentValue"]!["readOnly"]=ReadOnly;
                bodyNode["schema"]!["properties"]!["presentValue"]!["prioritized"]=Prioritized;
            }
            if(request.RequestUri.AbsolutePath.EndsWith("/commands"))
            {
                bodyNode.AsObject().Remove("effectivePermissions");
                if(!MissingPermissions)bodyNode["effectivePermissions"]=new JsonObject{["canInvoke"]=DenyCommand?new JsonArray():new JsonArray("commandIdEnumSet.adjustCommand")};
            }
            else if(bodyNode["effectivePermissions"] is JsonObject permissions)permissions["canModify"]=!DenyModify;
            return Json(bodyNode.ToJsonString());
        }
    }
    public static async Task VersionFour()
    {
        var transport=new VersionFourTransport();
        using var destination=new Api(new Connection{BaseUrl="https://destination.test",ApiVersion="v4"},"fixture-password",new HttpClient(transport));
        var source=new PointInfo{Id=Id,Value=JsonValue.Create(20),Type="number",Units="unitEnumSet.degC"};
        var mapping=new Mapping{SourceObject=Id,DestinationObject=Id};
        Check((await Preflight.ValidatePoint(mapping,source,destination,default)).Valid,"v4 writable attribute accepted");
        transport.ReadOnly=true;Check(!(await Preflight.ValidatePoint(mapping,source,destination,default)).Valid,"v4 readOnly attribute denied");transport.ReadOnly=false;
        transport.DenyModify=true;Check(!(await Preflight.ValidatePoint(mapping,source,destination,default)).Valid,"v4 canModify false denied");transport.DenyModify=false;
        transport.Prioritized=true;Check(!(await Preflight.ValidatePoint(mapping,source,destination,default)).Valid,"prioritized v4 point requires explicit command");
        mapping.WriteMode="command";mapping.CommandId="commandIdEnumSet.adjustCommand";
        var result=await Preflight.ValidatePoint(mapping,source,destination,default);
        Check(result.Valid,"prioritized v4 point accepts permitted scalar adjust command: "+string.Join(";",result.Errors));
        await destination.Command(Id,mapping.CommandId,Preflight.CommandBody(result.SelectedCommand!,mapping,JsonValue.Create(25)!),default);
        Check(transport.Writes==1,"validated v4 command sent once");
        transport.DenyCommand=true;Check(!(await Preflight.ValidatePoint(mapping,source,destination,default)).Valid,"v4 denied command rejected");
        transport.MissingPermissions=true;Check(!(await Preflight.ValidatePoint(mapping,source,destination,default)).Valid,"v4 missing command permissions fail closed");
    }
}
