using System.Net;
using System.Text.Json.Nodes;
using static ContractTransport;
public static class PreflightScenarios
{
    const string Id="32cfddc0-411f-5b9e-9a6d-442cb62fecbb";
    public sealed class Fixture
    {
        public string Type="number",Units="unitEnumSet.degC";public bool Writable=true,Aggregate=false,MissingSchema=false,DenyCommand=false,MissingCommandPermissions=false;public double Value=20,Max=100;public int Writes;public JsonNode? LastWrite;
        public JsonNode Schema=>new JsonObject{["type"]=Type,["minimum"]=0,["maximum"]=Max};
        public HttpResponseMessage Reply(HttpRequestMessage q)
        {
            var p=q.RequestUri!.AbsolutePath;
            if(p.EndsWith("/login"))return Json(new JsonObject{["accessToken"]="test",["expires"]=DateTimeOffset.UtcNow.AddHours(1).ToString("O")}.ToJsonString());
            if(p.EndsWith("/schemas/enums/writePriorityEnumSet"))return Json("{\"type\":\"string\",\"oneOf\":[{\"const\":\"writePriorityEnumSet.priorityDefault\"},{\"const\":\"writePriorityEnumSet.priorityOperatorOverride\"}]}");
            if(p.EndsWith("/commands"))return Json(Commands().ToJsonString());
            var item=new JsonObject{["name"]="Temp",["units"]=Units,["presentValue"]=Value};
            var schema=new JsonObject{["type"]="object",["properties"]=new JsonObject{["presentValue"]=Schema}};
            if(p.EndsWith("/attributes"))return Json(new JsonObject{["schema"]=new JsonObject{["properties"]=Writable?new JsonObject{["presentValue"]=Schema}:new JsonObject()}}.ToJsonString());
            var result=new JsonObject{["item"]=item,["effectivePermissions"]=new JsonObject{["canModify"]=true}};if(!MissingSchema)result["schema"]=schema;
            return Json(result.ToJsonString());
        }
        public JsonObject Commands()=>new(){["effectivePermissions"]=MissingCommandPermissions?null:new JsonObject{["canInvoke"]=DenyCommand?new JsonArray():new JsonArray("commandIdEnumSet.adjustCommand")},["items"]=new JsonArray(new JsonObject{
            ["id"]="commandIdEnumSet.adjustCommand",["title"]="Adjust",["aggregateCommand"]=Aggregate,
            ["commandBodySchema"]=new JsonObject{
                ["type"]="object",["properties"]=new JsonObject{
                    ["priority"]=JsonNode.Parse("{\"oneOf\":[{\"$ref\":\"#writePriorityEnumSet\"},{\"type\":\"null\"}]}"),
                    ["parameters"]=new JsonObject{["type"]="array",["items"]=new JsonArray(Schema),["minItems"]=1,["maxItems"]=1},
                    ["annotation"]=new JsonObject{["type"]="string",["minLength"]=1,["maxLength"]=255}},
                ["required"]=new JsonArray(),["additionalProperties"]=false,
                ["definitions"]=JsonNode.Parse("{\"writePriorityEnumSet\":{\"allOf\":[{\"$ref\":\"https://adx.test/api/v6/schemas/enums/writePriorityEnumSet\"}]}}")}})};
    }
    sealed class Handler(Fixture fixture):HttpMessageHandler
    {protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage q,CancellationToken ct){if(q.Method==HttpMethod.Put){fixture.Writes++;fixture.LastWrite=JsonNode.Parse(await q.Content!.ReadAsStringAsync(ct));return new(HttpStatusCode.Accepted);}return fixture.Reply(q);}}
    static Api Client(Fixture f,string host)=>new(new Connection{BaseUrl=host,ApiVersion="v6"},"test",new HttpClient(new Handler(f)));
    public static async Task Run()
    {
        var source=new Fixture();var destination=new Fixture();using var src=Client(source,"https://source.test");using var dst=Client(destination,"https://destination.test");
        var m=new Mapping{SourceObject=Id,DestinationObject=Id};
        var valid=await Preflight.Validate(m,src,dst,default);Check(valid.Valid,"compatible scalar preflight: "+string.Join(";",valid.Errors));
        destination.Units="unitEnumSet.degF";Check(!(await Preflight.Validate(m,src,dst,default)).Valid,"units mismatch must fail");destination.Units=source.Units;
        destination.Writable=false;Check(!(await Preflight.Validate(m,src,dst,default)).Valid,"read-only destination must fail");destination.Writable=true;
        destination.Type="string";Check(!(await Preflight.Validate(m,src,dst,default)).Valid,"incompatible types must fail");destination.Type="number";
        destination.Max=10;Check(!(await Preflight.Validate(m,src,dst,default)).Valid,"out-of-range current value must fail");destination.Max=100;
        destination.MissingSchema=true;Check(!(await Preflight.Validate(m,src,dst,default)).Valid,"missing metadata must fail");destination.MissingSchema=false;
        m.WriteMode="command";m.CommandId="commandIdEnumSet.adjustCommand";m.CommandPriority="writePriorityEnumSet.priorityDefault";
        var command=await Preflight.Validate(m,src,dst,default);Check(command.Valid,"official tuple/reference command preflight: "+string.Join(";",command.Errors));
        var body=Preflight.CommandBody(command.SelectedCommand!,m,JsonValue.Create(25)!);
        Check(body["parameters"] is JsonArray a && a.Count==1 && a[0]!.GetValue<int>()==25,"single scalar command parameter");
        Check(body["priority"]!.ToString()==m.CommandPriority,"schema-approved command priority");
        await dst.Command(Id,m.CommandId,body,default);Check(destination.Writes==1 && JsonNode.DeepEquals(destination.LastWrite,body),"HTTP202 command sends actual validated payload");
        m.CommandPriority="writePriorityEnumSet.priorityManualEmergency";Check(!(await Preflight.Validate(m,src,dst,default)).Valid,"undeclared priority must fail");m.CommandPriority=null;
        destination.DenyCommand=true;Check(!(await Preflight.Validate(m,src,dst,default)).Valid,"denied command must fail");destination.DenyCommand=false;
        destination.MissingCommandPermissions=true;Check(!(await Preflight.Validate(m,src,dst,default)).Valid,"missing command permissions must fail closed");destination.MissingCommandPermissions=false;
        destination.Aggregate=true;Check(!(await Preflight.Validate(m,src,dst,default)).Valid,"aggregate MV/BV adjust must fail");
        var requiredPriority=command.SelectedCommand!;
        requiredPriority.CommandBodySchema!["required"]=new JsonArray("parameters","priority");
        var requiredHandled=false;
        try
        {
            var missingPriority=Preflight.CommandBody(requiredPriority,m,JsonValue.Create(25)!);
            requiredHandled=missingPriority["priority"]!=null;
        }
        catch(InvalidOperationException e) when(e.Message.Contains("priority",StringComparison.OrdinalIgnoreCase)) { requiredHandled=true; }
        Check(requiredHandled,"CommandBody must enforce required priority or provide schema-approved default");
        Console.WriteLine("PASS Preflight type/units/range/writability/schema and command tuple/ref/priority/aggregate contracts");
    }
}
