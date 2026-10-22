using System.Net;
using System.Text.Json.Nodes;
using static ContractTransport;

public static class SchemaExpansionScenarios
{
    const string Id="32cfddc0-411f-5b9e-9a6d-442cb62fecbb";
    sealed class Handler(string version) : HttpMessageHandler
    {
        readonly PreflightScenarios.Fixture fixture=new();
        public int UnusedRequests,RequiredRequests;
        public bool DenyRequired,RequireAttributeEnum;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;
            if(path.EndsWith("/schemas/enums/attributeEnumSet"))
            {
                UnusedRequests++;
                return Json("{}",HttpStatusCode.Forbidden);
            }
            if(path.EndsWith("/schemas/enums/writePriorityEnumSet"))
            {
                RequiredRequests++;
                if(DenyRequired)return Json("{}",HttpStatusCode.Forbidden);
            }
            using var response=fixture.Reply(request);
            var body=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            if(path.EndsWith("/commands"))
            {
                var command=body["items"]![0]!;
                command["commandBodySchema"]!["definitions"]!["attributeEnumSet"]=new JsonObject
                    {["allOf"]=new JsonArray(new JsonObject{["$ref"]=$"https://schema.test/api/{version}/schemas/enums/attributeEnumSet"})};
                if(RequireAttributeEnum)command["commandBodySchema"]!["properties"]!["parameters"]!["items"]![0]=new JsonObject{["$ref"]="#attributeEnumSet"};
                body["effectivePermissions"]!["canInvoke"]!.AsArray().Add("commandIdEnumSet.releaseCommand");
                body["items"]!.AsArray().Add(new JsonObject
                {
                    ["id"]="commandIdEnumSet.releaseCommand",
                    ["commandBodySchema"]=new JsonObject
                    {
                        ["type"]="object",["properties"]=new JsonObject
                        {
                            ["parameters"]=new JsonObject{["type"]="array",["items"]=new JsonArray(new JsonObject
                                {["$ref"]=$"https://schema.test/api/{version}/schemas/enums/attributeEnumSet"})}
                        }
                    }
                });
            }
            return Json(body.ToJsonString(),response.StatusCode);
        }
    }
    public static async Task Run()
    {
        foreach(var version in new[]{"v4","v5","v6"})
        {
            var handler=new Handler(version);
            using var destination=new Api(new Connection{BaseUrl="https://schema.test",ApiVersion=version},"fixture-password",new HttpClient(handler)){ConnectionName="destination"};
            var source=new PointInfo{Id=Id,Value=JsonValue.Create(20),Type="number",Units="unitEnumSet.degC"};
            var mapping=new Mapping{Name="Fixture numeric point",SourceObject=Id,DestinationObject=Id,
                WriteMode="command",CommandId="commandIdEnumSet.adjustCommand",CommandPriority="writePriorityEnumSet.priorityDefault"};
            var result=await Preflight.ValidatePoint(mapping,source,destination,default);
            Check(result.Valid,"unused enum refusal cannot block direct numeric adjustment: "+string.Join(";",result.Errors));
            Check(handler.UnusedRequests==0,"unused definitions and unsupported commands never request attributeEnumSet");
            Check(handler.RequiredRequests>0,"actually referenced priority enum is still validated");
            Check(result.Commands.Single(command=>command.Id=="commandIdEnumSet.releaseCommand").Supported==false,"unexpanded release command remains unsupported");
            var body=Preflight.CommandBody(result.SelectedCommand!,mapping,JsonValue.Create(25)!);
            Check(body["parameters"]![0]!.GetValue<int>()==25&&body["priority"]!.ToString()==mapping.CommandPriority,"validated scalar and advertised priority preserved");
            handler.DenyRequired=true;
            var denied=await Preflight.ValidatePoint(mapping,source,destination,default);
            Check(!denied.Valid&&denied.Errors.Any(error=>error.Contains("403")&&error.Contains("schemas/enums/writePriorityEnumSet")),"required enum failure still blocks command and identifies exact request");
            handler.DenyRequired=false;handler.RequireAttributeEnum=true;
            var parameterDenied=await Preflight.ValidatePoint(mapping,source,destination,default);
            Check(!parameterDenied.Valid&&handler.UnusedRequests==1&&parameterDenied.Errors.Any(error=>error.Contains("schemas/enums/attributeEnumSet")),"attribute enum is still required when a supported command parameter actually references it");
        }
    }
}
