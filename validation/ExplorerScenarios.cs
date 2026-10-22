using System.Net;
using System.Text.Json.Nodes;
using static ContractTransport;

public static class ExplorerScenarios
{
    const string Id="52cfddc0-411f-5b9e-9a6d-442cb62fecbb";
    const string Equipment="32cfddc0-411f-5b9e-9a6d-442cb62fecbb";
    const string Canary="fixture-sensitive-value-never-display";
    public static async Task Run()
    {
        foreach(var version in new[]{"v4","v5","v6"})
        {
            var transport=new ContractTransport();
            using var api=new Api(new Connection{BaseUrl="https://explorer.test",ApiVersion=version},"fixture-password",new HttpClient(transport));
            transport.Expect(request=>Task.FromResult(Json("{\"accessToken\":\"fixture-token\",\"expires\":\"2099-01-01T00:00:00Z\"}")));
            foreach(var branch in new[]{("objects",(string?)null,"objects?depth=1&flatten=true"),("networkDevices",null,"networkDevices?page=1&pageSize=50"),
                ("equipment",null,"equipment?page=1&pageSize=50"),("spaces",null,"spaces?page=1&pageSize=50"),
                ("spaceEquipment",Id,$"spaces/{Id}/equipment?page=1&pageSize=50"),("spaces",Id,$"spaces/{Id}/spaces?page=1&pageSize=50"),
                ("networkDevices",Id,$"networkDevices/{Id}/networkDevices?page=1&pageSize=50"),("deviceObjects",Id,$"objects/{Id}/objects?depth=1&flatten=true")})
            {
                transport.Expect(request=>{
                    Check(request.Method==HttpMethod.Get&&request.RequestUri!.PathAndQuery==$"/api/{version}/"+branch.Item3,"documented discovery route "+branch.Item1);
                    return Task.FromResult(Json("{\"items\":[{\"id\":\""+Equipment+"\",\"name\":\"Fixture branch\",\"hasChildrenMatchingQuery\":true}],\"next\":null}"));
                });
                var page=await MetasysExplorer.Browse(api,branch.Item1,branch.Item2,1,default);
                Check(page.Items.Count==1&&page.Items[0].Children.Length>0,"branch navigation retained");
            }
            transport.Expect(request=>Task.FromResult(Json("{\"items\":[{\"id\":\""+Equipment+"\",\"label\":\"Fixture point\",\"objectUrl\":\"https://explorer.test/api/"+version+"/objects/"+Id+"\",\"attribute\":\"presentValue\"},{\"id\":\""+Equipment+"\",\"objectUrl\":\"https://untrusted.test/api/"+version+"/objects/"+Id+"\"}],\"next\":\"https://untrusted.test/ignored\"}")));
            var points=await MetasysExplorer.Browse(api,"equipmentPoints",Equipment,1,default);
            Check(points.Items.Count==1&&points.Items[0].Id==Id&&points.Items[0].Selectable,"point selection uses mapped object ID, never equipment ID or foreign URL");
            Check(points.NextPage==2,"pagination exposes number instead of following server URL");
            transport.Expect(request=>Task.FromResult(Json("{\"responses\":[{\"id\":\""+Id+"\",\"status\":200,\"body\":{\"item\":{\"presentValue\":21},\"password\":\""+Canary+"\",\"condition\":{\"presentValue\":{\"reliability\":\"reliabilityEnumSet.reliable\",\"priority\":\"writePriorityEnumSet.priorityDefault\"}}}},{\"id\":\""+Equipment+"\",\"status\":403,\"body\":{\"message\":\""+Canary+"\"}}]}")));
            var values=await MetasysExplorer.ReadValues(api,[Id,Equipment],default);
            Check(values["items"]![0]!["success"]!.GetValue<bool>()&&!values["items"]![1]!["success"]!.GetValue<bool>(),"batch partial failure remains per-point");
            Check(!values.ToJsonString().Contains(Canary)&&values["items"]![0]!["priority"]!=null,"batch keeps quality/priority but not arbitrary server fields");
            var end=DateTimeOffset.UtcNow;var start=end.AddHours(-1);
            transport.Expect(request=>{
                Check(request.Method==HttpMethod.Get&&request.RequestUri!.AbsolutePath==$"/api/{version}/objects/{Id}/trendedAttributes/presentValue/samples","read-only trend endpoint");
                Check(request.RequestUri!.Query.Contains(version=="v4"?"startTime=":"startSampleTime=")&&request.RequestUri.Query.Contains(version=="v4"?"sort=-timestamp":"sort=-sampleTime"),"version-specific trend query");
                Check(version=="v4"||!request.RequestUri.Query.Contains("page="),"no unsupported v5/v6 trend paging");
                return Task.FromResult(Json(version=="v4"?"{\"items\":[{\"timestamp\":\"2026-10-22T12:00:00Z\",\"value\":{\"value\":21,\"units\":\"unitEnumSet.degC\"},\"isReliable\":true}],\"next\":null}":"{\"items\":[{\"sampleTime\":\"2026-10-22T12:00:00Z\",\"value\":21,\"reliability\":\"reliabilityEnumSet.reliable\"}]}"));
            });
            var trend=await MetasysExplorer.Inspect(api,Id,"trends",start,end,1,null,default);
            Check(trend["items"]![0]!["value"]!.GetValue<int>()==21&&trend["items"]![0]!["timestamp"]!=null,"versioned trend scalar and timestamp normalization");
            foreach(var view in new[]{"alarms","audits"})
            {
                transport.Expect(request=>{
                    Check(request.Method==HttpMethod.Get&&request.RequestUri!.Query.Contains("object="+Id)&&request.RequestUri.Query.Contains("activityType="+(view=="alarms"?"alarm":"audit")),"object-scoped read-only activities");
                    return Task.FromResult(Json("{\"items\":[{\"id\":\"fixture-activity\",\"objectName\":\"Temperature\",\"audit\":{\"actionType\":\"write\",\"user\":\""+Canary+"\",\"parameters\":[\""+Canary+"\"],\"postData\":\""+Canary+"\"}}],\"next\":\"https://explorer.test/api/"+version+"/activities?continuationToken=fixture%2Bnext\"}"));
                });
                var activity=await MetasysExplorer.Inspect(api,Id,view,start,end,1,null,default);
                Check(!activity.ToJsonString().Contains(Canary)&&activity["continuationToken"]!.ToString()=="fixture+next","audit redaction and pinned continuation extraction");
            }
            transport.Expect(request=>{
                Check(request.RequestUri!.Query.Contains("continuationToken=fixture%2Bnext%26evil%3D1"),"continuation query injection encoded");
                return Task.FromResult(Json("{\"items\":[],\"next\":\"https://untrusted.test/api/"+version+"/activities?continuationToken=foreign\"}"));
            });
            Check((await MetasysExplorer.Inspect(api,Id,"audits",start,end,2,"fixture+next&evil=1",default))["continuationToken"]==null,"foreign continuation URL never followed");
            foreach(var action in new Func<Task>[] {
                async()=>{await MetasysExplorer.Browse(api,"../login",null,1,default);},
                async()=>{await MetasysExplorer.Browse(api,"equipmentPoints","../login",1,default);},
                async()=>{await MetasysExplorer.Inspect(api,Id,"acknowledge",start,end,1,null,default);},
                async()=>{await MetasysExplorer.Inspect(api,Id,"trends",end.AddDays(-8),end,1,null,default);},
                async()=>{await MetasysExplorer.ReadValues(api,[],default);}})
            {
                var rejected=false;try{await action();}catch(ArgumentException){rejected=true;}Check(rejected,"invalid explorer input rejected before HTTP");
            }
            transport.Complete();
        }
        await TokenAndTls();
        await ReadOnlyFailures();
    }
    static async Task ReadOnlyFailures()
    {
        var transport=new ContractTransport();using var api=new Api(new Connection{BaseUrl="https://explorer.test",ApiVersion="v6"},"fixture-password",new HttpClient(transport));
        transport.Expect(request=>Task.FromResult(Json("{\"accessToken\":\"fixture-token\",\"expires\":\"2099-01-01T00:00:00Z\"}")));
        transport.Expect(request=>Task.FromResult(Json("{\"item\":{\"presentValue\":21},\"schema\":{\"properties\":{\"presentValue\":{\"type\":\"number\"}}},\"condition\":{\"presentValue\":{\"priority\":\"writePriorityEnumSet.priorityDefault\"}}}")));
        transport.Expect(request=>Task.FromResult(Json("{\"item\":{\"name\":\"Fixture point\",\"units\":\"unitEnumSet.degC\"}}")));
        transport.Expect(request=>Task.FromResult(Json("{\"message\":\""+Canary+"\"}",HttpStatusCode.Forbidden)));
        var end=DateTimeOffset.UtcNow;
        var summary=await MetasysExplorer.Inspect(api,Id,"summary",end.AddHours(-1),end,1,null,default);
        Check(summary["value"]!.GetValue<int>()==21&&summary["commandsWarning"]!=null&&summary["reliability"]==null,"missing command access does not hide readable point or invent quality");
        Check(!summary.ToJsonString().Contains(Canary)&&summary["priority"]!=null,"summary retains priority and suppresses server failure body");
        transport.Expect(request=>{
            var items=new JsonArray(Enumerable.Range(0,51).Select(index=>(JsonNode)new JsonObject{["id"]=index.ToString(),["audit"]=new JsonObject{["user"]=Canary}}).ToArray());
            return Task.FromResult(Json(new JsonObject{["items"]=items,["next"]="https://explorer.test/api/v6/activities?continuationToken=after-undisplayed-records"}.ToJsonString()));
        });
        var truncated=await MetasysExplorer.Inspect(api,Id,"audits",end.AddHours(-1),end,1,null,default);
        Check(truncated["items"]!.AsArray().Count==50&&truncated["truncated"]!.GetValue<bool>()&&truncated["continuationToken"]==null,"truncated activities never skip undisplayed records through Next");
        Check(!truncated.ToJsonString().Contains(Canary),"every truncated audit row is projected without user data");
        transport.Expect(request=>Task.FromResult(Json("{\"responses\":[{\"id\":\""+Id+"\",\"status\":200},{\"id\":\""+Id+"\",\"status\":200}]}")));
        var duplicate=await MetasysExplorer.ReadValues(api,[Id],default);
        Check(!duplicate["items"]![0]!["success"]!.GetValue<bool>(),"duplicate batch response cannot establish a successful value");
        transport.Complete();
    }
    static async Task TokenAndTls()
    {
        var clock=new FailureScenarios.TestClock();var transport=new ContractTransport();
        using var api=new Api(new Connection{BaseUrl="https://explorer.test"},"fixture-password",new HttpClient(transport),clock);
        transport.Expect(request=>Task.FromResult(Json(new JsonObject{["accessToken"]="initial",["expires"]=clock.Now.AddMinutes(1).ToString("O")}.ToJsonString())));
        await api.Login(default);clock.Now=clock.Now.AddSeconds(40);
        transport.Expect(async request=>{await Task.Delay(50);Check(request.RequestUri!.AbsolutePath.EndsWith("/refreshToken"),"concurrent callers share refresh");return Json(new JsonObject{["accessToken"]="new",["expires"]=clock.Now.AddHours(1).ToString("O")}.ToJsonString());});
        transport.Expect(request=>Task.FromResult(Json("{}")));transport.Expect(request=>Task.FromResult(Json("{}")));
        await Task.WhenAll(api.Get("objects",default),api.Get("objects",default));transport.Complete();
        var bad=new ContractTransport();using var invalid=new Api(new Connection{BaseUrl="https://explorer.test"},"fixture-password",new HttpClient(bad));
        bad.Expect(request=>Task.FromResult(Json("{\"accessToken\":\"invalid\"}")));
        var rejected=false;try{await invalid.Login(default);}catch(IOException){rejected=true;}Check(rejected,"missing expiry rejected, not guessed");
        bad.Expect(request=>throw new HttpRequestException(Canary,new System.Security.Authentication.AuthenticationException(Canary)));
        rejected=false;try{await invalid.Login(default);}catch(IOException exception){rejected=exception.Message.Contains("certificate")&&!exception.Message.Contains(Canary);}
        Check(rejected,"certificate failure gives safe trust guidance without bypass or raw exception");bad.Complete();
        var large=new ContractTransport();using var bounded=new Api(new Connection{BaseUrl="https://explorer.test"},"fixture-password",new HttpClient(large));
        large.Expect(request=>Task.FromResult(Json("{\"accessToken\":\"fixture\",\"expires\":\"2099-01-01T00:00:00Z\"}")));
        large.Expect(request=>Task.FromResult(Json(new string(' ',4*1024*1024+1))));
        rejected=false;try{await bounded.Get("objects",default);}catch(HttpRequestException){rejected=true;}Check(rejected,"oversized API JSON bounded before parsing");large.Complete();
    }
}
